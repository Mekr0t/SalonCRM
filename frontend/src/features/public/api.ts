import { mainApi } from '@/api/mainApi'

export interface Service {
  id: string
  name: string
  category: 'Skin' | 'Nails' | 'BrowsLashes' | 'Waxing' | 'Massage' | 'Makeup'
  description: string | null
  durationMinutes: number
  price: number
}
export interface Employee { id: string; fullName: string }
export interface Slot { startsAt: string; employees: Employee[] }
export interface BookingConfirmation {
  appointmentId: string
  startsAt: string
  serviceName: string
  employeeName: string
  price: number
}
export const servicesQuery = {
  queryKey: ['public', 'services'],
  queryFn: () => mainApi.get<Service[]>('/public/services'),
  staleTime: 60_000,
}
export const categoryNames: Record<Service['category'], string> = {
  Skin: 'Starostlivosť o pleť', Nails: 'Nechty', BrowsLashes: 'Obočie a mihalnice',
  Waxing: 'Depilácia', Massage: 'Masáže', Makeup: 'Líčenie',
}
