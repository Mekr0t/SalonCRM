import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { mainApi } from '@/api/mainApi'
import { authKey, useAuth, type Role } from '@/auth/auth'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/Feedback'
import { ConfirmButton } from '@/components/ConfirmButton'
import { useToast } from '@/components/toast-context'
import { formatDateTime } from '@/lib/format'
import { adminServicesQuery, adminSessionsQuery, adminUsersQuery, type AdminService, type AdminSession, type AdminUser } from './api'

function UserEditor({ user, services, selfId, onSaved, onCancel }: { user: AdminUser | null; services: AdminService[]; selfId: string; onSaved: () => void; onCancel: () => void }) {
  const client = useQueryClient()
  const toast = useToast()
  const [fullName, setFullName] = useState(user?.fullName ?? '')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<Role>(user?.role ?? 'User')
  const [serviceIds, setServiceIds] = useState(user?.services.map(service => service.id) ?? [])
  const save = useMutation({
    mutationFn: () => user
      ? mainApi.put(`/admin/users/${user.id}`, { fullName: fullName.trim(), role, serviceIds })
      : mainApi.post('/admin/users', { email: email.trim(), fullName: fullName.trim(), role, password, serviceIds }),
    onSuccess: async () => { await client.invalidateQueries(); toast(user ? 'Používateľ bol uložený.' : 'Používateľ bol vytvorený.'); setPassword(''); onSaved() },
  })
  function submit(event: FormEvent) { event.preventDefault(); save.mutate() }
  return <form onSubmit={submit} className="panel space-y-5" aria-labelledby="user-editor-title">
    <h2 id="user-editor-title" className="text-xl font-semibold">{user ? `Upraviť: ${user.fullName}` : 'Nový používateľ'}</h2>
    <fieldset disabled={save.isPending} className="space-y-4">
      <div className="grid gap-4 md:grid-cols-2"><div><label htmlFor="user-name">Meno a priezvisko</label><input id="user-name" required maxLength={200} pattern=".*\S.*" value={fullName} onChange={e => setFullName(e.target.value)} autoComplete="name" /></div><div><label htmlFor="user-role">Rola</label><select id="user-role" disabled={user?.id === selfId} className="w-full rounded-lg border bg-background px-3 py-2.5" value={role} onChange={e => setRole(e.target.value as Role)}><option value="User">Pracovník</option><option value="Admin">Administrátor</option></select>{user?.id === selfId && <p className="mt-1 text-xs text-muted-foreground">Vlastnú rolu administrátora nemožno odobrať.</p>}</div></div>
      {user ? <p className="text-sm text-muted-foreground">E-mail: {user.email}</p> : <div className="grid gap-4 md:grid-cols-2"><div><label htmlFor="user-email">E-mail</label><input id="user-email" type="email" required maxLength={254} value={email} onChange={e => setEmail(e.target.value)} autoComplete="off" /></div><div><label htmlFor="user-password">Počiatočné heslo</label><input id="user-password" type="password" required minLength={8} maxLength={256} value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password" /><p className="mt-1 text-xs text-muted-foreground">Aspoň 8 znakov.</p></div></div>}
      <fieldset className="rounded-xl border p-4"><legend className="px-2 text-sm font-medium">Poskytované služby</legend><p className="mb-3 text-sm text-muted-foreground">Pracovník sa ponúkne pri rezervácii iba pre priradené aktívne služby.</p>{services.length === 0 && <p className="text-sm">Najprv vytvorte služby v cenníku.</p>}<div className="grid gap-3 sm:grid-cols-2">{services.map(service => <label key={service.id} className="flex items-center gap-3 text-sm"><input type="checkbox" checked={serviceIds.includes(service.id)} onChange={e => setServiceIds(ids => e.target.checked ? [...ids, service.id] : ids.filter(id => id !== service.id))} />{service.name}{!service.isActive && <span className="text-muted-foreground">(neaktívna)</span>}</label>)}</div></fieldset>
    </fieldset>
    {save.isError && <ErrorNotice error={save.error} />}
    <div className="flex flex-wrap gap-3"><Button type="submit" disabled={save.isPending}>{save.isPending ? 'Ukladám…' : 'Uložiť používateľa'}</Button><Button type="button" variant="outline" disabled={save.isPending} onClick={onCancel}>Zavrieť</Button></div>
  </form>
}

function PasswordReset({ user, onClose }: { user: AdminUser; onClose: () => void }) {
  const client = useQueryClient()
  const toast = useToast()
  const [password, setPassword] = useState('')
  const [valid, setValid] = useState(false)
  const reset = useMutation({
    mutationFn: () => mainApi.post(`/admin/users/${user.id}/password`, { password }),
    onSuccess: async () => { setPassword(''); await client.invalidateQueries({ queryKey: adminSessionsQuery.queryKey }); toast('Heslo bolo zmenené a ostatné sessions používateľa zrušené.'); onClose() },
  })
  return <section className="panel space-y-4" aria-labelledby="password-title"><h2 id="password-title" className="text-xl font-semibold">Nové heslo: {user.fullName}</h2><p className="text-sm text-muted-foreground">Reset zruší všetky sessions používateľa. Pri zmene vlastného hesla zostane táto session aktívna.</p><div className="max-w-md"><label htmlFor="reset-password">Nové heslo</label><input id="reset-password" type="password" minLength={8} maxLength={256} required autoComplete="new-password" value={password} onChange={e => { setPassword(e.target.value); setValid(e.target.validity.valid) }} disabled={reset.isPending} /><p className="mt-1 text-xs text-muted-foreground">Aspoň 8 znakov.</p></div><div className="flex flex-wrap gap-3"><ConfirmButton title="Zmeniť heslo a zrušiť sessions?" description={`Používateľ „${user.fullName}“ sa bude prihlasovať novým heslom. Jeho ostatné prihlásenia budú ukončené.`} disabled={!valid || reset.isPending} onConfirm={() => reset.mutateAsync()}>Potvrdiť nové heslo</ConfirmButton><Button variant="outline" onClick={onClose} disabled={reset.isPending}>Zavrieť</Button></div></section>
}

function Sessions() {
  const client = useQueryClient()
  const toast = useToast()
  const sessions = useQuery(adminSessionsQuery)
  const [userId, setUserId] = useState('')
  const revoke = useMutation({
    mutationFn: (session: AdminSession) => mainApi.delete(`/admin/sessions/${session.id}`),
    onSuccess: async (_, session) => { toast('Session bola zrušená.'); await client.invalidateQueries({ queryKey: adminSessionsQuery.queryKey }); if (session.isCurrent) await client.invalidateQueries({ queryKey: authKey }) },
  })
  const people = Array.from(new Map(sessions.data?.map(session => [session.userId, session.userFullName])).entries())
  const visible = sessions.data?.filter(session => !userId || session.userId === userId)
  return <section className="panel space-y-5" aria-labelledby="sessions-title">
    <div className="flex flex-wrap items-end justify-between gap-4"><div><h2 id="sessions-title" className="text-xl font-semibold">Aktívne sessions</h2><p className="mt-1 text-sm text-muted-foreground">Serverové prihlásenia. Zrušená session stratí prístup pri ďalšom overení.</p></div><Button variant="outline" disabled={sessions.isFetching} onClick={() => { void sessions.refetch() }}>Obnoviť sessions</Button></div>
    <div className="max-w-sm"><label htmlFor="session-user">Používateľ</label><select id="session-user" className="w-full rounded-lg border bg-background px-3 py-2.5" value={userId} onChange={e => setUserId(e.target.value)}><option value="">Všetci používatelia</option>{people.map(([id, name]) => <option key={id} value={id}>{name}</option>)}</select></div>
    {sessions.isPending && <p role="status">Načítavam sessions…</p>}{sessions.isError && <ErrorNotice error={sessions.error} retry={() => { void sessions.refetch() }} />}
    {visible?.length === 0 && <p className="notice">Žiadne aktívne sessions pre tento výber.</p>}
    {visible && visible.length > 0 && <div className="overflow-x-auto"><table className="min-w-[48rem]"><thead><tr><th>Používateľ</th><th>Vytvorená / posledná aktivita</th><th>Pripojenie</th><th>Akcia</th></tr></thead><tbody>{visible.map(session => <tr className="align-top" key={session.id}><td><p className="font-medium">{session.userFullName}{session.isCurrent && <span className="ml-2 text-primary">(táto session)</span>}</p><p className="mt-1 text-muted-foreground">{session.userEmail}</p></td><td><p>{formatDateTime(session.createdAt)}</p><p className="mt-1 text-muted-foreground">{formatDateTime(session.lastSeenAt)}</p></td><td className="max-w-xs break-words"><p>{session.ipAddress || 'IP adresa nie je dostupná'}</p><p className="mt-1 text-xs text-muted-foreground">{session.userAgent || 'Prehliadač nie je dostupný'}</p></td><td><ConfirmButton title={session.isCurrent ? 'Odhlásiť túto session?' : 'Zrušiť session?'} description={session.isCurrent ? 'Táto akcia vás odhlási z aplikácie.' : `Používateľ „${session.userFullName}“ stratí prístup z tohto prihlásenia.`} disabled={revoke.isPending} variant="destructive" onConfirm={() => revoke.mutateAsync(session)}>Zrušiť session</ConfirmButton></td></tr>)}</tbody></table></div>}
  </section>
}

export function UsersPage() {
  const client = useQueryClient()
  const toast = useToast()
  const { user: currentUser } = useAuth()
  const users = useQuery(adminUsersQuery)
  const services = useQuery(adminServicesQuery)
  const [editor, setEditor] = useState<AdminUser | 'new' | null>(null)
  const [passwordUser, setPasswordUser] = useState<AdminUser | null>(null)
  const [search, setSearch] = useState('')
  const [warning, setWarning] = useState<{ user: string; future: number } | null>(null)
  const activation = useMutation({
    mutationFn: async (user: AdminUser) => {
      if (user.isActive) return mainApi.post<{ futureAppointments: number }>(`/admin/users/${user.id}/deactivate`)
      await mainApi.post(`/admin/users/${user.id}/activate`)
      return { futureAppointments: 0 }
    },
    onSuccess: async (result, user) => { if (user.isActive && result.futureAppointments > 0) setWarning({ user: user.fullName, future: result.futureAppointments }); await client.invalidateQueries(); toast(user.isActive ? 'Používateľ bol deaktivovaný a jeho sessions zrušené.' : 'Používateľ bol aktivovaný.') },
  })
  const normalized = search.trim().toLocaleLowerCase('sk-SK')
  const visible = users.data?.filter(user => `${user.fullName} ${user.email}`.toLocaleLowerCase('sk-SK').includes(normalized))
  return <section className="space-y-7">
    <header className="flex flex-wrap items-end justify-between gap-4"><div><p className="eyebrow">Administrácia</p><h1 className="mt-2 text-3xl font-semibold">Používatelia</h1><p className="mt-2 text-muted-foreground">Tím salónu, poskytované služby a prihlásenia.</p></div><Button onClick={() => { setPasswordUser(null); setEditor('new') }} disabled={!services.isSuccess}>Pridať používateľa</Button></header>
    {warning && <div className="notice space-y-3" role="alert"><p><strong>{warning.user}</strong> má po deaktivácii stále {warning.future} budúcich naplánovaných termínov. Termíny zostali zachované; priraďte ich aktívnemu pracovníkovi.</p><div className="flex flex-wrap items-center gap-4"><Link className="font-semibold text-primary underline" to="/app/terminy">Prejsť na termíny</Link><Button variant="outline" onClick={() => setWarning(null)}>Rozumiem</Button></div></div>}
    {services.isError && <ErrorNotice error={services.error} retry={() => { void services.refetch() }} />}
    {editor && services.data && <UserEditor key={editor === 'new' ? 'new' : editor.id} user={editor === 'new' ? null : editor} services={services.data} selfId={currentUser?.id ?? ''} onSaved={() => setEditor(null)} onCancel={() => setEditor(null)} />}
    {passwordUser && <PasswordReset key={passwordUser.id} user={passwordUser} onClose={() => setPasswordUser(null)} />}
    <div className="max-w-md"><label htmlFor="user-search">Vyhľadať podľa mena alebo e-mailu</label><input id="user-search" type="search" value={search} onChange={e => setSearch(e.target.value)} /></div>
    {users.isPending && <p role="status">Načítavam používateľov…</p>}{users.isError && <ErrorNotice error={users.error} retry={() => { void users.refetch() }} />}
    {visible?.length === 0 && <p className="notice">Žiadny používateľ nezodpovedá vyhľadávaniu.</p>}
    <div className="grid gap-4 xl:grid-cols-2">{visible?.map(user => <article className="panel space-y-4" key={user.id}>
      <div className="flex items-start justify-between gap-3"><div><h2 className="text-lg font-semibold">{user.fullName}{user.id === currentUser?.id && <span className="ml-2 text-sm text-primary">(vy)</span>}</h2><p className="mt-1 break-all text-sm text-muted-foreground">{user.email}</p></div><span className={`rounded-full px-3 py-1 text-xs font-medium ${user.isActive ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground'}`}>{user.isActive ? 'Aktívny' : 'Neaktívny'}</span></div>
      <p className="text-sm">{user.role === 'Admin' ? 'Administrátor' : 'Pracovník'} · Posledné prihlásenie: {user.lastLoginAt ? formatDateTime(user.lastLoginAt) : 'zatiaľ žiadne'}</p>
      <p className="text-sm text-muted-foreground">Služby: {user.services.length ? user.services.map(service => service.name).join(', ') : 'žiadne priradené služby'}</p>
      <div className="flex flex-wrap gap-3 border-t pt-4"><Button variant="outline" disabled={!services.isSuccess || activation.isPending} onClick={() => { setPasswordUser(null); setEditor(user) }}>Upraviť</Button><Button variant="outline" onClick={() => { setEditor(null); setPasswordUser(user) }}>Reset hesla</Button><ConfirmButton title={user.isActive ? 'Deaktivovať používateľa?' : 'Aktivovať používateľa?'} description={user.isActive ? `Používateľ „${user.fullName}“ sa nebude môcť prihlásiť a jeho sessions sa okamžite zrušia. Budúce termíny zostanú zachované a bude potrebné ich preplánovať.` : `Používateľ „${user.fullName}“ sa bude môcť znovu prihlásiť.`} disabled={activation.isPending || (user.isActive && user.id === currentUser?.id)} variant={user.isActive ? 'destructive' : 'outline'} onConfirm={() => activation.mutateAsync(user)}>{user.isActive ? 'Deaktivovať' : 'Aktivovať'}</ConfirmButton></div>
    </article>)}</div>
    <Sessions />
  </section>
}
