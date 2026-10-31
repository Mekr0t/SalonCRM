import { z } from 'zod'

export const customerSchema = z.object({
  fullName: z.string().trim().min(1, 'Zadajte meno a priezvisko.').max(200, 'Meno môže mať najviac 200 znakov.'),
  phone: z.string().trim().max(50, 'Telefón môže mať najviac 50 znakov.'),
  notes: z.string().trim().max(4000, 'Poznámky môžu mať najviac 4 000 znakov.'),
})

export type CustomerValues = z.infer<typeof customerSchema>

export function customerPage(value: string | null): number {
  if (!value || !/^\d+$/.test(value)) return 1
  const page = Number(value)
  return Number.isSafeInteger(page) && page >= 1 && page <= 1_000_000 ? page : 1
}

export function customersPath(search: string, page: number, pageSize = 20): string {
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim()) query.set('search', search.trim())
  return `/customers?${query}`
}

export const customerStatusNames = {
  Scheduled: 'Naplánovaný', Completed: 'Dokončený', Cancelled: 'Zrušený', NoShow: 'Neprišiel',
} as const

export const paymentMethodNames = { Cash: 'Hotovosť', Card: 'Karta' } as const
