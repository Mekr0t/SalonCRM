import assert from 'node:assert/strict'
import { test } from 'node:test'
import { addCalendarDays, bookingPayload, contactSchema, salonDate } from '../src/features/public/booking.ts'
import { formatDateTime, formatCurrency } from '../src/lib/format.ts'

const contact = { fullName: ' Jana Nová ', email: ' jana@example.com ', phone: '', note: '', gdprConsent: true }

test('contact validation requires GDPR, a nonblank name and a valid bounded email', () => {
  assert.equal(contactSchema.safeParse({ ...contact, gdprConsent: false }).success, false)
  assert.equal(contactSchema.safeParse({ ...contact, fullName: '   ' }).success, false)
  assert.equal(contactSchema.safeParse({ ...contact, email: 'invalid' }).success, false)
  assert.equal(contactSchema.safeParse({ ...contact, phone: '1'.repeat(51) }).success, false)
  assert.equal(contactSchema.safeParse({ ...contact, note: 'a'.repeat(1001) }).success, false)
  assert.equal(contactSchema.safeParse(contact).success, true)
})

test('anyone stays unassigned, optional empty fields become null, server UTC slot is preserved', () => {
  const values = contactSchema.parse(contact)
  const slot = '2026-10-26T08:15:00Z'
  const payload = bookingPayload('service', '', slot, values)
  assert.equal(payload.employeeId, null)
  assert.equal(payload.startsAt, slot)
  assert.equal(payload.fullName, 'Jana Nová')
  assert.equal(payload.email, 'jana@example.com')
  assert.equal(payload.phone, null)
  assert.equal(payload.note, null)
  assert.equal(bookingPayload('service', 'employee', slot, values).employeeId, 'employee')
})

test('salon calendar date and 60 day window handle midnight and daylight saving changes', () => {
  assert.equal(salonDate(new Date('2026-07-01T22:30:00Z')), '2026-07-02')
  assert.equal(salonDate(new Date('2026-12-01T23:30:00Z')), '2026-12-02')
  assert.equal(addCalendarDays('2026-10-24', 60), '2026-12-23')
  assert.equal(addCalendarDays('2026-03-28', 1), '2026-03-29')
  assert.match(formatDateTime('2026-07-01T22:30:00Z'), /2\.\s*7\.\s*2026.*0:30/)
  assert.match(formatCurrency(45), /45,00\s*€/)
})
