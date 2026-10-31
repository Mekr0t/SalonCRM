import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { ApiError } from '@/api/http'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { useToast } from '@/components/toast-context'
import { formatDateTime } from '@/lib/format'
import { mlHealthQuery, mlModelQuery, mlRunsQuery, namesQuery, predictionsQuery, riskDistributionQuery, runPipeline, type Metrics, type MlModel } from './api'
import { RetentionDialog } from './RetentionDialog'
import { RiskBadge } from './RiskBadge'
import { formatProbability, withCustomerNames } from './risk'

const PAGE_SIZE = 50
const riskFilters = [['high', 'Vysoké riziko'], ['medium', 'Stredné riziko'], ['low', 'Nízke riziko']] as const
const metricRows: [string, (m: Metrics) => number | null][] = [
  ['ROC-AUC', m => m.rocAuc], ['Precision', m => m.precision], ['Recall', m => m.recall], ['F1', m => m.f1], ['Podiel odchodov (positiveRate)', m => m.positiveRate],
]
const number = (value: number | null) => value === null ? '—' : value.toLocaleString('sk-SK', { maximumFractionDigits: 3 })
const runStatus: Record<string, string> = { running: 'Beží', success: 'Úspešný', succeeded: 'Úspešný', completed: 'Dokončený', failed: 'Zlyhal' }

export function MlDashboardPage() {
  const model = useQuery(mlModelQuery)
  const health = useQuery(mlHealthQuery)
  const noModel = (model.error instanceof ApiError && model.error.status === 404) || health.data?.activeModel === false
  return <section className="space-y-7">
    <header className="flex flex-wrap items-start justify-between gap-4">
      <div><p className="eyebrow">Administrácia</p><h1 className="mt-2 text-3xl font-semibold">Riziko odchodu zákazníkov</h1><p className="mt-2 max-w-2xl text-muted-foreground">Predikcia z historických návštev. Zákazníci s menej ako dvoma návštevami sa neskórujú.</p></div>
      <RecalculateButton />
    </header>
    {noModel ? <div className="panel space-y-3" role="status"><h2 className="text-lg font-semibold">Zatiaľ nie je natrénovaný model</h2><p className="text-muted-foreground">Predikcie sa zobrazia po prvom prepočítaní. Systém stiahne dáta z evidencie, natrénuje model a vyhodnotí všetkých zákazníkov. Použite tlačidlo „Prepočítať predikcie“. Ak je v salóne málo histórie, prepočet nemusí uspieť.</p></div> : <>
      <Distribution />
      <AtRisk />
      <ModelSection model={model} />
      <Runs />
    </>}
  </section>
}

