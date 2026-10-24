import { createBrowserRouter } from 'react-router'
import { HomePage, NotFoundPage, WorkspacePage } from './App'
import { AuthRoot, AppIndex, LoginPage, BookingPage } from './auth/AuthRoot'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { PublicLayout } from './components/PublicLayout'
import { AppLayout } from './components/AppLayout'

export const router = createBrowserRouter([
  { element: <AuthRoot />, children: [
    { element: <PublicLayout />, children: [
      { path: '/', element: <HomePage /> },
      { path: '/rezervacia', element: <BookingPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '*', element: <NotFoundPage /> },
    ] },
    { element: <ProtectedRoute roles={['Admin', 'User']} />, children: [
      { path: '/app', element: <AppLayout />, children: [
        { index: true, element: <AppIndex /> },
        { path: 'kalendar', element: <WorkspacePage title="Kalendár" /> },
        { path: 'terminy', element: <WorkspacePage title="Termíny" /> },
        { path: 'zakaznici', element: <WorkspacePage title="Zákazníci" /> },
        { element: <ProtectedRoute roles={['Admin']} />, children: [
          { path: 'prehlad', element: <WorkspacePage title="Prehľad" /> },
          { path: 'pouzivatelia', element: <WorkspacePage title="Používatelia" /> },
          { path: 'sluzby', element: <WorkspacePage title="Služby a cenník" /> },
          { path: 'ml', element: <WorkspacePage title="Riziko odchodu" /> },
        ] },
        { path: '*', element: <NotFoundPage /> },
      ] },
    ] },
  ] },
])
