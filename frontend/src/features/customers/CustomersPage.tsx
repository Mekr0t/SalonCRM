import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import { Search } from 'lucide-react'
import { useAuth } from '@/auth/auth'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { formatDateTime } from '@/lib/format'
import { customersQuery } from './api'
import { customerPage } from './customer'
import { riskLookupQuery } from '@/features/ml/api'
import { RiskBadge } from '@/features/ml/RiskBadge'
import { formatProbability } from '@/features/ml/risk'

export function CustomersPage() {
  const { user } = useAuth()
  const [params, setParams] = useSearchParams()
  const search = (params.get('search') ?? '').trim().slice(0, 100)
  const page = customerPage(params.get('page'))
  const customers = useQuery(customersQuery(search, page))
  const risks = useQuery(riskLookupQuery(customers.data?.items.map(customer => customer.id) ?? []))
  function changePage(next: number) {
    const nextParams = new URLSearchParams(params)
    nextParams.set('page', String(next))
    setParams(nextParams)
  }

  return <section className="space-y-7">
    <header>
      <p className="eyebrow">Kontakty a návštevy</p>
      <h1 className="mt-2 text-3xl font-semibold">Zákazníci</h1>
      <p className="mt-3 text-muted-foreground">{user?.role === 'Admin' ? 'Prehľad zákazníkov salónu a ich návštev.' : 'Zákazníci, ktorí majú termín u vás. Počty návštev zahŕňajú iba vaše termíny.'}</p>
    </header>
    <SearchForm key={search} value={search} onSearch={value => {
      const next = new URLSearchParams()
      if (value.trim()) next.set('search', value.trim())
      setParams(next)
    }} />
    {customers.isPending && <p role="status" className="notice">Načítavam zákazníkov…</p>}
    {customers.isError && <ErrorNotice error={customers.error} retry={() => { void customers.refetch() }} />}
    {customers.isSuccess && <>
      <p className="text-sm text-muted-foreground" role="status">Počet zákazníkov: {customers.data.totalCount}</p>
      {customers.data.items.length === 0 ? <div className="panel space-y-3">
        <h2 className="text-lg font-semibold">{search ? 'Nenašli sa žiadni zákazníci' : 'Zatiaľ žiadni zákazníci'}</h2>
        <p className="text-muted-foreground">{search ? 'Skúste iné meno, e-mail alebo telefón.' : 'Zákazník sa do evidencie pridá pri vytvorení rezervácie.'}</p>
        {(search || page > 1) && <Button variant="outline" onClick={() => setParams({})}>Zobraziť všetkých zákazníkov</Button>}
        {!search && page === 1 && <Link to="/app/terminy/novy" className="action-link">Vytvoriť telefonickú rezerváciu</Link>}
      </div> : <div className="overflow-x-auto rounded-2xl border bg-card">
        <table>
          <caption className="sr-only">Zákazníci a prehľad návštev</caption>
          <thead className="border-b"><tr>
            <th scope="col">Zákazník</th><th scope="col">Kontakt</th>
            <th scope="col">Návštevy</th><th scope="col">Posledná návšteva</th>
            <th scope="col">Nasledujúci termín</th><th scope="col">Riziko</th>
          </tr></thead>
          <tbody>{customers.data.items.map(customer => <tr key={customer.id} className="hover:bg-muted/30">
            <th scope="row"><Link className="font-semibold text-primary underline-offset-4 hover:underline" to={`/app/zakaznici/${customer.id}`}>{customer.fullName}</Link></th>
            <td><a className="break-all hover:underline" href={`mailto:${customer.email}`}>{customer.email}</a>{customer.phone && <a className="mt-1 block whitespace-nowrap text-muted-foreground hover:underline" href={`tel:${customer.phone}`}>{customer.phone}</a>}</td>
            <td>{customer.visitCount}</td>
            <td className="whitespace-nowrap">{customer.lastVisitAt ? formatDateTime(customer.lastVisitAt) : 'Bez návštevy'}</td>
            <td className="whitespace-nowrap">{customer.nextAppointmentAt ? formatDateTime(customer.nextAppointmentAt) : 'Bez termínu'}</td>
            <td>{risks.data?.get(customer.id) ? <><RiskBadge level={risks.data.get(customer.id)!.riskLevel} /><span className="mt-1 block text-xs text-muted-foreground">{formatProbability(risks.data.get(customer.id)!.churnProbability)}</span></> : <span className="text-muted-foreground">—</span>}</td>
          </tr>)}</tbody>
        </table>
      </div>}
      {risks.isError && <p className="text-sm text-muted-foreground" role="status">Riziko odchodu sa momentálne nepodarilo načítať.</p>}
      {customers.data.totalCount > 0 && <nav aria-label="Stránkovanie zákazníkov" className="flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={page === 1 || customers.isFetching} onClick={() => changePage(page - 1)}>Predchádzajúca</Button>
        <span className="text-sm">Strana {page} z {Math.max(1, Math.ceil(customers.data.totalCount / customers.data.pageSize))}</span>
        <Button variant="outline" disabled={page * customers.data.pageSize >= customers.data.totalCount || customers.isFetching} onClick={() => changePage(page + 1)}>Nasledujúca</Button>
      </nav>}
    </>}
  </section>
}

function SearchForm({ value, onSearch }: { value: string; onSearch: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  function submit(event: FormEvent) {
    event.preventDefault()
    onSearch(draft)
  }
  return <form className="panel flex flex-wrap items-end gap-3" onSubmit={submit} role="search">
    <div className="min-w-48 flex-1"><label htmlFor="customer-search">Meno, e-mail alebo telefón</label>
      <input id="customer-search" type="search" maxLength={100} placeholder="Vyhľadať zákazníka" value={draft} onChange={event => setDraft(event.target.value)} />
    </div>
    <Button type="submit"><Search aria-hidden="true" /> Vyhľadať</Button>
    {value && <Button variant="outline" type="button" onClick={() => onSearch('')}>Zrušiť hľadanie</Button>}
  </form>
}
