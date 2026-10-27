import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router'
import './index.css'
import { router } from './router'
import { ApiError } from './api/http'
import { ToastProvider } from './components/Toast'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => failureCount < 2 && (!(error instanceof ApiError) || error.status >= 500),
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ToastProvider><RouterProvider router={router} /></ToastProvider>
    </QueryClientProvider>
  </StrictMode>,
)
