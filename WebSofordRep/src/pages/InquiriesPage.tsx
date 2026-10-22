import { useEffect, useState } from 'react'
import { Mail, RefreshCw, Save } from 'lucide-react'
import clsx from 'clsx'
import { api, errorText, post, type Inquiry, type InquiryPageResult, type InquiryStatus } from '../api'
import { Modal, Pagination, Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { formatDate, type Tone } from '../format'

const statusLabel: Record<InquiryStatus, string> = { New: '待处理', Replied: '已回复', Closed: '已关闭', Spam: '垃圾询盘' }
const statusTone: Record<InquiryStatus, Tone> = { New: 'warn', Replied: 'good', Closed: 'neutral', Spam: 'bad' }
const filters: { key: InquiryStatus | 'all'; label: string }[] = [
  { key: 'New', label: '待处理' },
  { key: 'Replied', label: '已回复' },
  { key: 'Closed', label: '已关闭' },
  { key: 'Spam', label: '垃圾询盘' },
  { key: 'all', label: '全部' },
]

/** Quote requests from the public website (SofordWebsite). */
export function InquiriesPage({ onChanged }: { onChanged?: () => void }) {
  const [result, setResult] = useState<InquiryPageResult | null>(null)
  const [filter, setFilter] = useState<InquiryStatus | 'all'>('New')
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState<Inquiry | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [refresh, setRefresh] = useState(0)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')
  const key = query.trim()

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      setLoading(true)
      setLoadError('')
      const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize), q: key })
      if (filter !== 'all') params.set('status', filter)
      try {
        const next = await api<InquiryPageResult>(`/api/inquiries/search?${params}`, { signal: controller.signal })
        if (!controller.signal.aborted) {
          setResult(next)
          setPage(next.page)
        }
      } catch (err) {
        if (!controller.signal.aborted) setLoadError(errorText(err, '加载询盘失败'))
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }, 250)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [filter, key, page, pageSize, refresh])

  const saved = (next: Inquiry) => {
    setResult((current) => current ? { ...current, items: current.items.map((x) => x.id === next.id ? next : x) } : null)
    setRefresh((value) => value + 1)
    setOpen(null)
    onChanged?.()
  }

  const visible = result?.items ?? []

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>网站询盘</strong>
            <span>官网「Request a quote」表单提交的询盘。回复后把状态改为「已回复」，方便跟进。</span>
          </div>
          <div className="actions">
            <input value={query} maxLength={200} aria-label="搜索询盘" onChange={(e) => { setQuery(e.target.value); setPage(1) }} placeholder="按客户、邮箱、国家、产品、备注搜索" />
            <button type="button" disabled={loading} onClick={() => setRefresh((value) => value + 1)}><RefreshCw size={16} /> 刷新</button>
          </div>
        </div>
        <div className="segmented">
          {filters.map((item) => (
            <button key={item.key} type="button" aria-pressed={filter === item.key} className={clsx(filter === item.key && 'active')} onClick={() => { setFilter(item.key); setPage(1) }}>
              {item.label}{item.key !== 'all' && result ? ` ${result.counts[item.key]}` : ''}
            </button>
          ))}
        </div>
        {loading && <div className="empty small" role="status" aria-label="加载询盘中"><Spinner size={20} /></div>}
        {loadError && <div className="banner bad" role="alert">{loadError}<button type="button" onClick={() => setRefresh((value) => value + 1)}>重试</button></div>}
        {!loading && !loadError && result && visible.length === 0 && <p className="muted">没有符合条件的询盘。</p>}
        {!loading && !loadError && visible.length > 0 && (
          <div className="table-wrap flat">
            <table>
              <thead><tr><th>时间</th><th>客户</th><th>国家</th><th>产品</th><th>数量</th><th>内容</th><th>状态</th></tr></thead>
              <tbody>
                {visible.map((x) => (
                  <tr key={x.id} className="clickable" onClick={() => setOpen(x)}>
                    <td>{formatDate(x.createdAt)}</td>
                    <td><button type="button" onClick={(event) => { event.stopPropagation(); setOpen(x) }}>{x.name}</button><br /><span className="muted">{x.company || x.email}</span></td>
                    <td>{x.country}</td>
                    <td className="clip" title={x.product}>{x.product || '—'}</td>
                    <td>{x.quantity}</td>
                    <td className="clip" title={x.message}>{x.message}</td>
                    <td><Pill tone={statusTone[x.status]}>{statusLabel[x.status]}</Pill></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {!loading && !loadError && result && <Pagination page={result.page} pageSize={pageSize} total={result.total} onPage={setPage}
          onPageSize={(size) => { setPageSize(size); setPage(1) }} />}
      </section>
      {open && <InquiryDialog inquiry={open} onClose={() => setOpen(null)} onSaved={saved} />}
    </div>
  )
}

function InquiryDialog({ inquiry, onClose, onSaved }: { inquiry: Inquiry; onClose: () => void; onSaved: (next: Inquiry) => void }) {
  const { notify } = useApp()
  const [status, setStatus] = useState<InquiryStatus>(inquiry.status)
  const [note, setNote] = useState(inquiry.note)
  const [busy, setBusy] = useState(false)
  const dirty = status !== inquiry.status || note !== inquiry.note
  const requestClose = () => {
    if (!busy && (!dirty || window.confirm('有未保存的询盘修改，确定放弃并关闭吗？'))) onClose()
  }

  const save = async () => {
    setBusy(true)
    try {
      onSaved(await post<Inquiry>(`/api/inquiries/${inquiry.id}`, { status, note }, 'PUT'))
      notify('success', '询盘已更新。')
    } catch (err) {
      notify('error', errorText(err, '保存失败'))
    } finally {
      setBusy(false)
    }
  }

  const subject = `Re: Your inquiry${inquiry.product ? ` about ${inquiry.product}` : ''} — Soford`
  const reply = `mailto:${inquiry.email}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(`Dear ${inquiry.name},\n\nThank you for your inquiry.\n\n\n\n> ${inquiry.message.replace(/\n/g, '\n> ')}`)}`
  const rows: [string, string][] = [
    ['邮箱', inquiry.email],
    ['公司', inquiry.company],
    ['国家', inquiry.country],
    ['电话', inquiry.phone],
    ['产品', inquiry.product],
    ['Alibaba 商品 ID', inquiry.productId],
    ['数量', inquiry.quantity],
    ['来源页面', inquiry.pageUrl],
    ['IP', inquiry.sourceIp],
    ['处理人', inquiry.handledBy ? `${inquiry.handledBy}（${formatDate(inquiry.updatedAt ?? '')}）` : ''],
  ]

  return (
    <Modal title={`${inquiry.name} 的询盘`} subtitle={formatDate(inquiry.createdAt)} onClose={requestClose} wide
      footer={<>
        <a className="button-link" href={reply}><Mail size={16} /> 用邮件回复</a>
        <button type="button" disabled={busy} onClick={requestClose}>取消</button>
        <button type="button" className="primary" disabled={busy || !dirty} onClick={() => void save()}>
          {busy ? <Spinner /> : <Save size={16} />} 保存
        </button>
      </>}>
      <div className="table-wrap flat">
        <table>
          <tbody>
            {rows.filter(([, v]) => v).map(([k, v]) => <tr key={k}><th>{k}</th><td>{v}</td></tr>)}
          </tbody>
        </table>
      </div>
      <p className="inquiry-message">{inquiry.message}</p>
      <div className="form-grid">
        <label className="field">状态
          <select value={status} disabled={busy} onChange={(e) => setStatus(e.target.value as InquiryStatus)}>
            {(Object.keys(statusLabel) as InquiryStatus[]).map((s) => <option key={s} value={s}>{statusLabel[s]}</option>)}
          </select>
        </label>
        <label className="field">备注<textarea value={note} maxLength={2000} disabled={busy} onChange={(e) => setNote(e.target.value)} placeholder="例如：已发报价单，等客户确认" /></label>
      </div>
    </Modal>
  )
}
