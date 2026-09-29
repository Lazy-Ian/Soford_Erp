import { useEffect, useMemo, useState } from 'react'
import { Code2 } from 'lucide-react'
import clsx from 'clsx'
import { api, errorText, post, type AlibabaApiDefinition, type AlibabaApiResult } from '../api'
import { Pill, Spinner } from '../components/ui'
import { useApp } from '../context'

const samples: Record<string, unknown> = {
  'category.predict': { title: 'portable bluetooth speaker', description: 'outdoor waterproof speaker' },
  'category.attributes': { category_id: 201896803 },
  'category.get': { cat_id: 0 },
  'photobank.group.list': {},
  'photobank.list': { current_page: 1, page_size: 20 },
  'product.search': { current_page: 1, page_size: 20 },
  'product.get': { product_id: 0 },
  'product.status': { product_id: 0 },
  'product.status.update': { product_id_list: [0], action: 'offline' },
  'product.inventory.update': { product_id: 0, inventory: 100 },
  'product.shippingTemplates': {},
  'video.query': { current_page: 1, page_size: 10 },
}

export function WorkbenchPage() {
  const { notify } = useApp()
  const [apis, setApis] = useState<AlibabaApiDefinition[]>([])
  const [apiKey, setApiKey] = useState('category.predict')
  const [payload, setPayload] = useState(JSON.stringify(samples['category.predict'], null, 2))
  const [result, setResult] = useState<AlibabaApiResult | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api<AlibabaApiDefinition[]>('/api/integrations/alibaba/apis')
      .then(setApis)
      .catch((err) => notify('error', errorText(err, '加载接口列表失败')))
  }, [notify])

  const current = useMemo(() => apis.find((x) => x.key === apiKey), [apis, apiKey])

  const call = async () => {
    let body: unknown
    try {
      body = payload.trim() ? JSON.parse(payload) : {}
    } catch {
      notify('error', '参数不是合法的 JSON。')
      return
    }
    setBusy(true)
    try {
      setResult(await post<AlibabaApiResult>('/api/integrations/alibaba/call', { apiKey, payload: body }))
    } catch (err) {
      notify('error', errorText(err, '调用失败'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page workbench">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>API 调试台</strong>
            <span>直接调用已登记的 Alibaba 接口，用于排障。参数中的对象会以 JSON 字符串形式发送。</span>
          </div>
        </div>
        <label className="field">接口
          <select value={apiKey} onChange={(e) => {
            setApiKey(e.target.value)
            setPayload(JSON.stringify(samples[e.target.value] ?? {}, null, 2))
            setResult(null)
          }}>
            {apis.map((item) => (
              <option key={item.key} value={item.key} disabled={!item.isConfigured}>{item.area} / {item.key} — {item.description}{item.isConfigured ? '' : '（未配置）'}</option>
            ))}
          </select>
        </label>
        {current && <p className="muted">路径 <code>{current.path || '未配置'}</code> · {current.requiresToken ? '需要店铺授权' : '无需授权'}</p>}
        <label className="field">参数 JSON
          <textarea className="code-input" value={payload} onChange={(e) => setPayload(e.target.value)} rows={10} spellCheck={false} />
        </label>
        <div className="actions left">
          <button type="button" className="primary" onClick={() => void call()} disabled={busy || !current?.isConfigured}>
            {busy ? <Spinner /> : <Code2 size={16} />} 调用
          </button>
        </div>
      </section>
      {result && (
        <section className="card">
          <div className="card-head">
            <div>
              <strong>响应</strong>
              <span>{result.durationMs}ms · request_id {result.requestId || '-'}</span>
            </div>
            <Pill tone={result.success ? 'good' : 'bad'}>{result.success ? '成功' : `${result.errorCode ?? '失败'}`}</Pill>
          </div>
          {!result.success && result.errorMessage && <p className={clsx('bad')}>{result.errorMessage}</p>}
          <pre className="code tall">{JSON.stringify(result.json ?? result.rawBody ?? null, null, 2)}</pre>
        </section>
      )}
    </div>
  )
}
