const LOCALE = 'sk-SK'
const TIME_ZONE = 'Europe/Bratislava'

export function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(LOCALE, {
    timeZone: TIME_ZONE,
    day: 'numeric',
    month: 'numeric',
    year: 'numeric',
  })
}

export function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString(LOCALE, {
    timeZone: TIME_ZONE,
    day: 'numeric',
    month: 'numeric',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(LOCALE, {
    timeZone: TIME_ZONE,
    hour: '2-digit',
    minute: '2-digit',
  })
}

export function formatCurrency(amount: number): string {
  return new Intl.NumberFormat(LOCALE, {
    style: 'currency',
    currency: 'EUR',
  }).format(amount)
}
