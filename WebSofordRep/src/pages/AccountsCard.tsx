import { useCallback, useState } from 'react'
import { Check, Pencil, RefreshCw, Star, Unplug, Users } from 'lucide-react'
import { api, errorText, post, type AlibabaAccount } from '../api'
import { Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

/** Main account and sub-accounts: who owns which listings, and which of them are authorized to act. */
export function AccountsCard({ reloadKey }: { reloadKey: number }) {
  const { notify, confirm, refreshAlibaba } = useApp()
  const [accounts, setAccounts] = useState<AlibabaAccount[] | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setAccounts(await api<AlibabaAccount[]>('/api/integrations/alibaba/accounts'))
    } catch (err) {
      notify('error', errorText(err, '加载账号失败'))
    }
    // reloadKey forces a reload after a new authorization on the parent page.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notify, reloadKey])

  useInitialLoad(load)

  const run = async (key: string, action: () => Promise<void>) => {
    setBusy(key)
    try {
      await action()
      await load()
      await refreshAlibaba()
    } catch (err) {
      notify('error', errorText(err))
    } finally {
      setBusy(null)
    }
  }

  const update = (account: AlibabaAccount, body: { name?: string; isDefault?: boolean; ownerAliIds?: string[] }, message: string) =>
    run(`update-${account.id}`, async () => {
      await post(`/api/integrations/alibaba/accounts/${account.id}`, body, 'PUT')
      notify('success', message)
    })

  const unauthorized = accounts?.filter((x) => !x.hasToken) ?? []

  return (
    <section className="card">
      <div className="card-head">
        <div>
          <strong>账号管理</strong>
          <span>店铺的主账号和子账号。商品按负责账号区分；用哪个账号授权，就能以哪个账号的身份发布和更新商品。</span>
        </div>
        <Users size={18} className="muted" />
      </div>

      {accounts === null && <div className="empty small"><Spinner size={20} /></div>}
      {accounts?.length === 0 && <p className="muted">还没有账号。授权店铺或「从 Alibaba 导入」商品后会自动出现。</p>}

      <div className="account-list">
        {accounts?.map((account) => (
          <article key={account.id} className="account">
            <div className="account-main">
              {editing === account.id ? (
                <div className="inline-form">
                  <input value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder="例如：张三（子账号）" />
                  <button type="button" className="primary" disabled={!name.trim()} onClick={() => {
                    setEditing(null)
                    void update(account, { name }, '名称已保存。')
                  }}><Check size={15} /> 保存</button>
                  <button type="button" onClick={() => setEditing(null)}>取消</button>
                </div>
              ) : (
                <div className="account-title">
                  <b>{account.name}</b>
                  {account.isDefault && <Pill tone="info">默认</Pill>}
                  <Pill tone={account.authorized ? 'good' : account.hasToken ? 'bad' : 'neutral'}>
                    {account.authorized ? '已授权' : account.hasToken ? '授权已过期' : '未授权'}
                  </Pill>
                  <button type="button" className="icon" aria-label="改名" title="改名" onClick={() => {
                    setEditing(account.id)
                    setName(account.name)
                  }}><Pencil size={14} /></button>
                </div>
              )}
              <span className="muted">
                {account.login ? `登录账号 ${account.login} · ` : ''}
                负责商品 {account.productCount} 个
                {account.assignedCount ? ` · 指定操作 ${account.assignedCount} 个` : ''}
                {account.ownerAliIds.length ? ` · 账号 ID ${account.ownerAliIds.join('、')}` : ' · 账号 ID 未识别'}
              </span>
              {account.hasToken && <span className="muted">授权有效至 {formatDate(account.refreshTokenExpiresAt || account.accessTokenExpiresAt)}</span>}
            </div>
            <div className="actions">
              {account.hasToken && unauthorized.length > 0 && (
                <select value="" aria-label="合并账号 ID" title="把未授权账号的 ID 合并到此账号（同一个人的账号）" onChange={(e) => {
                  const source = unauthorized.find((x) => x.id === e.target.value)
                  if (!source) return
                  confirm({
                    title: '合并账号',
                    message: `把「${source.name}」（ID ${source.ownerAliIds.join('、')}）合并到「${account.name}」？这些 ID 下的 ${source.productCount} 个商品将归入「${account.name}」，由它的授权来操作。`,
                    onConfirm: () => void update(account, { ownerAliIds: [...account.ownerAliIds, ...source.ownerAliIds] }, '已合并。'),
                  })
                }}>
                  <option value="">合并账号 ID…</option>
                  {unauthorized.map((x) => <option key={x.id} value={x.id}>{x.name}（{x.productCount} 个商品）</option>)}
                </select>
              )}
              {account.hasToken && !account.isDefault && (
                <button type="button" onClick={() => void update(account, { isDefault: true }, `「${account.name}」已设为默认账号。`)} disabled={busy !== null}>
                  <Star size={15} /> 设为默认
                </button>
              )}
              {account.hasToken && (
                <button type="button" onClick={() => void run(`refresh-${account.id}`, async () => {
                  await post(`/api/integrations/alibaba/token/refresh?accountId=${account.id}`)
                  notify('success', `「${account.name}」Token 已续期。`)
                })} disabled={busy !== null}>
                  {busy === `refresh-${account.id}` ? <Spinner /> : <RefreshCw size={15} />} 续期
                </button>
              )}
              {account.hasToken && (
                <button type="button" className="danger-text" onClick={() => confirm({
                  title: '断开授权',
                  danger: true,
                  confirmText: '断开',
                  message: `删除「${account.name}」的授权。之后用它负责的商品将改由默认账号操作；需要时可重新授权。`,
                  onConfirm: () => void run(`clear-${account.id}`, async () => {
                    await api(`/api/integrations/alibaba/token?accountId=${account.id}`, { method: 'DELETE' })
                    notify('success', '已断开。')
                  }),
                })} disabled={busy !== null}><Unplug size={15} /> 断开</button>
              )}
            </div>
          </article>
        ))}
      </div>

      <p className="muted small-note">
        授权子账号：点上方「授权 Alibaba 店铺」，在 Alibaba 登录页换成该子账号登录，完成后粘贴回调链接。系统会根据账号 ID 自动把它和已导入的商品对应起来；
        对应不上时，可在已授权账号上用「合并账号 ID」手动关联。
      </p>
    </section>
  )
}
