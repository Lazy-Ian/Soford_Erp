import { useCallback, useState } from 'react'
import { Check, Pencil, Plus, Trash2, UserCog } from 'lucide-react'
import { api, errorText, post, type AlibabaAccount, type AppUser } from '../api'
import { Modal, Pill, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate } from '../format'

type Draft = { username: string; displayName: string; password: string; role: 'Admin' | 'Operator'; accountIds: string[] }

/** Admins create one login per person and tie operators to the Alibaba accounts they are responsible for. */
export function UsersPage() {
  const { notify, confirm } = useApp()
  const [users, setUsers] = useState<AppUser[] | null>(null)
  const [accounts, setAccounts] = useState<AlibabaAccount[]>([])
  const [editing, setEditing] = useState<AppUser | 'new' | null>(null)

  const load = useCallback(async () => {
    try {
      const [list, accountList] = await Promise.all([
        api<AppUser[]>('/api/users'),
        api<AlibabaAccount[]>('/api/integrations/alibaba/accounts'),
      ])
      setUsers(list)
      setAccounts(accountList)
    } catch (err) {
      notify('error', errorText(err, '加载用户失败'))
    }
  }, [notify])

  useInitialLoad(load)

  const accountNames = (ids: string[]) => ids.map((id) => accounts.find((a) => a.id === id)?.name ?? '（已删除的账号）').join('、')

  const toggle = (user: AppUser) =>
    confirm({
      title: user.disabled ? '启用用户' : '停用用户',
      danger: !user.disabled,
      confirmText: user.disabled ? '启用' : '停用',
      message: user.disabled ? `恢复「${user.displayName}」的登录？` : `「${user.displayName}」将立即退出并不能再登录。操作记录保留。`,
      onConfirm: () => void post(`/api/users/${user.id}`, { disabled: !user.disabled }, 'PUT').then(load).catch((err) => notify('error', errorText(err))),
    })

  const remove = (user: AppUser) =>
    confirm({
      title: '删除用户',
      danger: true,
      confirmText: '删除',
      message: `删除「${user.displayName}」？只删除登录账号，操作记录和商品不受影响。通常「停用」就够了。`,
      onConfirm: () => void api(`/api/users/${user.id}`, { method: 'DELETE' }).then(load).catch((err) => notify('error', errorText(err))),
    })

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>用户管理</strong>
            <span>每人一个登录。「运营」只能看到和操作自己负责账号的商品；「管理员」可以管理店铺授权、账号和用户。服务器配置中的内置管理员不在此列表。</span>
          </div>
          <button type="button" className="primary" onClick={() => setEditing('new')}><Plus size={16} /> 新增用户</button>
        </div>
        {users === null && <div className="empty small"><Spinner size={20} /></div>}
        {users?.length === 0 && <p className="muted">还没有用户。为每个业务员新增一个，并选择他负责的 Alibaba 账号。</p>}
        {!!users?.length && (
          <div className="table-wrap flat">
            <table>
              <thead><tr><th>姓名</th><th>用户名</th><th>角色</th><th>负责账号</th><th>创建时间</th><th className="col-actions" /></tr></thead>
              <tbody>
                {users.map((user) => (
                  <tr key={user.id} className={user.disabled ? 'muted' : undefined}>
                    <td>{user.displayName} {user.disabled && <Pill tone="neutral">已停用</Pill>}</td>
                    <td>{user.username}</td>
                    <td><Pill tone={user.role === 'Admin' ? 'info' : 'neutral'}>{user.role === 'Admin' ? '管理员' : '运营'}</Pill></td>
                    <td className="clip" title={accountNames(user.accountIds)}>{user.role === 'Admin' ? '全部' : accountNames(user.accountIds) || <span className="warn">未指定（只能看到自己新建的商品）</span>}</td>
                    <td>{formatDate(user.createdAt)}</td>
                    <td className="col-actions row-actions">
                      <button type="button" className="icon" title="编辑" aria-label="编辑" onClick={() => setEditing(user)}><Pencil size={15} /></button>
                      <button type="button" className="icon" title={user.disabled ? '启用' : '停用'} aria-label={user.disabled ? '启用' : '停用'} onClick={() => toggle(user)}><UserCog size={15} /></button>
                      <button type="button" className="icon" title="删除" aria-label="删除" onClick={() => remove(user)}><Trash2 size={15} /></button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {editing && <UserEditor user={editing === 'new' ? null : editing} accounts={accounts} onClose={() => setEditing(null)} onSaved={async () => { setEditing(null); await load() }} />}
    </div>
  )
}

function UserEditor({ user, accounts, onClose, onSaved }: { user: AppUser | null; accounts: AlibabaAccount[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const { notify } = useApp()
  const [draft, setDraft] = useState<Draft>({
    username: user?.username ?? '',
    displayName: user?.displayName ?? '',
    password: '',
    role: user?.role ?? 'Operator',
    accountIds: user?.accountIds ?? [],
  })
  const [saving, setSaving] = useState(false)
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((current) => ({ ...current, [key]: value }))

  const save = async () => {
    setSaving(true)
    try {
      if (user) {
        await post(`/api/users/${user.id}`, { displayName: draft.displayName, role: draft.role, accountIds: draft.accountIds, password: draft.password || null }, 'PUT')
      } else {
        await post('/api/users', draft)
      }
      notify('success', user ? '已保存。' : `已新增用户「${draft.displayName || draft.username}」。`)
      await onSaved()
    } catch (err) {
      notify('error', errorText(err, '保存失败'))
    } finally {
      setSaving(false)
    }
  }

  const passwordOk = user ? draft.password === '' || draft.password.length >= 10 : draft.password.length >= 10
  return (
    <Modal title={user ? `编辑用户 ${user.displayName}` : '新增用户'} onClose={onClose}
      footer={<>
        <button type="button" onClick={onClose}>取消</button>
        <button type="button" className="primary" disabled={saving || !passwordOk || (!user && !draft.username.trim())} onClick={() => void save()}>
          {saving ? <Spinner /> : <Check size={16} />} 保存
        </button>
      </>}>
      <div className="form-grid">
        <label className="field">用户名<input value={draft.username} disabled={!!user} onChange={(e) => set('username', e.target.value)} placeholder="登录用，如 zhangsan" /></label>
        <label className="field">姓名<input value={draft.displayName} onChange={(e) => set('displayName', e.target.value)} placeholder="显示在操作记录里" /></label>
        <label className="field">
          {user ? '重置密码（不改请留空）' : '初始密码'}
          <input type="password" value={draft.password} onChange={(e) => set('password', e.target.value)} autoComplete="new-password" placeholder="至少 10 个字符" />
        </label>
        <label className="field">角色
          <select value={draft.role} onChange={(e) => set('role', e.target.value as Draft['role'])}>
            <option value="Operator">运营：只管自己负责账号的商品</option>
            <option value="Admin">管理员：全部商品、授权、账号和用户</option>
          </select>
        </label>
      </div>
      {draft.role === 'Operator' && (
        <fieldset className="account-picks">
          <legend>负责的 Alibaba 账号</legend>
          {accounts.map((account) => (
            <label key={account.id} className="check-line">
              <input type="checkbox" checked={draft.accountIds.includes(account.id)} onChange={(e) =>
                set('accountIds', e.target.checked ? [...draft.accountIds, account.id] : draft.accountIds.filter((id) => id !== account.id))} />
              {account.name}（{account.productCount} 个商品）{!account.authorized && <span className="muted">未授权，由默认账号代为操作</span>}
            </label>
          ))}
        </fieldset>
      )}
    </Modal>
  )
}
