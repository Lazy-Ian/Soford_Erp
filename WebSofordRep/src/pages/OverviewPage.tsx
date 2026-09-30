import { useCallback, useState } from 'react'
import { AlertTriangle, CheckCircle2, CloudOff, FileWarning, KeyRound, PencilLine, Search, XCircle } from 'lucide-react'
import { api, errorText, type CatalogSummary, type SummaryItem } from '../api'
import { Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

/** Start page: what needs attention today, then how each account's listings stand. */
export function OverviewPage() {
  const { notify, navigate } = useApp()
  const [summary, setSummary] = useState<CatalogSummary | null>(null)

  const load = useCallback(async () => {
    try {
      setSummary(await api<CatalogSummary>('/api/catalog/summary'))
    } catch (err) {
      notify('error', errorText(err, '加载概览失败'))
    }
  }, [notify])

  useInitialLoad(load)

  if (!summary) return <div className="page"><div className="empty"><Spinner size={22} /></div></div>

  const todos = [
    summary.failed > 0 && {
      key: 'failed', tone: 'bad' as const, icon: XCircle, count: summary.failed,
      title: '审核未通过 / 发布失败', hint: '看原因，修改后重新发布', open: () => navigate('products', 'Failed'),
    },
    summary.expiringAccounts.length > 0 && {
      key: 'auth', tone: 'bad' as const, icon: KeyRound, count: summary.expiringAccounts.length,
      title: '授权即将到期', hint: summary.expiringAccounts.map((x) => `${x.name}（${formatDate(x.expiresAt)}）`).join('、'), open: () => navigate('connection'),
    },
    summary.missing > 0 && {
      key: 'missing', tone: 'warn' as const, icon: CloudOff, count: summary.missing,
      title: 'Alibaba 上已找不到', hint: '可能已在卖家后台删除，确认后可在本系统删除', open: () => navigate('products', 'missing'),
    },
    summary.needsContent > 0 && {
      key: 'content', tone: 'warn' as const, icon: FileWarning, count: summary.needsContent,
      title: '内容不完整', hint: '导入时缺少描述等内容；后台会自动补全，也可以选中后「从 Alibaba 刷新」', open: () => navigate('products', 'needs-content'),
    },
    summary.unpublishedChanges > 0 && {
      key: 'changes', tone: 'info' as const, icon: PencilLine, count: summary.unpublishedChanges,
      title: '有未发布的修改', hint: '本系统里改过、还没更新到 Alibaba', open: () => navigate('products', 'changes'),
    },
    summary.fewKeywords > 0 && {
      key: 'keywords', tone: 'info' as const, icon: Search, count: summary.fewKeywords,
      title: '关键词少于 3 个', hint: '补充买家搜索词能提高曝光', open: () => navigate('products', 'few-keywords'),
    },
  ].filter((x) => !!x)

  return (
    <div className="page">
      <section className="metrics">
        <Tile label="商品总数" value={summary.total} onClick={() => navigate('products', 'all')} />
        <Tile label="已上架" value={summary.online} tone="good" onClick={() => navigate('products', 'Online')} />
        <Tile label="已下架" value={summary.offline} onClick={() => navigate('products', 'Offline')} />
        <Tile label="审核中" value={summary.pending} onClick={() => navigate('products', 'Pending')} />
        <Tile label="未通过/失败" value={summary.failed} tone={summary.failed ? 'bad' : undefined} onClick={() => navigate('products', 'Failed')} />
        <Tile label="新品待发布" value={summary.drafts} onClick={() => navigate('products', 'incomplete')} />
      </section>

      <section className="card">
        <div className="card-head">
          <div>
            <strong>待处理</strong>
            <span>按紧急程度排列，点击进入对应的商品列表。</span>
          </div>
        </div>
        {todos.length === 0
          ? <p className="good"><CheckCircle2 size={16} /> 没有需要处理的事项。</p>
          : (
            <div className="todo-list">
              {todos.map(({ key, tone, icon: Icon, count, title, hint, open }) => (
                <button key={key} type="button" className={`todo ${tone}`} onClick={open}>
                  <Icon size={18} />
                  <div>
                    <strong>{title}</strong>
                    <span>{hint}</span>
                  </div>
                  <b>{count}</b>
                </button>
              ))}
            </div>
          )}
        {summary.failedItems.length > 0 && <ItemList title="最近未通过 / 失败" items={summary.failedItems} />}
        {summary.missingItems.length > 0 && <ItemList title="Alibaba 上已找不到" items={summary.missingItems} />}
      </section>

      <section className="card">
        <div className="card-head">
          <div>
            <strong>各账号商品</strong>
            <span>按负责账号（业务员）统计。未授权的账号用默认账号代为操作。</span>
          </div>
        </div>
        <div className="table-wrap flat">
          <table>
            <thead>
              <tr><th>账号</th><th className="col-num">商品</th><th className="col-num">已上架</th><th className="col-num">已下架</th><th className="col-num">审核中</th><th className="col-num">未通过/失败</th></tr>
            </thead>
            <tbody>
              {summary.accounts.map((account) => (
                <tr key={account.id}>
                  <td>{account.name} {!account.authorized && <Pill tone="neutral">未授权</Pill>}</td>
                  <td className="col-num">{account.total}</td>
                  <td className="col-num">{account.online}</td>
                  <td className="col-num">{account.offline}</td>
                  <td className="col-num">{account.pending}</td>
                  <td className="col-num">{account.failed ? <Pill tone="bad">{account.failed}</Pill> : 0}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}

function Tile({ label, value, tone, onClick }: { label: string; value: number; tone?: 'good' | 'warn' | 'bad'; onClick: () => void }) {
  return (
    <button type="button" className={`metric ${tone ?? ''}`} onClick={onClick}>
      <span>{label}</span>
      <strong>{value}</strong>
    </button>
  )
}

function ItemList({ title, items }: { title: string; items: SummaryItem[] }) {
  return (
    <div className="summary-items">
      <small className="muted"><AlertTriangle size={13} /> {title}</small>
      <ul>
        {items.map((item) => (
          <li key={item.id}>
            {item.image ? <img src={item.image} alt="" loading="lazy" /> : <span className="img-placeholder" />}
            <div>
              <span className="clip" title={item.title}>{item.title || item.sku}</span>
              <small className="muted clip" title={item.remoteStatusMessage ?? ''}>{item.remoteProductId ? `Alibaba ${item.remoteProductId} · ` : ''}{item.remoteStatusMessage}</small>
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}
