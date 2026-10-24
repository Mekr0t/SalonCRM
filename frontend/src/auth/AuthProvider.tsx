import { useEffect, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { ApiError, onUnauthorized } from '@/api/http'
import { mainApi } from '@/api/mainApi'
import { AuthContext, authKey, type CurrentUser } from './auth'

export function AuthProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const session = useQuery({
    queryKey: authKey,
    queryFn: async () => {
      try { return await mainApi.get<CurrentUser>('/auth/me') }
      catch (error) {
        if (error instanceof ApiError && error.status === 401) return null
        throw error
      }
    },
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: 'always',
  })
  useEffect(() => onUnauthorized(path => {
    // Anonymous startup probes must leave the public booking pages accessible.
    // ProtectedRoute redirects anonymous /app/* visits after the probe finishes.
    if (path === '/auth/me' && !client.getQueryData<CurrentUser | null>(authKey)) return
    void client.cancelQueries()
    client.removeQueries({ predicate: query => query.queryKey[0] !== authKey[0] })
    client.setQueryData(authKey, null)
    void navigate('/login', { replace: true, state: { sessionExpired: path !== '/auth/login' } })
  }), [client, navigate])

  async function login(email: string, password: string) {
    const user = await mainApi.post<CurrentUser>('/auth/login', { email, password })
    await client.cancelQueries()
    client.removeQueries({ predicate: query => query.queryKey[0] !== authKey[0] })
    client.setQueryData(authKey, user)
    return user
  }
  async function logout() {
    try { await mainApi.post('/auth/logout') }
    catch (error) {
      if (!(error instanceof ApiError && error.status === 401)) throw error
    }
    await client.cancelQueries()
    client.removeQueries({ predicate: query => query.queryKey[0] !== authKey[0] })
    client.setQueryData(authKey, null)
    await navigate('/login', { replace: true })
  }
  return <AuthContext.Provider value={{
    user: session.data ?? null, isLoading: session.isPending, error: session.error,
    retry: () => { void session.refetch() }, login, logout,
  }}>{children}</AuthContext.Provider>
}
