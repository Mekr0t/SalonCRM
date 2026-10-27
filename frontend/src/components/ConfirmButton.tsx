import { useId, useRef, useState, type ReactNode } from 'react'
import { Button } from './ui/button'
import { ErrorNotice } from './Feedback'

export function ConfirmButton({ children, title, description, onConfirm, disabled, variant = 'outline' }: {
  children: ReactNode
  title: string
  description: string
  onConfirm: () => Promise<unknown>
  disabled?: boolean
  variant?: 'outline' | 'destructive' | 'default'
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<unknown>(null)
  async function confirm() {
    setError(null)
    setPending(true)
    try { await onConfirm(); dialog.current?.close() }
    catch (failure) { setError(failure) }
    finally { setPending(false) }
  }
  return <>
    <Button type="button" variant={variant} disabled={disabled || pending} onClick={() => { setError(null); dialog.current?.showModal() }}>{children}</Button>
    <dialog ref={dialog} aria-labelledby={titleId} aria-describedby={descriptionId} onCancel={event => { if (pending) event.preventDefault() }} className="m-auto w-[min(30rem,calc(100%-2rem))] rounded-2xl border bg-card p-6 text-foreground shadow-xl backdrop:bg-black/40">
      <h2 id={titleId} className="text-xl font-semibold">{title}</h2>
      <p id={descriptionId} className="mt-3 text-sm leading-relaxed text-muted-foreground">{description}</p>
      {error != null && <div className="mt-4"><ErrorNotice error={error} /></div>}
      <div className="mt-6 flex flex-wrap justify-end gap-3">
        <Button type="button" variant="outline" autoFocus disabled={pending} onClick={() => dialog.current?.close()}>Späť</Button>
        <Button type="button" variant={variant === 'destructive' ? 'destructive' : 'default'} disabled={pending} onClick={() => { void confirm() }}>{pending ? 'Spracúvam…' : 'Potvrdiť'}</Button>
      </div>
    </dialog>
  </>
}
