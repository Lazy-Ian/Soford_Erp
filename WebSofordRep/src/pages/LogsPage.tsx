import { useCallback, useMemo, useState } from 'react'
import { ChevronDown, ChevronRight, RefreshCw, Search } from 'lucide-react'
import clsx from 'clsx'
import { api, errorText, type ApiCallLog } from '../api'
import { Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

export function LogsPage() {
  const { notify } = useApp()
  const [logs, setLogs] = useState<ApiCallLog[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [onlyFailed, setOnlyFailed] = useState(false)
  const [query, setQuery] = useState('')

  const load = useCallback(async () => {
    try {
      setLogs(await api<ApiCallLog[]>('/api/integrations/alibaba/logs?take=300'))
    } catch (err) {
      notify('error', errorText(err, '加载日志失败'))
    }
  }, [notify])

  useInitialLoad(load)

  const visible = useMemo(() => {
    const key = query.trim().toLowerCase()
    return (logs ?? []).filter((log) =>
      (!onlyFailed || !log.success) &&
      (!key || [log.apiKey, log.path, log.errorCode ?? '', log.message, log.requestId ?? '', log.account ?? ''].some((value) => value?.toLowerCase().includes(key))))
  }, [logs, onlyFailed, query])

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>Alibaba API 日志</strong>
            <span>最近 1000 次调用。access_token、sign、授权码已脱敏。</span>
          </div>
          <div className="actions">
            <label className="search compact"><Search size={15} /><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="接口 / 错误码 / request_id" /></label>
            <label className="checkbox"><input type="checkbox" checked={onlyFailed} onChange={(e) => setOnlyFailed(e.target.checked)} /> 只看失败</label>
            <button type="button" onClick={() => void load()}><RefreshCw size={16} /> 刷新</button>
          </div>
        </div>
        {logs === null && <div className="empty small"><Spinner size={20} /></div>}
        {logs !== null && visible.length === 0 && <div className="empty small"><span>没有日志。</span></div>}
        <div className="log-list">
          {visible.map((log) => (
            <article key={log.id} className={clsx('log', !log.success && 'failed')}>
              <button type="button" className="log-head" onClick={() => setOpen(open === log.id ? null : log.id)}>
                {open === log.id ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
                <Pill tone={log.success ? 'good' : 'bad'}>{log.success ? '成功' : '失败'}</Pill>
                <b>{log.apiKey}</b>
                <span className="muted">{formatDate(log.createdAt)} · {log.durationMs}ms{log.account ? ` · ${log.account}` : ''}</span>
                <span className="log-message">{log.success ? '' : `${log.errorCode ?? ''} ${log.message}`}</span>
              </button>
              {open === log.id && (
                <div className="log-body">
                  <dl className="facts">
                    <dt>路径</dt><dd><code>{log.path}</code></dd>
                    <dt>HTTP</dt><dd>{log.httpStatus || '-'}</dd>
                    <dt>request_id</dt><dd>{log.requestId || '-'}</dd>
                  </dl>
                  <b>请求参数</b>
                  <pre className="code">{pretty(log.request)}</pre>
                  <b>响应</b>
                  <pre className="code">{pretty(log.response)}</pre>
                </div>
              )}
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}

function pretty(value?: string | null) {
  if (!value) return '-'
  try {
    const parsed = JSON.parse(value) as Record<string, unknown>
    // Nested JSON strings (e.g. product_info) are easier to read expanded.
    for (const [key, item] of Object.entries(parsed)) {
      if (typeof item === 'string' && /^[[{]/.test(item)) {
        try {
          parsed[key] = JSON.parse(item)
        } catch {
          // Leave as text.
        }
      }
    }
    return JSON.stringify(parsed, null, 2)
  } catch {
    return value
  }
}
