export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly detail?: string

  constructor(status: number, title: string, detail?: string) {
    super(title)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
  }
}

export interface ApiClient {
  get<T>(path: string): Promise<T>
  post<T>(path: string, body?: unknown): Promise<T>
  put<T>(path: string, body?: unknown): Promise<T>
  patch<T>(path: string, body?: unknown): Promise<T>
  delete<T>(path: string): Promise<T>
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

type UnauthorizedListener = (path: string) => void
const unauthorizedListeners = new Set<UnauthorizedListener>()

export function onUnauthorized(listener: UnauthorizedListener): () => void {
  unauthorizedListeners.add(listener)
  return () => { unauthorizedListeners.delete(listener) }
}

async function toApiError(res: Response): Promise<ApiError> {
  let title = res.statusText || `HTTP ${res.status}`
  let detail: string | undefined
  try {
    const problem: unknown = await res.json()
    if (problem && typeof problem === 'object') {
      const p = problem as { title?: unknown; detail?: unknown }
      if (typeof p.title === 'string') title = p.title
      if (typeof p.detail === 'string') detail = p.detail
    }
  } catch {
    // Body is not JSON (or empty) — keep the status-based title.
  }
  return new ApiError(res.status, title, detail)
}

export function createApiClient(baseUrl: string): ApiClient {
  async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
    const hasBody = body !== undefined
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      credentials: 'include',
      headers: hasBody
        ? { Accept: 'application/json', 'Content-Type': 'application/json' }
        : { Accept: 'application/json' },
      body: hasBody ? JSON.stringify(body) : undefined,
    })

    if (res.status === 204) {
      return undefined as T
    }
    if (!res.ok) {
      // Even an empty/non-JSON 401 expires the UI session.
      if (res.status === 401) unauthorizedListeners.forEach(listener => listener(path))
      throw await toApiError(res)
    }

    const text = await res.text()
    return (text ? JSON.parse(text) : undefined) as T
  }

  return {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
    put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
    patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
    delete: <T>(path: string) => request<T>('DELETE', path),
  }
}
