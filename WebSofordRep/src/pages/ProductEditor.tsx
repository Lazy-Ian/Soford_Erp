import { useCallback, useRef, useState } from 'react'
import { ArrowDown, ArrowUp, CheckCircle2, Code2, ImageUp, ListChecks, Plus, Sparkles, Trash2, Wand2 } from 'lucide-react'
import { api, errorText, post, type CategoryAttributeSet, type Features, type ListingSuggestion, type Product, type ProductDraft } from '../api'
import { useInitialLoad } from '../hooks'
import { Modal, Spinner } from '../components/ui'
import { useApp } from '../context'
import { splitList } from '../format'

const MAX_IMAGES = 6

function emptyDraft(): ProductDraft {
  return {
    sku: '',
    title: '',
    description: '',
    keywords: [],
    brandName: '',
    modelNumber: '',
    language: 'en_US',
    categoryId: '',
    categoryName: '',
    categoryPath: '',
    attributes: {},
    currency: 'USD',
    price: 0,
    tieredPrices: [],
    minimumOrderQuantity: 1,
    unit: 'Piece',
    stock: 0,
    leadTimeDays: 7,
    shippingTemplateId: '',
    weightKg: null,
    lengthCm: null,
    widthCm: null,
    heightCm: null,
    images: [],
    aiOptimize: false,
  }
}

function toDraft(product: Product | null): ProductDraft {
  if (!product) return emptyDraft()
  const draft = emptyDraft()
  for (const key of Object.keys(draft) as (keyof ProductDraft)[]) {
    ;(draft as Record<string, unknown>)[key] = product[key] ?? draft[key]
  }
  return draft
}

type AttributeRow = { name: string; value: string }

