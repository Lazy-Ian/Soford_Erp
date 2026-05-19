import { useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  Code2,
  Download,
  FileSpreadsheet,
  ImageUp,
  Layers3,
  Loader2,
  PackageCheck,
  RefreshCw,
  Rocket,
  Search,
  ShieldCheck,
  Trash2,
  Upload,
  Video,
  X,
} from 'lucide-react'
import clsx from 'clsx'
import './App.css'

type PublishState = 'Draft' | 'Ready' | 'Published' | 'Failed'
type Severity = 'Info' | 'Warning' | 'Blocker'

type QualityIssue = { severity: Severity; field: string; message: string }
type Product = {
  id: string
  sku: string
  title: string
  categoryId: string
  currency: string
  price: number
  minimumOrderQuantity: number
  stock: number
  leadTimeDays: number
  description: string
  mainImageUrl: string
  detailImageUrls: string[]
  keywords: string[]
  attributes: Record<string, string>
  publishState: PublishState
  lastPublishMessage?: string
  qualityIssues: QualityIssue[]
  updatedAt: string
}

type AlibabaStatus = {
  isConfigured: boolean
  hasAppCredentials: boolean
  hasToken: boolean
  accessTokenExpiresAt?: string
  refreshTokenExpiresAt?: string
  baseUrl?: string
  productPublishPath?: string
  authBaseUrl?: string
  authTokenCreatePath?: string
  message: string
  rules: string[]
}

type AlibabaApiDefinition = {
  key: string
  area: string
  method: string
  payloadParameter: string
  requiresToken: boolean
  description: string
  comment: string
}

type ApiCallLog = {
  id: string
  createdAt: string
  apiKey: string
  method: string
  endpoint: string
  statusCode: number
  success: boolean
  message: string
  traceId: string
}

type PublishJob = {
  id: string
  productIds: string[]
  preflightOnly: boolean
  status: string
  message: string
  exportPath?: string
  createdAt: string
}

type AlibabaTokenResponse = { hasToken: boolean; accessTokenExpiresAt: string; refreshTokenExpiresAt: string }
type ImportResult = { imported: number; skipped: number; warnings: string[]; products: Product[] }
type PublishBatchResult = {
  status: string
  productCount: number
  blockedCount: number
  message: string
  exportPath?: string
  products?: Product[]
  results?: unknown[]
}

type QuickAction = {
  key: string
  label: string
  apiKey: string
  endpoint: string
  buildPayload: (product: Product) => Record<string, unknown>
}

const api = async <T,>(url: string, init?: RequestInit): Promise<T> => {
  const response = await fetch(url, init)
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `Request failed: ${response.status}`)
  }
  return response.json() as Promise<T>
}

