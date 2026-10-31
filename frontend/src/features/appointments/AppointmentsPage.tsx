import { useState } from 'react'
import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/auth/auth'
import { ErrorNotice } from '@/components/Feedback'
import { addCalendarDays, salonDate } from '@/features/public/booking'
import { appointmentsQuery, employeesQuery, statusNames } from './api'
import { appointmentRange } from './time'
import { AppointmentList } from './AppointmentList'

export function AppointmentsPage() {
  const { user } = useAuth()
  const admin = user?.role === 'Admin'
  const [from, setFrom] = useState(() => salonDate())
  const [to, setTo] = useState(() => addCalendarDays(salonDate(), 30))
  const [status, setStatus] = useState('')
  const [employeeId, setEmployeeId] = useState('')
  const range = appointmentRange(from, to ? addCalendarDays(to, 1) : '')
  const appointments = useQuery(appointmentsQuery(range, status, admin ? employeeId : ''))
  const employees = useQuery({ ...employeesQuery, enabled: admin })
  return <section className="space-y-6">
    <div className="flex flex-wrap items-center justify-between gap-4"><div><p className="eyebrow">Pracovný priestor</p><h1 className="mt-2 text-3xl font-semibold">Termíny</h1><p className="mt-2 text-muted-foreground">{admin ? 'Všetky rezervácie salónu.' : 'Vaše rezervácie a návštevy.'}</p></div><Link className="action-link" to="/app/terminy/novy">Nová telefonická rezervácia</Link></div>
    <div className="panel grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <div><label htmlFor="appointments-from">Od</label><input id="appointments-from" type="date" value={from} onChange={event => setFrom(event.target.value)} /></div>
      <div><label htmlFor="appointments-to">Do (vrátane)</label><input id="appointments-to" type="date" value={to} onChange={event => setTo(event.target.value)} /></div>
      <div><label htmlFor="appointments-status">Stav</label><select id="appointments-status" value={status} onChange={event => setStatus(event.target.value)}><option value="">Všetky stavy</option>{Object.entries(statusNames).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></div>
      {admin && <div><label htmlFor="appointments-employee">Pracovník</label><select id="appointments-employee" value={employeeId} onChange={event => setEmployeeId(event.target.value)} disabled={employees.isPending || employees.isError}><option value="">Všetci pracovníci</option>{employees.data?.map(employee => <option value={employee.id} key={employee.id}>{employee.fullName}{employee.isActive ? '' : ' (neaktívny)'}</option>)}</select></div>}
    </div>
    {admin && employees.isError && <ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} />}
    {!range && <p className="notice notice-error" role="alert">Vyberte platné obdobie kratšie než tri mesiace.</p>}
    {range && appointments.isPending && <p role="status">Načítavam termíny…</p>}
    {range && appointments.isError && <ErrorNotice error={appointments.error} retry={() => { void appointments.refetch() }} />}
    {range && appointments.isSuccess && <AppointmentList appointments={appointments.data} showEmployee={admin} />}
  </section>
}
