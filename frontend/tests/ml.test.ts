import assert from 'node:assert/strict'
import { test } from 'node:test'
import { channelLabel, formatProbability, retentionSchema, riskLabel, uniqueIds, withCustomerNames } from '../src/features/ml/risk.ts'

test('risk levels have Slovak labels including insufficient data', () => {
  assert.equal(riskLabel('low'), 'Nízke riziko')
  assert.equal(riskLabel('medium'), 'Stredné riziko')
  assert.equal(riskLabel('high'), 'Vysoké riziko')
  assert.equal(riskLabel('insufficient_data'), 'Málo údajov')
  assert.equal(riskLabel('something-else'), 'Málo údajov')
})

test('probabilities format as sk-SK percent with at most one decimal', () => {
  assert.equal(formatProbability(null), '—')
  assert.equal(formatProbability(undefined), '—')
  assert.equal(formatProbability(0.5).replace(/\s/g, ''), '50%')
  assert.equal(formatProbability(0.1234).replace(/\s/g, ''), '12,3%')
  assert.equal(formatProbability(1).replace(/\s/g, ''), '100%')
})

test('retention channels have Slovak labels', () => {
  assert.deepEqual(['Phone', 'Email', 'Sms', 'InPerson'].map(channelLabel), ['Telefón', 'E-mail', 'SMS', 'Osobne'])
})

test('lookup names merge into prediction rows and unknown customers get a placeholder', () => {
  const rows = withCustomerNames(
    [{ customerId: 'a', churnProbability: 0.9 }, { customerId: 'b', churnProbability: 0.8 }],
    [{ id: 'a', fullName: 'Jana Nová' }],
  )
  assert.deepEqual(rows.map(r => [r.customerId, r.fullName, r.known]), [['a', 'Jana Nová', true], ['b', 'Neznámy zákazník', false]])
  assert.equal(rows[0].churnProbability, 0.9)
})

test('retention note is trimmed and bounded to 1-2000 characters', () => {
  assert.equal(retentionSchema.parse({ channel: 'Phone', note: '  Volala som  ' }).note, 'Volala som')
  assert.equal(retentionSchema.safeParse({ channel: 'Phone', note: '   ' }).success, false)
  assert.equal(retentionSchema.safeParse({ channel: 'Sms', note: 'a'.repeat(2000) }).success, true)
  assert.equal(retentionSchema.safeParse({ channel: 'Sms', note: 'a'.repeat(2001) }).success, false)
  assert.equal(retentionSchema.safeParse({ channel: 'Fax', note: 'x' }).success, false)
})

test('lookup ids are deduplicated and capped at 500', () => {
  assert.deepEqual(uniqueIds(['a', 'b', 'a']), ['a', 'b'])
  assert.equal(uniqueIds(Array.from({ length: 600 }, (_, i) => String(i))).length, 500)
})
