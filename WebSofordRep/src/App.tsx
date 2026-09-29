import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AlertTriangle, Boxes, Code2, History, LogOut, PlugZap, ScrollText, ShieldCheck } from 'lucide-react'
import clsx from 'clsx'
import './App.css'
import { api, ApiError, errorText, post, UNAUTHORIZED_EVENT, type AlibabaStatus, type AuthSession } from './api'
import { ConfirmDialog, Spinner, Toasts, type ConfirmRequest, type Toast } from './components/ui'
import { AppContext, type AppContextValue, type TabKey } from './context'
import { ConnectionPage } from './pages/ConnectionPage'
import { JobsPage } from './pages/JobsPage'
import { LogsPage } from './pages/LogsPage'
import { ProductsPage } from './pages/ProductsPage'
import { WorkbenchPage } from './pages/WorkbenchPage'

const tabs: { key: TabKey; label: string; icon: typeof Boxes }[] = [
  { key: 'products', label: '商品', icon: Boxes },
  { key: 'jobs', label: '发布任务', icon: History },
  { key: 'connection', label: '店铺连接', icon: PlugZap },
  { key: 'logs', label: 'API 日志', icon: ScrollText },
  { key: 'workbench', label: 'API 调试', icon: Code2 },
]

const readTab = (): TabKey => {
  const hash = window.location.hash.replace('#', '') as TabKey
  return tabs.some((tab) => tab.key === hash) ? hash : 'products'
}

function App() {
  const [session, setSession] = useState<AuthSession | null>(null)
  const [tab, setTab] = useState<TabKey>(readTab)
  const [alibaba, setAlibaba] = useState<AlibabaStatus | null>(null)
  const [serverDown, setServerDown] = useState(false)
  const [toasts, setToasts] = useState<Toast[]>([])
  const [confirmRequest, setConfirmRequest] = useState<ConfirmRequest | null>(null)
  const toastId = useRef(0)

  const notify = useCallback((kind: Toast['kind'], text: string) => {
    const id = ++toastId.current
    setToasts((current) => [...current.slice(-3), { id, kind, text }])
    window.setTimeout(() => setToasts((current) => current.filter((toast) => toast.id !== id)), kind === 'error' ? 9000 : 4500)
  }, [])

  const refreshAlibaba = useCallback(async () => {
    try {
      setAlibaba(await api<AlibabaStatus>('/api/integrations/alibaba/status'))
      setServerDown(false)
    } catch (err) {
      setAlibaba(null)
      // 0 = network failure, 5xx = proxy could not reach the API; don't mislabel that as missing Alibaba config.
      setServerDown(err instanceof ApiError && (err.status === 0 || err.status >= 500))
    }
  }, [])

  const navigate = useCallback((next: TabKey) => {
    window.location.hash = next
    setTab(next)
  }, [])

  useEffect(() => {
    api<AuthSession>('/api/auth/session')
      .then(setSession)
      .catch(() => setSession({ authenticated: false }))
    const onUnauthorized = () => setSession({ authenticated: false })
    const onHash = () => setTab(readTab())
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    window.addEventListener('hashchange', onHash)
    return () => {
      window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
      window.removeEventListener('hashchange', onHash)
    }
  }, [])

  useEffect(() => {
    if (!session?.authenticated) return
    const timer = window.setTimeout(() => {
      void refreshAlibaba()

      // Returning from the Alibaba OAuth callback.
      const params = new URLSearchParams(window.location.search)
      const result = params.get('alibabaAuth')
      if (!result) return
      if (result === 'success') notify('success', 'Alibaba 店铺授权成功。')
      else notify('error', `Alibaba 授权失败：${params.get('reason') || result}`)
      window.history.replaceState({}, '', `${window.location.pathname}#connection`)
      setTab('connection')
    }, 0)
    return () => window.clearTimeout(timer)
  }, [session?.authenticated, refreshAlibaba, notify])

  const context = useMemo<AppContextValue>(() => ({
    notify,
    confirm: setConfirmRequest,
    alibaba,
    refreshAlibaba,
    navigate,
  }), [notify, alibaba, refreshAlibaba, navigate])

  if (session === null) {
    return <main className="login-shell"><Spinner size={26} /></main>
  }

  if (!session.authenticated) {
    return <LoginScreen onLogin={setSession} />
  }

  const logout = async () => {
    await api('/api/auth/logout', { method: 'POST' }).catch(() => undefined)
    setSession({ authenticated: false })
  }

  return (
    <AppContext.Provider value={context}>
      <main className="shell">
        <header className="topbar">
          <div className="brand">
            <div className="brand-mark">S</div>
            <div>
              <strong>Soford ERP</strong>
              <span>Alibaba 国际站商品发布</span>
            </div>
          </div>
          <nav className="tabs">
            {tabs.map(({ key, label, icon: Icon }) => (
              <button key={key} type="button" className={clsx(tab === key && 'active')} onClick={() => navigate(key)}>
                <Icon size={16} /> {label}
              </button>
            ))}
          </nav>
          <button type="button" className={clsx('integration', alibaba?.ready && 'ok')} onClick={() => navigate('connection')}
            title={alibaba?.account ? `已授权账号 ${alibaba.account}` : '查看店铺连接'}>
            {alibaba?.ready ? <ShieldCheck size={16} /> : <AlertTriangle size={16} />}
            {serverDown ? '服务器未连接' : alibaba?.ready ? '店铺已连接' : alibaba?.hasCredentials ? '店铺未授权' : 'Alibaba 未配置'}
          </button>
          <button type="button" onClick={() => void logout()} title={session.username ? `当前用户 ${session.username}` : undefined}>
            <LogOut size={16} /> 退出
          </button>
        </header>

        {alibaba?.warning && (
          <div className="banner bad top">
            <AlertTriangle size={16} />
            <span>{alibaba.warning}</span>
            <button type="button" onClick={() => navigate('connection')}>去重新授权</button>
          </div>
        )}

        {tab === 'products' && <ProductsPage />}
        {tab === 'jobs' && <JobsPage />}
        {tab === 'connection' && <ConnectionPage />}
        {tab === 'logs' && <LogsPage />}
        {tab === 'workbench' && <WorkbenchPage />}

        {confirmRequest && <ConfirmDialog request={confirmRequest} onClose={() => setConfirmRequest(null)} />}
        <Toasts toasts={toasts} dismiss={(id) => setToasts((current) => current.filter((toast) => toast.id !== id))} />
      </main>
    </AppContext.Provider>
  )
}

function LoginScreen({ onLogin }: { onLogin: (session: AuthSession) => void }) {
  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onLogin(await post<AuthSession>('/api/auth/login', { username: username.trim(), password, remember: true }))
    } catch (err) {
      setError(errorText(err, '登录失败'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login-shell">
      <form className="login-panel" onSubmit={submit}>
        <div className="brand">
          <div className="brand-mark">S</div>
          <div>
            <strong>Soford ERP</strong>
            <span>登录工作台</span>
          </div>
        </div>
        <label>用户名<input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" /></label>
        <label>密码<input value={password} onChange={(e) => setPassword(e.target.value)} type="password" autoComplete="current-password" autoFocus /></label>
        {error && <div className="form-error block">{error}</div>}
        <button type="submit" className="primary" disabled={busy || !password}>
          {busy ? <Spinner /> : <ShieldCheck size={16} />} 登录
        </button>
      </form>
    </main>
  )
}

export default App
