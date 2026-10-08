import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ArrowDownToLine, ArrowUpFromLine, CheckCircle2, CloudDownload, Download, FileSpreadsheet, Layers3, PackageCheck, Pencil,
  Plus, RefreshCw, Rocket, Search, Tags, Trash2, Upload, Warehouse, DollarSign,
} from 'lucide-react'
import clsx from 'clsx'
import { accountOf, api, apiUrl, errorText, post, UNAUTHORIZED_EVENT, type AlibabaAccount, type BatchResult, type ImportJob, type ImportMode, type JobKind, type Product, type ProductDraft, type PublishJob, type PublishState, type PullResult } from '../api'
import { BatchResultDialog, Modal, Pagination, Pill, Spinner } from '../components/ui'
import { hashFilter, useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { downloadUrl, formatDate, formatPrice, jobKindLabel, jobStatusLabel, publishStateLabel, publishStateTone } from '../format'
import { ProductEditor } from './ProductEditor'

type Filter = 'all' | 'incomplete' | 'ready' | 'needs-content' | 'changes' | 'missing' | 'few-keywords' | PublishState

const filters: { key: Filter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'Online', label: '已上架' },
  { key: 'Offline', label: '已下架' },
  { key: 'Pending', label: '审核中' },
  { key: 'Failed', label: '未通过/失败' },
  { key: 'needs-content', label: '内容不完整' },
  { key: 'changes', label: '有未发布修改' },
  { key: 'incomplete', label: '新品待完善' },
  { key: 'ready', label: '新品可发布' },
]

const importModeLabel: Record<ImportMode, string> = {
  upsert: '新增并更新',
  'create-only': '仅新增',
  'update-only': '仅更新',
  'stock-price': '只更新库存价格',
}

const importStatusLabel: Record<ImportJob['status'], string> = {
  Previewed: '等待确认', Queued: '排队中', Running: '执行中', Completed: '已完成',
  CompletedWithWarnings: '完成但有警告', Failed: '失败', Interrupted: '已中断',
}

const hasBlockers = (item: Product) => item.qualityIssues.some((x) => x.severity === 'Blocker')

/** Quality checks are about publishing: for listings already on Alibaba they mean "content incomplete here", not "not ready". */
function matchesFilter(item: Product, filter: Filter) {
  const published = !!item.remoteProductId
  switch (filter) {
    case 'all': return true
    case 'incomplete': return !published && item.localState === 'Incomplete'
    case 'ready': return !published && item.localState === 'Ready' && item.publishState === 'NotPublished'
    case 'needs-content': return published && hasBlockers(item)
    case 'changes': return !!item.hasUnpublishedChanges
    case 'missing': return item.remoteStatus === 'missing'
    case 'few-keywords': return published && item.keywords.length < 3
    default: return item.publishState === filter
  }
}

