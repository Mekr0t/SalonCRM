import { z } from 'zod'

export const contactSchema = z.object({
  fullName: z.string().trim().min(1, 'Zadajte meno a priezvisko.').max(200, 'Meno môže mať najviac 200 znakov.'),
  email: z.string().trim().pipe(z.email('Zadajte platný e-mail.').max(254, 'E-mail môže mať najviac 254 znakov.')),
  phone: z.string().trim().max(50, 'Telefón môže mať najviac 50 znakov.'),
  note: z.string().trim().max(1000, 'Poznámka môže mať najviac 1 000 znakov.'),
  gdprConsent: z.boolean().refine(value => value, 'Na rezerváciu je potrebný súhlas so spracovaním osobných údajov.'),
})
export type ContactValues = z.infer<typeof contactSchema>

// Use the salon's calendar date, regardless of the browser's own time zone.
export function salonDate(now = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Europe/Bratislava', year: 'numeric', month: '2-digit', day: '2-digit',
  }).formatToParts(now)
  const part = (type: string) => parts.find(value => value.type === type)!.value
  return `${part('year')}-${part('month')}-${part('day')}`
}
export function addCalendarDays(date: string, days: number): string {
  const value = new Date(`${date}T12:00:00Z`)
  value.setUTCDate(value.getUTCDate() + days)
  return value.toISOString().slice(0, 10)
}
export function bookingPayload(serviceId: string, employeeId: string, startsAt: string, values: ContactValues) {
  return {
    serviceId, employeeId: employeeId || null, startsAt,
    ...values, phone: values.phone || null, note: values.note || null,
  }
}
