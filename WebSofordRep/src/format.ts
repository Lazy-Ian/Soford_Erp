import { apiUrl, type PublishJobStatus, type PublishState } from './api'

export const publishStateLabel: Record<PublishState, string> = {
  NotPublished: '未发布',
  Publishing: '发布中',
  Pending: '审核中',
  Online: '已上架',
  Offline: '已下架',
  Failed: '发布失败',
}

export const publishStateTone: Record<PublishState, Tone> = {
  NotPublished: 'neutral',
  Publishing: 'info',
  Pending: 'warn',
  Online: 'good',
  Offline: 'neutral',
  Failed: 'bad',
}

export const jobKindLabel: Record<string, string> = {
  publish: '发布',
  status: '同步状态',
  inventory: '同步库存',
  price: '同步价格',
  online: '上架',
  offline: '下架',
  predict: '预测类目',
  refresh: '从 Alibaba 刷新',
  'refresh-discard': '放弃修改并刷新',
}

export const jobStatusLabel: Record<PublishJobStatus, string> = {
  Queued: '排队中',
  Running: '执行中',
  Completed: '已完成',
  CompletedWithErrors: '部分失败',
  Failed: '失败',
  Interrupted: '已中断',
}

export const jobStatusTone: Record<PublishJobStatus, Tone> = {
  Queued: 'info',
  Running: 'info',
  Completed: 'good',
  CompletedWithErrors: 'warn',
  Failed: 'bad',
  Interrupted: 'warn',
}

export type Tone = 'neutral' | 'info' | 'good' | 'warn' | 'bad'

export function formatDate(value?: string | null) {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'
  return date.toLocaleString('zh-CN', { hour12: false })
}

export function formatPrice(currency: string, value: number) {
  return `${currency || 'USD'} ${value.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

export function splitList(value: string) {
  return value
    .split(/[\n,;；，]+/)
    .map((item) => item.trim())
    .filter(Boolean)
}

export function downloadUrl(url: string) {
  const link = document.createElement('a')
  link.href = apiUrl(url)
  link.rel = 'noopener'
  document.body.appendChild(link)
  link.click()
  link.remove()
}
