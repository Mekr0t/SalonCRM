import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { homeForRole, useAuth } from '@/auth/auth'
import { ErrorNotice } from '@/components/Feedback'
import { Button } from '@/components/ui/button'

const schema = z.object({
  email: z.string().trim().pipe(z.email('Zadajte platný e-mail.').max(254)),
  password: z.string().min(1, 'Zadajte heslo.').max(256),
})
type LoginValues = z.infer<typeof schema>

export function LoginPage() {
  const { user, isLoading, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const { register, handleSubmit, setError, formState: { errors, isSubmitting } } = useForm<LoginValues>({ resolver: zodResolver(schema) })
  const [failure, setFailure] = useState<unknown>(null)
  async function submit(values: LoginValues) {
    setFailure(null)
    try {
      const current = await login(values.email.trim(), values.password)
      await navigate(homeForRole(current.role), { replace: true })
    } catch (error) {
      setFailure(error)
      setError('password', { type: 'server' }, { shouldFocus: true })
    }
  }
  if (isLoading) return <p role="status" className="page-container py-16">Overujem prihlásenie…</p>
  if (user) return <Navigate to={homeForRole(user.role)} replace />
  return <main className="mx-auto max-w-lg px-5 py-12 md:py-20">
    <p className="eyebrow">Pracovný priestor</p><h1 className="mt-3 text-3xl font-semibold">Vitajte späť</h1>
    <p className="mt-3 text-muted-foreground">Prihlásenie pre pracovníkov a administrátora salónu.</p>
    <form className="panel mt-7 space-y-5" onSubmit={handleSubmit(submit)} noValidate>
      {location.state?.sessionExpired && failure == null && <p role="status" className="notice">Vaše prihlásenie vypršalo. Prihláste sa znova.</p>}
      {failure != null && <ErrorNotice error={failure} />}
      <div><label htmlFor="email">E-mail</label><input id="email" type="email" autoComplete="username" maxLength={254} {...register('email')} aria-invalid={!!errors.email} aria-describedby={errors.email ? 'email-error' : undefined} />{errors.email && <p className="field-error" id="email-error">{errors.email.message}</p>}</div>
      <div><label htmlFor="password">Heslo</label><input id="password" type="password" autoComplete="current-password" maxLength={256} {...register('password')} aria-invalid={!!errors.password} aria-describedby={errors.password?.message ? 'password-error' : undefined} />{errors.password?.message && <p className="field-error" id="password-error">{errors.password.message}</p>}</div>
      <Button type="submit" className="w-full" size="lg" disabled={isSubmitting}>{isSubmitting ? 'Prihlasujem…' : 'Prihlásiť sa'}</Button>
    </form>
  </main>
}
