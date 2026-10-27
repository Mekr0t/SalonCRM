// Match the backend's calendar-day limits. UTC arithmetic avoids browser-zone/DST drift.
export function statisticsRangeError(from: string, to: string): string | null {
  const valid = (date: string) => /^\d{4}-\d{2}-\d{2}$/.test(date)
    && Number.isFinite(Date.parse(`${date}T00:00:00Z`))
    && new Date(`${date}T00:00:00Z`).toISOString().slice(0, 10) === date
  if (!valid(from) || !valid(to)) return 'Zadajte platné dátumy.'
  if (from > to) return 'Dátum od nesmie byť po dátume do.'
  if (from < '1900-01-01' || to > '2999-12-31') return 'Vyberte dátumy medzi rokmi 1900 a 2999.'
  if ((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000 > 731) return 'Rozdiel dátumov môže byť najviac 731 dní.'
  return null
}
