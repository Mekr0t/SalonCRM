import assert from 'node:assert/strict'
import { test } from 'node:test'
import { statisticsRangeError } from '../src/features/admin/statistics.ts'

test('statistics dates reject missing, reversed, impossible and unsupported calendar dates', () => {
  assert.ok(statisticsRangeError('', '2026-10-27'))
  assert.ok(statisticsRangeError('2026-10-28', '2026-10-27'))
  assert.ok(statisticsRangeError('2026-02-30', '2026-03-01'))
  assert.ok(statisticsRangeError('1899-12-31', '1900-01-01'))
  assert.ok(statisticsRangeError('2999-12-31', '3000-01-01'))
  assert.equal(statisticsRangeError('2024-02-29', '2024-03-01'), null)
})

test('statistics limits match backend day differences across leap years and daylight saving', () => {
  assert.equal(statisticsRangeError('2024-03-30', '2026-03-31'), null)
  assert.ok(statisticsRangeError('2024-03-30', '2026-04-01'))
  assert.equal(statisticsRangeError('2026-10-25', '2026-10-25'), null)
})
