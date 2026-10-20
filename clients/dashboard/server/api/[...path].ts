/**
 * Proxies every `/api/**` call through to the API — the audit endpoints and the sample's product
 * endpoints alike, which is why both live under `/api` on the server.
 *
 * The browser therefore only ever talks to this app's own origin, which keeps the API free of CORS
 * configuration and lets the target move between environments by changing `NUXT_AUDIT_API_URL`
 * alone.
 *
 * Nothing is attached to the request. The sample has no authentication, and maps the audit
 * endpoints with `RequireAuthorization = false` so the trail can be read without one. Against a
 * real API — whose audit endpoints **require authorization** by default, unlike the sibling
 * libraries' — this handler is where the signed-in user's token belongs: attaching it here rather
 * than in the browser is the point, since an identity shipped to the client would let anyone with
 * the page read the whole trail.
 */
export default defineEventHandler(async (event) => {
  const { auditApiUrl } = useRuntimeConfig(event)
  const path = getRouterParam(event, 'path') ?? ''
  const { search } = getRequestURL(event)

  const target = `${auditApiUrl.replace(/\/$/, '')}/api/${path}${search}`

  return proxyRequest(event, target)
})
