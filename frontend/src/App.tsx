import { Link } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { ArrowUpRight, Clock3, Flower2 } from 'lucide-react'
import { servicesQuery, categoryNames } from '@/features/public/api'
import { formatCurrency } from '@/lib/format'
import { ErrorNotice } from '@/components/Feedback'

export function HomePage() {
  const services = useQuery(servicesQuery)
  return <main className="page-container">
    <section className="grid items-center gap-8 py-14 md:grid-cols-[1.5fr_1fr] md:py-20">
      <div>
        <p className="eyebrow">Váš čas pre seba</p>
        <h1 className="mt-4 max-w-2xl text-4xl font-semibold leading-tight tracking-tight md:text-6xl">Malá prestávka.<br /><span className="text-primary">Veľký rozdiel.</span></h1>
        <p className="mt-6 max-w-lg text-lg leading-relaxed text-muted-foreground">Doprajte si starostlivosť o pleť, nechty či chvíľu oddychu. Vyberte si službu a rezervujte si termín online.</p>
        <Link to="/rezervacia" className="action-link mt-7">Rezervovať termín <ArrowUpRight size={18} aria-hidden="true" /></Link>
      </div>
      <div className="hidden aspect-square items-center justify-center rounded-[3rem] bg-primary/10 text-primary md:flex" aria-hidden="true"><Flower2 size={180} strokeWidth={0.8} /></div>
    </section>
    <section aria-labelledby="services-title">
      <div className="mb-7 flex flex-wrap items-end justify-between gap-3">
        <div><p className="eyebrow">Starostlivosť podľa vás</p><h2 id="services-title" className="mt-2 text-3xl font-semibold">Služby a cenník</h2></div>
        <p className="text-sm text-muted-foreground">Cena aj trvanie prehľadne na jednom mieste.</p>
      </div>
      {services.isPending && <p role="status">Načítavam služby…</p>}
      {services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
      {services.isSuccess && services.data.length === 0 && <p className="notice">Momentálne nemáme dostupné služby. Skúste to neskôr.</p>}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {services.data?.map(service => <article className="panel flex flex-col" key={service.id}>
          <p className="eyebrow">{categoryNames[service.category]}</p>
          <h3 className="mt-3 text-xl font-semibold">{service.name}</h3>
          {service.description && <p className="mt-3 text-sm leading-relaxed text-muted-foreground">{service.description}</p>}
          <div className="mt-auto flex items-center justify-between gap-3 pt-7">
            <span className="flex items-center gap-2 text-sm text-muted-foreground"><Clock3 size={16} aria-hidden="true" />{service.durationMinutes} min</span>
            <strong className="text-xl">{formatCurrency(service.price)}</strong>
          </div>
          <Link className="mt-5 border-t pt-4 text-sm font-semibold text-primary hover:underline" to={`/rezervacia?serviceId=${encodeURIComponent(service.id)}`}>Vybrať službu →</Link>
        </article>)}
      </div>
    </section>
  </main>
}

export function NotFoundPage() {
  return <main className="page-container py-16 space-y-5">
    <h1 className="text-3xl font-semibold">Stránka nenájdená</h1>
    <p className="text-muted-foreground">Požadovaná stránka neexistuje.</p>
    <Link to="/" className="action-link">Späť na úvod</Link>
  </main>
}