export function ProductsPage() {
  const { notify, confirm, alibaba, navigate, user } = useApp()
  const [products, setProducts] = useState<Product[]>([])
  const [loading, setLoading] = useState(true)
  const [query, setQuery] = useState('')
  const [accounts, setAccounts] = useState<AlibabaAccount[]>([])
  const [accountFilter, setAccountFilter] = useState('all')
  // Some filters are only reached from the overview, so they have no button of their own.
  const [filter, setFilter] = useState<Filter>(() => (['missing', 'few-keywords'].includes(hashFilter()) || filters.some((x) => x.key === hashFilter()) ? hashFilter() as Filter : 'all'))
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [editing, setEditing] = useState<Product | 'new' | null>(null)
  const [issuesOf, setIssuesOf] = useState<Product | null>(null)
  const [batch, setBatch] = useState<{ title: string; result: BatchResult } | null>(null)
  const [importJob, setImportJob] = useState<ImportJob | null>(null)
  const [importMode, setImportMode] = useState<ImportMode>('upsert')
  const [importPollTick, setImportPollTick] = useState(0)
  const [job, setJob] = useState<PublishJob | null>(null)
  const [pollTick, setPollTick] = useState(0)
  const [pullResult, setPullResult] = useState<PullResult | null>(null)
  const fileInput = useRef<HTMLInputElement | null>(null)

  const load = useCallback(async () => {
    try {
      const [items, accountList] = await Promise.all([
        api<Product[]>('/api/catalog/products'),
        api<AlibabaAccount[]>('/api/integrations/alibaba/accounts').catch(() => [] as AlibabaAccount[]),
      ])
      setProducts(items)
      setAccounts(accountList)
    } catch (err) {
      notify('error', errorText(err, '加载商品失败'))
    } finally {
      setLoading(false)
    }
  }, [notify])

  useInitialLoad(load)

  // Poll the running publish job until it finishes, refreshing products as items complete.
  useEffect(() => {
    if (!job || !['Queued', 'Running'].includes(job.status)) return
    const timer = window.setTimeout(async () => {
      try {
        const next = await api<PublishJob>(`/api/catalog/publish-jobs/${job.id}`)
        // The list is heavy: reload it once when the job ends, not on every processed item.
        if (next.status !== job.status && !['Queued', 'Running'].includes(next.status)) void load()
        setJob(next)
      } catch (err) {
        // Keep polling after a transient failure instead of freezing the progress bar.
        notify('error', errorText(err, '查询发布进度失败，稍后重试'))
        window.setTimeout(() => setPollTick((tick) => tick + 1), 3000)
      }
    }, 1500)
    return () => window.clearTimeout(timer)
  }, [job, pollTick, load, notify])

  useEffect(() => {
    if (!importJob || !['Queued', 'Running'].includes(importJob.status)) return
    const timer = window.setTimeout(async () => {
      try {
        const next = await api<ImportJob>(`/api/catalog/import-jobs/${importJob.id}`)
        const finished = !['Queued', 'Running'].includes(next.status)
        setImportJob(next)
        if (finished) await load()
        else setImportPollTick((tick) => tick + 1)
      } catch (err) {
        notify('error', errorText(err, '查询导入进度失败，稍后重试'))
        setImportPollTick((tick) => tick + 1)
      }
    }, 1200)
    return () => window.clearTimeout(timer)
  }, [importJob, importPollTick, load, notify])

  const filtered = useMemo(() => {
    const key = query.trim().toLowerCase()
    return products.filter((item) => {
      if (!matchesFilter(item, filter)) return false
      if (accountFilter !== 'all') {
        const owner = accounts.find((a) => !!item.ownerAliId && a.ownerAliIds.includes(item.ownerAliId))
        if (accountFilter === 'none' ? owner || item.accountId : owner?.id !== accountFilter && item.accountId !== accountFilter) return false
      }
      if (!key) return true
      return [item.sku, item.title, item.categoryId, item.remoteProductId ?? '', item.brandName].some((value) => value.toLowerCase().includes(key))
    })
  }, [products, query, filter, accountFilter, accounts])

  const pages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const currentPage = Math.min(page, pages)
  const visible = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  // Selection only ever covers products that are currently visible under the filter, so batch actions never hit hidden rows.
  const selectedProducts = filtered.filter((item) => selected.has(item.id))
  const selectedIds = selectedProducts.map((item) => item.id)
  const publishedSelected = selectedProducts.filter((item) => item.remoteProductId).length
  const stockSelected = selectedProducts.filter((item) => item.remoteProductId && item.stock > 0).length

  const summary = useMemo(() => {
    const count = (key: Filter) => products.filter((x) => matchesFilter(x, key)).length
    return {
      total: products.length,
      online: count('Online'),
      pending: count('Pending'),
      failed: count('Failed'),
      needsContent: count('needs-content'),
      drafts: count('incomplete') + count('ready'),
    }
  }, [products])

  // The list omits descriptions to stay small; the editor needs the full product.
  const openEditor = (item: Product) =>
    run('open', async () => setEditing(await api<Product>(`/api/catalog/products/${item.id}`)))

  const applyFilter = (next: Filter) => {
    setFilter(next)
    setPage(1)
  }

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

  // Batch Alibaba operations run as background jobs; the progress bar below follows them.
  const startJob = (kind: Exclude<JobKind, 'publish'>) =>
    run(kind, async () => {
      setJob(await post<PublishJob>(`/api/catalog/jobs/${kind}`, { productIds: selectedIds }))
    })

  const refreshFromAlibaba = () => {
    const edited = selectedProducts.filter((x) => x.remoteProductId && x.hasUnpublishedChanges).length
    const choice = { discard: false }
    confirm({
      title: '从 Alibaba 刷新',
      confirmText: '开始刷新',
      message: (
        <>
          <p>用 Alibaba 上的最新内容（标题、描述、图片、属性、价格等）替换本系统中 {publishedSelected} 个商品的内容。SKU 和指定账号不变。</p>
          <p className="muted">在 Alibaba 后台改过的商品、或导入时缺少描述的商品，更新前都应先刷新，否则发布会被拦截。</p>
          {edited > 0 && (
            <label className="check-line">
              <input type="checkbox" onChange={(e) => (choice.discard = e.target.checked)} />
              其中 {edited} 个有未发布的本地修改，默认跳过；勾选则放弃这些修改
            </label>
          )}
        </>
      ),
      onConfirm: () => void startJob(choice.discard ? 'refresh-discard' : 'refresh'),
    })
  }

  const confirmJob = (kind: 'inventory' | 'price' | 'predict') => {
    const count = kind === 'predict' ? selectedIds.length : publishedSelected
    const message = {
      inventory: `把本系统中的库存写入 Alibaba 上 ${count} 个已发布商品？本地库存为 0 的商品会跳过。`,
      price: `把本系统中的价格（含阶梯价）写入 Alibaba 上 ${count} 个已发布商品？会直接改动线上价格。`,
      predict: `为 ${count} 个商品请求 Alibaba 推荐类目，并用推荐结果替换本地的类目 ID？`,
    }[kind]
    confirm({ title: jobKindLabel[kind], message, confirmText: '开始', onConfirm: () => void startJob(kind) })
  }

  const importFile = (file: File) =>
    run('import', async () => {
      const data = new FormData()
      data.append('file', file)
      data.append('mode', importMode)
      setImportJob(await api<ImportJob>('/api/catalog/import/preview', { method: 'POST', body: data }))
    })

  const commitImport = () =>
    run('import-commit', async () => {
      if (!importJob) return
      setImportJob(await post<ImportJob>(`/api/catalog/import-jobs/${importJob.id}/commit`))
    })

  const saveProduct = (draft: ProductDraft) =>
    run('save', async () => {
      const saved = editing && editing !== 'new'
        ? await post<Product>(`/api/catalog/products/${editing.id}`, { ...draft, expectedUpdatedAt: editing.updatedAt }, 'PUT')
        : await post<Product>('/api/catalog/products', draft)
      const blockers = saved.qualityIssues.filter((x) => x.severity === 'Blocker').length
      notify(blockers ? 'info' : 'success', blockers ? `${saved.sku} 已保存，还有 ${blockers} 个阻断问题需处理。` : `${saved.sku} 已保存，可以发布。`)
      setEditing(null)
      await load()
    })

  const qualityCheck = () =>
    run('quality', async () => {
      const result = await post<{ total: number; ready: number }>('/api/catalog/quality-check', { productIds: selectedIds })
      notify('success', `质检完成：${result.total} 个商品中 ${result.ready} 个可发布。`)
      await load()
    })

  const publish = () => {
    const blocked = selectedProducts.filter((x) => x.localState === 'Incomplete')
    const updates = selectedProducts.filter((x) => x.remoteProductId).length
    const authorized = accounts.filter((x) => x.authorized)
    // Uncontrolled picker: the dialog content is rendered once, the choice is read on confirm.
    const choice = { accountId: '' }
    confirm({
      title: '发布到 Alibaba',
      confirmText: '开始发布',
      message: (
        <>
          <p>将提交 {selectedIds.length} 个商品：新发布 {selectedIds.length - updates} 个，更新已发布商品 {updates} 个。</p>
          {blocked.length > 0 && <p className="warn">其中 {blocked.length} 个存在阻断问题，会被跳过并在结果中列出原因。</p>}
          {updates > 0 && (
            <p className="warn">
              更新会用本系统中的标题、描述、图片、属性、价格覆盖 Alibaba 上的内容。对从 Alibaba 导入的商品，只在确实需要修改内容时再发布；
              只改库存或价格请用「同步库存」「同步价格」。
            </p>
          )}
          {authorized.length > 1 && (
            <label className="field">
              用哪个账号操作
              <select defaultValue="" onChange={(e) => (choice.accountId = e.target.value)}>
                <option value="">自动：商品所属账号，没有授权时用默认账号</option>
                {authorized.map((a) => <option key={a.id} value={a.id}>{a.name}{a.isDefault ? '（默认）' : ''}</option>)}
              </select>
              <span className="muted">新发布的商品会归属到操作它的账号。子账号通常只能更新自己负责的商品。</span>
            </label>
          )}
          <p className="muted">发布在后台执行，可以离开本页面，进度可在「后台任务」查看。</p>
        </>
      ),
      onConfirm: () =>
        void run('publish', async () => {
          const created = await post<PublishJob>('/api/catalog/publish', { productIds: selectedIds, accountId: choice.accountId || null })
          setJob(created)
          await load()
        }),
    })
  }

  const pullFromAlibaba = () =>
    confirm({
      title: '从 Alibaba 导入商品',
      confirmText: '开始导入',
      message: (
        <>
          <p>读取 Alibaba 店铺中的全部商品：</p>
          <ul className="plain">
            <li>本系统没有的商品会新建；</li>
            <li>SKU（型号）相同但未关联的商品会自动关联 Alibaba 商品 ID；</li>
            <li>已关联的商品只更新 Alibaba 状态，不会覆盖本地已修改的内容。</li>
          </ul>
          <p className="muted">商品较多时需要一两分钟（每页 20 个）。系统每天也会自动执行一次。</p>
        </>
      ),
      onConfirm: () =>
        void run('pull', async () => {
          setPullResult(await post<PullResult>('/api/catalog/pull-from-alibaba'))
          await load()
        }),
    })

  const exportCsv = () =>
    run('export', async () => {
      // POST keeps large selections out of the URL (nginx rejects very long query strings).
      const response = await fetch(apiUrl('/api/catalog/export/products.csv'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ productIds: selectedIds }),
      })
      if (response.status === 401) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
      if (!response.ok) throw new Error(response.status === 401 ? '登录已过期，请重新登录。' : `导出失败（${response.status}）`)
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url
      link.download = `soford-products-${new Date().toISOString().slice(0, 10)}.csv`
      link.click()
      URL.revokeObjectURL(url)
    })

  const setOnline = (online: boolean) =>
    confirm({
      title: online ? '上架商品' : '下架商品',
      message: `确定要${online ? '上架' : '下架'}选中的 ${publishedSelected} 个已发布商品吗？`,
      danger: !online,
      confirmText: online ? '上架' : '下架',
      onConfirm: () => void startJob(online ? 'online' : 'offline'),
    })

  const remove = (ids: string[]) =>
    confirm({
      title: '删除商品',
      danger: true,
      confirmText: '删除',
      message: <p>确定删除 {ids.length} 个商品？只删除本系统中的数据，不会删除 Alibaba 上已发布的商品。此操作不可撤销。</p>,
      onConfirm: () =>
        void run('delete', async () => {
          await post('/api/catalog/products/batch-delete', { productIds: ids })
          setSelected((current) => new Set([...current].filter((id) => !ids.includes(id))))
          notify('success', `已删除 ${ids.length} 个商品。`)
          await load()
        }),
    })

  const allVisibleSelected = visible.length > 0 && visible.every((item) => selected.has(item.id))
  const needsAlibaba = !alibaba?.ready
  const alibabaHint = needsAlibaba ? '需要先在「店铺连接」完成配置和授权' : undefined
  const none = selectedIds.length === 0

  return (
    <div className="page">
      <section className="metrics">
        <Metric label="商品总数" value={summary.total} onClick={() => applyFilter('all')} />
        <Metric label="已上架" value={summary.online} tone="good" onClick={() => applyFilter('Online')} />
        <Metric label="审核中" value={summary.pending} onClick={() => applyFilter('Pending')} />
        <Metric label="未通过/失败" value={summary.failed} tone={summary.failed ? 'bad' : undefined} onClick={() => applyFilter('Failed')} />
        <Metric label="内容不完整" value={summary.needsContent} tone={summary.needsContent ? 'warn' : undefined} onClick={() => applyFilter('needs-content')} />
        <Metric label="新品待发布" value={summary.drafts} onClick={() => applyFilter('incomplete')} />
      </section>

      {needsAlibaba && (
        <div className="banner warn">
          <span>Alibaba 店铺尚未连接，发布、类目预测、同步等功能暂不可用。{user?.isAdmin ? '' : '请联系管理员。'}</span>
          {user?.isAdmin && <button type="button" onClick={() => navigate('connection')}>去连接店铺</button>}
        </div>
      )}

      <section
        className="toolbar"
        onDragOver={(event) => event.preventDefault()}
        onDrop={(event) => {
          event.preventDefault()
          const file = event.dataTransfer.files?.[0]
          if (file) void importFile(file)
        }}
      >
        <label className="search">
          <Search size={17} />
          <input value={query} onChange={(event) => { setQuery(event.target.value); setPage(1) }} placeholder="搜索 SKU / 标题 / 类目 / Alibaba 商品 ID" />
        </label>
        <div className="segmented">
          {filters.map((item) => (
            <button key={item.key} type="button" className={clsx(filter === item.key && 'active')} onClick={() => applyFilter(item.key)}>{item.label}</button>
          ))}
          {accounts.length > 0 && (
            <select className="account-filter" value={accountFilter} onChange={(e) => { setAccountFilter(e.target.value); setPage(1) }} aria-label="按账号筛选">
              <option value="all">全部账号</option>
              {accounts.map((a) => <option key={a.id} value={a.id}>{a.name}（{a.productCount + a.assignedCount}）</option>)}
              <option value="none">未归属账号</option>
            </select>
          )}
        </div>
        <div className="actions">
          <button type="button" onClick={() => setEditing('new')}><Plus size={16} /> 新建</button>
          {user?.isAdmin && (
            <button type="button" onClick={pullFromAlibaba} disabled={busy !== null || needsAlibaba} title={alibabaHint ?? '把 Alibaba 店铺中已有的商品导入到本系统'}>
              {busy === 'pull' ? <Spinner /> : <CloudDownload size={16} />} 从 Alibaba 导入
            </button>
          )}
          <button type="button" onClick={() => downloadUrl('/api/catalog/import/template.xlsx')}><Download size={16} /> 模板</button>
          <select value={importMode} onChange={(event) => setImportMode(event.target.value as ImportMode)} disabled={busy === 'import'} aria-label="导入模式" title="选择表格导入时允许执行的操作">
            <option value="upsert">新增并更新</option>
            <option value="create-only">仅新增</option>
            <option value="update-only">仅更新</option>
            <option value="stock-price">只更新库存价格</option>
          </select>
          <button type="button" className="primary" onClick={() => fileInput.current?.click()} disabled={busy === 'import'} title="支持 .xlsx / .csv，也可以把文件拖到这里">
            {busy === 'import' ? <Spinner /> : <Upload size={16} />} 导入
          </button>
          <input ref={fileInput} type="file" accept=".xlsx,.csv" hidden onChange={(event) => {
            const file = event.target.files?.[0]
            if (file) void importFile(file)
            event.currentTarget.value = ''
          }} />
        </div>
      </section>

      <section className={clsx('batchbar', !none && 'active')}>
        <span>
          {none ? '勾选商品后可批量操作' : `已选 ${selectedIds.length} 个${publishedSelected ? `（已发布 ${publishedSelected} 个）` : ''}`}
          {allVisibleSelected && filtered.length > selectedIds.length && (
            <button type="button" className="link" onClick={() => setSelected(new Set(filtered.map((x) => x.id)))}>选择筛选出的全部 {filtered.length} 个</button>
          )}
          {!none && <button type="button" className="link" onClick={() => setSelected(new Set())}>清除</button>}
        </span>
        <button type="button" onClick={qualityCheck} disabled={none || busy !== null}>{busy === 'quality' ? <Spinner /> : <PackageCheck size={16} />} 质检</button>
        <button type="button" onClick={() => confirmJob('predict')} disabled={none || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'predict' ? <Spinner /> : <Tags size={16} />} 预测类目
        </button>
        <button type="button" className="primary" onClick={publish} disabled={none || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'publish' ? <Spinner /> : <Rocket size={16} />} 发布到 Alibaba
        </button>
        <span className="divider" />
        <button type="button" onClick={() => void startJob('status')} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint ?? '查询 Alibaba 上架/审核状态'}>
          {busy === 'status' ? <Spinner /> : <RefreshCw size={16} />} 同步状态
        </button>
        <button type="button" onClick={refreshFromAlibaba} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint ?? '取回 Alibaba 上的最新内容；在 Alibaba 后台改过的商品，更新前要先刷新'}>
          {busy === 'refresh' || busy === 'refresh-discard' ? <Spinner /> : <CloudDownload size={16} />} 从 Alibaba 刷新
        </button>
        <button type="button" onClick={() => confirmJob('inventory')} disabled={!stockSelected || busy !== null || needsAlibaba} title={alibabaHint ?? (stockSelected ? undefined : '选中的商品本地库存都是 0（B2B 商品通常不管库存），没有可同步的库存')}>
          {busy === 'inventory' ? <Spinner /> : <Warehouse size={16} />} 同步库存
        </button>
        <button type="button" onClick={() => confirmJob('price')} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'price' ? <Spinner /> : <DollarSign size={16} />} 同步价格
        </button>
        <button type="button" onClick={() => setOnline(true)} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'online' ? <Spinner /> : <ArrowUpFromLine size={16} />} 上架
        </button>
        <button type="button" onClick={() => setOnline(false)} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'offline' ? <Spinner /> : <ArrowDownToLine size={16} />} 下架
        </button>
        <span className="divider" />
        <button type="button" onClick={() => void exportCsv()} disabled={busy === 'export'} title={none ? '导出全部商品' : '导出选中商品'}>
          <FileSpreadsheet size={16} /> 导出 CSV
        </button>
        {accounts.some((a) => a.authorized) && user?.isAdmin && (
          <select value="" disabled={none || busy !== null} aria-label="指定操作账号" title="指定用哪个账号发布、更新、同步选中的商品" onChange={(e) => {
            const value = e.target.value
            if (!value) return
            void run('assign', async () => {
              const accountId = value === 'auto' ? null : value
              await post('/api/catalog/assign-account', { productIds: selectedIds, accountId })
              notify('success', accountId ? `已指定 ${selectedIds.length} 个商品由「${accounts.find((a) => a.id === accountId)?.name}」操作。` : `已恢复 ${selectedIds.length} 个商品为自动选择账号。`)
              await load()
            })
          }}>
            <option value="">指定账号…</option>
            <option value="auto">自动（所属账号 / 默认账号）</option>
            {accounts.filter((a) => a.authorized).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
        )}
        <button type="button" className="danger-text" onClick={() => remove(selectedIds)} disabled={none || busy !== null}><Trash2 size={16} /> 删除</button>
      </section>

      {job && (
        <div className={clsx('job-progress', job.failed > 0 && 'has-errors')}>
          <div>
            <strong>{jobKindLabel[job.kind ?? 'publish']} · {jobStatusLabel[job.status]}</strong>
            <span>已处理 {job.processed}/{job.total}，成功 {job.succeeded}，失败 {job.failed}</span>
          </div>
          <div className="progress"><div style={{ width: `${job.total ? (job.processed / job.total) * 100 : 0}%` }} /></div>
          {!['Queued', 'Running'].includes(job.status) && (
            <>
              <button type="button" onClick={() => setBatch({ title: `${jobKindLabel[job.kind ?? 'publish']}结果`, result: { total: job.total, succeeded: job.succeeded, failed: job.failed, items: job.items } })}>查看结果</button>
              <button type="button" className="icon" aria-label="关闭" onClick={() => setJob(null)}>×</button>
            </>
          )}
        </div>
      )}

      <section className="table-wrap">
        <table>
          <thead>
            <tr>
              <th className="check">
                <input type="checkbox" aria-label="全选本页" checked={allVisibleSelected} onChange={(event) => setSelected((current) => {
                  const next = new Set(current)
                  for (const item of visible) {
                    if (event.target.checked) next.add(item.id)
                    else next.delete(item.id)
                  }
                  return next
                })} />
              </th>
              <th>商品</th>
              <th className="col-account">账号</th>
              <th className="col-cat">类目</th>
              <th className="col-num">价格 / 起订</th>
              <th className="col-num">库存</th>
              <th className="col-state">质检</th>
              <th className="col-state">Alibaba 状态</th>
              <th className="col-actions" />
            </tr>
          </thead>
          <tbody>
            {visible.map((item) => {
              const blockers = item.qualityIssues.filter((x) => x.severity === 'Blocker').length
              const variants = item.remoteSkuCount && item.remoteSkuCount > 1 ? item.remoteSkuCount : 0
              const warnings = item.qualityIssues.filter((x) => x.severity === 'Warning').length
              return (
                <tr key={item.id} className={clsx(selected.has(item.id) && 'selected')}>
                  <td className="check">
                    <input type="checkbox" aria-label={`选择 ${item.sku}`} checked={selected.has(item.id)} onChange={(event) => setSelected((current) => {
                      const next = new Set(current)
                      if (event.target.checked) next.add(item.id)
                      else next.delete(item.id)
                      return next
                    })} />
                  </td>
                  <td>
                    <div className="product-cell">
                      {item.images[0] ? <img src={item.images[0]} alt="" loading="lazy" onError={(e) => (e.currentTarget.style.visibility = 'hidden')} /> : <div className="img-placeholder" />}
                      <div>
                        <button type="button" className="link" onClick={() => void openEditor(item)}>{item.title || '（无标题）'}</button>
                        <span>{item.sku}{item.remoteProductId ? ` · Alibaba ${item.remoteProductId}` : ''}</span>
                      </div>
                    </div>
                  </td>
                  <td className="col-account">
                    <AccountCell product={item} accounts={accounts} />
                  </td>
                  <td className="col-cat">
                    {item.categoryId ? <><div>{item.categoryId}</div><small className="muted clip" title={item.categoryPath || item.categoryName}>{item.categoryPath || item.categoryName}</small></> : <span className="muted">-</span>}
                  </td>
                  <td className="col-num">
                    <div>{item.tieredPrices.length ? `${formatPrice(item.currency, Math.min(...item.tieredPrices.map((x) => x.price)))} 起` : formatPrice(item.currency, item.price)}</div>
                    <small className="muted">≥ {item.minimumOrderQuantity} {item.unit}</small>
                  </td>
                  <td className="col-num">{item.stock}</td>
                  <td className="col-state">
                    <button type="button" className="issue-button" onClick={() => setIssuesOf(item)}>
                      {blockers
                        ? <Pill tone={item.remoteProductId ? 'warn' : 'bad'}>{item.remoteProductId ? '内容不完整' : `${blockers} 个阻断`}</Pill>
                        : warnings ? <Pill tone="warn">{warnings} 个建议</Pill> : <Pill tone="good">通过</Pill>}
                    </button>
                  </td>
                  <td className="col-state">
                    <Pill tone={publishStateTone[item.publishState]}>{publishStateLabel[item.publishState]}</Pill>
                    {item.hasUnpublishedChanges && <Pill tone="warn">有未发布的修改</Pill>}
                    {variants > 0 && <Pill tone="neutral">{variants} 个规格</Pill>}
                    {item.remoteStatusMessage && <small className="muted clip" title={item.remoteStatusMessage}>{item.remoteStatusMessage}</small>}
                  </td>
                  <td className="col-actions row-actions">
                    <button type="button" className="icon" aria-label="编辑" title="编辑" onClick={() => void openEditor(item)}><Pencil size={15} /></button>
                    <button type="button" className="icon" aria-label="删除" title="删除" onClick={() => remove([item.id])}><Trash2 size={15} /></button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
        {loading && <div className="empty"><Spinner size={22} /></div>}
        {!loading && products.length === 0 && (
          <div className="empty">
            <FileSpreadsheet size={28} />
            <strong>还没有商品</strong>
            <span>下载模板填写后导入，或点击「新建」手动录入。也可以直接把 Excel 文件拖到上方工具栏。</span>
            <div className="actions">
              <button type="button" onClick={() => downloadUrl('/api/catalog/import/template.xlsx')}><Download size={16} /> 下载模板</button>
              <button type="button" className="primary" onClick={() => fileInput.current?.click()}><Upload size={16} /> 导入商品</button>
            </div>
          </div>
        )}
        {!loading && products.length > 0 && filtered.length === 0 && <div className="empty"><Layers3 size={24} /><span>没有符合条件的商品</span></div>}
        {filtered.length > 0 && (
          <Pagination page={currentPage} pageSize={pageSize} total={filtered.length} onPage={setPage} onPageSize={(size) => { setPageSize(size); setPage(1) }} />
        )}
      </section>

      {editing && (
        <ProductEditor
          key={editing === 'new' ? 'new' : editing.id}
          product={editing === 'new' ? null : editing}
          saving={busy === 'save'}
          onClose={() => setEditing(null)}
          onSave={(draft) => void saveProduct(draft)}
        />
      )}

      {issuesOf && (
        <Modal title={`质检结果 · ${issuesOf.sku}`} subtitle={issuesOf.title} onClose={() => setIssuesOf(null)}
          footer={<button type="button" className="primary" onClick={() => { void openEditor(issuesOf); setIssuesOf(null) }}><Pencil size={16} /> 去修改</button>}>
          {issuesOf.qualityIssues.length === 0
            ? <p className="good"><CheckCircle2 size={16} /> 全部检查通过。</p>
            : (
              <ul className="issues">
                {[...issuesOf.qualityIssues].sort((a, b) => (a.severity === 'Blocker' ? -1 : 0) - (b.severity === 'Blocker' ? -1 : 0)).map((issue, index) => (
                  <li key={index} className={issue.severity.toLowerCase()}>
                    <Pill tone={issue.severity === 'Blocker' ? 'bad' : issue.severity === 'Warning' ? 'warn' : 'info'}>{issue.severity === 'Blocker' ? '阻断' : issue.severity === 'Warning' ? '建议' : '提示'}</Pill>
                    {issue.message}
                  </li>
                ))}
              </ul>
            )}
          {issuesOf.remoteStatusMessage && <p className="muted">Alibaba：{issuesOf.remoteStatusMessage}（{formatDate(issuesOf.lastSyncedAt || issuesOf.lastPublishedAt)}）</p>}
        </Modal>
      )}

      {importJob && (
        <Modal
          title={importJob.status === 'Previewed' ? '确认表格导入' : '表格导入任务'}
          subtitle={`${importJob.fileName} · ${importModeLabel[importJob.mode]}`}
          onClose={() => setImportJob(null)}
          wide
          footer={
            <>
              {importJob.warningCount > 0 && <button type="button" onClick={() => downloadUrl(`/api/catalog/import-jobs/${importJob.id}/errors.xlsx`)}><Download size={16} /> 错误明细</button>}
              <button type="button" onClick={() => setImportJob(null)}>关闭</button>
              {['Previewed', 'Interrupted', 'Failed'].includes(importJob.status) && (
                <button type="button" className="primary" disabled={busy === 'import-commit'} onClick={() => void commitImport()}>
                  {busy === 'import-commit' ? <Spinner /> : <Upload size={16} />} {importJob.status === 'Previewed' ? '确认并后台导入' : '重新执行'}
                </button>
              )}
            </>
          }
        >
          <div className="import-summary">
            <Pill tone={importJob.status === 'Failed' || importJob.status === 'Interrupted' ? 'bad' : importJob.status === 'CompletedWithWarnings' ? 'warn' : 'good'}>{importStatusLabel[importJob.status]}</Pill>
            <span>共 {importJob.total} 行</span>
            <span>新增 {importJob.created}</span>
            <span>更新 {importJob.updated}</span>
            <span>跳过 {importJob.skipped}</span>
            <span>警告 {importJob.warningCount}</span>
          </div>
          {['Queued', 'Running'].includes(importJob.status) && (
            <div className="progress"><div style={{ width: `${importJob.total ? (importJob.processed / importJob.total) * 100 : 5}%` }} /></div>
          )}
          {importJob.message && <p className="muted">{importJob.message}</p>}
          {importJob.unknownHeaders.length > 0 && <p className="warn">未识别的列（将忽略）：{importJob.unknownHeaders.join('、')}</p>}
          {importJob.status === 'Previewed' && (
            <>
              <p className="muted">以下是预检结果，尚未写入任何商品。确认后任务将在后台执行并自动质检。</p>
              <div className="table-wrap flat import-preview">
                <table>
                  <thead><tr><th>行</th><th>商品</th><th>操作</th><th>变化字段 / 原因</th></tr></thead>
                  <tbody>
                    {importJob.preview.map((item) => (
                      <tr key={`${item.rowNumber}-${item.key}`}>
                        <td>{item.rowNumber}</td>
                        <td>{item.key}</td>
                        <td><Pill tone={item.action === 'Skip' ? 'warn' : item.action === 'Create' ? 'good' : 'info'}>{item.action === 'Create' ? '新增' : item.action === 'Update' ? '更新' : '跳过'}</Pill></td>
                        <td>{item.message || (item.fields.length ? item.fields.join('、') : '内容相同')}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {importJob.preview.length < importJob.total && <p className="muted">这里只展示前 {importJob.preview.length} 行预览。</p>}
            </>
          )}
          {importJob.warnings.length > 0
            ? <ul className="issues">{importJob.warnings.slice(0, 100).map((warning, index) => <li key={index} className="warning">{warning}</li>)}</ul>
            : importJob.status === 'Previewed' && <p className="good">预检没有发现警告。</p>}
          {importJob.status === 'Completed' || importJob.status === 'CompletedWithWarnings'
            ? <p className="muted">导入完成并已自动质检，商品列表已经刷新。</p>
            : null}
        </Modal>
      )}

      {pullResult && (
        <Modal title="从 Alibaba 导入完成" subtitle={`读取 ${pullResult.total} 个商品（${pullResult.pages} 页）`} onClose={() => setPullResult(null)}>
          <p>新建 {pullResult.created} 个，按 SKU 关联 {pullResult.linked} 个，更新状态 {pullResult.refreshed} 个。</p>
          {pullResult.missing > 0 && <p className="warn">{pullResult.missing} 个商品在 Alibaba 上已找不到（可能已删除），已标为下架，状态说明中有提示。</p>}
          {pullResult.warnings.map((warning, index) => <p key={index} className="warn">{warning}</p>)}
          {pullResult.created > 0 && <p className="muted">新建的商品已自动质检；多规格（SKU）商品在本系统中按单一价格和总库存显示。</p>}
        </Modal>
      )}

      {batch && <BatchResultDialog title={batch.title} result={batch.result} onClose={() => setBatch(null)} />}
    </div>
  )
}

function Metric({ label, value, tone, onClick }: { label: string; value: number; tone?: 'good' | 'warn' | 'bad'; onClick: () => void }) {
  return (
    <button type="button" className={clsx('metric', tone)} onClick={onClick}>
      <span>{label}</span>
      <strong>{value}</strong>
    </button>
  )
}

/** Owner account of the listing, plus the operating account when one was assigned explicitly. */
function AccountCell({ product, accounts }: { product: Product; accounts: AlibabaAccount[] }) {
  const owner = accounts.find((a) => !!product.ownerAliId && a.ownerAliIds.includes(product.ownerAliId))
  const operator = accountOf(product, accounts)
  if (!owner && !operator) return <span className="muted">-</span>
  return (
    <>
      <div className="clip" title={owner?.name}>{owner?.name ?? '（新建）'}</div>
      {operator && operator.id !== owner?.id && <small className="muted clip" title={`由「${operator.name}」操作`}>由 {operator.name} 操作</small>}
    </>
  )
}
