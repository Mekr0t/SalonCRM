import { Button } from './ui/button'
import { ApiError } from '@/api/http'

function errorMessage(error: unknown) {
  if (error instanceof ApiError) {
    if (error.status === 403) return 'Na túto akciu nemáte oprávnenie.'
    if (error.status === 404) return 'Záznam neexistuje alebo k nemu nemáte prístup.'
    if (error.status === 429) return 'Priveľa pokusov. Počkajte chvíľu a skúste to znova.'
    if (error.status >= 500) return 'Služba je dočasne nedostupná. Skúste to znova neskôr.'
    return error.detail || 'Požiadavku sa nepodarilo spracovať. Skúste to znova.'
  }
  return 'Nepodarilo sa spojiť so serverom. Skontrolujte pripojenie a skúste to znova.'
}
export function ErrorNotice({ error, retry }: { error: unknown; retry?: () => void }) {
  return <div role="alert" className="notice notice-error">
    <p>{errorMessage(error)}</p>
    {retry && <Button variant="outline" onClick={retry} className="mt-3">Skúsiť znova</Button>}
  </div>
}
