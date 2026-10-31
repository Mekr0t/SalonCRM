import assert from 'node:assert/strict'
import { test } from 'node:test'
import { appointmentActions, appointmentRange, salonLocalDateTime, salonLocalToUtc, validPaymentAmount } from '../src/features/appointments/time.ts'

test('salon wall times resolve with the date-specific offset independently of browser timezone', () => {
  assert.equal(salonLocalToUtc('2026-07-01T09:15'), '2026-07-01T07:15:00.000Z')
  assert.equal(salonLocalToUtc('2026-12-01T09:15'), '2026-12-01T08:15:00.000Z')
  assert.equal(salonLocalDateTime('2026-07-01T22:30:00Z'), '2026-07-02T00:30')
  assert.equal(salonLocalDateTime('2026-10-26T08:15:00Z'), '2026-10-26T09:15')
  assert.equal(salonLocalToUtc('2026-03-29T02:15'), null)
  assert.equal(salonLocalToUtc('2026-02-30T09:15'), null)
  assert.equal(salonLocalToUtc(''), null)
})

test('appointment ranges follow Bratislava midnight across the clock change and reject invalid ranges', () => {
  assert.deepEqual(appointmentRange('2026-10-24', '2026-10-27'), { from: '2026-10-23T22:00:00.000Z', to: '2026-10-26T23:00:00.000Z' })
  assert.equal(appointmentRange('2026-10-27', '2026-10-27'), null)
  assert.equal(appointmentRange('', '2026-10-27'), null)
  assert.equal(appointmentRange('2026-01-01', '2026-06-01'), null)
})

test('final states cannot transition, worker cancellation stops at start and admin can cancel afterwards', () => {
  const start = '2026-10-27T08:00:00Z'
  const now = Date.parse(start)
  assert.deepEqual(appointmentActions('Scheduled', start, false, now - 1), { complete: false, noShow: false, cancel: true, reschedule: true })
  assert.deepEqual(appointmentActions('Scheduled', start, false, now), { complete: true, noShow: true, cancel: false, reschedule: true })
  assert.equal(appointmentActions('Scheduled', start, true, now).cancel, true)
  for (const status of ['Completed', 'Cancelled', 'NoShow']) assert.deepEqual(appointmentActions(status, start, true, now), { complete: false, noShow: false, cancel: false, reschedule: false })
})

test('payment accepts free and discounted visits but bounds precision and amount', () => {
  for (const amount of ['0', '0.00', '35.5', '10000.00']) assert.equal(validPaymentAmount(amount), true)
  for (const amount of ['', '-1', '10000.01', '1.234', 'NaN', '1e2', 'Infinity']) assert.equal(validPaymentAmount(amount), false)
})
