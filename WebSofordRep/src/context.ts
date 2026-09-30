import { createContext, useContext } from 'react'
import type { AlibabaStatus } from './api'
import type { ConfirmRequest, Toast } from './components/ui'

export type AppContextValue = {
  notify: (kind: Toast['kind'], text: string) => void
  confirm: (request: ConfirmRequest) => void
  alibaba: AlibabaStatus | null
  refreshAlibaba: () => Promise<void>
  /** `filter` preselects a product list filter, e.g. navigate('products', 'Failed'). */
  navigate: (tab: TabKey, filter?: string) => void
  /** The signed-in person; operators only see their own accounts' products and cannot manage the store. */
  user: { name: string; isAdmin: boolean } | null
}

export type TabKey = 'overview' | 'products' | 'jobs' | 'connection' | 'workbench' | 'logs' | 'users' | 'audit'

/** The hash is "#tab" or "#tab/filter". */
export const hashFilter = () => window.location.hash.replace('#', '').split('/')[1] ?? ''

export const AppContext = createContext<AppContextValue | null>(null)

export function useApp() {
  const value = useContext(AppContext)
  if (!value) throw new Error('AppContext is missing')
  return value
}
