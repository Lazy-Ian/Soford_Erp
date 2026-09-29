import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ArrowDownToLine, ArrowUpFromLine, CheckCircle2, CloudDownload, Download, FileSpreadsheet, Layers3, PackageCheck, Pencil,
  Plus, RefreshCw, Rocket, Search, Tags, Trash2, Upload, Warehouse, DollarSign,
} from 'lucide-react'
import clsx from 'clsx'
import { api, errorText, post, type BatchResult, type ImportResult, type Product, type ProductDraft, type PublishJob, type PublishState, type PullResult } from '../api'
import { BatchResultDialog, Modal, Pagination, Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { downloadUrl, formatDate, formatPrice, jobStatusLabel, publishStateLabel, publishStateTone } from '../format'
import { ProductEditor } from './ProductEditor'

type Filter = 'all' | 'incomplete' | 'ready' | PublishState

const filters: { key: Filter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'incomplete', label: '待完善' },
  { key: 'ready', label: '可发布' },
  { key: 'Pending', label: '审核中' },
  { key: 'Online', label: '已上架' },
  { key: 'Offline', label: '已下架' },
  { key: 'Failed', label: '发布失败' },
]

export function ProductsPage() {
  const { notify, confirm, alibaba, navigate } = useApp()
  const [products, setProducts] = useState<Product[]>([])
  const [loading, setLoading] = useState(true)
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [editing, setEditing] = useState<Product | 'new' | null>(null)
  const [issuesOf, setIssuesOf] = useState<Product | null>(null)
  const [batch, setBatch] = useState<{ title: string; result: BatchResult } | null>(null)
  const [importResult, setImportResult] = useState<ImportResult | null>(null)
  const [job, setJob] = useState<PublishJob | null>(null)
  const [pollTick, setPollTick] = useState(0)
  const [pullResult, setPullResult] = useState<PullResult | null>(null)
  const fileInput = useRef<HTMLInputElement | null>(null)

  const load = useCallback(async () => {
    try {
      setProducts(await api<Product[]>('/api/catalog/products'))
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
        if (next.processed !== job.processed || next.status !== job.status) void load()
        setJob(next)
      } catch (err) {
        // Keep polling after a transient failure instead of freezing the progress bar.
        notify('error', errorText(err, '查询发布进度失败，稍后重试'))
        window.setTimeout(() => setPollTick((tick) => tick + 1), 3000)
      }
    }, 1500)
    return () => window.clearTimeout(timer)
  }, [job, pollTick, load, notify])

  const filtered = useMemo(() => {
    const key = query.trim().toLowerCase()
    return products.filter((item) => {
      if (filter === 'incomplete' && item.localState !== 'Incomplete') return false
      if (filter === 'ready' && !(item.localState === 'Ready' && item.publishState === 'NotPublished')) return false
      if (!['all', 'incomplete', 'ready'].includes(filter) && item.publishState !== filter) return false
      if (!key) return true
      return [item.sku, item.title, item.categoryId, item.remoteProductId ?? '', item.brandName].some((value) => value.toLowerCase().includes(key))
    })
  }, [products, query, filter])

  const pages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const currentPage = Math.min(page, pages)
  const visible = filtered.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  // Selection only ever covers products that are currently visible under the filter, so batch actions never hit hidden rows.
  const selectedIds = filtered.filter((item) => selected.has(item.id)).map((item) => item.id)
  const selectedProducts = products.filter((item) => selectedIds.includes(item.id))
  const publishedSelected = selectedProducts.filter((item) => item.remoteProductId).length

  const summary = useMemo(() => ({
    total: products.length,
    incomplete: products.filter((x) => x.localState === 'Incomplete').length,
    ready: products.filter((x) => x.localState === 'Ready' && x.publishState === 'NotPublished').length,
    pending: products.filter((x) => x.publishState === 'Pending').length,
    online: products.filter((x) => x.publishState === 'Online').length,
    failed: products.filter((x) => x.publishState === 'Failed').length,
  }), [products])

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

  const runBatch = (key: string, title: string, url: string) =>
    run(key, async () => {
      const result = await post<BatchResult>(url, { productIds: selectedIds })
      setBatch({ title, result })
      await load()
    })

  const importFile = (file: File) =>
    run('import', async () => {
      const data = new FormData()
      data.append('file', file)
      const result = await api<ImportResult>('/api/catalog/import', { method: 'POST', body: data })
      setImportResult(result)
      await load()
    })

  const saveProduct = (draft: ProductDraft) =>
    run('save', async () => {
      const saved = editing && editing !== 'new'
        ? await post<Product>(`/api/catalog/products/${editing.id}`, draft, 'PUT')
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
    confirm({
      title: '发布到 Alibaba',
      confirmText: '开始发布',
      message: (
        <>
          <p>将提交 {selectedIds.length} 个商品：新发布 {selectedIds.length - updates} 个，更新已发布商品 {updates} 个。</p>
          {blocked.length > 0 && <p className="warn">其中 {blocked.length} 个存在阻断问题，会被跳过并在结果中列出原因。</p>}
          <p className="muted">发布在后台执行，可以离开本页面，进度可在「发布任务」查看。</p>
        </>
      ),
      onConfirm: () =>
        void run('publish', async () => {
          const created = await post<PublishJob>('/api/catalog/publish', { productIds: selectedIds })
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
          <p className="muted">商品较多时需要一些时间（每页 20 个）。</p>
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
      const response = await fetch('/api/catalog/export/products.csv', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ productIds: selectedIds }),
      })
      if (!response.ok) throw new Error(`导出失败（${response.status}）`)
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
      onConfirm: () => void runBatch(online ? 'online' : 'offline', online ? '上架结果' : '下架结果', online ? '/api/catalog/online' : '/api/catalog/offline'),
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
        <Metric label="商品总数" value={summary.total} onClick={() => setFilter('all')} />
        <Metric label="待完善" value={summary.incomplete} tone={summary.incomplete ? 'warn' : undefined} onClick={() => setFilter('incomplete')} />
        <Metric label="可发布" value={summary.ready} tone="good" onClick={() => setFilter('ready')} />
        <Metric label="审核中" value={summary.pending} onClick={() => setFilter('Pending')} />
        <Metric label="已上架" value={summary.online} tone="good" onClick={() => setFilter('Online')} />
        <Metric label="发布失败" value={summary.failed} tone={summary.failed ? 'bad' : undefined} onClick={() => setFilter('Failed')} />
      </section>

      {needsAlibaba && (
        <div className="banner warn">
          <span>Alibaba 店铺尚未连接，发布、类目预测、同步等功能暂不可用。</span>
          <button type="button" onClick={() => navigate('connection')}>去连接店铺</button>
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
            <button key={item.key} type="button" className={clsx(filter === item.key && 'active')} onClick={() => { setFilter(item.key); setPage(1) }}>{item.label}</button>
          ))}
        </div>
        <div className="actions">
          <button type="button" onClick={() => setEditing('new')}><Plus size={16} /> 新建</button>
          <button type="button" onClick={pullFromAlibaba} disabled={busy !== null || needsAlibaba} title={alibabaHint ?? '把 Alibaba 店铺中已有的商品导入到本系统'}>
            {busy === 'pull' ? <Spinner /> : <CloudDownload size={16} />} 从 Alibaba 导入
          </button>
          <button type="button" onClick={() => downloadUrl('/api/catalog/import/template.xlsx')}><Download size={16} /> 模板</button>
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
        <span>{none ? '勾选商品后可批量操作' : `已选 ${selectedIds.length} 个${publishedSelected ? `（已发布 ${publishedSelected} 个）` : ''}`}</span>
        <button type="button" onClick={qualityCheck} disabled={none || busy !== null}>{busy === 'quality' ? <Spinner /> : <PackageCheck size={16} />} 质检</button>
        <button type="button" onClick={() => void runBatch('predict', '类目预测结果', '/api/catalog/predict-category')} disabled={none || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'predict' ? <Spinner /> : <Tags size={16} />} 预测类目
        </button>
        <button type="button" className="primary" onClick={publish} disabled={none || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'publish' ? <Spinner /> : <Rocket size={16} />} 发布到 Alibaba
        </button>
        <span className="divider" />
        <button type="button" onClick={() => void runBatch('status', '状态同步结果', '/api/catalog/sync/status')} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint ?? '查询 Alibaba 上架/审核状态'}>
          {busy === 'status' ? <Spinner /> : <RefreshCw size={16} />} 同步状态
        </button>
        <button type="button" onClick={() => void runBatch('inventory', '库存同步结果', '/api/catalog/sync/inventory')} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint}>
          {busy === 'inventory' ? <Spinner /> : <Warehouse size={16} />} 同步库存
        </button>
        <button type="button" onClick={() => void runBatch('price', '价格同步结果', '/api/catalog/sync/price')} disabled={!publishedSelected || busy !== null || needsAlibaba} title={alibabaHint}>
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
        <button type="button" className="danger-text" onClick={() => remove(selectedIds)} disabled={none || busy !== null}><Trash2 size={16} /> 删除</button>
      </section>

      {job && (
        <div className={clsx('job-progress', job.failed > 0 && 'has-errors')}>
          <div>
            <strong>发布任务 · {jobStatusLabel[job.status]}</strong>
            <span>已处理 {job.processed}/{job.total}，成功 {job.succeeded}，失败 {job.failed}</span>
          </div>
          <div className="progress"><div style={{ width: `${job.total ? (job.processed / job.total) * 100 : 0}%` }} /></div>
          {!['Queued', 'Running'].includes(job.status) && (
            <>
              <button type="button" onClick={() => setBatch({ title: '发布结果', result: { total: job.total, succeeded: job.succeeded, failed: job.failed, items: job.items } })}>查看结果</button>
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
                        <button type="button" className="link" onClick={() => setEditing(item)}>{item.title || '（无标题）'}</button>
                        <span>{item.sku}{item.remoteProductId ? ` · Alibaba ${item.remoteProductId}` : ''}</span>
                      </div>
                    </div>
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
                      {blockers ? <Pill tone="bad">{blockers} 个阻断</Pill> : warnings ? <Pill tone="warn">{warnings} 个建议</Pill> : <Pill tone="good">通过</Pill>}
                    </button>
                  </td>
                  <td className="col-state">
                    <Pill tone={publishStateTone[item.publishState]}>{publishStateLabel[item.publishState]}</Pill>
                    {item.hasUnpublishedChanges && <Pill tone="warn">有未发布的修改</Pill>}
                    {item.remoteStatusMessage && <small className="muted clip" title={item.remoteStatusMessage}>{item.remoteStatusMessage}</small>}
                  </td>
                  <td className="col-actions row-actions">
                    <button type="button" className="icon" aria-label="编辑" title="编辑" onClick={() => setEditing(item)}><Pencil size={15} /></button>
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
          footer={<button type="button" className="primary" onClick={() => { setEditing(issuesOf); setIssuesOf(null) }}><Pencil size={16} /> 去修改</button>}>
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

      {importResult && (
        <Modal title="导入完成" subtitle={`新增 ${importResult.created}，更新 ${importResult.updated}，跳过 ${importResult.skipped}`} onClose={() => setImportResult(null)}>
          {importResult.unknownHeaders.length > 0 && <p className="warn">未识别的列（已忽略）：{importResult.unknownHeaders.join('、')}</p>}
          {importResult.warnings.length > 0
            ? <ul className="issues">{importResult.warnings.map((warning, index) => <li key={index} className="warning">{warning}</li>)}</ul>
            : <p className="good">没有警告。</p>}
          <p className="muted">导入后已自动质检，可在列表「质检」列查看每个商品的问题。</p>
        </Modal>
      )}

      {pullResult && (
        <Modal title="从 Alibaba 导入完成" subtitle={`读取 ${pullResult.total} 个商品（${pullResult.pages} 页）`} onClose={() => setPullResult(null)}>
          <p>新建 {pullResult.created} 个，按 SKU 关联 {pullResult.linked} 个，更新状态 {pullResult.refreshed} 个。</p>
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
