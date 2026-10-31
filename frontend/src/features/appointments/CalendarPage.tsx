import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import FullCalendar from '@fullcalendar/react'
import timeGridPlugin from '@fullcalendar/timegrid'
import dayGridPlugin from '@fullcalendar/daygrid'
import skLocale from '@fullcalendar/core/locales/sk'
import { useAuth } from '@/auth/auth'
import { ErrorNotice } from '@/components/Feedback'
import { salonDate } from '@/features/public/booking'
import { appointmentsQuery, statusNames } from './api'
import { appointmentRange, salonLocalDateTime } from './time'
import { adminUsersQuery } from '@/features/admin/api'
import { riskLookupQuery } from '@/features/ml/api'
import { formatProbability, riskLabel } from '@/features/ml/risk'

const colors = { Scheduled: '#346451', Completed: '#437fbd', Cancelled: '#768075', NoShow: '#b16a20' }

export function CalendarPage() {
  const { user } = useAuth()
  const admin = user?.role === 'Admin'
  const navigate = useNavigate()
  const [employeeId, setEmployeeId] = useState('')
  const [dates, setDates] = useState<{ from: string; to: string } | null>(null)
  const range = useMemo(() => dates ? appointmentRange(dates.from, dates.to) : null, [dates])
  const appointments = useQuery(appointmentsQuery(range, '', admin ? employeeId : ''))
  const employees = useQuery({ ...adminUsersQuery, enabled: admin })
  const risks = useQuery(riskLookupQuery(appointments.data?.map(item => item.customer.id) ?? []))
  return <section className="space-y-6">
    <div className="flex flex-wrap items-center justify-between gap-4"><div><p className="eyebrow">Pracovný priestor</p><h1 className="mt-2 text-3xl font-semibold">Kalendár</h1><p className="mt-2 text-muted-foreground">Časy sú v časovom pásme Europe/Bratislava. Kliknutím otvoríte detail termínu.</p></div><Link className="action-link" to="/app/terminy/novy">Nová telefonická rezervácia</Link></div>
    {admin && <div className="max-w-sm"><label htmlFor="calendar-employee">Pracovník</label><select id="calendar-employee" value={employeeId} onChange={event => setEmployeeId(event.target.value)} disabled={employees.isPending || employees.isError}><option value="">Všetci pracovníci</option>{employees.data?.map(employee => <option value={employee.id} key={employee.id}>{employee.fullName}{employee.isActive ? '' : ' (neaktívny)'}</option>)}</select></div>}
    {admin && employees.isError && <ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} />}
    {range && appointments.isPending && <p role="status">Načítavam termíny…</p>}
    {appointments.isError && <ErrorNotice error={appointments.error} retry={() => { void appointments.refetch() }} />}
    {appointments.isSuccess && appointments.data.length === 0 && <p className="notice">V zobrazenom období nie sú žiadne termíny.</p>}
    <div className="panel calendar-panel min-w-0 overflow-x-auto">
      {/* UTC is a display coordinate. Every event is first converted to Bratislava wall time;
          this avoids browser-zone rendering and does not require a non-stack timezone plugin. */}
      <FullCalendar plugins={[timeGridPlugin, dayGridPlugin]} locale={skLocale} timeZone="UTC"
        initialDate={salonDate()} initialView="timeGridWeek" firstDay={1} allDaySlot={false} height="auto" nowIndicator
        now={`${salonLocalDateTime(new Date())}:00Z`} slotMinTime="07:00:00" slotMaxTime="20:00:00" slotDuration="00:15:00"
        headerToolbar={{ left: 'prev,next today', center: 'title', right: 'timeGridWeek,timeGridDay,dayGridMonth' }}
        businessHours={[{ daysOfWeek: [1, 2, 3, 4, 5], startTime: '08:00', endTime: '18:00' }, { daysOfWeek: [6], startTime: '08:00', endTime: '13:00' }]}
        datesSet={info => {
          const next = { from: info.startStr.slice(0, 10), to: info.endStr.slice(0, 10) }
          setDates(current => current?.from === next.from && current?.to === next.to ? current : next)
        }}
        events={(appointments.data ?? []).map(item => {
          const risk = risks.data?.get(item.customer.id)
          const flagged = risk?.riskLevel === 'high' || risk?.riskLevel === 'medium'
          const riskText = risk && risk.riskLevel !== 'insufficient_data' ? ` · ${riskLabel(risk.riskLevel)} (${formatProbability(risk.churnProbability)})` : ''
          return {
          id: item.id,
          title: `${item.customer.fullName} · ${item.service.name}${admin ? ` · ${item.employee.fullName}` : ''} (${statusNames[item.status]})`,
          extendedProps: { risk: flagged ? risk.riskLevel : null, riskText },
          start: `${salonLocalDateTime(item.startsAt)}:00Z`,
          end: `${salonLocalDateTime(item.endsAt)}:00Z`,
          backgroundColor: colors[item.status], borderColor: colors[item.status],
          url: `/app/terminy/${item.id}`,
        }})}
        eventContent={info => {
          const risk = info.event.extendedProps.risk as string | null
          return <div className="fc-event-main-frame" title={`${info.event.title}${info.event.extendedProps.riskText}`}>{info.timeText && <div className="fc-event-time">{info.timeText}</div>}<div className="fc-event-title-container"><div className="fc-event-title fc-sticky">{risk && <span className={`mr-1 inline-block rounded px-1 text-[10px] font-bold ${risk === 'high' ? 'bg-white text-red-700' : 'bg-amber-200 text-amber-900'}`}>{risk === 'high' ? '! Vysoké riziko' : '! Stredné riziko'}</span>}{info.event.title}</div></div></div>
        }}
        eventClick={info => { info.jsEvent.preventDefault(); void navigate(`/app/terminy/${info.event.id}`) }}
        eventDidMount={info => { info.el.title = info.event.title }}
      />
    </div>
    {risks.isError && <p className="text-sm text-muted-foreground" role="status">Riziko odchodu zákazníkov sa momentálne nepodarilo načítať.</p>}
    <div className="flex flex-wrap gap-4 text-sm">{Object.entries(statusNames).map(([status, label]) => <span key={status} className="inline-flex items-center gap-2"><span aria-hidden="true" className="size-3 rounded-full" style={{ background: colors[status as keyof typeof colors] }} />{label}</span>)}</div>
  </section>
}
