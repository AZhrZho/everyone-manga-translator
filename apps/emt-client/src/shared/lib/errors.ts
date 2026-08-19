import { ApiRequestError } from '../../api'

export function logBackgroundError(operation: string, reason: unknown, context?: Record<string, unknown>) {
  const error = reason instanceof Error ? reason : new Error(String(reason))
  const request = error instanceof ApiRequestError
    ? {
        method: error.method,
        url: error.url,
        status: error.status,
        statusText: error.statusText,
        responseBody: error.responseBody,
      }
    : undefined
  console.groupCollapsed(`[EMT] Background operation failed: ${operation}`)
  console.error(error)
  console.info({ operation, ...context, request })
  console.groupEnd()
}

export function readError(reason: unknown) {
  if (reason instanceof ApiRequestError) logBackgroundError('api-request', reason)
  if (reason instanceof Error) return reason.message
  return String(reason)
}

export function isPresentableError(message: string) {
  return !/^\d{3}(?:\s|$)/.test(message.trim())
    && !/^(?:failed to fetch|load failed|networkerror)/i.test(message.trim())
}