export function ProductEditor({ product, saving, onClose, onSave }: {
  product: Product | null
  saving: boolean
  onClose: () => void
  onSave: (draft: ProductDraft) => void
}) {
  const { notify, alibaba } = useApp()
  const initial = toDraft(product)
  const [form, setForm] = useState<ProductDraft>(initial)
  const [keywordText, setKeywordText] = useState(initial.keywords.join('; '))
  const [attributes, setAttributes] = useState<AttributeRow[]>(Object.entries(initial.attributes).map(([name, value]) => ({ name, value })))
  const [categorySet, setCategorySet] = useState<CategoryAttributeSet | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<string | null>(null)
  const [newImage, setNewImage] = useState('')
  const fileInput = useRef<HTMLInputElement | null>(null)
  const [snapshot] = useState(() => JSON.stringify([form, keywordText, attributes]))
  const [features, setFeatures] = useState<Features | null>(null)
  const [suggestion, setSuggestion] = useState<ListingSuggestion | null>(null)

  const loadFeatures = useCallback(async () => {
    setFeatures(await api<Features>('/api/system/features').catch(() => null))
  }, [])
  useInitialLoad(loadFeatures)

  const suggest = async () => {
    if (!product) return
    setBusy('suggest')
    setError(null)
    try {
      setSuggestion(await post<ListingSuggestion>(`/api/catalog/products/${product.id}/ai-suggest`))
    } catch (err) {
      setError(errorText(err, 'AI 建议失败'))
    } finally {
      setBusy(null)
    }
  }

  // Escape, the backdrop and 取消 all come through here, so an accidental click never throws away edits.
  const close = () => {
    if (JSON.stringify([form, keywordText, attributes]) === snapshot || window.confirm('有未保存的修改，确定放弃并关闭吗？')) onClose()
  }

  const set = <K extends keyof ProductDraft>(key: K, value: ProductDraft[K]) => setForm((current) => ({ ...current, [key]: value }))
  const num = (value: string) => (value.trim() === '' ? 0 : Number(value))
  const optionalNum = (value: string) => (value.trim() === '' ? null : Number(value))

  const loadCategoryAttributes = async (refresh = false) => {
    if (!form.categoryId.trim()) {
      setError('请先填写类目 ID。')
      return
    }
    setBusy('attributes')
    setError(null)
    try {
      const result = await api<CategoryAttributeSet>(`/api/integrations/alibaba/categories/${encodeURIComponent(form.categoryId.trim())}/attributes${refresh ? '?refresh=true' : ''}`)
      setCategorySet(result)
      // Add rows for attributes that are not in the form yet, required ones first.
      setAttributes((rows) => {
        const existing = new Set(rows.map((row) => row.name.toLowerCase()))
        const additions = [...result.attributes]
          .sort((a, b) => Number(b.required) - Number(a.required))
          .filter((attribute) => attribute.required && !existing.has(attribute.name.toLowerCase()))
          .map((attribute) => ({ name: attribute.name, value: '' }))
        return [...rows, ...additions]
      })
      notify('success', `已加载 ${result.attributes.length} 个类目属性，其中必填 ${result.attributes.filter((x) => x.required).length} 个。`)
    } catch (err) {
      setError(errorText(err, '加载类目属性失败'))
    } finally {
      setBusy(null)
    }
  }

  const uploadImage = async (file: File) => {
    if (form.images.length >= MAX_IMAGES) {
      setError(`最多 ${MAX_IMAGES} 张图片。`)
      return
    }
    const data = new FormData()
    data.append('file', file)
    setBusy('upload')
    setError(null)
    try {
      const result = await api<{ url: string }>('/api/integrations/alibaba/images/upload', { method: 'POST', body: data })
      // Functional update: keeps any image edits made while the upload was running.
      setForm((current) => ({ ...current, images: [...current.images, result.url].slice(0, MAX_IMAGES) }))
      notify('success', '图片已上传到 Alibaba 图片银行。')
    } catch (err) {
      setError(errorText(err, '图片上传失败'))
    } finally {
      setBusy(null)
    }
  }

  const loadPreview = async () => {
    if (!product) return
    setBusy('preview')
    try {
      const result = await api<unknown>(`/api/catalog/products/${product.id}/listing-preview`)
      setPreview(JSON.stringify(result, null, 2))
    } catch (err) {
      setError(errorText(err, '生成报文失败'))
    } finally {
      setBusy(null)
    }
  }

  const moveImage = (index: number, delta: number) => {
    const next = [...form.images]
    const target = index + delta
    if (target < 0 || target >= next.length) return
    ;[next[index], next[target]] = [next[target], next[index]]
    set('images', next)
  }

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!form.sku.trim() || !form.title.trim()) {
      setError('SKU 和标题为必填项。')
      return
    }
    const attributeMap: Record<string, string> = {}
    for (const row of attributes) {
      if (row.name.trim() && row.value.trim()) attributeMap[row.name.trim()] = row.value.trim()
    }
    setError(null)
    onSave({
      ...form,
      keywords: splitList(keywordText),
      attributes: attributeMap,
      tieredPrices: form.tieredPrices.filter((tier) => tier.quantity > 0 && tier.price > 0),
    })
  }

  const attributeOptions = (name: string) => categorySet?.attributes.find((x) => x.name.toLowerCase() === name.toLowerCase())

  return (
    <Modal
      title={product ? `编辑商品 ${product.sku}` : '新建商品'}
      subtitle="字段与 Alibaba 发布接口 product_info 对应。带 * 为必填。"
      onClose={close}
      wide
      footer={
        <>
          {error && <span className="form-error">{error}</span>}
          {product && (
            <button type="button" onClick={() => void loadPreview()} disabled={busy === 'preview'} title="查看按已保存数据生成的发布报文">
              {busy === 'preview' ? <Spinner /> : <Code2 size={16} />} 发布报文
            </button>
          )}
          <button type="button" onClick={close}>取消</button>
          <button type="submit" form="product-form" className="primary" disabled={saving}>
            {saving ? <Spinner /> : <CheckCircle2 size={16} />} 保存
          </button>
        </>
      }
    >
      <form id="product-form" className="editor" onSubmit={submit}>
        {product?.remoteSkuCount && product.remoteSkuCount > 1 ? (
          <div className="banner warn">
            该商品在 Alibaba 上有 {product.remoteSkuCount} 个规格。本系统只维护单一价格和库存，无法整体更新这个商品，修改请到 Alibaba 后台进行。
          </div>
        ) : null}
        {product?.remoteProductId && !product.description ? (
          <div className="banner info">Alibaba 接口不提供这个商品的描述（详情页是在卖家后台用详情编辑器做的）。描述留空时，更新不会改动线上的详情页。</div>
        ) : null}
        <fieldset>
          <legend>基础信息</legend>
          <div className="grid four">
            <label>SKU *<input value={form.sku} onChange={(e) => set('sku', e.target.value)} autoFocus={!product} /></label>
            <label>品牌<input value={form.brandName} onChange={(e) => set('brandName', e.target.value)} /></label>
            <label>型号<input value={form.modelNumber} onChange={(e) => set('modelNumber', e.target.value)} /></label>
            <label>语言
              <select value={form.language} onChange={(e) => set('language', e.target.value)}>
                <option value="en_US">英文 en_US</option>
                <option value="zh_CN">中文 zh_CN（Alibaba 自动翻译）</option>
              </select>
            </label>
            {product && features?.aiSuggestions && (
              <div className="span4 ai-suggest">
                <button type="button" onClick={() => void suggest()} disabled={busy === 'suggest'} title="根据已保存的标题、类目、属性和描述生成建议；不会自动修改">
                  {busy === 'suggest' ? <Spinner /> : <Wand2 size={16} />} AI 建议标题和关键词
                </button>
                {suggestion && (
                  <div className="suggestion">
                    {suggestion.notes && <p className="muted">{suggestion.notes}</p>}
                    <div className="suggestion-row">
                      <span><b>标题</b>（{suggestion.title.length} 字符）{suggestion.title}</span>
                      <button type="button" onClick={() => set('title', suggestion.title)} disabled={form.title === suggestion.title}>采用标题</button>
                    </div>
                    <div className="suggestion-row">
                      <span><b>关键词</b> {suggestion.keywords.join('; ')}</span>
                      <button type="button" onClick={() => setKeywordText(suggestion.keywords.join('; '))}>采用关键词</button>
                    </div>
                    <small className="muted">采用后请检查内容是否属实，再保存；已上架商品需要再「发布到 Alibaba」才会更新。</small>
                  </div>
                )}
              </div>
            )}
            <label className="span4">英文标题 *（≤128 字符，当前 {form.title.length}）
              <input value={form.title} onChange={(e) => set('title', e.target.value)} maxLength={200} />
            </label>
            <label className="span4">描述 *（支持 HTML）
              <textarea value={form.description} onChange={(e) => set('description', e.target.value)} rows={4} />
            </label>
            <label className="span3">关键词（用 ; 或换行分隔，最多 10 个）
              <input value={keywordText} onChange={(e) => setKeywordText(e.target.value)} placeholder="bluetooth speaker; portable speaker" />
            </label>
            <label className="checkbox">
              <input type="checkbox" checked={form.aiOptimize} onChange={(e) => set('aiOptimize', e.target.checked)} />
              <span><Sparkles size={14} /> 发布时启用 AI 优化</span>
            </label>
          </div>
        </fieldset>

        <fieldset>
          <legend>类目与属性</legend>
          <div className="grid four">
            <label>类目 ID *<input value={form.categoryId} onChange={(e) => set('categoryId', e.target.value)} placeholder="Alibaba 叶子类目 ID" /></label>
            <label className="span2">类目名称 / 路径<input value={form.categoryPath || form.categoryName} onChange={(e) => set('categoryPath', e.target.value)} /></label>
            <div className="inline-actions">
              <button type="button" onClick={() => void loadCategoryAttributes(categorySet !== null)} disabled={busy === 'attributes' || !alibaba?.ready}
                title={alibaba?.ready ? '从 Alibaba 加载该类目的属性' : '需要先完成店铺授权'}>
                {busy === 'attributes' ? <Spinner /> : <ListChecks size={16} />} {categorySet ? '刷新类目属性' : '加载类目属性'}
              </button>
            </div>
          </div>
          <div className="attribute-table">
            {attributes.map((row, index) => {
              const definition = attributeOptions(row.name)
              const listId = `attr-values-${index}`
              return (
                <div key={index} className="attribute-row">
                  <input value={row.name} placeholder="属性名，如 Material" onChange={(e) => setAttributes((rows) => rows.map((r, i) => (i === index ? { ...r, name: e.target.value } : r)))} />
                  <input value={row.value} placeholder={definition?.required ? '必填' : '属性值'} list={definition?.values.length ? listId : undefined}
                    className={definition?.required && !row.value.trim() ? 'missing' : undefined}
                    onChange={(e) => setAttributes((rows) => rows.map((r, i) => (i === index ? { ...r, value: e.target.value } : r)))} />
                  {definition?.values.length ? <datalist id={listId}>{definition.values.map((v) => <option key={v} value={v} />)}</datalist> : null}
                  <span className="attr-flag">{definition?.required ? '必填' : ''}</span>
                  <button type="button" className="icon" aria-label="删除属性" onClick={() => setAttributes((rows) => rows.filter((_, i) => i !== index))}><Trash2 size={15} /></button>
                </div>
              )
            })}
            <div className="attribute-actions">
              <button type="button" onClick={() => setAttributes((rows) => [...rows, { name: '', value: '' }])}><Plus size={15} /> 添加属性</button>
              {categorySet && (
                <select value="" onChange={(e) => e.target.value && setAttributes((rows) => [...rows, { name: e.target.value, value: '' }])} aria-label="从类目属性添加">
                  <option value="">从类目属性中选择…</option>
                  {categorySet.attributes
                    .filter((a) => !attributes.some((row) => row.name.toLowerCase() === a.name.toLowerCase()))
                    .map((a) => <option key={a.id || a.name} value={a.name}>{a.name}{a.required ? '（必填）' : ''}</option>)}
                </select>
              )}
            </div>
          </div>
        </fieldset>

        <fieldset>
          <legend>价格与库存</legend>
          <div className="grid four">
            <label>币种
              <select value={form.currency} onChange={(e) => set('currency', e.target.value)}>
                {['USD', 'CNY', 'EUR', 'GBP'].map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </label>
            <label>单价 *<input type="number" min="0" step="0.01" value={form.price || ''} onChange={(e) => set('price', num(e.target.value))} disabled={form.tieredPrices.length > 0} /></label>
            <label>起订量 *<input type="number" min="1" value={form.minimumOrderQuantity || ''} onChange={(e) => set('minimumOrderQuantity', num(e.target.value))} /></label>
            <label>销售单位
              <input value={form.unit} onChange={(e) => set('unit', e.target.value)} list="unit-options" />
              <datalist id="unit-options">{['Piece', 'Set', 'Pair', 'Bag', 'Box', 'Carton', 'Kilogram', 'Meter', 'Roll', 'Unit'].map((u) => <option key={u} value={u} />)}</datalist>
            </label>
            <label>库存<input type="number" min="0" value={form.stock || ''} onChange={(e) => set('stock', num(e.target.value))} /></label>
          </div>
          <div className="tiers">
            <span className="muted">阶梯价（可选，数量递增、价格递减；填写后替代单价）</span>
            {form.tieredPrices.map((tier, index) => (
              <div key={index} className="tier-row">
                <label>≥ 数量<input type="number" min="1" value={tier.quantity || ''} onChange={(e) => set('tieredPrices', form.tieredPrices.map((t, i) => (i === index ? { ...t, quantity: num(e.target.value) } : t)))} /></label>
                <label>单价<input type="number" min="0" step="0.01" value={tier.price || ''} onChange={(e) => set('tieredPrices', form.tieredPrices.map((t, i) => (i === index ? { ...t, price: num(e.target.value) } : t)))} /></label>
                <button type="button" className="icon" aria-label="删除阶梯" onClick={() => set('tieredPrices', form.tieredPrices.filter((_, i) => i !== index))}><Trash2 size={15} /></button>
              </div>
            ))}
            <button type="button" onClick={() => set('tieredPrices', [...form.tieredPrices, { quantity: form.tieredPrices.at(-1)?.quantity ? form.tieredPrices.at(-1)!.quantity * 5 : form.minimumOrderQuantity || 1, price: 0 }])}>
              <Plus size={15} /> 添加阶梯价
            </button>
          </div>
        </fieldset>

        <fieldset>
          <legend>物流</legend>
          <div className="grid four">
            <label>交期（天）<input type="number" min="0" value={form.leadTimeDays || ''} onChange={(e) => set('leadTimeDays', num(e.target.value))} /></label>
            <label>运费模板 ID<input value={form.shippingTemplateId} onChange={(e) => set('shippingTemplateId', e.target.value)} /></label>
            <label>毛重（kg）<input type="number" min="0" step="0.01" value={form.weightKg ?? ''} onChange={(e) => set('weightKg', optionalNum(e.target.value))} /></label>
            <div className="grid three dims">
              <label>长 cm<input type="number" min="0" value={form.lengthCm ?? ''} onChange={(e) => set('lengthCm', optionalNum(e.target.value))} /></label>
              <label>宽 cm<input type="number" min="0" value={form.widthCm ?? ''} onChange={(e) => set('widthCm', optionalNum(e.target.value))} /></label>
              <label>高 cm<input type="number" min="0" value={form.heightCm ?? ''} onChange={(e) => set('heightCm', optionalNum(e.target.value))} /></label>
            </div>
          </div>
        </fieldset>

        <fieldset>
          <legend>图片 *（最多 {MAX_IMAGES} 张，第一张为主图）</legend>
          <div className="image-grid">
            {form.images.map((url, index) => (
              <div key={`${url}-${index}`} className="image-card">
                <img src={url} alt="" onError={(e) => (e.currentTarget.style.visibility = 'hidden')} />
                {index === 0 && <span className="badge">主图</span>}
                <div className="image-actions">
                  <button type="button" className="icon" aria-label="前移" onClick={() => moveImage(index, -1)} disabled={index === 0}><ArrowUp size={14} /></button>
                  <button type="button" className="icon" aria-label="后移" onClick={() => moveImage(index, 1)} disabled={index === form.images.length - 1}><ArrowDown size={14} /></button>
                  <button type="button" className="icon" aria-label="删除图片" onClick={() => set('images', form.images.filter((_, i) => i !== index))}><Trash2 size={14} /></button>
                </div>
                <small title={url}>{url}</small>
              </div>
            ))}
          </div>
          <div className="image-add">
            <input value={newImage} onChange={(e) => setNewImage(e.target.value)} placeholder="粘贴图片 URL（https://…）" />
            <button type="button" disabled={!newImage.trim() || form.images.length >= MAX_IMAGES} onClick={() => {
              set('images', [...form.images, ...splitList(newImage)].slice(0, MAX_IMAGES))
              setNewImage('')
            }}><Plus size={15} /> 添加</button>
            <button type="button" onClick={() => fileInput.current?.click()} disabled={busy === 'upload' || !alibaba?.ready || form.images.length >= MAX_IMAGES}
              title={alibaba?.ready ? '上传本地图片到 Alibaba 图片银行' : '需要先完成店铺授权'}>
              {busy === 'upload' ? <Spinner /> : <ImageUp size={15} />} 上传本地图片
            </button>
            <input ref={fileInput} type="file" accept="image/*" hidden onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void uploadImage(file)
              e.currentTarget.value = ''
            }} />
          </div>
        </fieldset>

        {preview && (
          <fieldset>
            <legend>发布报文预览（基于已保存的数据）</legend>
            <pre className="code">{preview}</pre>
          </fieldset>
        )}
      </form>
    </Modal>
  )
}