const jsonPost = <T,>(url: string, body?: unknown) =>
  api<T>(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

const stateText: Record<PublishState, string> = {
  Draft: 'Draft',
  Ready: 'Ready',
  Published: 'Published',
  Failed: 'Failed',
}

const quickActions: QuickAction[] = [
  {
    key: 'predict',
    label: 'Predict Category',
    apiKey: 'category.predict',
    endpoint: '/api/integrations/alibaba/catalog/categories/predict',
    buildPayload: (product) => ({ title: product.title, description: product.description, image: product.mainImageUrl }),
  },
  {
    key: 'attributes',
    label: 'Query Attributes',
    apiKey: 'category.attributes',
    endpoint: '/api/integrations/alibaba/catalog/categories/attributes',
    buildPayload: (product) => ({ categoryId: product.categoryId }),
  },
  {
    key: 'create',
    label: 'Create Listing',
    apiKey: 'product.create',
    endpoint: '/api/integrations/alibaba/products/create',
    buildPayload: (product) => ({ productId: product.id }),
  },
  {
    key: 'update',
    label: 'Update Listing',
    apiKey: 'product.update',
    endpoint: '/api/integrations/alibaba/products/update',
    buildPayload: (product) => ({ productId: product.id }),
  },
  {
    key: 'status',
    label: 'Query Status',
    apiKey: 'product.status',
    endpoint: '/api/integrations/alibaba/products/status',
    buildPayload: (product) => ({ payload: { product_id: product.attributes.remoteProductId || product.sku } }),
  },
  {
    key: 'inventory',
    label: 'Sync Inventory',
    apiKey: 'product.inventory.update',
    endpoint: '/api/integrations/alibaba/products/inventory/update',
    buildPayload: (product) => ({ payload: { product_id: product.attributes.remoteProductId || product.sku, inventory: product.stock } }),
  },
  {
    key: 'price',
    label: 'Sync Price',
    apiKey: 'product.price.update',
    endpoint: '/api/integrations/alibaba/products/price/update',
    buildPayload: (product) => ({ payload: { product_id: product.attributes.remoteProductId || product.sku, price: product.price, currency: product.currency } }),
  },
]

const samplePayloads: Record<string, string> = {
  'category.predict': '{\n  "title": "wireless bluetooth speaker",\n  "description": "portable outdoor speaker",\n  "image": "https://example.com/image.jpg"\n}',
  'category.attributes': '{\n  "categoryId": "201896803"\n}',
  'photobank.group.list': '{\n  "id": 100001004\n}',
  'photobank.list': '{\n  "group_id": 100001004,\n  "current_page": 1,\n  "page_size": 20\n}',
  'product.search': '{\n  "page_no": 1,\n  "page_size": 20\n}',
  'product.status': '{\n  "product_id": "123456"\n}',
  'product.status.update': '{\n  "product_ids": "123456",\n  "status": "offline"\n}',
  'product.shippingTemplates': '{\n  "page_no": 1,\n  "page_size": 20\n}',
  'video.query': '{\n  "current_page": 1,\n  "page_size": 10\n}',
}

function App() {
  const fileInput = useRef<HTMLInputElement | null>(null)
  const imageInput = useRef<HTMLInputElement | null>(null)
  const [products, setProducts] = useState<Product[]>([])
  const [alibabaApis, setAlibabaApis] = useState<AlibabaApiDefinition[]>([])
  const [apiLogs, setApiLogs] = useState<ApiCallLog[]>([])
  const [publishJobs, setPublishJobs] = useState<PublishJob[]>([])
  const [status, setStatus] = useState<AlibabaStatus | null>(null)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [publishResult, setPublishResult] = useState<PublishBatchResult | null>(null)
  const [authCode, setAuthCode] = useState('')
  const [apiKey, setApiKey] = useState('category.predict')
  const [apiPayload, setApiPayload] = useState(samplePayloads['category.predict'])
  const [apiResult, setApiResult] = useState('')
  const [imageFolder, setImageFolder] = useState('')
  const [error, setError] = useState<string | null>(null)

  const refresh = async () => {
    setBusy('refresh')
    try {
      const [items, integration, apis, logs, jobs] = await Promise.all([
        api<Product[]>('/api/catalog/products'),
        api<AlibabaStatus>('/api/integrations/alibaba/status'),
        api<AlibabaApiDefinition[]>('/api/integrations/alibaba/apis'),
        api<ApiCallLog[]>('/api/integrations/alibaba/logs?take=20'),
        api<PublishJob[]>('/api/catalog/publish-jobs?take=20'),
      ])
      setProducts(items)
      setStatus(integration)
      setAlibabaApis(apis)
      setApiLogs(logs)
      setPublishJobs(jobs)
      setError(null)
    } catch (err) {
      setError(readError(err, 'Failed to load workspace data.'))
    } finally {
      setBusy(null)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const selectedProducts = useMemo(() => {
    const ids = selected
    return products.filter((item) => ids.has(item.id))
  }, [products, selected])

  const filtered = useMemo(() => {
    const key = query.trim().toLowerCase()
    if (!key) return products
    return products.filter((item) =>
      [item.sku, item.title, item.categoryId, item.publishState].some((value) => value.toLowerCase().includes(key)),
    )
  }, [products, query])

  const summary = useMemo(() => {
    const blockers = products.reduce((count, item) => count + item.qualityIssues.filter((issue) => issue.severity === 'Blocker').length, 0)
    const warnings = products.reduce((count, item) => count + item.qualityIssues.filter((issue) => issue.severity === 'Warning').length, 0)
    return {
      total: products.length,
      ready: products.filter((item) => item.publishState === 'Ready').length,
      published: products.filter((item) => item.publishState === 'Published').length,
      blockers,
      warnings,
    }
  }, [products])

  const configuredKeys = useMemo(() => new Set(alibabaApis.filter((item) => item.method).map((item) => item.key)), [alibabaApis])

  const importFile = async (file: File) => {
    const data = new FormData()
    data.append('file', file)
    setBusy('import')
    try {
      const result = await api<ImportResult>('/api/catalog/import', { method: 'POST', body: data })
      setNotice(`Imported ${result.imported} products, skipped ${result.skipped}.`)
      await refresh()
    } catch (err) {
      setError(readError(err, 'Import failed.'))
    } finally {
      setBusy(null)
    }
  }

  const uploadImage = async (file: File) => {
    const data = new FormData()
    data.append('file', file)
    if (imageFolder.trim()) data.append('folder', imageFolder.trim())
    setBusy('image-upload')
    try {
      const result = await api('/api/integrations/alibaba/images/upload', { method: 'POST', body: data })
      setApiResult(JSON.stringify(result, null, 2))
      setNotice('Image upload request completed.')
      await refresh()
    } catch (err) {
      setError(readError(err, 'Image upload failed.'))
    } finally {
      setBusy(null)
    }
  }

  const runQualityCheck = async () => {
    if (selectedProducts.length === 0) return
    setBusy('quality')
    try {
      await jsonPost('/api/catalog/quality-check', { productIds: selectedProducts.map((item) => item.id), preflightOnly: true })
      setNotice('Quality checks updated.')
      await refresh()
    } catch (err) {
      setError(readError(err, 'Quality check failed.'))
    } finally {
      setBusy(null)
    }
  }

  const preflightPublish = async () => {
    if (selectedProducts.length === 0) return
    setBusy('preflight')
    try {
      const result = await jsonPost<PublishBatchResult>('/api/catalog/publish', { productIds: selectedProducts.map((item) => item.id), preflightOnly: true })
      setPublishResult(result)
      await refresh()
    } catch (err) {
      setError(readError(err, 'Preflight failed.'))
    } finally {
      setBusy(null)
    }
  }

  const publish = async () => {
    if (selectedProducts.length === 0) return
    setBusy('publish')
    try {
      const result = await jsonPost<PublishBatchResult>('/api/catalog/publish', { productIds: selectedProducts.map((item) => item.id), preflightOnly: false })
      setPublishResult(result)
      await refresh()
    } catch (err) {
      setError(readError(err, 'Publish failed.'))
    } finally {
      setBusy(null)
    }
  }

  const createAlibabaToken = async () => {
    if (!authCode.trim()) {
      setError('Paste the seller authorization code first.')
      return
    }
    setBusy('token-create')
    try {
      const result = await jsonPost<AlibabaTokenResponse>('/api/integrations/alibaba/token/create', { code: authCode.trim() })
      setNotice(`Alibaba token saved. Access token expires at ${formatDate(result.accessTokenExpiresAt)}.`)
      setAuthCode('')
      await refresh()
    } catch (err) {
      setError(readError(err, 'Token creation failed.'))
    } finally {
      setBusy(null)
    }
  }

  const refreshAlibabaToken = async () => {
    setBusy('token-refresh')
    try {
      const result = await api<AlibabaTokenResponse>('/api/integrations/alibaba/token/refresh', { method: 'POST' })
      setNotice(`Alibaba token refreshed. Access token expires at ${formatDate(result.accessTokenExpiresAt)}.`)
      await refresh()
    } catch (err) {
      setError(readError(err, 'Token refresh failed.'))
    } finally {
      setBusy(null)
    }
  }

  const callAlibabaApi = async () => {
    setBusy('api-call')
    try {
      const payload = apiPayload.trim() ? JSON.parse(apiPayload) : {}
      const result = await jsonPost('/api/integrations/alibaba/call', { apiKey, payload })
      setApiResult(JSON.stringify(result, null, 2))
      setNotice('Alibaba API request completed.')
      await refresh()
    } catch (err) {
      setError(readError(err, 'Alibaba API request failed.'))
    } finally {
      setBusy(null)
    }
  }

  const runQuickAction = async (action: QuickAction) => {
    if (selectedProducts.length === 0) return
    setBusy(action.key)
    const results: unknown[] = []
    try {
      for (const product of selectedProducts) {
        results.push(await jsonPost(action.endpoint, action.buildPayload(product)))
      }
      setApiKey(action.apiKey)
      setApiPayload(JSON.stringify(action.buildPayload(selectedProducts[0]), null, 2))
      setApiResult(JSON.stringify(results.length === 1 ? results[0] : results, null, 2))
      setNotice(`${action.label} completed for ${selectedProducts.length} product(s).`)
      await refresh()
    } catch (err) {
      setError(readError(err, `${action.label} failed.`))
    } finally {
      setBusy(null)
    }
  }

  const deleteProduct = async (id: string) => {
    setBusy(id)
    try {
      await fetch(`/api/catalog/products/${id}`, { method: 'DELETE' })
      setSelected((next) => {
        const copy = new Set(next)
        copy.delete(id)
        return copy
      })
      await refresh()
    } finally {
      setBusy(null)
    }
  }

  const toggleAll = (checked: boolean) => setSelected(checked ? new Set(filtered.map((item) => item.id)) : new Set())

  const downloadTemplate = () => {
    const headers = ['Sku', 'Title', 'CategoryId', 'Currency', 'Price', 'MOQ', 'Stock', 'LeadTimeDays', 'MainImageUrl', 'DetailImageUrls', 'Keywords', 'Attributes', 'Description']
    const blob = new Blob([`${headers.join(',')}\n`], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = 'soford-alibaba-import-template.csv'
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div className="brand">
          <div className="brand-mark">S</div>
          <div>
            <strong>Soford ERP</strong>
            <span>Alibaba product publishing workspace</span>
          </div>
        </div>
        <div className={clsx('integration', status?.isConfigured && 'ok')}>
          {status?.isConfigured ? <ShieldCheck size={18} /> : <AlertTriangle size={18} />}
          <span>{status?.isConfigured ? 'API configured' : 'API incomplete'}</span>
        </div>
      </header>

      <section className="toolbar">
        <label className="search">
          <Search size={18} />
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search SKU, title, category, status" />
        </label>
        <div className="actions">
          <button type="button" onClick={downloadTemplate}><Download size={17} /> Template</button>
          <button type="button" onClick={() => fileInput.current?.click()} className="primary" disabled={busy === 'import'}>
            {busy === 'import' ? <Loader2 className="spin" size={17} /> : <Upload size={17} />}
            Import
          </button>
          <input ref={fileInput} type="file" accept=".xlsx,.csv" hidden onChange={(event) => {
            const file = event.target.files?.[0]
            if (file) void importFile(file)
            event.currentTarget.value = ''
          }} />
        </div>
      </section>

      <section className="metrics">
        <Metric label="Products" value={summary.total} />
        <Metric label="Ready" value={summary.ready} tone="good" />
        <Metric label="Published" value={summary.published} />
        <Metric label="Blockers" value={summary.blockers} tone={summary.blockers ? 'bad' : 'good'} />
        <Metric label="Warnings" value={summary.warnings} tone={summary.warnings ? 'warn' : undefined} />
      </section>

      {(error || notice) && (
        <div className={clsx('message', error && 'error')}>
          <span>{error || notice}</span>
          <button type="button" aria-label="Close" onClick={() => (error ? setError(null) : setNotice(null))}><X size={16} /></button>
        </div>
      )}

      <section className="import-zone" onDragOver={(event) => event.preventDefault()} onDrop={(event) => {
        event.preventDefault()
        const file = event.dataTransfer.files?.[0]
        if (file) void importFile(file)
      }}>
        <FileSpreadsheet size={22} />
        <span>Drop a .xlsx or .csv product file here</span>
        <button type="button" onClick={() => fileInput.current?.click()}>Choose file</button>
      </section>

      <section className="batchbar">
        <span>{selectedProducts.length} selected</span>
        <button type="button" onClick={runQualityCheck} disabled={selectedProducts.length === 0 || busy === 'quality'}>
          {busy === 'quality' ? <Loader2 className="spin" size={16} /> : <PackageCheck size={16} />}
          Quality
        </button>
        <button type="button" onClick={preflightPublish} disabled={selectedProducts.length === 0 || busy === 'preflight'}><CheckCircle2 size={16} /> Preflight</button>
        <button type="button" className="primary" onClick={publish} disabled={selectedProducts.length === 0 || busy === 'publish'}>
          {busy === 'publish' ? <Loader2 className="spin" size={16} /> : <Rocket size={16} />}
          Batch Publish
        </button>
        <a className="button-link" href="/api/catalog/export/alibaba.csv"><Download size={16} /> CSV Fallback</a>
        <button type="button" onClick={refresh} disabled={busy === 'refresh'}><RefreshCw size={16} /> Refresh</button>
      </section>

      <section className="workflow">
        {quickActions.map((action) => (
          <button
            key={action.key}
            type="button"
            onClick={() => void runQuickAction(action)}
            disabled={selectedProducts.length === 0 || busy === action.key || !configuredKeys.has(action.apiKey)}
            title={configuredKeys.has(action.apiKey) ? action.apiKey : `${action.apiKey} is not configured`}
          >
            {busy === action.key ? <Loader2 className="spin" size={16} /> : <Layers3 size={16} />}
            {action.label}
          </button>
        ))}
      </section>

      <ProductTable products={filtered} selected={selected} setSelected={setSelected} deleteProduct={deleteProduct} toggleAll={toggleAll} setPublishResult={setPublishResult} busy={busy} />

      <aside className="rules">
        <div className="rules-head">
          <div>
            <strong>Alibaba Authorization</strong>
            <span>{status?.message}</span>
          </div>
          <span className={clsx('pill', status?.hasToken ? 'ready' : 'failed')}>{status?.hasToken ? 'Token saved' : 'Missing token'}</span>
        </div>
        <div className="token-panel">
          <label>
            Authorization code
            <input value={authCode} onChange={(event) => setAuthCode(event.target.value)} placeholder="Paste seller callback code" />
          </label>
          <button type="button" className="primary" onClick={createAlibabaToken} disabled={busy === 'token-create'}>
            {busy === 'token-create' ? <Loader2 className="spin" size={16} /> : <ShieldCheck size={16} />}
            Create Token
          </button>
          <button type="button" onClick={refreshAlibabaToken} disabled={!status?.hasToken || busy === 'token-refresh'}>
            {busy === 'token-refresh' ? <Loader2 className="spin" size={16} /> : <RefreshCw size={16} />}
            Refresh Token
          </button>
        </div>
        <div className="token-meta">
          <span>Auth endpoint: {status?.authBaseUrl || '-'} {status?.authTokenCreatePath || ''}</span>
          <span>Access expires: {formatDate(status?.accessTokenExpiresAt)}</span>
          <span>Refresh expires: {formatDate(status?.refreshTokenExpiresAt)}</span>
        </div>
      </aside>

      <section className="api-center">
        <div className="api-console">
          <div>
            <strong>API Workbench</strong>
            <span>Use direct Alibaba wrappers for testing and recovery operations.</span>
          </div>
          <label>
            API
            <select value={apiKey} onChange={(event) => {
              setApiKey(event.target.value)
              setApiPayload(samplePayloads[event.target.value] || '{\n}\n')
            }}>
              {alibabaApis.map((item) => <option key={item.key} value={item.key}>{item.area} / {item.key}</option>)}
            </select>
          </label>
          <label>
            Payload JSON
            <textarea value={apiPayload} onChange={(event) => setApiPayload(event.target.value)} />
          </label>
          <button type="button" className="primary" onClick={callAlibabaApi} disabled={busy === 'api-call'}>
            {busy === 'api-call' ? <Loader2 className="spin" size={16} /> : <Code2 size={16} />}
            Call API
          </button>
          {apiResult && <pre>{apiResult}</pre>}
        </div>
        <div className="resource-panel">
          <div className="api-console compact">
            <strong>Images</strong>
            <span>Upload to Alibaba photobank, then use returned URLs or IDs in listing payloads.</span>
            <label>
              Folder or group
              <input value={imageFolder} onChange={(event) => setImageFolder(event.target.value)} placeholder="Optional folder/group" />
            </label>
            <button type="button" onClick={() => imageInput.current?.click()} disabled={busy === 'image-upload' || !configuredKeys.has('image.upload')}>
              {busy === 'image-upload' ? <Loader2 className="spin" size={16} /> : <ImageUp size={16} />}
              Upload Image
            </button>
            <button type="button" onClick={() => {
              setApiKey('photobank.group.list')
              setApiPayload(samplePayloads['photobank.group.list'])
            }}><Layers3 size={16} /> Group List Payload</button>
            <input ref={imageInput} type="file" accept="image/*" hidden onChange={(event) => {
              const file = event.target.files?.[0]
              if (file) void uploadImage(file)
              event.currentTarget.value = ''
            }} />
          </div>
          <div className="api-console compact">
            <strong>Videos</strong>
            <span>Query, upload, poll upload result, then attach a main video to a product.</span>
            <button type="button" onClick={() => {
              setApiKey('video.query')
              setApiPayload(samplePayloads['video.query'])
            }}><Video size={16} /> Query Videos</button>
            <button type="button" onClick={() => {
              setApiKey('video.relation.product.main')
              setApiPayload('{\n  "product_id": "123456",\n  "video_id": "12345"\n}')
            }}><Video size={16} /> Main Video Payload</button>
          </div>
        </div>
      </section>

      <section className="api-list">
        {alibabaApis.map((item) => (
          <article key={item.key} className={clsx(!item.method && 'missing')}>
            <div>
              <strong>{item.key}</strong>
              <span>{item.area} · {item.requiresToken ? 'token required' : 'no token'}</span>
            </div>
            <p>{item.description}</p>
            <code>{item.method || 'method not configured'}</code>
            <small>{item.comment}</small>
          </article>
        ))}
      </section>

      <section className="ops-grid">
        <div className="ops-panel">
          <strong>Publish Jobs</strong>
          <span>Preflight, fallback export, and real publish attempts.</span>
          <div className="ops-list">
            {publishJobs.length === 0 && <p>No publish jobs yet.</p>}
            {publishJobs.map((job) => (
              <article key={job.id}>
                <div><b>{job.status}</b><small>{formatDate(job.createdAt)}</small></div>
                <p>{job.message}</p>
                <code>{job.productIds.length} products · {job.preflightOnly ? 'preflight' : 'publish'}</code>
              </article>
            ))}
          </div>
        </div>
        <div className="ops-panel">
          <strong>API Logs</strong>
          <span>Latest Alibaba API audit trail.</span>
          <div className="ops-list">
            {apiLogs.length === 0 && <p>No API logs yet.</p>}
            {apiLogs.map((log) => (
              <article key={log.id} className={clsx(!log.success && 'failed')}>
                <div><b>{log.apiKey}</b><small>{formatDate(log.createdAt)}</small></div>
                <p>{log.statusCode} · {log.message}</p>
                <code>{log.traceId}</code>
              </article>
            ))}
          </div>
        </div>
      </section>

      {publishResult && <PublishModal result={publishResult} close={() => setPublishResult(null)} />}
    </main>
  )
}

function ProductTable({ products, selected, setSelected, deleteProduct, toggleAll, setPublishResult, busy }: {
  products: Product[]
  selected: Set<string>
  setSelected: React.Dispatch<React.SetStateAction<Set<string>>>
  deleteProduct: (id: string) => void
  toggleAll: (checked: boolean) => void
  setPublishResult: React.Dispatch<React.SetStateAction<PublishBatchResult | null>>
  busy: string | null
}) {
  return (
    <section className="table-wrap">
      <table>
        <thead>
          <tr>
            <th className="check"><input type="checkbox" checked={products.length > 0 && products.every((item) => selected.has(item.id))} onChange={(event) => toggleAll(event.target.checked)} /></th>
            <th>Product</th>
            <th>Category</th>
            <th>Price</th>
            <th>MOQ</th>
            <th>Stock</th>
            <th>Status</th>
            <th>Issues</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {products.map((item) => {
            const blockers = item.qualityIssues.filter((issue) => issue.severity === 'Blocker').length
            const warnings = item.qualityIssues.filter((issue) => issue.severity === 'Warning').length
            return (
              <tr key={item.id}>
                <td className="check">
                  <input type="checkbox" checked={selected.has(item.id)} onChange={(event) => setSelected((next) => {
                    const copy = new Set(next)
                    if (event.target.checked) copy.add(item.id)
                    else copy.delete(item.id)
                    return copy
                  })} />
                </td>
                <td>
                  <div className="product-cell">
                    <img src={item.mainImageUrl} alt="" onError={(event) => (event.currentTarget.style.display = 'none')} />
                    <div><strong>{item.title || 'Untitled product'}</strong><span>{item.sku || 'Missing SKU'}</span></div>
                  </div>
                </td>
                <td>{item.categoryId || '-'}</td>
                <td>{item.currency} {item.price}</td>
                <td>{item.minimumOrderQuantity}</td>
                <td>{item.stock}</td>
                <td><span className={clsx('pill', item.publishState.toLowerCase())}>{stateText[item.publishState]}</span></td>
                <td>
                  <button type="button" className="issue-button" onClick={() => setPublishResult({ status: 'Issues', productCount: 1, blockedCount: blockers, message: item.title, products: [item] })}>
                    <span className={clsx(blockers ? 'bad' : warnings ? 'warn' : 'good')}>{blockers ? `${blockers} blockers` : warnings ? `${warnings} warnings` : 'Passed'}</span>
                  </button>
                </td>
                <td className="row-actions"><button type="button" aria-label="Delete" onClick={() => deleteProduct(item.id)} disabled={busy === item.id}><Trash2 size={16} /></button></td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {products.length === 0 && (
        <div className="empty">
          <FileSpreadsheet size={26} />
          <strong>No products yet</strong>
          <span>Import a real product sheet to start quality checks and Alibaba publishing.</span>
        </div>
      )}
    </section>
  )
}

function PublishModal({ result, close }: { result: PublishBatchResult; close: () => void }) {
  return (
    <div className="modal-backdrop" role="presentation" onClick={close}>
      <div className="modal" role="dialog" aria-modal="true" onClick={(event) => event.stopPropagation()}>
        <header>
          <div><strong>{result.status === 'Issues' ? 'Product Issues' : 'Publish Result'}</strong><span>{result.message}</span></div>
          <button type="button" aria-label="Close" onClick={close}><X size={18} /></button>
        </header>
        {result.exportPath && <div className="export-path">Fallback file: {result.exportPath}</div>}
        <div className="issue-list">
          {(result.products || []).map((item) => (
            <div key={item.id} className="issue-product">
              <strong>{item.title || item.sku}</strong>
              {item.qualityIssues.length === 0 ? <span className="good">Passed</span> : item.qualityIssues.map((issue, index) => (
                <p key={`${issue.field}-${index}`} className={issue.severity.toLowerCase()}>
                  {issue.severity} · {issue.field} · {issue.message}
                </p>
              ))}
            </div>
          ))}
          {result.results && <pre>{JSON.stringify(result.results, null, 2)}</pre>}
        </div>
      </div>
    </div>
  )
}

function Metric({ label, value, tone }: { label: string; value: number; tone?: 'good' | 'warn' | 'bad' }) {
  return <div className={clsx('metric', tone)}><span>{label}</span><strong>{value}</strong></div>
}

function formatDate(value?: string) {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'
  return date.toLocaleString()
}

function readError(err: unknown, fallback: string) {
  if (!(err instanceof Error)) return fallback
  try {
    const parsed = JSON.parse(err.message)
    return parsed.title || parsed.detail || err.message
  } catch {
    return err.message || fallback
  }
}

export default App
