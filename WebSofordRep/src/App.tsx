import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AlertTriangle, Boxes, ChevronDown, ClipboardList, Code2, History, KeyRound, LayoutDashboard, LogOut, PlugZap, ScrollText, ShieldCheck, Users } from 'lucide-react'
import clsx from 'clsx'
import './App.css'
import { api, ApiError, errorText, post, UNAUTHORIZED_EVENT, type AlibabaStatus, type AuthSession } from './api'
import { ConfirmDialog, Modal, Spinner, Toasts, type ConfirmRequest, type Toast } from './components/ui'
import { AppContext, type AppContextValue, type TabKey } from './context'
import { ConnectionPage } from './pages/ConnectionPage'
import { AuditPage } from './pages/AuditPage'
import { OverviewPage } from './pages/OverviewPage'
import { UsersPage } from './pages/UsersPage'
import { JobsPage } from './pages/JobsPage'
import { LogsPage } from './pages/LogsPage'
import { ProductsPage } from './pages/ProductsPage'
import { WorkbenchPage } from './pages/WorkbenchPage'

// Troubleshooting tools sit behind 「高级」 so day-to-day users are not confronted with raw API calls.
// Admin-only pages manage the whole store (authorization, accounts, users, raw API access).
const tabs: { key: TabKey; label: string; icon: typeof Boxes; advanced?: boolean; admin?: boolean }[] = [
  { key: 'overview', label: '概览', icon: LayoutDashboard },
  { key: 'products', label: '商品', icon: Boxes },
  { key: 'jobs', label: '后台任务', icon: History },
  { key: 'connection', label: '店铺连接', icon: PlugZap, admin: true },
  { key: 'users', label: '用户管理', icon: Users, advanced: true, admin: true },
  { key: 'audit', label: '操作记录', icon: ClipboardList, advanced: true },
  { key: 'logs', label: 'API 日志', icon: ScrollText, advanced: true, admin: true },
  { key: 'workbench', label: 'API 调试', icon: Code2, advanced: true, admin: true },
]

