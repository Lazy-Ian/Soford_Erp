import { useCallback, useState } from 'react'
import { AlertTriangle, CheckCircle2, ExternalLink, KeyRound, RefreshCw, ShieldCheck, Stethoscope, Timer, Unplug, XCircle } from 'lucide-react'
import clsx from 'clsx'
import { api, errorText, post, type AutomationStatus, type DiagnosticCheck } from '../api'
import { Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

export function ConnectionPage() {
  const { notify, confirm, alibaba, refreshAlibaba } = useApp()
  const [checks, setChecks] = useState<DiagnosticCheck[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [code, setCode] = useState('')

  const runDiagnostics = useCallback(async () => {
    setBusy('diagnostics')
    try {
      setChecks(await api<DiagnosticCheck[]>('/api/system/diagnostics'))
    } catch (err) {
      notify('error', errorText(err, '自检失败'))
    } finally {
      setBusy(null)
    }
  }, [notify])

  useInitialLoad(runDiagnostics)

  const run = async (key: string, action: () => Promise<void>) => {
    setBusy(key)
    try {
      await action()
    } catch (err) {
      notify('error', errorText(err))
    } finally {
      setBusy(null)
    }
  }

  const authorize = () =>
    run('authorize', async () => {
      const result = await api<{ url: string }>('/api/integrations/alibaba/oauth/url')
      window.location.assign(result.url)
    })

  const exchangeCode = () =>
    run('code', async () => {
      const result = await post<{ accessTokenExpiresAt: string; account?: string }>('/api/integrations/alibaba/token/create', { code })
      notify('success', `授权成功${result.account ? `（${result.account}）` : ''}，有效至 ${formatDate(result.accessTokenExpiresAt)}。`)
      setCode('')
      await refreshAlibaba()
      await runDiagnostics()
    })

  const refreshToken = () =>
    run('refresh', async () => {
      const result = await post<{ accessTokenExpiresAt: string }>('/api/integrations/alibaba/token/refresh')
      notify('success', `Token 已续期，有效至 ${formatDate(result.accessTokenExpiresAt)}。`)
      await refreshAlibaba()
      await runDiagnostics()
    })

  const disconnect = () =>
    confirm({
      title: '断开店铺授权',
      danger: true,
      confirmText: '断开',
      message: '删除本地保存的 Alibaba Token。之后需要重新授权才能发布和同步。',
      onConfirm: () =>
        void run('disconnect', async () => {
          await api('/api/integrations/alibaba/token', { method: 'DELETE' })
          notify('success', '已断开授权。')
          await refreshAlibaba()
          await runDiagnostics()
        }),
    })

  const allOk = checks?.every((x) => x.status === 'ok')

  return (
    <div className="page narrow">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>连接自检</strong>
            <span>逐项检查 Alibaba 开放平台的配置、网络、签名和授权。</span>
          </div>
          <button type="button" onClick={() => void runDiagnostics()} disabled={busy === 'diagnostics'}>
            {busy === 'diagnostics' ? <Spinner /> : <Stethoscope size={16} />} 重新检查
          </button>
        </div>
        {checks === null ? <div className="empty small"><Spinner size={20} /></div> : (
          <ul className="checks">
            {checks.map((check) => (
              <li key={check.key} className={check.status}>
                {check.status === 'ok' ? <CheckCircle2 size={18} /> : check.status === 'warn' ? <AlertTriangle size={18} /> : <XCircle size={18} />}
                <div>
                  <b>{check.title}</b>
                  <span>{check.message}</span>
                  {check.fix && check.status !== 'ok' && <small>处理：{check.fix}</small>}
                  {check.fix && check.status === 'ok' && check.key === 'callback' && <small>{check.fix}</small>}
                </div>
              </li>
            ))}
          </ul>
        )}
        {allOk && <p className="good">全部检查通过，可以正常发布商品。</p>}
      </section>

      <section className="card">
        <div className="card-head">
          <div>
            <strong>店铺授权</strong>
            <span>授权后系统代表卖家调用 Alibaba 接口；Token 到期前会自动续期。</span>
          </div>
          <Pill tone={alibaba?.ready ? 'good' : 'bad'}>{alibaba?.ready ? '已授权' : alibaba?.hasToken ? '授权已过期' : '未授权'}</Pill>
        </div>

        {alibaba?.hasToken && (
          <dl className="facts">
            {alibaba.account && <><dt>账号</dt><dd>{alibaba.account}</dd></>}
            <dt>access_token 到期</dt><dd>{formatDate(alibaba.accessTokenExpiresAt)}</dd>
            <dt>refresh_token 到期</dt><dd>{formatDate(alibaba.refreshTokenExpiresAt)}</dd>
          </dl>
        )}

        <div className="steps">
          <div className="step">
            <b>方式一：一键授权（服务器环境）</b>
            <p className="muted">跳转到 Alibaba 登录并授权，完成后自动回到本系统。回调地址 <code>{alibaba?.callbackUrl}</code> 必须已在 App Console 登记。</p>
            <button type="button" className="primary" onClick={() => void authorize()} disabled={!alibaba?.hasCredentials || busy === 'authorize'}>
              {busy === 'authorize' ? <Spinner /> : <ExternalLink size={16} />} 授权 Alibaba 店铺
            </button>
          </div>
          <div className="step">
            <b>方式二：粘贴回调链接（本地环境）</b>
            <p className="muted">在 Alibaba 授权后，浏览器会跳转到回调地址（即使页面打不开也没关系），复制地址栏中的完整链接或其中的 <code>code</code> 粘贴到这里。code 30 分钟内有效，只能使用一次。</p>
            <div className="inline-form">
              <input value={code} onChange={(e) => setCode(e.target.value)} placeholder="https://…/openapi/callback?code=…  或  code" />
              <button type="button" className="primary" onClick={() => void exchangeCode()} disabled={!code.trim() || busy === 'code' || !alibaba?.hasCredentials}>
                {busy === 'code' ? <Spinner /> : <KeyRound size={16} />} 换取 Token
              </button>
            </div>
          </div>
        </div>

        <div className={clsx('actions', 'left')}>
          <button type="button" onClick={() => void refreshToken()} disabled={!alibaba?.hasToken || busy === 'refresh'}>
            {busy === 'refresh' ? <Spinner /> : <RefreshCw size={16} />} 立即续期
          </button>
          <button type="button" className="danger-text" onClick={disconnect} disabled={!alibaba?.hasToken}><Unplug size={16} /> 断开授权</button>
        </div>
      </section>

      <AutomationCard />

      <section className="card">
        <div className="card-head">
          <div>
            <strong>配置说明</strong>
            <span>密钥只保存在服务器配置中，不会在页面上显示。</span>
          </div>
          <ShieldCheck size={18} className="muted" />
        </div>
        <ul className="plain">
          <li>本地：在项目根目录 <code>.env</code> 中设置 <code>Alibaba__AppKey</code>、<code>Alibaba__AppSecret</code>，重启 API 生效。</li>
          <li>服务器：编辑 <code>/opt/soford-erp/env/soford-api.env</code>，然后 <code>sudo systemctl restart soford-erp-api</code>。</li>
          <li>网关：<code>{alibaba?.gatewayUrl}</code></li>
          <li>如果接口提示无权限，请在 Alibaba App Console 为应用申请对应的 API 权限包。</li>
        </ul>
      </section>
    </div>
  )
}

function AutomationCard() {
  const { notify, refreshAlibaba } = useApp()
  const [status, setStatus] = useState<AutomationStatus | null>(null)
  const [running, setRunning] = useState(false)

  const load = useCallback(async () => {
    try {
      setStatus(await api<AutomationStatus>('/api/system/automation'))
    } catch (err) {
      notify('error', errorText(err, '读取自动同步状态失败'))
    }
  }, [notify])

  useInitialLoad(load)

  const runNow = async () => {
    setRunning(true)
    try {
      const result = await post<AutomationStatus>('/api/system/automation/run')
      setStatus(result)
      notify('success', result.lastMessage || '同步完成。')
      await refreshAlibaba()
    } catch (err) {
      notify('error', errorText(err, '同步失败'))
    } finally {
      setRunning(false)
    }
  }

  return (
    <section className="card">
      <div className="card-head">
        <div>
          <strong>自动同步</strong>
          <span>后台定时续期 Token，并查询「审核中」商品的最新状态，无需手动点击。</span>
        </div>
        <Pill tone={status?.enabled ? 'good' : 'neutral'}>{status?.enabled ? `每 ${status.intervalMinutes} 分钟` : '未开启'}</Pill>
      </div>
      <dl className="facts">
        <dt>上次运行</dt><dd>{formatDate(status?.lastRunAt)}</dd>
        <dt>下次运行</dt><dd>{formatDate(status?.nextRunAt)}</dd>
        <dt>结果</dt><dd>{status?.lastMessage || '-'}</dd>
      </dl>
      <div className="actions left">
        <button type="button" onClick={() => void runNow()} disabled={running}>
          {running ? <Spinner /> : <Timer size={16} />} 立即同步
        </button>
      </div>
    </section>
  )
}