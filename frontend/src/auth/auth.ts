import { createContext, useContext } from 'react'

export type Role = 'Admin' | 'User'
export interface CurrentUser {
  id: string
  email: string
  fullName: string
  role: Role
}
export const authKey = ['auth', 'me'] as const
export const homeForRole = (role: Role) => role === 'Admin' ? '/app/prehlad' : '/app/kalendar'

interface AuthContextValue {
  user: CurrentUser | null
  isLoading: boolean
  error: Error | null
  retry: () => void
  login: (email: string, password: string) => Promise<CurrentUser>
  logout: () => Promise<void>
}
export const AuthContext = createContext<AuthContextValue | null>(null)
export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth requires AuthProvider')
  return context
}
