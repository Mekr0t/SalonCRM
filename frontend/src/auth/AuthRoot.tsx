import { lazy, Suspense } from 'react'
import { Navigate, Outlet } from 'react-router'
import { AuthProvider } from './AuthProvider'
import { homeForRole, useAuth } from './auth'

export const LoginPage = lazy(() => import('../features/auth/LoginPage').then(module => ({ default: module.LoginPage })))
export const BookingPage = lazy(() => import('../features/public/BookingPage').then(module => ({ default: module.BookingPage })))

export function AuthRoot() {
  return <AuthProvider><Suspense fallback={<p className="page-container py-16" role="status">Načítavam stránku…</p>}><Outlet /></Suspense></AuthProvider>
}
export function AppIndex() {
  const { user } = useAuth()
  return <Navigate to={homeForRole(user!.role)} replace />
}
