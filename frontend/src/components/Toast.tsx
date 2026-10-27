import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { CheckCircle2, X } from 'lucide-react'
import { ToastContext } from './toast-context'

export function ToastProvider({ children }: { children: ReactNode }) {
  const [messages, setMessages] = useState<{ id: number; text: string }[]>([])
  const nextId = useRef(0)
  const timers = useRef(new Map<number, ReturnType<typeof setTimeout>>())
  const dismiss = useCallback((id: number) => {
    clearTimeout(timers.current.get(id))
    timers.current.delete(id)
    setMessages(current => current.filter(message => message.id !== id))
  }, [])
  const notify = useCallback((text: string) => {
    const id = ++nextId.current
    setMessages(current => [...current.slice(-3), { id, text }])
    timers.current.set(id, setTimeout(() => dismiss(id), 6000))
  }, [dismiss])
  useEffect(() => {
    const pending = timers.current
    return () => { pending.forEach(clearTimeout); pending.clear() }
  }, [])
  return <ToastContext.Provider value={notify}>
    {children}
    <div className="fixed right-4 bottom-4 z-50 flex w-[min(26rem,calc(100%-2rem))] flex-col gap-2" aria-live="polite" aria-atomic="false">
      {messages.map(message => <div key={message.id} className="flex items-start gap-3 rounded-xl border border-primary/25 bg-card p-4 shadow-lg" role="status">
        <CheckCircle2 className="mt-0.5 shrink-0 text-primary" size={20} aria-hidden="true" />
        <p className="min-w-0 flex-1 text-sm">{message.text}</p>
        <button type="button" onClick={() => dismiss(message.id)} aria-label="Zavrieť oznámenie" className="rounded p-1"><X size={18} aria-hidden="true" /></button>
      </div>)}
    </div>
  </ToastContext.Provider>
}
