import { useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { CheckCircle2 } from 'lucide-react'
import { mainApi } from '@/api/mainApi'
import { ApiError } from '@/api/http'
import { ErrorNotice } from '@/components/Feedback'
import { Button } from '@/components/ui/button'
import { formatCurrency, formatDateTime, formatTime } from '@/lib/format'
import { servicesQuery, type Employee, type Slot, type BookingConfirmation } from './api'
import { addCalendarDays, bookingPayload, contactSchema, salonDate, type ContactValues } from './booking'

const steps = ['Služba', 'Pracovník', 'Dátum a čas', 'Kontakt', 'Potvrdenie']

export function BookingPage() {
  const [params] = useSearchParams()
  const [step, setStep] = useState(0)
  const [serviceId, setServiceId] = useState(params.get('serviceId') ?? '')
  const [employeeId, setEmployeeId] = useState('')
  const [date, setDate] = useState(() => salonDate())
  const [startsAt, setStartsAt] = useState('')
  const [confirmation, setConfirmation] = useState<BookingConfirmation | null>(null)
  const [failure, setFailure] = useState<unknown>(null)
  const [conflict, setConflict] = useState(false)
  const submitting = useRef(false)
  const heading = useRef<HTMLHeadingElement>(null)
  const services = useQuery(servicesQuery)
  const service = services.data?.find(item => item.id === serviceId)
  const employees = useQuery({
    queryKey: ['public', 'employees', serviceId],
    queryFn: () => mainApi.get<Employee[]>(`/public/services/${encodeURIComponent(serviceId)}/employees`),
    enabled: !!service && step >= 1 && step < 4,
  })
  const availability = useQuery({
    queryKey: ['public', 'availability', serviceId, employeeId, date],
    queryFn: () => {
      const query = new URLSearchParams({ serviceId, date })
      if (employeeId) query.set('employeeId', employeeId)
      return mainApi.get<Slot[]>(`/public/availability?${query}`)
    },
    enabled: !!service && !!date && step >= 2 && step < 4,
    staleTime: 0,
    refetchOnWindowFocus: true,
  })
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<ContactValues>({
    resolver: zodResolver(contactSchema),
    defaultValues: { fullName: '', email: '', phone: '', note: '', gdprConsent: false },
  })
  const selectedSlot = availability.data?.some(slot => slot.startsAt === startsAt)
  const today = salonDate()

  function go(next: number) {
    setStep(next)
    requestAnimationFrame(() => heading.current?.focus())
  }
  async function submit(values: ContactValues) {
    if (submitting.current || confirmation) return
    if (!service || !startsAt || !selectedSlot || availability.isError) {
      setStartsAt('')
      setConflict(true)
      go(2)
      void availability.refetch()
      return
    }
    submitting.current = true
    setFailure(null)
    try {
      const result = await mainApi.post<BookingConfirmation>('/public/bookings', bookingPayload(serviceId, employeeId, startsAt, values))
      setConfirmation(result)
      go(4)
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setStartsAt('')
        setConflict(true)
        go(2)
        void availability.refetch()
      } else {
        setFailure(error)
      }
    } finally { submitting.current = false }
  }

  return <main className="page-container py-10 md:py-14">
    <p className="eyebrow">Chvíľa pre vás</p><h1 className="mt-3 text-3xl font-semibold md:text-4xl">Rezervácia termínu</h1>
    <p className="mt-3 text-muted-foreground">Bez účtu. Vyberte si starostlivosť a čas, ktorý vám vyhovuje.</p>
    <ol aria-label="Kroky rezervácie" className="my-9 grid grid-cols-5 gap-2">
      {steps.map((label, index) => <li key={label} aria-current={index === step ? 'step' : undefined} className={`step ${index <= step ? 'step-active' : ''}`}>
        <span className="font-semibold">{index + 1}</span><span className="hidden text-sm sm:inline">{label}</span>
      </li>)}
    </ol>
    <div className="grid items-start gap-7 lg:grid-cols-[1fr_19rem]">
      <section className="panel min-w-0" aria-labelledby="booking-step">
        <h2 id="booking-step" ref={heading} tabIndex={-1} className="mb-6 text-2xl font-semibold outline-none">{step === 4 ? 'Termín je rezervovaný' : steps[step]}</h2>
        {step === 0 && <>
          {services.isPending && <p role="status">Načítavam služby…</p>}
          {services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
          {services.isSuccess && services.data.length === 0 && <p className="notice">Momentálne nemáme dostupné služby. Skúste to neskôr.</p>}
          <fieldset className="space-y-3"><legend className="sr-only">Vyberte službu</legend>
            {services.data?.map(item => <label key={item.id} className={`choice ${item.id === serviceId ? 'choice-selected' : ''}`}>
              <input type="radio" name="service" value={item.id} checked={item.id === serviceId} onChange={() => { setServiceId(item.id); setEmployeeId(''); setStartsAt(''); setConflict(false); setFailure(null) }} />
              <span className="flex-1"><strong className="block">{item.name}</strong><span className="text-sm text-muted-foreground">{item.durationMinutes} min</span></span><strong>{formatCurrency(item.price)}</strong>
            </label>)}
          </fieldset>
          <Button className="mt-6" onClick={() => go(1)} disabled={!service || services.isError}>Pokračovať</Button>
        </>}
        {step === 1 && <>
          {employees.isPending && <p role="status">Načítavam pracovníkov…</p>}
          {employees.isError && <ErrorNotice error={employees.error} retry={() => { void employees.refetch() }} />}
          {employees.isSuccess && employees.data.length === 0 && <p className="notice">Pre túto službu momentálne nie je dostupný pracovník. Vyberte inú službu.</p>}
          {employees.isSuccess && employees.data.length > 0 && <fieldset className="space-y-3"><legend className="sr-only">Vyberte pracovníka</legend>
            {[{ id: '', fullName: 'Ktokoľvek' }, ...employees.data].map(item => <label key={item.id} className={`choice ${item.id === employeeId ? 'choice-selected' : ''}`}>
              <input type="radio" name="employee" value={item.id} checked={item.id === employeeId} onChange={() => { setEmployeeId(item.id); setStartsAt(''); setConflict(false); setFailure(null) }} />
              <span><strong className="block">{item.fullName}</strong>{!item.id && <span className="text-sm text-muted-foreground">Priradíme vám voľného pracovníka.</span>}</span>
            </label>)}
          </fieldset>}
          <div className="mt-6 flex gap-3"><Button variant="outline" onClick={() => go(0)}>Späť</Button><Button onClick={() => go(2)} disabled={!employees.isSuccess || employees.data.length === 0 || (!!employeeId && !employees.data.some(item => item.id === employeeId))}>Pokračovať</Button></div>
        </>}
        {step === 2 && <>
          {conflict && <div role="alert" className="notice notice-error mb-5">Vybraný termín už nie je voľný. Vyberte si, prosím, iný čas. Vaše kontaktné údaje sme zachovali.</div>}
          <label htmlFor="date">Dátum</label><input className="max-w-xs" id="date" type="date" min={today} max={addCalendarDays(today, 60)} value={date} onChange={event => { setDate(event.target.value); setStartsAt(''); setConflict(false); setFailure(null) }} />
          <p className="my-4 text-sm text-muted-foreground">Časy sú v časovom pásme Europe/Bratislava. Rezervovať môžete najviac 60 dní vopred.</p>
          {date && availability.isFetching && <p role="status">Načítavam voľné časy…</p>}
          {availability.isError && <ErrorNotice error={availability.error} retry={() => { void availability.refetch() }} />}
          {availability.isSuccess && !availability.isFetching && availability.data.length === 0 && <p className="notice">V tento deň nie sú voľné termíny. Vyberte iný dátum.</p>}
          {!availability.isError && <fieldset className="mt-5 grid grid-cols-3 gap-2 sm:grid-cols-4"><legend className="sr-only">Voľné časy</legend>
            {availability.data?.map(slot => <label key={slot.startsAt} className={`choice justify-center px-2 ${slot.startsAt === startsAt ? 'choice-selected' : ''}`}>
              <input className="sr-only" type="radio" name="slot" value={slot.startsAt} checked={slot.startsAt === startsAt} onChange={() => { setStartsAt(slot.startsAt); setConflict(false); setFailure(null) }} />
              {formatTime(slot.startsAt)}
            </label>)}
          </fieldset>}
          <div className="mt-6 flex gap-3"><Button variant="outline" onClick={() => go(1)}>Späť</Button><Button onClick={() => go(3)} disabled={!startsAt || !date || !selectedSlot || availability.isFetching || availability.isError}>Pokračovať</Button></div>
        </>}
        {step === 3 && <form onSubmit={event => { void handleSubmit(submit)(event) }} className="space-y-5" noValidate>
          {failure != null && <ErrorNotice error={failure} />}
          <div><label htmlFor="fullName">Meno a priezvisko *</label><input id="fullName" autoComplete="name" maxLength={200} {...register('fullName')} aria-invalid={!!errors.fullName} aria-describedby={errors.fullName ? 'name-error' : undefined} />{errors.fullName && <p id="name-error" className="field-error">{errors.fullName.message}</p>}</div>
          <div><label htmlFor="booking-email">E-mail *</label><input id="booking-email" type="email" autoComplete="email" maxLength={254} {...register('email')} aria-invalid={!!errors.email} aria-describedby={errors.email ? 'booking-email-error' : undefined} />{errors.email && <p id="booking-email-error" className="field-error">{errors.email.message}</p>}</div>
          <div><label htmlFor="phone">Telefón <span className="text-muted-foreground">(nepovinné)</span></label><input id="phone" type="tel" autoComplete="tel" maxLength={50} {...register('phone')} aria-invalid={!!errors.phone} aria-describedby={errors.phone ? 'phone-error' : undefined} />{errors.phone && <p id="phone-error" className="field-error">{errors.phone.message}</p>}</div>
          <div><label htmlFor="note">Poznámka <span className="text-muted-foreground">(nepovinné)</span></label><textarea id="note" rows={3} maxLength={1000} {...register('note')} aria-invalid={!!errors.note} aria-describedby={errors.note ? 'note-error' : undefined} />{errors.note && <p id="note-error" className="field-error">{errors.note.message}</p>}</div>
          <div><label className="flex items-start gap-3 text-sm leading-relaxed"><input className="mt-1" type="checkbox" {...register('gdprConsent')} aria-invalid={!!errors.gdprConsent} aria-describedby="gdpr-info gdpr-error" /><span>Súhlasím so spracovaním svojich osobných údajov na účel vytvorenia a správy rezervácie. *</span></label><p id="gdpr-info" className="mt-2 text-sm text-muted-foreground">Meno a kontaktné údaje použijeme na evidenciu vašej návštevy.</p><p id="gdpr-error" className="field-error">{errors.gdprConsent?.message}</p></div>
          <div className="flex flex-wrap gap-3"><Button type="button" variant="outline" onClick={() => go(2)} disabled={isSubmitting}>Späť</Button><Button type="submit" disabled={isSubmitting}>{isSubmitting ? 'Rezervujem…' : 'Potvrdiť rezerváciu'}</Button></div>
        </form>}
        {step === 4 && confirmation && <div className="space-y-5">
          <CheckCircle2 size={44} className="text-primary" aria-hidden="true" />
          <p role="status">Ďakujeme. Vaša rezervácia bola úspešne vytvorená.</p>
          <dl className="summary space-y-4">
            <div><dt>Služba</dt><dd>{confirmation.serviceName}</dd></div>
            <div><dt>Dátum a čas</dt><dd>{formatDateTime(confirmation.startsAt)}</dd></div>
            <div><dt>Pracovník</dt><dd>{confirmation.employeeName}</dd></div>
            <div><dt>Cena</dt><dd>{formatCurrency(confirmation.price)}</dd></div>
          </dl>
          <p className="text-sm text-muted-foreground">Potvrdenie si uložte. E-mailové ani SMS potvrdenie neposielame.</p>
          <Link to="/" className="action-link">Späť na služby</Link>
        </div>}
      </section>
      <aside className="panel bg-primary/5 lg:sticky lg:top-6" aria-label="Zhrnutie rezervácie">
        <h2 className="mb-5 text-lg font-semibold">Vaša rezervácia</h2>
        <dl className="summary space-y-5">
          <div><dt>Služba</dt><dd>{confirmation?.serviceName ?? service?.name ?? 'Zatiaľ nevybraná'}</dd></div>
          <div><dt>Pracovník</dt><dd>{confirmation?.employeeName ?? employees.data?.find(item => item.id === employeeId)?.fullName ?? 'Ktokoľvek'}</dd></div>
          <div><dt>Dátum a čas</dt><dd>{confirmation ? formatDateTime(confirmation.startsAt) : startsAt ? formatDateTime(startsAt) : 'Zatiaľ nevybraný'}</dd></div>
          {service && <div className="border-t pt-4"><dt>{service.durationMinutes} min · Cena</dt><dd className="text-2xl">{formatCurrency(confirmation?.price ?? service.price)}</dd></div>}
        </dl>
      </aside>
    </div>
  </main>
}
