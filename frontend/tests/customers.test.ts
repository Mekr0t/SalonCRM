import assert from 'node:assert/strict'
import { test } from 'node:test'
import { customerPage, customerSchema, customersPath } from '../src/features/customers/customer.ts'

test('customer edits reject empty names and oversized fields while allowing phone and notes to be cleared', () => {
  assert.deepEqual(customerSchema.parse({ fullName: ' Jana Nová ', phone: ' ', notes: ' ' }), {
    fullName: 'Jana Nová', phone: '', notes: '',
  })
  assert.equal(customerSchema.safeParse({ fullName: ' ', phone: '', notes: '' }).success, false)
  assert.equal(customerSchema.safeParse({ fullName: 'a'.repeat(201), phone: '', notes: '' }).success, false)
  assert.equal(customerSchema.safeParse({ fullName: 'Jana', phone: '1'.repeat(51), notes: '' }).success, false)
  assert.equal(customerSchema.safeParse({ fullName: 'Jana', phone: '', notes: 'a'.repeat(4001) }).success, false)
})

test('customer pagination rejects malformed or unsupported URLs', () => {
  for (const value of [null, '', '0', '-1', '1.5', 'Infinity', '2x', '1000001', '99999999999999999']) {
    assert.equal(customerPage(value), 1)
  }
  assert.equal(customerPage('2'), 2)
  assert.equal(customerPage('1000000'), 1_000_000)
})

test('customer search stays one encoded query value and supports Slovak names and literal search symbols', () => {
  const path = customersPath('  Ľubica +421 & 20%_  ', 3)
  const url = new URL(path, 'https://salon.test')
  assert.equal(url.pathname, '/customers')
  assert.equal(url.searchParams.get('search'), 'Ľubica +421 & 20%_')
  assert.equal(url.searchParams.get('page'), '3')
  assert.equal(url.searchParams.get('pageSize'), '20')
  assert.equal(new URL(customersPath('', 1), 'https://salon.test').searchParams.has('search'), false)
})
