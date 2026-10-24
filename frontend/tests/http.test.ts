import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { ApiError, createApiClient, onUnauthorized } from '../src/api/http.ts'

const originalFetch = globalThis.fetch
afterEach(() => { globalThis.fetch = originalFetch })

test('both API origins send the session cookie and serialize JSON', async () => {
  const calls: { url: string; init: RequestInit }[] = []
  globalThis.fetch = async (input, init) => {
    calls.push({ url: String(input), init: init! })
    return Response.json({ ok: true })
  }
  for (const base of ['/api', 'http://localhost:8000/ml']) {
    assert.deepEqual(await createApiClient(base).post('/example', { value: 1 }), { ok: true })
  }
  assert.deepEqual(calls.map(call => call.url), ['/api/example', 'http://localhost:8000/ml/example'])
  for (const { init } of calls) {
    assert.equal(init.credentials, 'include')
    assert.equal(init.body, '{"value":1}')
    assert.equal(new Headers(init.headers).get('Content-Type'), 'application/json')
  }
})

test('401 from main or ML notifies auth, even with a non-JSON body; unsubscribe works', async () => {
  const paths: string[] = []
  const unsubscribe = onUnauthorized(path => paths.push(path))
  try {
    globalThis.fetch = async () => new Response('expired', { status: 401 })
    for (const base of ['/api', 'http://localhost:8000/ml']) {
      await assert.rejects(createApiClient(base).get('/private'), error => error instanceof ApiError && error.status === 401)
    }
    assert.deepEqual(paths, ['/private', '/private'])
    unsubscribe()
    await assert.rejects(createApiClient('/api').get('/private'))
    assert.equal(paths.length, 2)
  } finally { unsubscribe() }
})

test('409 preserves Slovak ProblemDetails without logging the user out', async () => {
  let unauthorized = false
  const unsubscribe = onUnauthorized(() => { unauthorized = true })
  try {
    globalThis.fetch = async () => Response.json({ title: 'Conflict', detail: 'Vybraný termín už nie je voľný.' }, { status: 409 })
    await assert.rejects(createApiClient('/api').post('/public/bookings', {}), error =>
      error instanceof ApiError && error.status === 409 && error.detail === 'Vybraný termín už nie je voľný.')
    assert.equal(unauthorized, false)
  } finally { unsubscribe() }
})

test('logout accepts 204 with no response body', async () => {
  globalThis.fetch = async () => new Response(null, { status: 204 })
  assert.equal(await createApiClient('/api').post('/auth/logout'), undefined)
})
