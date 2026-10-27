const zone = 'Europe/Bratislava'

/** A wall-clock value for datetime-local and calendar coordinates, never browser local time. */
export function salonLocalDateTime(iso: string | Date): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(new Date(iso))
  const part = (type: string) => parts.find(value => value.type === type)!.value
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`
}

/** Resolve a salon wall time to an instant, including the offset on that particular date. */
export function salonLocalToUtc(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) return null
  const wall = Date.parse(`${value}:00Z`)
  if (!Number.isFinite(wall) || new Date(wall).toISOString().slice(0, 16) !== value) return null
  let instant = wall
  for (let i = 0; i < 3; i++) {
    const local = Date.parse(`${salonLocalDateTime(new Date(instant))}:00Z`)
    instant += wall - local
  }
  // A missing hour during the spring clock change is not a valid instant.
  return salonLocalDateTime(new Date(instant)) === value ? new Date(instant).toISOString() : null
}

export function appointmentRange(from: string, toExclusive: string) {
  const start = salonLocalToUtc(`${from}T00:00`)
  const end = salonLocalToUtc(`${toExclusive}T00:00`)
  if (!start || !end) return null
  const duration = Date.parse(end) - Date.parse(start)
  return duration > 0 && duration <= 92 * 86_400_000 ? { from: start, to: end } : null
}

export function appointmentActions(status: string, startsAt: string, isAdmin: boolean, now = Date.now()) {
  const scheduled = status === 'Scheduled'
  const started = Date.parse(startsAt) <= now
  return { complete: scheduled && started, noShow: scheduled && started, cancel: scheduled && (isAdmin || !started), reschedule: scheduled }
}

export function validPaymentAmount(value: string): boolean {
  return /^\d+(\.\d{1,2})?$/.test(value) && Number(value) <= 10_000
}

export function isPastAppointmentTime(iso: string, now = Date.now()): boolean {
  return Date.parse(iso) < now
}
