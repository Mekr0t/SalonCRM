import { useEffect, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { mainApi } from '@/api/mainApi'
import { useAuth } from '@/auth/auth'
import { ErrorNotice } from '@/components/Feedback'
import { ConfirmButton } from '@/components/ConfirmButton'
import { useToast } from '@/components/toast-context'
import { Button } from '@/components/ui/button'
import { formatCurrency, formatDateTime } from '@/lib/format'
import { addCalendarDays, salonDate } from '@/features/public/booking'
import type { Employee } from '@/features/public/api'
import { appointmentQuery, paymentNames, type AppointmentDetail, type PaymentMethod } from './api'
import { AppointmentStatusBadge } from './AppointmentList'
import { appointmentActions, salonLocalDateTime, salonLocalToUtc, validPaymentAmount } from './time'

export function AppointmentDetailPage() {
  const { id = '' } = useParams()
  const detail = useQuery(appointmentQuery(id))
  return <section className="space-y-6">
    <Link to="/app/terminy" className="text-sm font-medium text-primary hover:underline">← Späť na termíny</Link>
    <h1 className="text-3xl font-semibold">Detail termínu</h1>
    {detail.isPending && <p role="status">Načítavam termín…</p>}
    {detail.isError && <ErrorNotice error={detail.error} retry={() => { void detail.refetch() }} />}
    {detail.data && <AppointmentEditor key={`${detail.data.id}:${detail.data.updatedAt}`} appointment={detail.data} />}
  </section>
}

function AppointmentEditor({ appointment: item }: { appointment: AppointmentDetail }) {
  const { user } = useAuth()
  const admin = user?.role === 'Admin'
  const toast = useToast()
  const client = useQueryClient()
  const [note, setNote] = useState(item.staffNote ?? '')
  const [start, setStart] = useState(salonLocalDateTime(item.startsAt))
  const [employeeId, setEmployeeId] = useState(item.employee.id)
  const [paymentMethod, setPaymentMethod] = useState<PaymentMethod>('Cash')
  const [amount, setAmount] = useState(String(item.price))
  const [timeError, setTimeError] = useState('')
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30_000)
    return () => window.clearInterval(timer)
  }, [])
  const actions = appointmentActions(item.status, item.startsAt, admin, now)
  const employees = useQuery({
    queryKey: ['public', 'employees', item.service.id],
    queryFn: () => mainApi.get<Employee[]>(`/public/services/${item.service.id}/employees`),
    enabled: admin && actions.reschedule,
    retry: false,
  })
  const mutation = useMutation({
    mutationFn: async ({ action, body }: { action: 'update' | 'complete' | 'cancel' | 'no-show'; body?: unknown }) => {
      const path = `/appointments/${item.id}`
      return action === 'update' ? mainApi.patch<AppointmentDetail>(path, body) : mainApi.post<AppointmentDetail>(`${path}/${action}`, body)
    },
    onSuccess: async (data, variables) => {
      toast(variables.action === 'update' ? 'Zmeny termínu boli uložené.' : variables.action === 'complete' ? 'Návšteva a platba boli zaznamenané.' : variables.action === 'cancel' ? 'Termín bol zrušený.' : 'Neúčasť bola zaznamenaná.')
      client.setQueryData(appointmentQuery(item.id).queryKey, data)
      await Promise.all([
        client.invalidateQueries({ queryKey: ['appointments'] }),
        client.invalidateQueries({ queryKey: ['customers'] }),
        client.invalidateQueries({ queryKey: ['admin'] }),
      ])
    },
    onError: () => { void client.invalidateQueries({ queryKey: ['appointments'] }) },
  })

  function saveNote(event: FormEvent) {
    event.preventDefault()
    mutation.mutate({ action: 'update', body: { staffNote: note.trim() } })
  }
  function reschedule(event: FormEvent) {
    event.preventDefault()
    const utc = salonLocalToUtc(start)
    if (!utc) { setTimeError('Zadajte platný dátum a čas v časovom pásme salónu.'); return }
    if (Date.parse(utc) < Date.now()) { setTimeError('Termín nie je možné preplánovať do minulosti.'); return }
    setTimeError('')
    mutation.mutate({ action: 'update', body: { startsAt: utc, ...(admin ? { employeeId } : {}) } })
  }

  return <>
    <div className="panel grid gap-7 lg:grid-cols-2">
      <div><div className="mb-4"><AppointmentStatusBadge status={item.status} /></div><h2 className="text-2xl font-semibold">{item.service.name}</h2><p className="mt-3 text-lg">{formatDateTime(item.startsAt)}</p><p className="mt-1 text-sm text-muted-foreground">Do {formatDateTime(item.endsAt)} · {item.service.durationMinutes} min · Europe/Bratislava</p><p className="mt-4">Pracovník: <strong>{item.employee.fullName}</strong></p><p className="mt-2">Cena termínu: <strong>{formatCurrency(item.price)}</strong></p><p className="mt-2 text-sm text-muted-foreground">Zdroj: {item.source === 'Online' ? 'Online rezervácia' : 'Telefonická rezervácia'}</p></div>
      <div><h2 className="text-lg font-semibold">Zákazník</h2><Link to={`/app/zakaznici/${item.customer.id}`} className="mt-3 inline-block text-lg font-medium text-primary underline underline-offset-4">{item.customer.fullName}</Link><p className="mt-2 break-all"><a href={`mailto:${item.customer.email}`}>{item.customer.email}</a></p>{item.customer.phone && <p className="mt-2"><a href={`tel:${item.customer.phone}`}>{item.customer.phone}</a></p>}{item.customerNote && <div className="mt-5"><h3 className="text-sm font-medium text-muted-foreground">Poznámka zákazníka</h3><p className="mt-2 whitespace-pre-wrap break-words">{item.customerNote}</p></div>}</div>
    </div>
    {mutation.isError && <ErrorNotice error={mutation.error} />}
    {item.payment && <div className="panel"><h2 className="text-xl font-semibold">Platba</h2><p className="mt-4 text-lg font-semibold">{formatCurrency(item.payment.amount)} · {paymentNames[item.payment.method]}</p><p className="mt-2 text-sm text-muted-foreground">{formatDateTime(item.payment.paidAt)} · zaznamenal {item.payment.recordedBy.fullName}</p></div>}
    {item.status === 'Scheduled' && <div className="panel space-y-5">
      <h2 className="text-xl font-semibold">Uzavrieť návštevu</h2>
      {actions.complete ? <>
        <div className="grid max-w-xl gap-4 sm:grid-cols-2"><div><label htmlFor="payment-method">Spôsob platby</label><select id="payment-method" value={paymentMethod} onChange={event => setPaymentMethod(event.target.value as PaymentMethod)}>{Object.entries(paymentNames).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></div><div><label htmlFor="payment-amount">Zaplatená suma (€)</label><input id="payment-amount" type="number" min="0" max="10000" step="0.01" value={amount} onChange={event => setAmount(event.target.value)} aria-invalid={!validPaymentAmount(amount)} />{!validPaymentAmount(amount) && <p className="field-error">Suma musí byť 0–10 000 € s najviac dvoma desatinnými miestami.</p>}</div></div>
        <div className="flex flex-wrap gap-3"><ConfirmButton title="Dokončiť návštevu?" description={`Zaznamená sa platba ${validPaymentAmount(amount) ? formatCurrency(Number(amount)) : ''} (${paymentNames[paymentMethod]}). Stav bude konečný.`} onConfirm={() => mutation.mutateAsync({ action: 'complete', body: { paymentMethod, amount: Number(amount) } })} disabled={mutation.isPending || !validPaymentAmount(amount)} variant="default">Dokončiť a zaznamenať platbu</ConfirmButton><ConfirmButton title="Označiť, že zákazník neprišiel?" description="Termín bude uzavretý bez platby. Tento stav je konečný." onConfirm={() => mutation.mutateAsync({ action: 'no-show' })} disabled={mutation.isPending}>Zákazník neprišiel</ConfirmButton></div>
      </> : <p className="text-sm text-muted-foreground">Návštevu možno dokončiť alebo označiť ako neúčasť po začiatku termínu.</p>}
      {actions.cancel && <div className="border-t pt-5"><ConfirmButton title="Zrušiť termín?" description="Zrušený termín ostane v histórii a jeho čas sa uvoľní. Tento stav je konečný." onConfirm={() => mutation.mutateAsync({ action: 'cancel' })} disabled={mutation.isPending} variant="destructive">Zrušiť termín</ConfirmButton></div>}
    </div>}
    {actions.reschedule && <form className="panel space-y-5" onSubmit={reschedule}>
      <h2 className="text-xl font-semibold">Preplánovať termín</h2><p className="text-sm text-muted-foreground">Zadajte čas v pásme Europe/Bratislava. Dostupnosť overíme pri uložení; pre personál neplatí hodinový predstih.</p>
      <div className="grid max-w-2xl gap-4 sm:grid-cols-2"><div><label htmlFor="reschedule-time">Nový dátum a čas</label><input id="reschedule-time" type="datetime-local" required step="900" min={`${salonDate()}T00:00`} max={`${addCalendarDays(salonDate(), 60)}T23:59`} value={start} onChange={event => setStart(event.target.value)} /></div>
      {admin && <div><label htmlFor="reschedule-employee">Pracovník</label><select id="reschedule-employee" value={employeeId} onChange={event => setEmployeeId(event.target.value)} disabled={employees.isPending || employees.isError}><option value={item.employee.id}>{item.employee.fullName} (aktuálny)</option>{employees.data?.filter(employee => employee.id !== item.employee.id).map(employee => <option key={employee.id} value={employee.id}>{employee.fullName}</option>)}</select></div>}</div>
      {admin && employees.isError && <><ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} /><p className="text-sm text-muted-foreground">Pri neaktívnej službe možno zachovať súčasného pracovníka.</p></>}
      {timeError && <p className="field-error" role="alert">{timeError}</p>}
      <Button type="submit" disabled={mutation.isPending || (start === salonLocalDateTime(item.startsAt) && employeeId === item.employee.id)}>Uložiť nový termín</Button>
    </form>}
    <form className="panel space-y-4" onSubmit={saveNote}><h2 className="text-xl font-semibold">Interná poznámka</h2><div><label htmlFor="staff-note">Poznámka personálu</label><textarea id="staff-note" rows={4} maxLength={2000} value={note} onChange={event => setNote(event.target.value)} /></div><Button type="submit" disabled={mutation.isPending || note === (item.staffNote ?? '')}>Uložiť poznámku</Button></form>
    <p className="text-xs text-muted-foreground">Vytvorené {formatDateTime(item.createdAt)} · Posledná zmena {formatDateTime(item.updatedAt)}</p>
  </>
}
