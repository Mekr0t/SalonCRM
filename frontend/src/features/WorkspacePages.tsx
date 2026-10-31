import { lazy } from 'react'

export const CalendarPage = lazy(() => import('./appointments/CalendarPage').then(module => ({ default: module.CalendarPage })))
export const AppointmentsPage = lazy(() => import('./appointments/AppointmentsPage').then(module => ({ default: module.AppointmentsPage })))
export const AppointmentDetailPage = lazy(() => import('./appointments/AppointmentDetailPage').then(module => ({ default: module.AppointmentDetailPage })))
export const StaffBookingPage = lazy(() => import('./appointments/StaffBookingPage').then(module => ({ default: module.StaffBookingPage })))
export const CustomersPage = lazy(() => import('./customers/CustomersPage').then(module => ({ default: module.CustomersPage })))
export const CustomerDetailPage = lazy(() => import('./customers/CustomerDetailPage').then(module => ({ default: module.CustomerDetailPage })))
export const OverviewPage = lazy(() => import('./admin/OverviewPage').then(module => ({ default: module.OverviewPage })))
export const UsersPage = lazy(() => import('./admin/UsersPage').then(module => ({ default: module.UsersPage })))
export const ServicesPage = lazy(() => import('./admin/ServicesPage').then(module => ({ default: module.ServicesPage })))
export const MlDashboardPage = lazy(() => import('./ml/MlDashboardPage').then(module => ({ default: module.MlDashboardPage })))