function RecalculateButton() {
  const client = useQueryClient()
  const toast = useToast()
  const run = useMutation({
    mutationFn: runPipeline,
    onSuccess: async result => {
      toast(`Prepočet hotový: ohodnotených ${result.customersScored.toLocaleString('sk-SK')} zákazníkov, algoritmus ${result.algorithm}, ${result.beatsBaseline ? 'model je lepší než baseline' : 'model nie je lepší než baseline'}.`)
      await Promise.all([client.invalidateQueries({ queryKey: ['ml'] }), client.invalidateQueries({ queryKey: ['customers'] })])
    },
  })
  return <div className="max-w-md space-y-3">
    <Button type="button" disabled={run.isPending} onClick={() => run.mutate()}>{run.isPending ? 'Prepočítavam…' : 'Prepočítať predikcie'}</Button>
    {run.isError && <ErrorNotice error={run.error} />}
    {run.isSuccess && run.data.warnings.length > 0 && <ul className="notice list-disc pl-6">{run.data.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>}
  </div>
}

function Distribution() {
  const dist = useQuery(riskDistributionQuery)
  const share = (n: number, total: number) => total ? new Intl.NumberFormat('sk-SK', { style: 'percent', maximumFractionDigits: 1 }).format(n / total) : '—'
  const cards = dist.data && [['high', dist.data.high], ['medium', dist.data.medium], ['low', dist.data.low], ['insufficient_data', dist.data.insufficientData]] as const
  return <section aria-labelledby="ml-dist" className="space-y-4">
    <h2 id="ml-dist" className="text-xl font-semibold">Rozdelenie rizika</h2>
    {dist.isPending && <p role="status">Načítavam rozdelenie…</p>}
    {dist.isError && <ErrorNotice error={dist.error} retry={() => { void dist.refetch() }} />}
    {cards && <><div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{cards.map(([level, count]) => <article className="panel" key={level}><RiskBadge level={level} /><p className="mt-3 text-3xl font-semibold">{count.toLocaleString('sk-SK')}</p><p className="text-sm text-muted-foreground">{share(count, dist.data.total)} z {dist.data.total.toLocaleString('sk-SK')} zákazníkov</p></article>)}</div>
      <p className="text-sm text-muted-foreground">{dist.data.lastPredictionAt ? `Posledná predikcia: ${formatDateTime(dist.data.lastPredictionAt)}` : 'Predikcie zatiaľ neexistujú.'}</p></>}
  </section>
}

function AtRisk() {
  const [risk, setRisk] = useState<string>('high')
  const [offset, setOffset] = useState(0)
  const predictions = useQuery(predictionsQuery(risk, PAGE_SIZE, offset))
  const ids = predictions.data?.items.map(item => item.customerId) ?? []
  const names = useQuery(namesQuery(ids))
  const rows = predictions.data && names.data ? withCustomerNames(predictions.data.items, names.data) : null
  const total = predictions.data?.total ?? 0
  return <section aria-labelledby="ml-risk" className="panel space-y-5">
    <div className="flex flex-wrap items-end justify-between gap-4">
      <div><h2 id="ml-risk" className="text-xl font-semibold">Zákazníci podľa rizika</h2><p className="mt-1 text-sm text-muted-foreground" role="status">{predictions.data ? `Počet zákazníkov: ${total.toLocaleString('sk-SK')}` : ' '}</p></div>
      <div><label htmlFor="ml-risk-filter">Úroveň rizika</label><select id="ml-risk-filter" value={risk} onChange={event => { setRisk(event.target.value); setOffset(0) }}>{riskFilters.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
    </div>
    {(predictions.isPending || (predictions.isSuccess && names.isPending && ids.length > 0)) && <p role="status">Načítavam zákazníkov…</p>}
    {predictions.isError && <ErrorNotice error={predictions.error} retry={() => { void predictions.refetch() }} />}
    {names.isError && <ErrorNotice error={names.error} retry={() => { void names.refetch() }} />}
    {predictions.data?.items.length === 0 && <p className="notice">V tejto skupine nie sú žiadni zákazníci.</p>}
    {rows && rows.length > 0 && <div className="overflow-x-auto rounded-2xl border bg-background"><table>
      <caption className="sr-only">Zákazníci s rizikom odchodu</caption>
      <thead className="border-b"><tr><th scope="col">Zákazník</th><th scope="col">Riziko</th><th scope="col">Zdôvodnenie</th><th scope="col"><span className="sr-only">Akcie</span></th></tr></thead>
      <tbody>{rows.map(row => <tr key={row.customerId}>
        <th scope="row">{row.known ? <Link className="font-semibold text-primary hover:underline" to={`/app/zakaznici/${row.customerId}`}>{row.fullName}</Link> : <span className="font-medium text-muted-foreground">{row.fullName}</span>}</th>
        <td><RiskBadge level={row.riskLevel} /><span className="mt-1 block text-sm font-medium">{formatProbability(row.churnProbability)}</span></td>
        <td className="min-w-64 text-sm">{row.explanation ?? '—'}</td>
        <td>{row.known && <RetentionDialog customerId={row.customerId} customerName={row.fullName} churnProbability={row.churnProbability} />}</td>
      </tr>)}</tbody>
    </table></div>}
    {total > PAGE_SIZE && <nav aria-label="Stránkovanie rizikových zákazníkov" className="flex flex-wrap items-center gap-3">
      <Button variant="outline" disabled={offset === 0 || predictions.isFetching} onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}>Predchádzajúca</Button>
      <span className="text-sm">Strana {offset / PAGE_SIZE + 1} z {Math.ceil(total / PAGE_SIZE)}</span>
      <Button variant="outline" disabled={offset + PAGE_SIZE >= total || predictions.isFetching} onClick={() => setOffset(offset + PAGE_SIZE)}>Nasledujúca</Button>
    </nav>}
  </section>
}

function ModelSection({ model }: { model: { data?: MlModel; isPending: boolean; isError: boolean; error: unknown; refetch: () => unknown } }) {
  const m = model.data
  const top = m?.featureImportances.slice(0, 10) ?? []
  const wins = m && (m.metrics.rocAuc !== null && m.baselineMetrics.rocAuc !== null && m.metrics.rocAuc > m.baselineMetrics.rocAuc)
  return <>
    <section aria-labelledby="ml-model" className="panel space-y-5">
      <h2 id="ml-model" className="text-xl font-semibold">Model a baseline</h2>
      {model.isPending && <p role="status">Načítavam model…</p>}
      {model.isError && <ErrorNotice error={model.error} retry={() => { void model.refetch() }} />}
      {m && <>
        <p className={`notice ${wins ? 'border-primary/30 bg-primary/5' : ''}`} role="status"><strong>{wins ? 'Model je lepší než baseline' : 'Model nie je lepší než baseline'}</strong> (podľa ROC-AUC: {number(m.metrics.rocAuc)} vs. {number(m.baselineMetrics.rocAuc)}).</p>
        <dl className="grid gap-4 text-sm sm:grid-cols-2 xl:grid-cols-4">
          <div><dt className="text-muted-foreground">Algoritmus</dt><dd className="font-semibold">{m.algorithm}</dd></div>
          <div><dt className="text-muted-foreground">Natrénovaný</dt><dd className="font-semibold">{formatDateTime(m.createdAt)}</dd></div>
          <div><dt className="text-muted-foreground">Trénovacie vzorky</dt><dd className="font-semibold">{m.nTrain.toLocaleString('sk-SK')}</dd></div>
          <div><dt className="text-muted-foreground">Validačné vzorky</dt><dd className="font-semibold">{m.nValid.toLocaleString('sk-SK')}</dd></div>
        </dl>
        <div className="overflow-x-auto"><table>
          <caption className="sr-only">Metriky modelu a baseline</caption>
          <thead><tr><th scope="col">Metrika</th><th scope="col">Model ({m.algorithm})</th><th scope="col">Baseline</th></tr></thead>
          <tbody>{metricRows.map(([label, pick]) => <tr key={label}><th scope="row" className="font-medium">{label}</th><td>{number(pick(m.metrics))}</td><td>{number(pick(m.baselineMetrics))}</td></tr>)}</tbody>
        </table></div>
        <div><h3 className="mb-2 font-semibold">Kandidáti</h3><div className="overflow-x-auto"><table>
          <thead><tr><th scope="col">Algoritmus</th><th scope="col">ROC-AUC</th><th scope="col">Precision</th><th scope="col">Recall</th><th scope="col">F1</th></tr></thead>
          <tbody>{m.candidates.map(c => <tr key={c.algorithm}><th scope="row" className="font-medium">{c.algorithm}{c.algorithm === m.algorithm && ' (aktívny)'}</th><td>{number(c.metrics.rocAuc)}</td><td>{number(c.metrics.precision)}</td><td>{number(c.metrics.recall)}</td><td>{number(c.metrics.f1)}</td></tr>)}</tbody>
        </table></div></div>
      </>}
    </section>
    {m && <section aria-labelledby="ml-features" className="panel">
      <h2 id="ml-features" className="mb-5 text-xl font-semibold">Dôležitosť príznakov</h2>
      {top.length === 0 ? <p className="notice">Model neposkytuje dôležitosť príznakov.</p> : <>
        <div className="w-full" style={{ height: Math.max(220, top.length * 34 + 40) }} aria-label="Graf dôležitosti príznakov"><ResponsiveContainer width="100%" height="100%"><BarChart data={top} layout="vertical" margin={{ top: 5, right: 20, bottom: 5, left: 10 }} accessibilityLayer><CartesianGrid strokeDasharray="3 3" /><XAxis type="number" /><YAxis type="category" dataKey="feature" width={190} tick={{ fontSize: 12 }} /><Tooltip formatter={value => [Number(value).toLocaleString('sk-SK', { maximumFractionDigits: 4 }), 'Dôležitosť']} /><Bar dataKey="importance" name="Dôležitosť" fill="#22634f" radius={[0, 4, 4, 0]} /></BarChart></ResponsiveContainer></div>
        <details className="mt-3"><summary className="cursor-pointer text-sm font-medium text-primary">Zobraziť údaje grafu v tabuľke</summary><div className="mt-3 overflow-x-auto"><table><thead><tr><th>Príznak</th><th>Dôležitosť</th></tr></thead><tbody>{top.map(row => <tr key={row.feature}><th className="font-medium">{row.feature}</th><td>{row.importance.toLocaleString('sk-SK', { maximumFractionDigits: 4 })}</td></tr>)}</tbody></table></div></details>
      </>}
    </section>}
  </>
}

function Runs() {
  const runs = useQuery(mlRunsQuery)
  const syncs = runs.data?.syncRuns ?? []
  const trainings = runs.data?.modelVersions ?? []
  const predictions = runs.data?.predictionRuns ?? []
  return <section aria-labelledby="ml-runs" className="panel space-y-6">
    <h2 id="ml-runs" className="text-xl font-semibold">História behov</h2>
    {runs.isPending && <p role="status">Načítavam históriu…</p>}
    {runs.isError && <ErrorNotice error={runs.error} retry={() => { void runs.refetch() }} />}
    {runs.data && syncs.length + trainings.length + predictions.length === 0 && <p className="notice">Zatiaľ neprebehol žiadny beh.</p>}
    {syncs.length > 0 && <div><h3 className="mb-2 font-semibold">Synchronizácia dát</h3><div className="overflow-x-auto"><table>
      <thead><tr><th scope="col">Začiatok</th><th scope="col">Koniec</th><th scope="col">Stav</th><th scope="col">Zákazníci</th><th scope="col">Termíny</th><th scope="col">Chyba</th></tr></thead>
      <tbody>{syncs.map(run => <tr key={run.id}><td className="whitespace-nowrap">{formatDateTime(run.startedAt)}</td><td className="whitespace-nowrap">{run.finishedAt ? formatDateTime(run.finishedAt) : '—'}</td><td>{runStatus[run.status] ?? run.status}</td><td>{run.customersCount ?? '—'}</td><td>{run.appointmentsCount ?? '—'}</td><td className="text-sm text-destructive">{run.error ?? ''}</td></tr>)}</tbody>
    </table></div></div>}
    {trainings.length > 0 && <div><h3 className="mb-2 font-semibold">Tréning modelov</h3><div className="overflow-x-auto"><table>
      <thead><tr><th scope="col">Vytvorený</th><th scope="col">Algoritmus</th><th scope="col">ROC-AUC</th><th scope="col">Baseline ROC-AUC</th><th scope="col">Aktívny</th></tr></thead>
      <tbody>{trainings.map(item => <tr key={item.id}><td className="whitespace-nowrap">{formatDateTime(item.createdAt)}</td><td>{item.algorithm}</td><td>{number(item.rocAuc)}</td><td>{number(item.baselineRocAuc)}</td><td>{item.isActive ? 'Áno' : 'Nie'}</td></tr>)}</tbody>
    </table></div></div>}
    {predictions.length > 0 && <div><h3 className="mb-2 font-semibold">Predikcie</h3><div className="overflow-x-auto"><table>
      <thead><tr><th scope="col">Vytvorené</th><th scope="col">Referenčný dátum</th><th scope="col">Ohodnotení zákazníci</th></tr></thead>
      <tbody>{predictions.map(item => <tr key={item.id}><td className="whitespace-nowrap">{formatDateTime(item.createdAt)}</td><td className="whitespace-nowrap">{formatDateTime(item.referenceDate)}</td><td>{item.customersScored.toLocaleString('sk-SK')}</td></tr>)}</tbody>
    </table></div></div>}
  </section>
}
