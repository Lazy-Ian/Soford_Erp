import { useCallback, useEffect, useRef, useState } from 'react'
import { ArrowDown, ArrowUp, Check, Plus, Trash2 } from 'lucide-react'
import { api, errorText, post, type WebsiteContact, type WebsiteProfile, type WebsiteTerms } from '../api'
import { Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'

const emptyTerms = (): WebsiteTerms => ({ payment: '', sample: '', tradeTerms: '', moq: '', leadTime: '', warranty: '' })

const blankContact = (): WebsiteContact => ({
  id: crypto.randomUUID(),
  name: '',
  title: '',
  email: '',
  phone: '',
  whatsapp: '',
  wechat: '',
  qq: '',
})

/** Company facts and sales contacts shown on the public website. */
export function WebsitePage() {
  const { notify, navigate } = useApp()
  const [profile, setProfile] = useState<WebsiteProfile | null>(null)
  const [saving, setSaving] = useState(false)
  const [loadError, setLoadError] = useState('')
  const [baseline, setBaseline] = useState('')
  const dirty = !!profile && JSON.stringify(profile) !== baseline
  const dirtyRef = useRef(dirty)
  useEffect(() => { dirtyRef.current = dirty }, [dirty])

  useEffect(() => {
    const onLeave = (event: Event) => {
      if (!dirtyRef.current) return
      if (!window.confirm('官网资料有未保存的修改，确定放弃并离开吗？')) event.preventDefault()
      else dirtyRef.current = false
    }
    const onUnload = (event: BeforeUnloadEvent) => {
      if (dirtyRef.current) { event.preventDefault(); event.returnValue = '' }
    }
    window.addEventListener('soford:before-navigate', onLeave)
    window.addEventListener('beforeunload', onUnload)
    return () => {
      window.removeEventListener('soford:before-navigate', onLeave)
      window.removeEventListener('beforeunload', onUnload)
    }
  }, [])

  const load = useCallback(async () => {
    setLoadError('')
    try {
      const data = await api<WebsiteProfile>('/api/website/profile')
      const next = { ...data, terms: { ...emptyTerms(), ...data.terms }, contacts: data.contacts ?? [] }
      setProfile(next)
      setBaseline(JSON.stringify(next))
    } catch (err) {
      setLoadError(errorText(err, '加载官网资料失败'))
    }
  }, [])

  useInitialLoad(load)

  const patch = (partial: Partial<WebsiteProfile>) => setProfile((current) => current && { ...current, ...partial })
  const patchTerms = (key: keyof WebsiteTerms, value: string) =>
    setProfile((current) => current && { ...current, terms: { ...current.terms, [key]: value } })
  const patchContact = (id: string, key: keyof WebsiteContact, value: string) =>
    setProfile((current) => current && {
      ...current,
      contacts: current.contacts.map((contact) => contact.id === id ? { ...contact, [key]: value } : contact),
    })

  const move = (index: number, delta: number) => setProfile((current) => {
    if (!current) return current
    const target = index + delta
    if (target < 0 || target >= current.contacts.length) return current
    const contacts = current.contacts.slice()
    const [item] = contacts.splice(index, 1)
    contacts.splice(target, 0, item)
    return { ...current, contacts }
  })

  const save = async () => {
    if (!profile) return
    setSaving(true)
    try {
      const saved = await post<WebsiteProfile>('/api/website/profile', profile, 'PUT')
      const next = { ...saved, terms: { ...emptyTerms(), ...saved.terms }, contacts: saved.contacts ?? [] }
      setProfile(next)
      setBaseline(JSON.stringify(next))
      notify('success', '官网资料已保存。刷新官网即可看到。')
    } catch (err) {
      notify('error', errorText(err, '保存官网资料失败'))
    } finally {
      setSaving(false)
    }
  }

  if (!profile) return <div className="page">{loadError
    ? <div className="banner bad" role="alert">{loadError}<button type="button" onClick={() => void load()}>重试</button></div>
    : <div className="empty small"><Spinner size={20} /></div>}</div>

  return (
    <fieldset className="page" disabled={saving} style={{ border: 0, minWidth: 0 }}>
      <section className="card"><strong>官网商品</strong><p>商品列表和详情在商品页一键同步，普通保存不会改变官网已同步的版本。</p><button type="button" onClick={() => navigate('products')}>去同步商品</button></section>
      <section className="card">
        <div className="card-head">
          <div>
            <strong>官网资料</strong>
            <span>公司、地址、国际站链接和联系人显示在官网页脚和联系区。可以添加多位联系人，排在前面的先显示。</span>
          </div>
          <div className="actions">
          <button type="button" onClick={() => { if (!dirty || window.confirm('放弃未保存修改并重新加载最新资料吗？')) void load() }}>重新加载</button>
          <button type="button" className="primary" disabled={saving || !dirty} onClick={() => void save()}>
            {saving ? <Spinner size={16} /> : <Check size={16} />} 保存
          </button>
          </div>
        </div>
        <div className="form-grid">
          <label className="field">公司名称
            <input value={profile.company} maxLength={200} onChange={(e) => patch({ company: e.target.value })} />
          </label>
          <label className="field">国际站店铺链接
            <input value={profile.alibabaStore} maxLength={300} placeholder="https://….en.alibaba.com" onChange={(e) => patch({ alibabaStore: e.target.value })} />
          </label>
          <label className="field field-span">地址
            <input value={profile.address} maxLength={300} placeholder="街道、门牌、区、城市、国家" onChange={(e) => patch({ address: e.target.value })} />
          </label>
        </div>
      </section>

      <section className="card">
        <div className="card-head">
          <div>
            <strong>联系人</strong>
            <span>邮箱、电话、WhatsApp、微信、QQ 按人分开填。空着的项不会出现在官网上。</span>
          </div>
          <button type="button" disabled={profile.contacts.length >= 12} onClick={() => patch({ contacts: [...profile.contacts, blankContact()] })}>
            <Plus size={16} /> 新增联系人
          </button>
        </div>
        {profile.contacts.length === 0 && <p className="muted">还没有联系人。新增后，官网联系区和页脚会列出他们。</p>}
        <div className="person-list">
          {profile.contacts.map((contact, index) => (
            <article key={contact.id} className="person-card">
              <header>
                <strong>联系人 {index + 1}</strong>
                <span className="row-actions">
                  <button type="button" className="icon" title="上移" aria-label="上移" disabled={index === 0} onClick={() => move(index, -1)}><ArrowUp size={15} /></button>
                  <button type="button" className="icon" title="下移" aria-label="下移" disabled={index === profile.contacts.length - 1} onClick={() => move(index, 1)}><ArrowDown size={15} /></button>
                  <button type="button" className="icon" title="删除" aria-label="删除" onClick={() => patch({ contacts: profile.contacts.filter((item) => item.id !== contact.id) })}><Trash2 size={15} /></button>
                </span>
              </header>
              <div className="form-grid">
                <label className="field">姓名
                  <input value={contact.name} maxLength={80} onChange={(e) => patchContact(contact.id, 'name', e.target.value)} />
                </label>
                <label className="field">职务
                  <input value={contact.title} maxLength={80} placeholder="Trade Manager" onChange={(e) => patchContact(contact.id, 'title', e.target.value)} />
                </label>
                <label className="field">邮箱
                  <input value={contact.email} maxLength={200} inputMode="email" onChange={(e) => patchContact(contact.id, 'email', e.target.value)} />
                </label>
                <label className="field">电话
                  <input value={contact.phone} maxLength={40} onChange={(e) => patchContact(contact.id, 'phone', e.target.value)} />
                </label>
                <label className="field">WhatsApp
                  <input value={contact.whatsapp} maxLength={24} placeholder="国家码+手机号，如 8613800138000" onChange={(e) => patchContact(contact.id, 'whatsapp', e.target.value)} />
                </label>
                <label className="field">微信
                  <input value={contact.wechat} maxLength={40} onChange={(e) => patchContact(contact.id, 'wechat', e.target.value)} />
                </label>
                <label className="field">QQ
                  <input value={contact.qq} maxLength={12} inputMode="numeric" onChange={(e) => patchContact(contact.id, 'qq', e.target.value)} />
                </label>
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="card">
        <div className="card-head">
          <div>
            <strong>首页常见问题</strong>
            <span>留空则继续用网站自带的多语言文案。填写后，首页对应回答改成你写的原文，每种语言都显示这段。建议写英文完整句。</span>
          </div>
        </div>
        <div className="form-grid">
          <label className="field">付款
            <textarea rows={2} maxLength={800} value={profile.terms.payment} placeholder="30% T/T deposit." onChange={(e) => patchTerms('payment', e.target.value)} />
          </label>
          <label className="field">样品
            <textarea rows={2} maxLength={800} value={profile.terms.sample} placeholder="Yes. The sample fee is deducted from your first order." onChange={(e) => patchTerms('sample', e.target.value)} />
          </label>
          <label className="field">起订量
            <textarea rows={3} maxLength={800} value={profile.terms.moq} placeholder="Rubber mats start at 100–200 m², EVA mats at 200 pieces, and artificial grass at 1,000 m²." onChange={(e) => patchTerms('moq', e.target.value)} />
          </label>
          <label className="field">认证与质保
            <textarea rows={3} maxLength={800} value={profile.terms.warranty} placeholder="SGS and REACH. Artificial grass and rubber flooring have a 6–8 year warranty." onChange={(e) => patchTerms('warranty', e.target.value)} />
          </label>
          <label className="field">交期
            <textarea rows={3} maxLength={800} value={profile.terms.leadTime} placeholder="Production takes 7–20 days after the deposit is received." onChange={(e) => patchTerms('leadTime', e.target.value)} />
          </label>
          <label className="field">贸易条款（FOB 等）
            <textarea rows={3} maxLength={800} value={profile.terms.tradeTerms} placeholder="Trade terms are FOB Qingdao." onChange={(e) => patchTerms('tradeTerms', e.target.value)} />
          </label>
        </div>
        <p className="muted">交期和贸易条款会接在一起，替换首页「生产要多久」的回答。</p>
      </section>
    </fieldset>
  )
}
