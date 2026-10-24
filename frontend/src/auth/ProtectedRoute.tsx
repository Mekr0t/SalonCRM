import { Navigate, Outlet } from 'react-router'
import { Button } from '@/components/ui/button'
import { homeForRole, useAuth, type Role } from './auth'

export function ProtectedRoute({ roles }: { roles: Role[] }) {
  const { user, isLoading, error, retry } = useAuth()
  if (isLoading) return <p role="status" className="page-container py-16">Overujem prihlásenie…</p>
  if (error) return <main className="page-container py-16 space-y-4">
    <p role="alert">Prihlásenie sa nepodarilo overiť. Skontrolujte pripojenie.</p>
    <Button onClick={retry}>Skúsiť znova</Button>
  </main>
  if (!user) return <Navigate to="/login" replace />
  if (!roles.includes(user.role)) return <Navigate to={homeForRole(user.role)} replace />
  return <Outlet />
}
