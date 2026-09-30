import { useEffect, type ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Loader2, X, XCircle } from 'lucide-react'
import clsx from 'clsx'
import type { BatchResult } from '../api'
import type { Tone } from '../format'

export function Pill({ tone = 'neutral', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={clsx('pill', tone)}>{children}</span>
}

export function Spinner({ size = 16 }: { size?: number }) {
  return <Loader2 className="spin" size={size} />
}

export function Modal({ title, subtitle, onClose, children, footer, wide }: {
  title: string
  subtitle?: ReactNode
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  wide?: boolean
}) {
  useEffect(() => {
    // Escape also ends a Chinese IME composition; that must not close the dialog.
    const onKey = (event: KeyboardEvent) => event.key === 'Escape' && !event.isComposing && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <div className={clsx('modal', wide && 'wide')} role="dialog" aria-modal="true" aria-label={title}>
        <header>
          <div>
            <strong>{title}</strong>
            {subtitle && <span>{subtitle}</span>}
          </div>
          <button type="button" className="icon" aria-label="关闭" onClick={onClose}><X size={18} /></button>
        </header>
        <div className="modal-body">{children}</div>
        {footer && <footer>{footer}</footer>}
      </div>
    </div>
  )
}

export type ConfirmRequest = {
  title: string
  message: ReactNode
  confirmText?: string
  danger?: boolean
  onConfirm: () => void
}

export function ConfirmDialog({ request, onClose }: { request: ConfirmRequest; onClose: () => void }) {
  return (
    <Modal
      title={request.title}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose}>取消</button>
          <button
            type="button"
            className={request.danger ? 'danger' : 'primary'}
            autoFocus
            onClick={() => {
              onClose()
              request.onConfirm()
            }}
          >
            {request.confirmText || '确定'}
          </button>
        </>
      }
    >
      <div className="confirm-message">{request.message}</div>
    </Modal>
  )
}

export function BatchResultDialog({ title, result, onClose }: { title: string; result: BatchResult; onClose: () => void }) {
  return (
    <Modal title={title} subtitle={`共 ${result.total} 个，成功 ${result.succeeded}，失败 ${result.failed}`} onClose={onClose} wide>
      <ResultList items={result.items} />
    </Modal>
  )
}

export function ResultList({ items }: { items: BatchResult['items'] }) {
  if (items.length === 0) return <p className="muted">没有结果。</p>
  return (
    <div className="result-list">
      {items.map((item, index) => (
        <div key={`${item.productId}-${index}`} className={clsx('result-row', item.success ? 'ok' : 'fail')}>
          {item.success ? <CheckCircle2 size={16} /> : <XCircle size={16} />}
          <b>{item.sku || item.productId.slice(0, 8)}</b>
          <span>{item.message}</span>
        </div>
      ))}
    </div>
  )
}

export type Toast = { id: number; kind: 'success' | 'error' | 'info'; text: string }

export function Toasts({ toasts, dismiss }: { toasts: Toast[]; dismiss: (id: number) => void }) {
  return (
    <div className="toasts" aria-live="polite">
      {toasts.map((toast) => (
        <div key={toast.id} className={clsx('toast', toast.kind)}>
          {toast.kind === 'error' ? <AlertTriangle size={16} /> : <CheckCircle2 size={16} />}
          <span>{toast.text}</span>
          <button type="button" className="icon" aria-label="关闭" onClick={() => dismiss(toast.id)}><X size={14} /></button>
        </div>
      ))}
    </div>
  )
}

export function Pagination({ page, pageSize, total, onPage, onPageSize }: {
  page: number
  pageSize: number
  total: number
  onPage: (page: number) => void
  onPageSize: (size: number) => void
}) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  return (
    <div className="pagination">
      <span>共 {total} 条</span>
      <select value={pageSize} onChange={(event) => onPageSize(Number(event.target.value))} aria-label="每页条数">
        {[20, 50, 100].map((size) => <option key={size} value={size}>{size} 条/页</option>)}
      </select>
      <button type="button" disabled={page <= 1} onClick={() => onPage(page - 1)}>上一页</button>
      <span>{page} / {pages}</span>
      <button type="button" disabled={page >= pages} onClick={() => onPage(page + 1)}>下一页</button>
    </div>
  )
}
