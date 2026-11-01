import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router'
import { useAuth } from '@/auth/auth'
import { mainApi } from '@/api/mainApi'
import { ApiError } from '@/api/http'
import { ErrorNotice } from '@/components/Feedback'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/toast-context'
import { formatCurrency, formatDate, formatDateTime } from '@/lib/format'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { predictionHistoryQuery, predictionQuery, retentionActionsQuery } from '@/features/ml/api'
import { RetentionDialog } from '@/features/ml/RetentionDialog'
import { RiskBadge } from '@/features/ml/RiskBadge'
import { channelLabel, formatProbability } from '@/features/ml/risk'
import { customerQuery, type CustomerDetail } from './api'
import { customerSchema, customerStatusNames, paymentMethodNames, type CustomerValues } from './customer'

export function CustomerDetailPage() {
  const { id = '' } = useParams()
  const { user } = useAuth()
  const customer = useQuery(customerQuery(id))
  return <section className="space-y-7">
    <Link to="/app/zakaznici" className="text-sm font-medium text-primary hover:underline">← Zákazníci</Link>
    {customer.isPending && <p role="status" className="notice">Načítavam zákazníka…</p>}
    {customer.isError && (customer.error instanceof ApiError && customer.error.status === 404
      ? <div className="notice" role="alert">Zákazník neexistuje alebo k nemu nemáte prístup.</div>
      : <ErrorNotice error={customer.error} retry={() => { void customer.refetch() }} />)}
    {customer.data && <>
      <header><p className="eyebrow">Karta zákazníka</p><h1 className="mt-2 break-words text-3xl font-semibold">{customer.data.fullName}</h1>
        <div className="mt-3 flex flex-wrap gap-x-5 gap-y-2 text-sm text-muted-foreground"><a className="break-all hover:underline" href={`mailto:${customer.data.email}`}>{customer.data.email}</a>{customer.data.phone && <a className="hover:underline" href={`tel:${customer.data.phone}`}>{customer.data.phone}</a>}</div>
      </header>
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="panel"><p className="text-sm text-muted-foreground">Dokončené návštevy</p><p className="mt-2 text-2xl font-semibold">{customer.data.visitCount}</p></div>
        <div className="panel"><p className="text-sm text-muted-foreground">Celková útrata</p><p className="mt-2 text-2xl font-semibold">{formatCurrency(customer.data.totalSpent)}</p></div>
        <div className="panel"><p className="text-sm text-muted-foreground">V evidencii od</p><p className="mt-2 text-xl font-semibold">{formatDate(customer.data.createdAt)}</p></div>
      </div>
      {user?.role === 'User' && <p className="text-sm text-muted-foreground">Návštevy, útrata a história zahŕňajú iba termíny u vás.</p>}
      <ChurnRisk customerId={customer.data.id} customerName={customer.data.fullName} admin={user?.role === 'Admin'} />
      <CustomerEditor key={JSON.stringify([customer.data.id, customer.data.fullName, customer.data.phone, customer.data.notes])} customer={customer.data} />
      <div className="space-y-4">
        <h2 className="text-xl font-semibold">História termínov a platieb</h2>
        {customer.data.appointments.length === 0 ? <p className="notice">Zákazník zatiaľ nemá žiadne termíny.</p> : <div className="overflow-x-auto rounded-2xl border bg-card">
          <table>
            <caption className="sr-only">Termíny a platby zákazníka</caption>
            <thead className="border-b"><tr><th scope="col">Termín a služba</th><th scope="col">Pracovník</th><th scope="col">Stav</th><th scope="col">Cena</th><th scope="col">Platba</th></tr></thead>
            <tbody>{customer.data.appointments.map(appointment => <tr key={appointment.id} className="hover:bg-muted/30">
              <th scope="row"><Link className="font-semibold text-primary hover:underline" to={`/app/terminy/${appointment.id}`}>{appointment.service.name}</Link><span className="mt-1 block whitespace-nowrap font-normal text-muted-foreground">{formatDateTime(appointment.startsAt)}</span></th>
              <td>{appointment.employee.fullName}</td>
              <td><span className={`inline-flex whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-medium ${appointment.status === 'Completed' ? 'bg-primary/10 text-primary' : appointment.status === 'NoShow' ? 'bg-destructive/10 text-destructive' : 'bg-muted text-muted-foreground'}`}>{customerStatusNames[appointment.status]}</span></td>
              <td className="whitespace-nowrap">{formatCurrency(appointment.price)}</td>
              <td>{appointment.payment ? <><span className="whitespace-nowrap font-medium">{formatCurrency(appointment.payment.amount)}</span><span className="mt-1 block text-muted-foreground">{paymentMethodNames[appointment.payment.method]}</span><span className="mt-1 block whitespace-nowrap text-xs text-muted-foreground">{formatDateTime(appointment.payment.paidAt)}</span></> : <span className="text-muted-foreground">Bez platby</span>}</td>
            </tr>)}</tbody>
          </table>
        </div>}
      </div>
    </>}
  </section>
}

