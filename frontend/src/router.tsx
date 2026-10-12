import { createBrowserRouter } from 'react-router'
import { HomePage, NotFoundPage } from './App'

export const router = createBrowserRouter([
  { path: '/', element: <HomePage /> },
  { path: '*', element: <NotFoundPage /> },
])