/** Before the certificate exists the site runs on plain HTTP; passwords and data then cross the network unencrypted. */
const insecure = window.location.protocol === 'http:' && !['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname)
const insecureText = '当前是 HTTP 明文连接，密码和数据可能被窃听。请尽快为域名启用 HTTPS（需先完成 ICP 备案），在此之前不要在公共网络中登录。'

const readTab = (): TabKey => {
  const hash = window.location.hash.replace('#', '').split('/')[0] as TabKey
  return tabs.some((tab) => tab.key === hash) ? hash : 'overview'
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

  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [visit, setVisit] = useState(0)

  const navigate = useCallback((next: TabKey, filter?: string) => {
    window.location.hash = filter ? `${next}/${filter}` : next
    setTab(next)
    setAdvancedOpen(false)
    // Remount the page so a filter passed from the overview applies even when the tab is already open.
    setVisit((value) => value + 1)
  }, [])

  useEffect(() => {
    api<AuthSession>('/api/auth/session')
      .then(setSession)
      .catch(() => setSession({ authenticated: false }))
    const onUnauthorized = () => setSession((current) => (current?.authenticated ? { ...current, expired: true } : { authenticated: false }))
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

  const [passwordOpen, setPasswordOpen] = useState(false)
  const isAdmin = session?.role === 'Admin'
  const signedIn = session?.authenticated === true
  const userName = session?.username ?? ''
  const user = useMemo(() => (signedIn ? { name: userName, isAdmin } : null), [signedIn, userName, isAdmin])

  const context = useMemo<AppContextValue>(() => ({
    notify,
    confirm: setConfirmRequest,
    alibaba,
    refreshAlibaba,
    navigate,
    user,
  }), [notify, alibaba, refreshAlibaba, navigate, user])

  if (session === null) {
    return <main className="login-shell"><Spinner size={26} /></main>
  }

  if (!session.authenticated) {
    return <LoginScreen onLogin={setSession} />
  }

  const allowedTabs = tabs.filter((x) => !x.admin || isAdmin)

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
            {allowedTabs.filter((x) => !x.advanced).map(({ key, label, icon: Icon }) => (
              <button key={key} type="button" className={clsx(tab === key && 'active')} onClick={() => navigate(key)}>
                <Icon size={16} /> {label}
              </button>
            ))}
            <div className="more">
              <button type="button" className={clsx(tabs.some((x) => x.advanced && x.key === tab) && 'active')} onClick={() => setAdvancedOpen((open) => !open)} aria-expanded={advancedOpen}>
                高级 <ChevronDown size={14} />
              </button>
              {advancedOpen && (
                <div className="more-menu" role="menu">
                  {allowedTabs.filter((x) => x.advanced).map(({ key, label, icon: Icon }) => (
                    <button key={key} type="button" role="menuitem" onClick={() => navigate(key)}><Icon size={16} /> {label}</button>
                  ))}
                </div>
              )}
            </div>
          </nav>
          <button type="button" className={clsx('integration', alibaba?.ready && 'ok')} onClick={() => isAdmin && navigate('connection')}
            title={alibaba?.account ? `已授权账号 ${alibaba.account}` : '查看店铺连接'}>
            {alibaba?.ready ? <ShieldCheck size={16} /> : <AlertTriangle size={16} />}
            {serverDown ? '服务器未连接' : alibaba?.ready ? '店铺已连接' : alibaba?.hasCredentials ? '店铺未授权' : 'Alibaba 未配置'}
          </button>
          <button type="button" onClick={() => session.canChangePassword && setPasswordOpen(true)} title={session.canChangePassword ? '修改密码' : '内置管理员的密码在服务器配置中修改'}>
            <KeyRound size={16} /> {session.username}{isAdmin ? '（管理员）' : ''}
          </button>
          <button type="button" onClick={() => void logout()}>
            <LogOut size={16} /> 退出
          </button>
        </header>

        {insecure && (
          <div className="banner bad top">
            <AlertTriangle size={16} />
            <span>{insecureText}</span>
          </div>
        )}

        {alibaba?.warning && (
          <div className="banner bad top">
            <AlertTriangle size={16} />
            <span>{alibaba.warning}</span>
            {isAdmin && <button type="button" onClick={() => navigate('connection')}>去重新授权</button>}
          </div>
        )}

        {tab === 'overview' && <OverviewPage key={visit} />}
        {tab === 'products' && <ProductsPage key={visit} />}
        {tab === 'jobs' && <JobsPage />}
        {tab === 'connection' && isAdmin && <ConnectionPage />}
        {tab === 'users' && isAdmin && <UsersPage />}
        {tab === 'audit' && <AuditPage />}
        {tab === 'logs' && isAdmin && <LogsPage />}
        {tab === 'workbench' && isAdmin && <WorkbenchPage />}
        {!allowedTabs.some((x) => x.key === tab) && <div className="page"><p className="muted">这个页面只有管理员可以使用。</p></div>}
        {passwordOpen && <PasswordDialog onClose={() => setPasswordOpen(false)} notify={notify} />}

        {session.expired && <LoginScreen overlay onLogin={setSession} />}
        {confirmRequest && <ConfirmDialog request={confirmRequest} onClose={() => setConfirmRequest(null)} />}
        <Toasts toasts={toasts} dismiss={(id) => setToasts((current) => current.filter((toast) => toast.id !== id))} />
      </main>
    </AppContext.Provider>
  )
}

function PasswordDialog({ onClose, notify }: { onClose: () => void; notify: (kind: Toast['kind'], text: string) => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [busy, setBusy] = useState(false)
  const problem = next && next.length < 10 ? '新密码至少 10 个字符' : again && again !== next ? '两次输入的新密码不一致' : null

  const save = async () => {
    setBusy(true)
    try {
      await post('/api/auth/password', { current, next })
      // A new password ends every session, including this one.
      notify('success', '密码已修改，请用新密码重新登录。')
      window.location.reload()
    } catch (err) {
      notify('error', errorText(err, '修改密码失败'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="修改密码" onClose={onClose}
      footer={<>
        <button type="button" onClick={onClose}>取消</button>
        <button type="button" className="primary" disabled={busy || !current || !next || next !== again || !!problem} onClick={() => void save()}>
          {busy ? <Spinner /> : <KeyRound size={16} />} 修改
        </button>
      </>}>
      <div className="form-grid">
        <label className="field">当前密码<input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" /></label>
        <label className="field">新密码<input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" placeholder="至少 10 个字符" /></label>
        <label className="field">再输入一次<input type="password" value={again} onChange={(e) => setAgain(e.target.value)} autoComplete="new-password" /></label>
      </div>
      {problem && <p className="warn">{problem}</p>}
    </Modal>
  )
}

function LoginScreen({ onLogin, overlay }: { onLogin: (session: AuthSession) => void; overlay?: boolean }) {
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
    <main className={clsx('login-shell', overlay && 'overlay')}>
      <form className="login-panel" onSubmit={submit}>
        <div className="brand">
          <div className="brand-mark">S</div>
          <div>
            <strong>Soford ERP</strong>
            <span>{overlay ? '登录已过期，重新登录后可继续当前操作' : '登录工作台'}</span>
          </div>
        </div>
        <label>用户名<input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" /></label>
        <label>密码<input value={password} onChange={(e) => setPassword(e.target.value)} type="password" autoComplete="current-password" autoFocus /></label>
        {insecure && <div className="form-error block">{insecureText}</div>}
        {error && <div className="form-error block">{error}</div>}
        <button type="submit" className="primary" disabled={busy || !password}>
          {busy ? <Spinner /> : <ShieldCheck size={16} />} 登录
        </button>
      </form>
    </main>
  )
}

export default App
