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
      <CustomerEditor key={JSON.stringify([customer.data.id, customer.data.fullName, customer.data.phone, customer.data.notes])} customer={customer.data} />
      <div className="space-y-4">
        <h2 className="text-xl font-semibold">História termínov a platieb</h2>
        {customer.data.appointments.length === 0 ? <p className="notice">Zákazník zatiaľ nemá žiadne termíny.</p> : <div className="overflow-x-auto rounded-2xl border bg-card">
          <table className="w-full text-left text-sm">
            <caption className="sr-only">Termíny a platby zákazníka</caption>
            <thead className="border-b bg-muted/50"><tr><th scope="col" className="p-4">Termín a služba</th><th scope="col" className="p-4">Pracovník</th><th scope="col" className="p-4">Stav</th><th scope="col" className="p-4">Cena</th><th scope="col" className="p-4">Platba</th></tr></thead>
            <tbody>{customer.data.appointments.map(appointment => <tr key={appointment.id} className="border-b last:border-0 hover:bg-muted/30">
              <th scope="row" className="p-4"><Link className="font-semibold text-primary hover:underline" to={`/app/terminy/${appointment.id}`}>{appointment.service.name}</Link><span className="mt-1 block whitespace-nowrap font-normal text-muted-foreground">{formatDateTime(appointment.startsAt)}</span></th>
              <td className="p-4">{appointment.employee.fullName}</td>
              <td className="p-4"><span className={`inline-flex whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-medium ${appointment.status === 'Completed' ? 'bg-primary/10 text-primary' : appointment.status === 'NoShow' ? 'bg-destructive/10 text-destructive' : 'bg-muted text-muted-foreground'}`}>{customerStatusNames[appointment.status]}</span></td>
              <td className="whitespace-nowrap p-4">{formatCurrency(appointment.price)}</td>
              <td className="p-4">{appointment.payment ? <><span className="whitespace-nowrap font-medium">{formatCurrency(appointment.payment.amount)}</span><span className="mt-1 block text-muted-foreground">{paymentMethodNames[appointment.payment.method]}</span><span className="mt-1 block whitespace-nowrap text-xs text-muted-foreground">{formatDateTime(appointment.payment.paidAt)}</span></> : <span className="text-muted-foreground">Bez platby</span>}</td>
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
