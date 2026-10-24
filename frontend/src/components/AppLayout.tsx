import { useState } from 'react'
import { Link, NavLink, Outlet } from 'react-router'
import { CalendarDays, ChartNoAxesCombined, Flower2, List, LogOut, Sparkles, Users, UserRoundCog, BrainCircuit } from 'lucide-react'
import { useAuth } from '@/auth/auth'
import { ErrorNotice } from './Feedback'
import { Button } from './ui/button'

const commonLinks = [
  { to: '/app/kalendar', label: 'Kalendár', icon: CalendarDays },
  { to: '/app/terminy', label: 'Termíny', icon: List },
  { to: '/app/zakaznici', label: 'Zákazníci', icon: Users },
]
const adminLinks = [
  { to: '/app/prehlad', label: 'Prehľad', icon: ChartNoAxesCombined },
  { to: '/app/pouzivatelia', label: 'Používatelia', icon: UserRoundCog },
  { to: '/app/sluzby', label: 'Služby a cenník', icon: Sparkles },
  { to: '/app/ml', label: 'Riziko odchodu', icon: BrainCircuit },
]
export function AppLayout() {
  const { user, logout } = useAuth()
  const [error, setError] = useState<unknown>(null)
  const [pending, setPending] = useState(false)
  async function signOut() {
    setPending(true)
    setError(null)
    try { await logout() } catch (error) { setError(error) } finally { setPending(false) }
  }
  const links = user?.role === 'Admin' ? [...adminLinks.slice(0, 1), ...commonLinks, ...adminLinks.slice(1)] : commonLinks
  return <div className="min-h-screen md:grid md:grid-cols-[15rem_1fr]">
    <aside className="border-b bg-card p-5 md:sticky md:top-0 md:flex md:h-screen md:flex-col md:border-r">
      <Link to="/" className="brand mb-8"><Flower2 aria-hidden="true" /> SalonCRM</Link>
      <p className="eyebrow mb-3">Pracovný priestor</p>
      <nav aria-label="Navigácia aplikácie" className="flex flex-wrap gap-1 md:flex-col">
        {links.map(({ to, label, icon: Icon }) => <NavLink key={to} to={to} className={({ isActive }) => `sidebar-link ${isActive ? 'sidebar-active' : ''}`}>
          <Icon size={18} aria-hidden="true" />{label}
        </NavLink>)}
      </nav>
      <div className="mt-6 border-t pt-5 md:mt-auto">
        <p className="font-medium">{user?.fullName}</p>
        <p className="mb-4 text-sm text-muted-foreground">{user?.role === 'Admin' ? 'Administrátor' : 'Pracovník'}</p>
        <Button variant="outline" onClick={signOut} disabled={pending}><LogOut aria-hidden="true" />{pending ? 'Odhlasujem…' : 'Odhlásiť'}</Button>
        {error != null && <div className="mt-3"><ErrorNotice error={error} /></div>}
      </div>
    </aside>
    <main className="min-w-0 p-6 lg:p-12"><Outlet /></main>
  </div>
}
