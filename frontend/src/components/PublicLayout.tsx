import { Link, NavLink, Outlet } from 'react-router'
import { Flower2 } from 'lucide-react'
import { homeForRole, useAuth } from '@/auth/auth'

export function PublicLayout() {
  const { user } = useAuth()
  return <div className="flex min-h-screen flex-col">
    <header className="border-b bg-card">
      <div className="page-container flex min-h-20 flex-wrap items-center justify-between gap-4 py-4">
        <Link to="/" className="brand"><Flower2 aria-hidden="true" /> SalonCRM</Link>
        <nav aria-label="Hlavná navigácia" className="flex flex-wrap items-center gap-5 text-sm font-medium">
          <NavLink to="/" end>Služby a cenník</NavLink>
          <NavLink to="/rezervacia">Rezervovať</NavLink>
          <Link to={user ? homeForRole(user.role) : '/login'}>{user ? 'Môj salón' : 'Prihlásenie personálu'}</Link>
        </nav>
      </div>
    </header>
    <div className="flex-1"><Outlet /></div>
    <footer className="mt-16 border-t py-7 text-sm text-muted-foreground">
      <div className="page-container flex flex-wrap justify-between gap-3">
        <span>SalonCRM · Starostlivosť začína rezerváciou.</span>
        <span>Časy termínov: Europe/Bratislava · Ceny v EUR</span>
      </div>
    </footer>
  </div>
}