function CustomerEditor({ customer }: { customer: CustomerDetail }) {
  const client = useQueryClient()
  const toast = useToast()
  const form = useForm<CustomerValues>({ resolver: zodResolver(customerSchema), defaultValues: {
    fullName: customer.fullName, phone: customer.phone ?? '', notes: customer.notes ?? '',
  } })
  const update = useMutation({
    mutationFn: (values: CustomerValues) => mainApi.patch<CustomerDetail>(`/customers/${encodeURIComponent(customer.id)}`, values),
    onSuccess: async data => {
      client.setQueryData(customerQuery(customer.id).queryKey, data)
      await Promise.all([
        client.invalidateQueries({ queryKey: ['customers', 'list'] }),
        client.invalidateQueries({ queryKey: ['appointments'] }),
      ])
      toast('Údaje zákazníka boli uložené.')
    },
  })
  const errors = form.formState.errors
  return <form className="panel space-y-5" onSubmit={form.handleSubmit(values => update.mutate(values))} noValidate>
    <h2 className="text-xl font-semibold">Kontaktné údaje a poznámky</h2>
    <fieldset className="space-y-5" disabled={update.isPending}>
      <div className="grid gap-5 md:grid-cols-2">
        <div><label htmlFor="customer-name">Meno a priezvisko</label><input id="customer-name" autoComplete="name" maxLength={200} aria-invalid={Boolean(errors.fullName)} aria-describedby={errors.fullName ? 'customer-name-error' : undefined} {...form.register('fullName')} />{errors.fullName && <p className="field-error" id="customer-name-error">{errors.fullName.message}</p>}</div>
        <div><label htmlFor="customer-phone">Telefón</label><input id="customer-phone" type="tel" autoComplete="tel" maxLength={50} aria-invalid={Boolean(errors.phone)} aria-describedby={errors.phone ? 'customer-phone-error' : undefined} {...form.register('phone')} />{errors.phone && <p className="field-error" id="customer-phone-error">{errors.phone.message}</p>}</div>
      </div>
      <div><label htmlFor="customer-notes">Interné poznámky</label><textarea id="customer-notes" rows={4} maxLength={4000} aria-invalid={Boolean(errors.notes)} aria-describedby={errors.notes ? 'customer-notes-error' : undefined} {...form.register('notes')} />{errors.notes && <p className="field-error" id="customer-notes-error">{errors.notes.message}</p>}</div>
    </fieldset>
    <p className="text-sm text-muted-foreground">Súhlas so spracovaním osobných údajov získaný {formatDateTime(customer.gdprConsentAt)}.</p>
    {update.isError && <ErrorNotice error={update.error} />}
    <Button type="submit" disabled={update.isPending}>{update.isPending ? 'Ukladám…' : 'Uložiť zmeny'}</Button>
  </form>
}

