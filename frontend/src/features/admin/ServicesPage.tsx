import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { mainApi } from '@/api/mainApi'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { ConfirmButton } from '@/components/ConfirmButton'
import { useToast } from '@/components/toast-context'
import { categoryNames, type Service } from '@/features/public/api'
import { formatCurrency } from '@/lib/format'
import { adminServicesQuery, type AdminService } from './api'

function ServiceEditor({ service, onSaved, onCancel }: { service: AdminService | null; onSaved: () => void; onCancel: () => void }) {
  const client = useQueryClient()
  const toast = useToast()
  const [name, setName] = useState(service?.name ?? '')
  const [category, setCategory] = useState<Service['category']>(service?.category ?? 'Skin')
  const [description, setDescription] = useState(service?.description ?? '')
  const [duration, setDuration] = useState(String(service?.durationMinutes ?? 60))
  const [price, setPrice] = useState(String(service?.price ?? 0))
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), category, description: description.trim() || null, durationMinutes: Number(duration), price: Number(price) }
      return service ? mainApi.put(`/admin/services/${service.id}`, body) : mainApi.post('/admin/services', body)
    },
    onSuccess: async () => { await client.invalidateQueries(); toast(service ? 'Služba bola uložená.' : 'Služba bola vytvorená.'); onSaved() },
  })
  function submit(event: FormEvent) { event.preventDefault(); save.mutate() }
  return <form onSubmit={submit} className="panel space-y-4" aria-labelledby="service-editor-title">
    <h2 id="service-editor-title" className="text-xl font-semibold">{service ? 'Upraviť službu' : 'Nová služba'}</h2>
    <p className="text-sm text-muted-foreground">Zmena ceny sa prejaví pri nových rezerváciách. Existujúce termíny si ponechajú pôvodnú cenu.</p>
    <fieldset disabled={save.isPending} className="space-y-4">
      <div><label htmlFor="service-name">Názov</label><input id="service-name" required maxLength={200} value={name} onChange={e => setName(e.target.value)} pattern=".*\S.*" /></div>
      <div><label htmlFor="service-category">Kategória</label><select id="service-category" className="w-full rounded-lg border bg-background px-3 py-2.5" value={category} onChange={e => setCategory(e.target.value as Service['category'])}>{Object.entries(categoryNames).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></div>
      <div><label htmlFor="service-description">Popis</label><textarea id="service-description" rows={3} maxLength={2000} value={description} onChange={e => setDescription(e.target.value)} /></div>
      <div className="grid gap-4 sm:grid-cols-2"><div><label htmlFor="service-duration">Trvanie (minúty)</label><input id="service-duration" type="number" min={15} max={480} step={15} required value={duration} onChange={e => setDuration(e.target.value)} /><p className="mt-1 text-xs text-muted-foreground">Násobok 15 minút, najviac 480.</p></div><div><label htmlFor="service-price">Cena (€)</label><input id="service-price" type="number" min={0} max={10000} step="0.01" required value={price} onChange={e => setPrice(e.target.value)} /></div></div>
    </fieldset>
    {save.isError && <ErrorNotice error={save.error} />}
    <div className="flex flex-wrap gap-3"><Button type="submit" disabled={save.isPending}>{save.isPending ? 'Ukladám…' : 'Uložiť službu'}</Button><Button type="button" variant="outline" disabled={save.isPending} onClick={onCancel}>Zavrieť</Button></div>
  </form>
}

export function ServicesPage() {
  const client = useQueryClient()
  const toast = useToast()
  const services = useQuery(adminServicesQuery)
  const [editor, setEditor] = useState<AdminService | 'new' | null>(null)
  const [showInactive, setShowInactive] = useState(true)
  const active = useMutation({
    mutationFn: (service: AdminService) => mainApi.post(`/admin/services/${service.id}/${service.isActive ? 'deactivate' : 'activate'}`),
    onSuccess: async (_, service) => { await client.invalidateQueries(); toast(service.isActive ? 'Služba bola deaktivovaná.' : 'Služba bola aktivovaná.') },
  })
  const visible = services.data?.filter(service => showInactive || service.isActive)
  return <section className="space-y-7">
    <header className="flex flex-wrap items-end justify-between gap-4"><div><p className="eyebrow">Administrácia</p><h1 className="mt-2 text-3xl font-semibold">Služby a cenník</h1><p className="mt-2 text-muted-foreground">Ponuka ošetrení, trvanie a aktuálne ceny.</p></div><Button onClick={() => setEditor('new')}>Pridať službu</Button></header>
    {editor && <ServiceEditor key={editor === 'new' ? 'new' : editor.id} service={editor === 'new' ? null : editor} onSaved={() => setEditor(null)} onCancel={() => setEditor(null)} />}
    <label className="flex items-center gap-3 text-sm"><input type="checkbox" checked={showInactive} onChange={e => setShowInactive(e.target.checked)} />Zobraziť aj neaktívne služby</label>
    {services.isPending && <p role="status">Načítavam služby…</p>}{services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
    {visible?.length === 0 && <p className="notice">Žiadne služby. Pridajte novú službu alebo zobrazte aj neaktívne.</p>}
    <div className="grid gap-4 lg:grid-cols-2">{visible?.map(service => <article key={service.id} className="panel flex flex-col gap-3">
      <div className="flex items-start justify-between gap-3"><div><p className="eyebrow">{categoryNames[service.category]}</p><h2 className="mt-2 text-xl font-semibold">{service.name}</h2></div><span className={`rounded-full px-3 py-1 text-xs font-medium ${service.isActive ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground'}`}>{service.isActive ? 'Aktívna' : 'Neaktívna'}</span></div>
      {service.description && <p className="text-sm text-muted-foreground">{service.description}</p>}
      <p className="mt-auto pt-3"><strong>{formatCurrency(service.price)}</strong><span className="ml-3 text-sm text-muted-foreground">{service.durationMinutes} min</span></p>
      <div className="flex flex-wrap gap-3 border-t pt-4"><Button variant="outline" onClick={() => setEditor(service)} disabled={active.isPending}>Upraviť</Button><ConfirmButton title={service.isActive ? 'Deaktivovať službu?' : 'Aktivovať službu?'} description={service.isActive ? `Služba „${service.name}“ nebude dostupná pre nové rezervácie. Existujúce termíny a história zostanú zachované.` : `Služba „${service.name}“ bude znovu dostupná na rezervovanie.`} disabled={active.isPending} variant={service.isActive ? 'destructive' : 'outline'} onConfirm={() => active.mutateAsync(service)}>{service.isActive ? 'Deaktivovať' : 'Aktivovať'}</ConfirmButton></div>
    </article>)}</div>
  </section>
}
