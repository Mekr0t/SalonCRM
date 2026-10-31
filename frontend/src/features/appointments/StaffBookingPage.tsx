import { useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { mainApi } from '@/api/mainApi'
import { useAuth } from '@/auth/auth'
import { ErrorNotice } from '@/components/Feedback'
import { useToast } from '@/components/toast-context'
import { Button } from '@/components/ui/button'
import { formatCurrency } from '@/lib/format'
import { servicesQuery, type Employee } from '@/features/public/api'
import { addCalendarDays, contactSchema, salonDate } from '@/features/public/booking'
import type { AppointmentDetail } from './api'
import { isPastAppointmentTime, salonLocalDateTime, salonLocalToUtc } from './time'

const staffSchema = contactSchema.extend({ staffNote: z.string().trim().max(2000, 'Poznámka môže mať najviac 2 000 znakov.') })
type StaffValues = z.infer<typeof staffSchema>

export function StaffBookingPage() {
  const { user } = useAuth()
  const admin = user?.role === 'Admin'
  const navigate = useNavigate()
  const toast = useToast()
  const client = useQueryClient()
  const [serviceId, setServiceId] = useState('')
  const [employeeId, setEmployeeId] = useState('')
  const [start, setStart] = useState('')
  const [timeError, setTimeError] = useState('')
  const submitting = useRef(false)
  const services = useQuery(servicesQuery)
  const service = services.data?.find(value => value.id === serviceId)
  const employees = useQuery({
    queryKey: ['public', 'employees', serviceId],
    queryFn: () => mainApi.get<Employee[]>(`/public/services/${serviceId}/employees`),
    enabled: !!service,
  })
  const eligible = admin ? !!employees.data?.length : !!employees.data?.some(employee => employee.id === user?.id)
  const form = useForm<StaffValues>({
    resolver: zodResolver(staffSchema),
    defaultValues: { fullName: '', email: '', phone: '', note: '', staffNote: '', gdprConsent: false },
  })
  const mutation = useMutation({
    mutationFn: (body: unknown) => mainApi.post<AppointmentDetail>('/appointments', body),
    onSuccess: async data => {
      toast('Telefonická rezervácia bola vytvorená.')
      await Promise.all([client.invalidateQueries({ queryKey: ['appointments'] }), client.invalidateQueries({ queryKey: ['customers'] }), client.invalidateQueries({ queryKey: ['admin'] })])
      await navigate(`/app/terminy/${data.id}`, { replace: true })
    },
    onSettled: () => { submitting.current = false },
  })

  function submit(event: FormEvent<HTMLFormElement>) {
    void form.handleSubmit(values => {
      if (submitting.current) return
      const startsAt = salonLocalToUtc(start)
      if (!startsAt) { setTimeError('Vyberte platný dátum a čas termínu.'); return }
      if (isPastAppointmentTime(startsAt)) { setTimeError('Termín nie je možné rezervovať do minulosti.'); return }
      if (!service || !eligible) return
      setTimeError('')
      submitting.current = true
      mutation.mutate({
        serviceId, ...(admin && employeeId ? { employeeId } : {}), startsAt,
        fullName: values.fullName, email: values.email, phone: values.phone || null,
        customerNote: values.note || null, staffNote: values.staffNote || null, gdprConsent: values.gdprConsent,
      })
    })(event)
  }
  const fieldError = (name: keyof StaffValues) => form.formState.errors[name] && <p className="field-error" id={`booking-error-${name}`}>{form.formState.errors[name]?.message}</p>
  const fieldProps = (name: keyof StaffValues) => ({ 'aria-invalid': !!form.formState.errors[name], 'aria-describedby': form.formState.errors[name] ? `booking-error-${name}` : undefined })

  return <section className="max-w-4xl space-y-6">
    <Link to="/app/kalendar" className="text-sm font-medium text-primary hover:underline">← Späť na kalendár</Link>
    <div><p className="eyebrow">Rezervácia za zákazníka</p><h1 className="mt-2 text-3xl font-semibold">Telefonická rezervácia</h1><p className="mt-3 text-muted-foreground">Čas zadávate v pásme Europe/Bratislava. Pre personál neplatí hodinový predstih; dostupnosť overíme pri uložení.</p></div>
    {services.isPending && <p role="status">Načítavam služby…</p>}
    {services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
    {services.isSuccess && services.data.length === 0 && <p className="notice">Nie sú dostupné žiadne aktívne služby. Požiadajte administrátora o ich nastavenie.</p>}
    {!!services.data?.length && <form className="space-y-6" onSubmit={submit}>
      <div className="panel space-y-5"><h2 className="text-xl font-semibold">Služba a termín</h2>
        <div><label htmlFor="staff-service">Služba</label><select id="staff-service" required value={serviceId} onChange={event => { setServiceId(event.target.value); setEmployeeId(''); mutation.reset() }}><option value="">Vyberte službu</option>{services.data.map(value => <option value={value.id} key={value.id}>{value.name} · {value.durationMinutes} min · {formatCurrency(value.price)}</option>)}</select></div>
        {service && employees.isPending && <p role="status">Načítavam pracovníkov…</p>}
        {employees.isError && <ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} />}
        {service && employees.isSuccess && !eligible && <p className="notice notice-error" role="alert">{admin ? 'Pre túto službu nie je dostupný aktívny pracovník.' : 'Túto službu nemáte priradenú. Vyberte inú službu alebo kontaktujte administrátora.'}</p>}
        {admin && service && eligible && <div><label htmlFor="staff-employee">Pracovník</label><select id="staff-employee" value={employeeId} onChange={event => setEmployeeId(event.target.value)}><option value="">Ktokoľvek dostupný</option>{employees.data?.map(employee => <option key={employee.id} value={employee.id}>{employee.fullName}</option>)}</select></div>}
        {!admin && <p className="text-sm text-muted-foreground">Rezervácia bude priradená vám: {user?.fullName}.</p>}
        <div><label htmlFor="staff-start">Dátum a čas začiatku</label><input id="staff-start" type="datetime-local" step="900" required min={`${salonDate()}T00:00`} max={`${addCalendarDays(salonDate(), 60)}T23:59`} value={start} onChange={event => setStart(event.target.value)} aria-describedby="staff-start-hint" /><p className="mt-2 text-sm text-muted-foreground" id="staff-start-hint">Po–Pi 8:00–18:00, So 8:00–13:00; začiatok každých 15 minút. Služba musí skončiť pred zatvorením.</p>{timeError && <p className="field-error" role="alert">{timeError}</p>}</div>
      </div>
      <div className="panel space-y-5"><h2 className="text-xl font-semibold">Kontaktné údaje zákazníka</h2><p className="text-sm text-muted-foreground">Existujúceho zákazníka vyhľadáme podľa e-mailu. Jeho uložené meno sa neprepíše.</p>
        <div><label htmlFor="staff-fullName">Meno a priezvisko</label><input id="staff-fullName" autoComplete="name" maxLength={200} {...form.register('fullName')} {...fieldProps('fullName')} />{fieldError('fullName')}</div>
        <div className="grid gap-4 sm:grid-cols-2"><div><label htmlFor="staff-email">E-mail</label><input id="staff-email" type="email" autoComplete="email" maxLength={254} {...form.register('email')} {...fieldProps('email')} />{fieldError('email')}</div><div><label htmlFor="staff-phone">Telefón (nepovinné)</label><input id="staff-phone" type="tel" autoComplete="tel" maxLength={50} {...form.register('phone')} {...fieldProps('phone')} />{fieldError('phone')}</div></div>
        <div><label htmlFor="staff-customerNote">Poznámka zákazníka (nepovinné)</label><textarea id="staff-customerNote" rows={3} maxLength={1000} {...form.register('note')} {...fieldProps('note')} />{fieldError('note')}</div>
        <div><label htmlFor="staff-internalNote">Interná poznámka (nepovinné)</label><textarea id="staff-internalNote" rows={3} maxLength={2000} {...form.register('staffNote')} {...fieldProps('staffNote')} />{fieldError('staffNote')}</div>
        <div><label className="flex items-start gap-3" htmlFor="staff-gdpr"><input id="staff-gdpr" className="mt-1" type="checkbox" {...form.register('gdprConsent')} {...fieldProps('gdprConsent')} /><span>Potvrdzujem, že som získal/a súhlas zákazníka so spracovaním osobných údajov na účely rezervácie.</span></label>{fieldError('gdprConsent')}</div>
      </div>
      {mutation.isError && <ErrorNotice error={mutation.error} />}
      <Button type="submit" disabled={mutation.isPending || !service || !eligible || employees.isPending}>{mutation.isPending ? 'Vytváram rezerváciu…' : 'Vytvoriť rezerváciu'}</Button>
    </form>}
    <p className="text-xs text-muted-foreground">Aktuálny čas salónu: {salonLocalDateTime(new Date()).replace('T', ' ')}</p>
  </section>
}
