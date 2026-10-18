/**
 * Proxies every `/api/**` call through to the API — the audit endpoints and the sample's product
 * endpoints alike, which is why both live under `/api` on the server.
 *
 * The browser therefore only ever talks to this app's own origin, which keeps the API free of CORS
 * configuration and lets the target move between environments by changing `NUXT_AUDIT_API_URL`
 * alone.
 *
 * It also adds the identity. The audit endpoints **require authorization**, unlike the sibling
 * libraries', so the trail cannot be read without one — and attaching it here rather than in the
 * browser is the point: an identity shipped to the client would let anyone with the page read the
 * whole trail. A real dashboard forwards the signed-in user's own token from this same place.
 */
export default defineEventHandler(async (event) => {
  const { auditApiUrl, demoUser } = useRuntimeConfig(event)
  const path = getRouterParam(event, 'path') ?? ''
  const { search } = getRequestURL(event)

  const target = `${auditApiUrl.replace(/\/$/, '')}/api/${path}${search}`

  return proxyRequest(event, target, {
    headers: {
      'X-Demo-User': demoUser
    }
  })
})
