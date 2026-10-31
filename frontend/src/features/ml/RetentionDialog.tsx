import { useId, useRef } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { useToast } from '@/components/toast-context'
import { createRetentionAction } from './api'
import { channelLabels, retentionChannels, retentionSchema, type RetentionValues } from './risk'

export function RetentionDialog({ customerId, customerName, churnProbability }: { customerId: string; customerName?: string; churnProbability?: number | null }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const id = useId()
  const client = useQueryClient()
  const toast = useToast()
  const form = useForm<RetentionValues>({ resolver: zodResolver(retentionSchema), defaultValues: { channel: 'Phone', note: '' } })
  const save = useMutation({
    mutationFn: (values: RetentionValues) => createRetentionAction(customerId, { ...values, churnProbability: churnProbability ?? null }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['customers', 'retention', customerId] })
      toast('Kontakt bol zaznamenaný.')
      dialog.current?.close()
    },
  })
  const error = form.formState.errors.note
  return <>
    <Button type="button" variant="outline" onClick={() => { form.reset(); save.reset(); dialog.current?.showModal() }}>Zaznamenať kontakt</Button>
    <dialog ref={dialog} aria-labelledby={`${id}-title`} onCancel={event => { if (save.isPending) event.preventDefault() }} className="m-auto w-[min(32rem,calc(100%-2rem))] rounded-2xl border bg-card p-6 text-foreground shadow-xl backdrop:bg-black/40">
      <form onSubmit={form.handleSubmit(values => save.mutate(values))} noValidate className="space-y-5">
        <h2 id={`${id}-title`} className="text-xl font-semibold">Zaznamenať kontakt{customerName ? `: ${customerName}` : ''}</h2>
        <fieldset className="space-y-5" disabled={save.isPending}>
          <div><label htmlFor={`${id}-channel`}>Kanál</label><select id={`${id}-channel`} {...form.register('channel')}>{retentionChannels.map(channel => <option key={channel} value={channel}>{channelLabels[channel]}</option>)}</select></div>
          <div><label htmlFor={`${id}-note`}>Poznámka</label><textarea id={`${id}-note`} rows={5} maxLength={2000} aria-invalid={Boolean(error)} aria-describedby={error ? `${id}-note-error` : undefined} {...form.register('note')} />{error && <p className="field-error" id={`${id}-note-error`}>{error.message}</p>}</div>
        </fieldset>
        {save.isError && <ErrorNotice error={save.error} />}
        <div className="flex flex-wrap justify-end gap-3">
          <Button type="button" variant="outline" disabled={save.isPending} onClick={() => dialog.current?.close()}>Zrušiť</Button>
          <Button type="submit" disabled={save.isPending}>{save.isPending ? 'Ukladám…' : 'Uložiť kontakt'}</Button>
        </div>
      </form>
    </dialog>
  </>
}
