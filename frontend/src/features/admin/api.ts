import { mainApi } from '@/api/mainApi'
import type { Service } from '@/features/public/api'
import type { Role } from '@/auth/auth'

export interface AdminService extends Service { isActive: boolean }
export interface AdminUser {
  id: string
  email: string
  fullName: string
  role: Role
  isActive: boolean
  createdAt: string
  lastLoginAt: string | null
  services: { id: string; name: string }[]
}
export interface AdminSession {
  id: string
  userId: string
  userFullName: string
  userEmail: string
  createdAt: string
  lastSeenAt: string
  ipAddress: string | null
  userAgent: string | null
  isCurrent: boolean
}
export interface StatsOverview {
  from: string
  to: string
  revenue: number
  completedVisits: number
  noShowRate: number | null
  newCustomers: number
  averageVisitValue: number | null
}
export interface MonthlyStats { month: string; revenue: number; completedVisits: number; newCustomers: number }
export interface ServiceStats { serviceId: string; name: string; category: Service['category']; completedVisits: number; revenue: number }
export interface EmployeeStats {
  employeeId: string
  fullName: string
  completedVisits: number
  revenue: number
  bookedMinutes: number
  openingMinutes: number
  utilization: number
}
export const adminUsersQuery = { queryKey: ['admin', 'users'], queryFn: () => mainApi.get<AdminUser[]>('/admin/users') }
export const adminServicesQuery = { queryKey: ['admin', 'services'], queryFn: () => mainApi.get<AdminService[]>('/admin/services') }
export const adminSessionsQuery = { queryKey: ['admin', 'sessions'], queryFn: () => mainApi.get<AdminSession[]>('/admin/sessions') }
