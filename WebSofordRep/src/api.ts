export type Severity = 'Info' | 'Warning' | 'Blocker'
export type LocalState = 'Incomplete' | 'Ready'
export type PublishState = 'NotPublished' | 'Publishing' | 'Pending' | 'Online' | 'Offline' | 'Failed'

export type QualityIssue = { severity: Severity; field: string; message: string }
export type PriceTier = { quantity: number; price: number }

export type ProductDraft = {
  sku: string
  title: string
  description: string
  keywords: string[]
  brandName: string
  modelNumber: string
  language: string
  categoryId: string
  categoryName: string
  categoryPath: string
  attributes: Record<string, string>
  currency: string
  price: number
  tieredPrices: PriceTier[]
  minimumOrderQuantity: number
  unit: string
  stock: number
  leadTimeDays: number
  shippingTemplateId: string
  weightKg: number | null
  lengthCm: number | null
  widthCm: number | null
  heightCm: number | null
  images: string[]
  aiOptimize: boolean
}

export type Product = ProductDraft & {
  id: string
  localState: LocalState
  publishState: PublishState
  remoteProductId?: string | null
  remoteStatus?: string | null
  remoteStatusMessage?: string | null
  lastPublishedAt?: string | null
  lastSyncedAt?: string | null
  qualityIssues: QualityIssue[]
  createdAt: string
  updatedAt: string
}

export type OperationItem = { productId: string; sku: string; success: boolean; message: string; remoteProductId?: string | null; action?: string | null }
export type BatchResult = { total: number; succeeded: number; failed: number; items: OperationItem[] }

export type PublishJobStatus = 'Queued' | 'Running' | 'Completed' | 'CompletedWithErrors' | 'Failed' | 'Interrupted'
export type PublishJob = {
  id: string
  productIds: string[]
  status: PublishJobStatus
  total: number
  processed: number
  succeeded: number
  failed: number
  message: string
  items: OperationItem[]
  createdAt: string
  startedAt?: string | null
  finishedAt?: string | null
}

export type AlibabaStatus = {
  hasCredentials: boolean
  hasToken: boolean
  accessTokenExpiresAt?: string | null
  refreshTokenExpiresAt?: string | null
  account?: string | null
  gatewayUrl: string
  callbackUrl: string
  ready: boolean
}

export type DiagnosticCheck = { key: string; title: string; status: 'ok' | 'warn' | 'fail'; message: string; fix?: string | null }

export type AlibabaApiDefinition = { key: string; area: string; path: string; requiresToken: boolean; description: string; isConfigured: boolean }

export type ApiCallLog = {
  id: string
  createdAt: string
  apiKey: string
  path: string
  httpStatus: number
  success: boolean
  errorCode?: string | null
  message: string
  requestId?: string | null
  durationMs: number
  request?: string | null
  response?: string | null
}

export type AlibabaApiResult = {
  apiKey: string
  path: string
  success: boolean
  httpStatus: number
  errorCode?: string | null
  errorMessage?: string | null
  requestId?: string | null
  durationMs: number
  json?: unknown
  rawBody?: string | null
}

export type CategoryAttribute = { id: string; name: string; required: boolean; supportCustomValue: boolean; supportMultiValue: boolean; values: string[] }
export type CategoryAttributeSet = { categoryId: string; fetchedAt: string; attributes: CategoryAttribute[]; saleAttributes: CategoryAttribute[] }

export type ImportResult = { created: number; updated: number; skipped: number; warnings: string[]; headers: string[]; unknownHeaders: string[] }
export type AuthSession = { authenticated: boolean; username?: string | null }

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/** Fired when any request comes back 401, so the app can return to the login screen. */
export const UNAUTHORIZED_EVENT = 'soford:unauthorized'

export async function api<T>(url: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(url, { credentials: 'same-origin', ...init })
  } catch {
    throw new ApiError(0, '无法连接服务器，请确认 API 已启动。')
  }

  if (!response.ok) {
    if (response.status === 401 && !url.endsWith('/api/auth/login')) {
      window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
    }
    throw new ApiError(response.status, await readProblem(response))
  }

  if (response.status === 204) return undefined as T
  const text = await response.text()
  if (!text) return undefined as T
  return (response.headers.get('content-type')?.includes('json') ? JSON.parse(text) : text) as T
}

export const post = <T>(url: string, body?: unknown, method = 'POST') =>
  api<T>(url, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

async function readProblem(response: Response) {
  const text = await response.text()
  try {
    const problem = JSON.parse(text) as { title?: string; detail?: string }
    if (problem.title && problem.detail) return `${problem.title}：${problem.detail}`
    if (problem.title || problem.detail) return (problem.title || problem.detail)!
  } catch {
    // Not JSON; fall through.
  }
  if (response.status === 401) return '登录已过期，请重新登录。'
  if (response.status >= 500) return `服务器错误（${response.status}），请查看 API 日志。`
  return text || `请求失败（${response.status}）`
}

export function errorText(err: unknown, fallback = '操作失败') {
  return err instanceof Error && err.message ? err.message : fallback
}
