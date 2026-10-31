import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Bar, CartesianGrid, ComposedChart, Legend, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { mainApi } from '@/api/mainApi'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { formatCurrency } from '@/lib/format'
import { salonDate } from '@/features/public/booking'
import type { EmployeeStats, MonthlyStats, ServiceStats, StatsOverview } from './api'
import { statisticsRangeError } from './statistics'

const percent = (value: number | null) => value === null ? '—' : new Intl.NumberFormat('sk-SK', { style: 'percent', maximumFractionDigits: 1 }).format(value)

export function OverviewPage() {
  const today = salonDate()
  const [range, setRange] = useState({ from: `${today.slice(0, 7)}-01`, to: today })
  const [from, setFrom] = useState(range.from)
  const [to, setTo] = useState(range.to)
  const [months, setMonths] = useState(12)
  const [validation, setValidation] = useState('')
  const query = new URLSearchParams(range).toString()
  const overview = useQuery({ queryKey: ['admin', 'stats', 'overview', range], queryFn: () => mainApi.get<StatsOverview>(`/admin/stats/overview?${query}`) })
  const services = useQuery({ queryKey: ['admin', 'stats', 'services', range], queryFn: () => mainApi.get<ServiceStats[]>(`/admin/stats/services?${query}`) })
  const employees = useQuery({ queryKey: ['admin', 'stats', 'employees', range], queryFn: () => mainApi.get<EmployeeStats[]>(`/admin/stats/employees?${query}`) })
  const monthly = useQuery({ queryKey: ['admin', 'stats', 'monthly', months], queryFn: () => mainApi.get<MonthlyStats[]>(`/admin/stats/monthly?months=${months}`) })
  function filter(event: FormEvent) {
    event.preventDefault()
    const error = statisticsRangeError(from, to)
    if (error) { setValidation(error); return }
    setValidation('')
    setRange({ from, to })
  }
  const cards = overview.data && [
    ['Tržby', formatCurrency(overview.data.revenue)],
    ['Dokončené návštevy', overview.data.completedVisits.toLocaleString('sk-SK')],
    ['Noví zákazníci', overview.data.newCustomers.toLocaleString('sk-SK')],
    ['Priemerná návšteva', overview.data.averageVisitValue === null ? '—' : formatCurrency(overview.data.averageVisitValue)],
    ['Miera neúčasti', percent(overview.data.noShowRate)],
  ]
  return <section className="space-y-7">
    <header><p className="eyebrow">Administrácia</p><h1 className="mt-2 text-3xl font-semibold">Prehľad salónu</h1><p className="mt-2 text-muted-foreground">Výsledky návštev a vyťaženosť tímu.</p></header>
    <form onSubmit={filter} className="panel flex flex-wrap items-end gap-4">
      <div><label htmlFor="stats-from">Od</label><input id="stats-from" type="date" required value={from} onChange={e => setFrom(e.target.value)} /></div>
      <div><label htmlFor="stats-to">Do (vrátane)</label><input id="stats-to" type="date" required value={to} min={from} onChange={e => setTo(e.target.value)} /></div>
      <Button type="submit">Zobraziť obdobie</Button>
      {validation && <p className="field-error basis-full" role="alert">{validation}</p>}
    </form>
    {overview.isPending && <p role="status">Načítavam prehľad…</p>}
    {overview.isError && <ErrorNotice error={overview.error} retry={() => { void overview.refetch() }} />}
    {cards && <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">{cards.map(([label, value]) => <article className="panel" key={label}><p className="text-sm text-muted-foreground">{label}</p><p className="mt-3 text-2xl font-semibold">{value}</p></article>)}</div>}
    {overview.data?.completedVisits === 0 && <p className="notice">Vo vybranom období zatiaľ nie sú dokončené návštevy.</p>}
    <section className="panel" aria-labelledby="monthly-title">
      <div className="mb-5 flex flex-wrap items-end justify-between gap-4"><div><h2 id="monthly-title" className="text-xl font-semibold">Tržby a návštevy po mesiacoch</h2><p className="mt-1 text-sm text-muted-foreground">Kalendárne mesiace vrátane aktuálneho neukončeného mesiaca. Výber obdobia vyššie tento graf nemení.</p></div><div><label htmlFor="stats-months">Počet mesiacov</label><select id="stats-months" className="rounded-lg border bg-background px-3 py-2.5" value={months} onChange={e => setMonths(Number(e.target.value))}>{[3, 6, 12, 24].map(n => <option key={n} value={n}>{n}</option>)}</select></div></div>
      {monthly.isPending && <p role="status">Načítavam graf…</p>}
      {monthly.isError && <ErrorNotice error={monthly.error} retry={() => { void monthly.refetch() }} />}
      {monthly.data && <><div className="h-80 w-full" aria-label="Graf mesačných tržieb a dokončených návštev"><ResponsiveContainer width="100%" height="100%"><ComposedChart data={monthly.data} margin={{ top: 10, right: 10, bottom: 10, left: 0 }} accessibilityLayer><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="month" /><YAxis yAxisId="revenue" width={60} /><YAxis yAxisId="visits" orientation="right" allowDecimals={false} width={40} /><Tooltip /><Legend /><Bar yAxisId="revenue" dataKey="revenue" name="Tržby (€)" fill="#22634f" radius={[4, 4, 0, 0]} /><Line yAxisId="visits" dataKey="completedVisits" name="Dokončené návštevy" stroke="#a15d22" strokeWidth={2} /></ComposedChart></ResponsiveContainer></div><details className="mt-3"><summary className="cursor-pointer text-sm font-medium text-primary">Zobraziť údaje grafu v tabuľke</summary><div className="mt-3 overflow-x-auto"><table><thead><tr><th>Mesiac</th><th>Tržby</th><th>Návštevy</th><th>Noví zákazníci</th></tr></thead><tbody>{monthly.data.map(row => <tr key={row.month}><th className="font-medium">{row.month}</th><td>{formatCurrency(row.revenue)}</td><td>{row.completedVisits}</td><td>{row.newCustomers}</td></tr>)}</tbody></table></div></details></>}
    </section>
    <div className="grid items-start gap-6 xl:grid-cols-2">
      <section className="panel" aria-labelledby="top-services"><h2 id="top-services" className="mb-5 text-xl font-semibold">Najúspešnejšie služby</h2>
        {services.isPending && <p role="status">Načítavam služby…</p>}{services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
        {services.data?.length === 0 && <p className="notice">Žiadne dokončené návštevy vo vybranom období.</p>}
        {services.data && services.data.length > 0 && <div className="overflow-x-auto"><table><thead><tr><th>Služba</th><th>Návštevy</th><th className="text-right">Tržby</th></tr></thead><tbody>{services.data.map(row => <tr key={row.serviceId}><th className="font-medium">{row.name}</th><td>{row.completedVisits}</td><td className="text-right">{formatCurrency(row.revenue)}</td></tr>)}</tbody></table></div>}
      </section>
      <section className="panel" aria-labelledby="employee-stats"><h2 id="employee-stats" className="mb-5 text-xl font-semibold">Vyťaženosť pracovníkov</h2>
        {employees.isPending && <p role="status">Načítavam pracovníkov…</p>}{employees.isError && <ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} />}
        {employees.data?.length === 0 && <p className="notice">V období nie sú dostupní pracovníci ani termíny.</p>}
        <div className="space-y-5">{employees.data?.map(row => <article key={row.employeeId}><div className="flex justify-between gap-3"><h3 className="font-medium">{row.fullName}</h3><strong>{percent(row.utilization)}</strong></div><progress className="mt-2 h-2 w-full accent-primary" max={Math.max(1, row.utilization)} value={row.utilization} aria-label={`Vyťaženosť: ${row.fullName}`} /><p className="mt-1 text-sm text-muted-foreground">{row.bookedMinutes} / {row.openingMinutes} min · {row.completedVisits} návštev · {formatCurrency(row.revenue)}</p></article>)}</div>
      </section>
    </div>
  </section>
}
