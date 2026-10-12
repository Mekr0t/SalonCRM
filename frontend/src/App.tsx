import { Button } from '@/components/ui/button'

export function HomePage() {
  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-4 p-4 text-center">
      <h1 className="text-3xl font-semibold">SalonCRM</h1>
      <p className="text-muted-foreground">Kostra aplikácie je pripravená.</p>
      <Button>Pokračovať</Button>
    </main>
  )
}

export function NotFoundPage() {
  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-4 p-4 text-center">
      <h1 className="text-3xl font-semibold">Stránka nenájdená</h1>
    </main>
  )
}
