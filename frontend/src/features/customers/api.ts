import { mainApi } from '@/api/mainApi'
import { customersPath } from './customer'

export interface CustomerListItem {
  id: string
  fullName: string
  email: string
  phone: string | null
  visitCount: number
  lastVisitAt: string | null
  nextAppointmentAt: string | null
}

export interface CustomerPage {
  items: CustomerListItem[]
  page: number
  pageSize: number
  totalCount: number
}

export interface CustomerAppointment {
  id: string
  startsAt: string
  endsAt: string
  status: 'Scheduled' | 'Completed' | 'Cancelled' | 'NoShow'
  price: number
  service: { id: string; name: string }
  employee: { id: string; fullName: string }
  payment: { amount: number; method: 'Cash' | 'Card'; paidAt: string } | null
}

export interface CustomerDetail {
  id: string
  fullName: string
  email: string
  phone: string | null
  notes: string | null
  gdprConsentAt: string
  createdAt: string
  visitCount: number
  totalSpent: number
  appointments: CustomerAppointment[]
}

export function customersQuery(search: string, page: number) {
  return {
    queryKey: ['customers', 'list', { search, page }],
    queryFn: () => mainApi.get<CustomerPage>(customersPath(search, page)),
  }
}

export function customerQuery(id: string) {
  return {
    queryKey: ['customers', 'detail', id],
    queryFn: () => mainApi.get<CustomerDetail>(`/customers/${encodeURIComponent(id)}`),
    enabled: Boolean(id),
  }
}
