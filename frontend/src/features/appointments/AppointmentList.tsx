import { Link } from 'react-router'
import { formatCurrency, formatDateTime } from '@/lib/format'
import { statusNames, type Appointment } from './api'

export function AppointmentStatusBadge({ status }: { status: Appointment['status'] }) {
  const colors = {
    Scheduled: 'bg-primary/10 text-primary', Completed: 'bg-emerald-100 text-emerald-900',
    Cancelled: 'bg-muted text-muted-foreground', NoShow: 'bg-amber-100 text-amber-900',
  }
  return <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-medium ${colors[status]}`}>{statusNames[status]}</span>
}

export function AppointmentList({ appointments, showEmployee = true }: { appointments: Appointment[]; showEmployee?: boolean }) {
  if (!appointments.length) return <p className="notice">Vo vybranom období a filtroch nie sú žiadne termíny.</p>
  return <div className="overflow-x-auto rounded-xl border bg-card">
    <table className="min-w-[650px]">
      <caption className="sr-only">Zoznam termínov</caption>
      <thead><tr>
        <th className="font-medium">Dátum a čas</th><th className="font-medium">Zákazník a služba</th>
        {showEmployee && <th className="font-medium">Pracovník</th>}
        <th className="font-medium">Stav</th><th className="font-medium">Cena</th>
      </tr></thead>
      <tbody>{appointments.map(item => <tr key={item.id}>
        <td><Link className="font-medium text-primary underline underline-offset-4" to={`/app/terminy/${item.id}`}>{formatDateTime(item.startsAt)}</Link></td>
        <td><Link to={`/app/zakaznici/${item.customer.id}`} className="font-medium hover:underline">{item.customer.fullName}</Link><p className="mt-1 text-muted-foreground">{item.service.name}</p></td>
        {showEmployee && <td>{item.employee.fullName}</td>}
        <td><AppointmentStatusBadge status={item.status} /></td><td className="whitespace-nowrap">{formatCurrency(item.price)}</td>
      </tr>)}</tbody>
    </table>
  </div>
}