function ChurnRisk({ customerId, customerName, admin }: { customerId: string; customerName: string; admin: boolean }) {
  const prediction = useQuery(predictionQuery(customerId))
  const history = useQuery(predictionHistoryQuery(customerId))
  const actions = useQuery({ ...retentionActionsQuery(customerId), enabled: admin })
  const p = prediction.data
  const points = history.data?.items.map(item => ({ at: formatDateTime(item.createdAt), probability: item.churnProbability * 100 })) ?? []
  return <>
    <section className="panel space-y-5" aria-labelledby="churn-title">
      <h2 id="churn-title" className="text-xl font-semibold">Riziko odchodu</h2>
      {prediction.isPending && <p role="status">Načítavam riziko…</p>}
      {prediction.isError && <p className="notice" role="status">Riziko odchodu sa momentálne nepodarilo načítať.</p>}
      {p && (p.riskLevel === 'insufficient_data' ? <div className="space-y-2"><RiskBadge level={p.riskLevel} /><p className="text-sm text-muted-foreground">Zákazník má menej ako dve návštevy alebo ešte nebol vyhodnotený, preto riziko odchodu nevieme odhadnúť.</p></div> : <div className="space-y-3">
        <div className="flex flex-wrap items-center gap-3"><RiskBadge level={p.riskLevel} /><strong className="text-2xl">{formatProbability(p.churnProbability)}</strong><span className="text-sm text-muted-foreground">pravdepodobnosť odchodu</span></div>
        {p.explanation && <p className="leading-relaxed">{p.explanation}</p>}
        {p.createdAt && <p className="text-sm text-muted-foreground">Vypočítané {formatDateTime(p.createdAt)}</p>}
      </div>)}
      {history.isSuccess && (points.length === 0 ? <p className="notice">Zatiaľ nie je k dispozícii vývoj rizika v čase.</p> : <div>
        <h3 className="mb-2 font-semibold">Vývoj pravdepodobnosti</h3>
        <div className="h-64 w-full" aria-label="Graf vývoja pravdepodobnosti odchodu"><ResponsiveContainer width="100%" height="100%"><LineChart data={points} margin={{ top: 10, right: 20, bottom: 10, left: 0 }} accessibilityLayer><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="at" tick={{ fontSize: 12 }} /><YAxis domain={[0, 100]} unit=" %" width={55} /><Tooltip formatter={value => [`${Number(value).toLocaleString('sk-SK', { maximumFractionDigits: 1 })} %`, 'Pravdepodobnosť']} /><Line dataKey="probability" name="Pravdepodobnosť" stroke="#22634f" strokeWidth={2} dot /></LineChart></ResponsiveContainer></div>
        <details className="mt-3"><summary className="cursor-pointer text-sm font-medium text-primary">Zobraziť údaje grafu v tabuľke</summary><div className="mt-3 overflow-x-auto"><table><thead><tr><th>Dátum</th><th>Pravdepodobnosť</th></tr></thead><tbody>{history.data.items.map(item => <tr key={item.predictionRunId}><th className="font-medium">{formatDateTime(item.createdAt)}</th><td>{formatProbability(item.churnProbability)}</td></tr>)}</tbody></table></div></details>
      </div>)}
    </section>
    {admin && <section className="panel space-y-5" aria-labelledby="retention-title">
      <div className="flex flex-wrap items-center justify-between gap-3"><h2 id="retention-title" className="text-xl font-semibold">Retenčné akcie</h2><RetentionDialog customerId={customerId} customerName={customerName} churnProbability={p?.churnProbability ?? null} /></div>
      {actions.isPending && <p role="status">Načítavam retenčné akcie…</p>}
      {actions.isError && <ErrorNotice error={actions.error} retry={() => { void actions.refetch() }} />}
      {actions.data?.length === 0 && <p className="notice">Zatiaľ nebol zaznamenaný žiadny kontakt so zákazníkom.</p>}
      {actions.data && actions.data.length > 0 && <div className="overflow-x-auto rounded-2xl border bg-background"><table>
        <caption className="sr-only">Zaznamenané retenčné kontakty</caption>
        <thead className="border-b"><tr><th scope="col">Dátum</th><th scope="col">Kanál</th><th scope="col">Poznámka</th><th scope="col">Zaznamenal</th><th scope="col">Riziko v čase kontaktu</th></tr></thead>
        <tbody>{actions.data.map(action => <tr key={action.id}><td className="whitespace-nowrap">{formatDateTime(action.createdAt)}</td><td>{channelLabel(action.channel)}</td><td className="min-w-56 whitespace-pre-wrap break-words text-sm">{action.note}</td><td>{action.createdBy.fullName}</td><td>{formatProbability(action.churnProbability)}</td></tr>)}</tbody>
      </table></div>}
    </section>}
  </>
}
