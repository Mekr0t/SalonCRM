import { createApiClient } from './http'

export const mlApi = createApiClient(
  `${import.meta.env.VITE_ML_API_URL ?? 'http://localhost:8000'}/ml`,
)
