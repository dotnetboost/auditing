import type { ProblemDetails } from '~/types'

/** ofetch surfaces the HTTP status differently depending on where the failure happened. */
export function statusOf(error: unknown): number | undefined {
  const e = error as { status?: number, statusCode?: number, response?: { status?: number } }
  return e?.status ?? e?.statusCode ?? e?.response?.status
}

/**
 * The text to show for a failed call.
 *
 * The API answers with problem details, and its `detail` is written for the caller — "The actor is
 * 300 characters; the column holds 256." — so it is shown as it is rather than replaced with
 * something vaguer of our own.
 */
export function problemMessage(error: unknown): string {
  const data = (error as { data?: ProblemDetails | string })?.data

  if (typeof data === 'string' && data) {
    return data
  }

  if (data && typeof data === 'object') {
    if (data.errors) {
      const messages = Object.values(data.errors).flat()
      if (messages.length) {
        return messages.join(' ')
      }
    }

    if (data.detail || data.title) {
      return data.detail || data.title!
    }
  }

  // 401 is the one worth naming: the audit endpoints require authorization, so a dashboard that
  // has not been given an identity sees this rather than an empty trail.
  if (statusOf(error) === 401) {
    return 'The API refused the request as unauthenticated. The audit endpoints require '
      + 'authorization — check NUXT_DEMO_USER reaches the proxy.'
  }

  return (error as Error)?.message || 'The API could not be reached.'
}
