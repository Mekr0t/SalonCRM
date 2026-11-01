import { createBrowserRouter } from 'react-router'
import { HomePage, NotFoundPage } from './App'
import { AuthRoot, AppIndex, LoginPage, BookingPage } from './auth/AuthRoot'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { PublicLayout } from './components/PublicLayout'
import { AppLayout } from './components/AppLayout'

import { CalendarPage, AppointmentsPage, AppointmentDetailPage, StaffBookingPage, CustomersPage, CustomerDetailPage, OverviewPage, UsersPage, ServicesPage, MlDashboardPage } from './features/WorkspacePages'

export const router = createBrowserRouter([
  { element: <AuthRoot />, children: [
    { element: <PublicLayout />, children: [
      { path: '/', element: <HomePage /> },
      { path: '/rezervacia', element: <BookingPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '*', element: <NotFoundPage /> },
    ] },
    { element: <ProtectedRoute roles={['Admin', 'User']} />, children: [
      { path: '/app', element: <AppLayout />, children: [
        { index: true, element: <AppIndex /> },
        { path: 'kalendar', element: <CalendarPage /> },
        { path: 'terminy', element: <AppointmentsPage /> },
        { path: 'terminy/novy', element: <StaffBookingPage /> },
        { path: 'terminy/:id', element: <AppointmentDetailPage /> },
        { path: 'zakaznici', element: <CustomersPage /> },
        { path: 'zakaznici/:id', element: <CustomerDetailPage /> },
        { element: <ProtectedRoute roles={['Admin']} />, children: [
          { path: 'prehlad', element: <OverviewPage /> },
          { path: 'pouzivatelia', element: <UsersPage /> },
          { path: 'sluzby', element: <ServicesPage /> },
          { path: 'ml', element: <MlDashboardPage /> },
        ] },
        { path: '*', element: <NotFoundPage /> },
      ] },
    ] },
  ] },
])
