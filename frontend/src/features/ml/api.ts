import { mainApi } from '@/api/mainApi'
import { mlApi } from '@/api/mlApi'
import { uniqueIds, type RetentionChannel, type RiskLevel } from './risk'

export interface Metrics { rocAuc: number | null; precision: number; recall: number; f1: number; confusionMatrix: number[][]; positiveRate: number; n: number }
export interface Candidate { algorithm: string; metrics: Metrics }
export interface MlHealth { status: 'ok' | 'degraded'; database: 'ok' | 'unavailable'; activeModel: boolean }
export interface PredictionItem {
  customerId: string
  churnProbability: number | null
  riskLevel: RiskLevel
  explanation: string | null
  createdAt: string | null
  predictionRunId: string | null
}
export interface PredictionDetail extends PredictionItem { features: Record<string, number> | null }
export interface PredictionPage { items: PredictionItem[]; total: number }
export interface HistoryPoint { createdAt: string; churnProbability: number; riskLevel: RiskLevel; predictionRunId: string }
export interface PredictionHistory { customerId: string; items: HistoryPoint[] }
export interface RiskDistribution { low: number; medium: number; high: number; insufficientData: number; total: number; lastPredictionAt: string | null }
export interface MlModel {
  id: string
  createdAt: string
  algorithm: string
  params: Record<string, unknown>
  metrics: Metrics
  baselineMetrics: Metrics
  candidates: Candidate[]
  featureImportances: { feature: string; importance: number }[]
  nTrain: number
  nValid: number
  positiveRate: number
}
export interface PipelineResult {
  syncRunId: string
  modelVersionId: string
  predictionRunId: string
  referenceDate: string
  customersCount: number
  appointmentsCount: number
  customersScored: number
  algorithm: string
  metrics: Metrics
  baselineMetrics: Metrics
  candidates: Candidate[]
  beatsBaseline: boolean
  warnings: string[]
  durationSeconds: number
}
export interface MlRuns {
  syncRuns: { id: string; startedAt: string; finishedAt: string | null; status: string; customersCount: number | null; appointmentsCount: number | null; error: string | null }[]
  modelVersions: { id: string; createdAt: string; algorithm: string; rocAuc: number | null; baselineRocAuc: number | null; isActive: boolean }[]
  predictionRuns: { id: string; createdAt: string; modelVersionId: string; referenceDate: string; customersScored: number }[]
}
export interface LookupCustomer { id: string; fullName: string; email: string }
export interface RetentionAction {
  id: string
  channel: RetentionChannel
  note: string
  churnProbability: number | null
  createdAt: string
  createdBy: { id: string; fullName: string }
}
export interface RetentionInput { channel: RetentionChannel; note: string; churnProbability?: number | null }

// ML is an optional enhancement on most pages: never retry endlessly when it is down.
const optional = { retry: false, staleTime: 30_000 } as const

export const mlHealthQuery = { queryKey: ['ml', 'health'], queryFn: () => mlApi.get<MlHealth>('/health'), ...optional }
export const mlModelQuery = { queryKey: ['ml', 'model'], queryFn: () => mlApi.get<MlModel>('/model'), ...optional }
export const mlRunsQuery = { queryKey: ['ml', 'runs'], queryFn: () => mlApi.get<MlRuns>('/runs?limit=20'), ...optional }
export const riskDistributionQuery = { queryKey: ['ml', 'risk-distribution'], queryFn: () => mlApi.get<RiskDistribution>('/stats/risk-distribution'), ...optional }
export const predictionsQuery = (risk: string, limit: number, offset: number) => ({
  queryKey: ['ml', 'predictions', { risk, limit, offset }],
  queryFn: () => mlApi.get<PredictionPage>(`/predictions?risk=${encodeURIComponent(risk)}&limit=${limit}&offset=${offset}`),
  ...optional,
})
export const predictionQuery = (id: string) => ({
  queryKey: ['ml', 'prediction', id],
  queryFn: () => mlApi.get<PredictionDetail>(`/predictions/${encodeURIComponent(id)}`),
  enabled: Boolean(id),
  ...optional,
})
export const predictionHistoryQuery = (id: string) => ({
  queryKey: ['ml', 'prediction-history', id],
  queryFn: () => mlApi.get<PredictionHistory>(`/predictions/${encodeURIComponent(id)}/history`),
  enabled: Boolean(id),
  ...optional,
})
export function riskLookupQuery(customerIds: string[]) {
  const ids = uniqueIds(customerIds).sort()
  return {
    queryKey: ['ml', 'lookup', ids],
    queryFn: async () => {
      const result = await mlApi.post<{ items: PredictionItem[] }>('/predictions/lookup', { customerIds: ids })
      return new Map(result.items.map(item => [item.customerId, item]))
    },
    enabled: ids.length > 0,
    ...optional,
  }
}
export const runPipeline = () => mlApi.post<PipelineResult>('/pipeline/run')

export const namesQuery = (ids: string[]) => ({
  queryKey: ['customers', 'lookup', ids],
  queryFn: () => mainApi.post<LookupCustomer[]>('/customers/lookup', { ids }),
  enabled: ids.length > 0,
})

export const retentionActionsQuery = (customerId: string) => ({
  queryKey: ['customers', 'retention', customerId],
  queryFn: () => mainApi.get<RetentionAction[]>(`/customers/${encodeURIComponent(customerId)}/retention-actions`),
  enabled: Boolean(customerId),
})
export const createRetentionAction = (customerId: string, input: RetentionInput) =>
  mainApi.post<RetentionAction>(`/customers/${encodeURIComponent(customerId)}/retention-actions`, input)
