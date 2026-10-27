import { mainApi } from '@/api/mainApi'
import type { Service, Employee } from '@/features/public/api'

export type AppointmentStatus = 'Scheduled' | 'Completed' | 'Cancelled' | 'NoShow'
export type PaymentMethod = 'Cash' | 'Card'
export interface Appointment {
  id: string
  startsAt: string
  endsAt: string
  status: AppointmentStatus
  source: 'Online' | 'Staff'
  price: number
  service: Pick<Service, 'id' | 'name' | 'category' | 'durationMinutes'>
  employee: Employee
  customer: Employee
}
export interface AppointmentDetail extends Appointment {
  customer: Employee & { email: string; phone: string | null }
  customerNote: string | null
  staffNote: string | null
  createdAt: string
  updatedAt: string
  completedAt: string | null
  cancelledAt: string | null
  payment: { amount: number; method: PaymentMethod; paidAt: string; recordedBy: Employee } | null
}
export interface AppointmentEmployee extends Employee {
  isActive: boolean
  services: { id: string; name: string }[]
}
export const statusNames: Record<AppointmentStatus, string> = {
  Scheduled: 'Naplánovaný', Completed: 'Dokončený', Cancelled: 'Zrušený', NoShow: 'Neprišiel',
}
export const paymentNames: Record<PaymentMethod, string> = { Cash: 'Hotovosť', Card: 'Karta' }
export const employeesQuery = {
  queryKey: ['admin', 'users'],
  queryFn: () => mainApi.get<AppointmentEmployee[]>('/admin/users'),
}
export function appointmentsQuery(range: { from: string; to: string } | null, status = '', employeeId = '') {
  const query = new URLSearchParams(range ?? {})
  if (status) query.set('status', status)
  if (employeeId) query.set('employeeId', employeeId)
  return {
    queryKey: ['appointments', 'list', range, status, employeeId],
    queryFn: () => mainApi.get<Appointment[]>(`/appointments?${query}`),
    enabled: !!range,
    staleTime: 0,
  }
}
export function appointmentQuery(id: string) {
  return {
    queryKey: ['appointments', 'detail', id],
    queryFn: () => mainApi.get<AppointmentDetail>(`/appointments/${encodeURIComponent(id)}`),
    staleTime: 0,
  }
}
