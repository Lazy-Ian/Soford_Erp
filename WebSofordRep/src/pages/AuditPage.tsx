import { useCallback, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { api, errorText, type AuditEntry } from '../api'
import { Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

/** Who did what. Admins see everyone; operators see their own history. */
export function AuditPage() {
  const { notify, user } = useApp()
  const [entries, setEntries] = useState<AuditEntry[] | null>(null)
  const [query, setQuery] = useState('')

  const load = useCallback(async () => {
    try {
      setEntries(await api<AuditEntry[]>('/api/audit?take=500'))
    } catch (err) {
      notify('error', errorText(err, '加载操作记录失败'))
    }
  }, [notify])

  useInitialLoad(load)

  const key = query.trim().toLowerCase()
  const visible = (entries ?? []).filter((x) => !key || [x.userName, x.action, x.summary].some((v) => v.toLowerCase().includes(key)))

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>操作记录</strong>
            <span>{user?.isAdmin ? '所有人的登录、编辑、导入、发布、同步、上下架和账号变更。' : '你的登录、编辑、导入、发布和同步记录。'}最近 500 条。</span>
          </div>
          <div className="actions">
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="按人、操作或内容筛选" />
            <button type="button" onClick={() => void load()}><RefreshCw size={16} /> 刷新</button>
          </div>
        </div>
        {entries === null && <div className="empty small"><Spinner size={20} /></div>}
        {entries?.length === 0 && <p className="muted">还没有记录。</p>}
        {visible.length > 0 && (
          <div className="table-wrap flat">
            <table>
              <thead><tr><th>时间</th><th>操作人</th><th>操作</th><th>内容</th><th className="col-num">商品数</th></tr></thead>
              <tbody>
                {visible.map((entry) => (
                  <tr key={entry.id}>
                    <td>{formatDate(entry.at)}</td>
                    <td>{entry.userName}</td>
                    <td>{entry.action}</td>
                    <td className="clip" title={entry.summary}>{entry.summary}</td>
                    <td className="col-num">{entry.count || ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  )
}
