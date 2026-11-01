import { z } from 'zod'

export type RiskLevel = 'low' | 'medium' | 'high' | 'insufficient_data'
export type RetentionChannel = 'Phone' | 'Email' | 'Sms' | 'InPerson'

export const riskLabels: Record<RiskLevel, string> = {
  low: 'Nízke riziko', medium: 'Stredné riziko', high: 'Vysoké riziko', insufficient_data: 'Málo údajov',
}
export const channelLabels: Record<RetentionChannel, string> = {
  Phone: 'Telefón', Email: 'E-mail', Sms: 'SMS', InPerson: 'Osobne',
}
export const retentionChannels = Object.keys(channelLabels) as RetentionChannel[]

export const riskLabel = (level: string) => riskLabels[level as RiskLevel] ?? riskLabels.insufficient_data
export const channelLabel = (channel: string) => channelLabels[channel as RetentionChannel] ?? channel

/** Probability 0-1 as sk-SK percent with at most one decimal; null -> em dash. */
export function formatProbability(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return new Intl.NumberFormat('sk-SK', { style: 'percent', maximumFractionDigits: 1 }).format(value)
}

export const retentionSchema = z.object({
  channel: z.enum(['Phone', 'Email', 'Sms', 'InPerson']),
  note: z.string().trim().min(1, 'Zadajte poznámku ku kontaktu.').max(2000, 'Poznámka môže mať najviac 2000 znakov.'),
})
export type RetentionValues = z.infer<typeof retentionSchema>

export const UNKNOWN_CUSTOMER = 'Neznámy zákazník'

/** Joins prediction rows with names from the main API lookup; missing customers get a placeholder. */
export function withCustomerNames<P extends { customerId: string }>(
  items: P[],
  customers: { id: string; fullName: string }[],
): (P & { fullName: string; known: boolean })[] {
  const names = new Map(customers.map(customer => [customer.id, customer.fullName]))
  return items.map(item => {
    const name = names.get(item.customerId)
    return { ...item, fullName: name ?? UNKNOWN_CUSTOMER, known: name !== undefined }
  })
}

/** Unique ids, capped at the ML lookup limit (500). */
export const uniqueIds = (ids: string[]) => [...new Set(ids)].slice(0, 500)
