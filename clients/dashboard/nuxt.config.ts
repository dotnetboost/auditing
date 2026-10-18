// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  modules: [
    '@nuxt/eslint',
    '@nuxt/ui',
    '@vueuse/nuxt'
  ],

  // Client-rendered single-page app. Nitro is still used, but only to proxy the
  // API (see server/api/[...path].ts) — never to render pages.
  ssr: false,

  devtools: {
    enabled: true
  },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    // Base address of the API: the audit endpoints and the sample's product endpoints. Read by the
    // proxy handler only, so the browser never sees it. Override with NUXT_AUDIT_API_URL.
    auditApiUrl: 'http://localhost:5280',

    // The audit endpoints require authorization — unlike the sibling libraries' — and the sample
    // authenticates whoever this names. Sent by the proxy, so it stays server-side: a dashboard
    // that put an identity in the browser would be handing out the trail.
    //
    // A stand-in for the sample's demo scheme. A real dashboard forwards the user's own token.
    demoUser: 'dashboard@example.com',

    public: {
      // Linked from the sidebar — the API's own Scalar reference.
      // Override with NUXT_PUBLIC_API_REFERENCE_URL.
      apiReferenceUrl: 'http://localhost:5280/scalar',

      // AuditingOptions.MaxPageSize on the API. The trail browser will not ask for a page the
      // engine would only clamp, so the page-size selector stops here.
      // Override with NUXT_PUBLIC_MAX_PAGE_SIZE.
      maxPageSize: 200
    }
  },

  compatibilityDate: '2026-06-30',

  eslint: {
    config: {
      stylistic: {
        commaDangle: 'never',
        braceStyle: '1tbs'
      }
    }
  }
})
