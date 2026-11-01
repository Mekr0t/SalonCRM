import { riskLabel, type RiskLevel } from './risk'

const tones: Record<RiskLevel, string> = {
  high: 'bg-destructive/10 text-destructive ring-destructive/30',
  medium: 'bg-amber-100 text-amber-900 ring-amber-300',
  low: 'bg-primary/10 text-primary ring-primary/30',
  insufficient_data: 'bg-muted text-muted-foreground ring-border',
}
const dots: Record<RiskLevel, string> = { high: 'bg-destructive', medium: 'bg-amber-500', low: 'bg-primary', insufficient_data: 'bg-muted-foreground' }

export function RiskBadge({ level }: { level: RiskLevel }) {
  const safe: RiskLevel = level in tones ? level : 'insufficient_data'
  return <span className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-medium ring-1 ring-inset ${tones[safe]}`}><span aria-hidden="true" className={`size-2 rounded-full ${dots[safe]}`} />{riskLabel(level)}</span>
}
