import { createContext, useContext } from 'react'
import type { AlibabaStatus } from './api'
import type { ConfirmRequest, Toast } from './components/ui'

export type AppContextValue = {
  notify: (kind: Toast['kind'], text: string) => void
  confirm: (request: ConfirmRequest) => void
  alibaba: AlibabaStatus | null
  refreshAlibaba: () => Promise<void>
  navigate: (tab: TabKey) => void
}

export type TabKey = 'products' | 'jobs' | 'connection' | 'workbench' | 'logs'

export const AppContext = createContext<AppContextValue | null>(null)

export function useApp() {
  const value = useContext(AppContext)
  if (!value) throw new Error('AppContext is missing')
  return value
}
