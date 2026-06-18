/*
 * Small fetch wrapper for the SwarmRoute web frontend (timeout + JSON).
 *
 * NOTE on the response shape: the simulation endpoint returns its DTO DIRECTLY as
 * camelCase JSON — it is NOT wrapped in { code, msg, data }. So this wrapper parses
 * and returns the JSON body as-is. On a non-2xx it throws an HttpError carrying the
 * status and (when present) the ProblemDetails title/detail so the UI can surface it.
 *
 * Mirrors gurkirbshmi's src/renderer/src/api/client.ts conventions (configurable base,
 * timeout via AbortController, typed get/post helpers) minus the PRD unwrap + retry/WS.
 */

const API_BASE: string = import.meta.env?.VITE_API_BASE ?? '/api'

const DEFAULT_TIMEOUT_MS = 15_000

export class HttpError extends Error {
  status: number
  detail?: string
  constructor(message: string, opts: { status: number; detail?: string }) {
    super(message)
    this.name = 'HttpError'
    this.status = opts.status
    this.detail = opts.detail
  }
}

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE' | 'PATCH'

export interface RequestOptions {
  method?: HttpMethod
  headers?: Record<string, string>
  body?: unknown
  timeoutMs?: number
  signal?: AbortSignal
}

function buildUrl(path: string): string {
  const base = API_BASE.replace(/\/$/, '')
  // Absolute URLs pass through untouched; relative paths are prefixed with the base.
  if (/^https?:\/\//i.test(path)) return path
  return `${base}${path.startsWith('/') ? path : `/${path}`}`
}

/** Shape of an ASP.NET Core ProblemDetails 400 body. */
interface ProblemDetails {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

/**
 * Issue a request and return the parsed JSON body as <T>. The body IS the result
 * for these endpoints — there is no { code, msg, data } envelope to unwrap.
 */
export async function request<T = unknown>(path: string, opts: RequestOptions = {}): Promise<T> {
  const method = opts.method ?? 'GET'
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    Accept: 'application/json',
    ...(opts.headers ?? {}),
  }

  const ctrl = new AbortController()
  const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS
  const timer = setTimeout(() => ctrl.abort(), timeoutMs)
  // Bridge an external abort signal into our controller.
  opts.signal?.addEventListener('abort', () => ctrl.abort(), { once: true })

  let resp: Response
  try {
    resp = await fetch(buildUrl(path), {
      method,
      headers,
      body: method === 'GET' || opts.body === undefined ? undefined : JSON.stringify(opts.body),
      signal: ctrl.signal,
    })
  } catch (err) {
    clearTimeout(timer)
    if ((err as Error)?.name === 'AbortError') {
      throw new HttpError('请求超时或已取消 / Request timed out or was cancelled', { status: 0 })
    }
    // Network error — most often the dev proxy can't reach the .NET Host (502).
    throw new HttpError('无法连接到仿真服务 / Cannot reach the simulation service', { status: 0 })
  } finally {
    clearTimeout(timer)
  }

  const contentType = resp.headers.get('content-type') ?? ''
  const isJson = contentType.includes('application/json') || contentType.includes('problem+json')

  if (resp.ok) {
    if (isJson) return (await resp.json()) as T
    // Endpoints here are JSON; fall back to text just in case.
    return (await resp.text()) as unknown as T
  }

  // Non-2xx: prefer the ProblemDetails message when the server provides one.
  let message = `请求失败 (HTTP ${resp.status})`
  let detail: string | undefined
  if (isJson) {
    try {
      const problem = (await resp.json()) as ProblemDetails
      if (problem.title) message = problem.title
      detail = problem.detail
      if (!detail && problem.errors) {
        detail = Object.values(problem.errors).flat().join('；')
      }
    } catch {
      /* keep the default message */
    }
  }
  throw new HttpError(message, { status: resp.status, detail })
}

export const get = <T = unknown>(path: string, opts: Omit<RequestOptions, 'method' | 'body'> = {}) =>
  request<T>(path, { ...opts, method: 'GET' })

export const post = <T = unknown>(
  path: string,
  body?: unknown,
  opts: Omit<RequestOptions, 'method' | 'body'> = {}
) => request<T>(path, { ...opts, method: 'POST', body })
