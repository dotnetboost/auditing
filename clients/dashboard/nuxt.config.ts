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

    public: {
      // Linked from the sidebar — the API's own Scalar reference.
      // Override with NUXT_PUBLIC_API_REFERENCE_URL.
      apiReferenceUrl: 'http://localhost:5280/scalar',

      // FALLBACK for AuditingOptions.MaxPageSize. The API publishes its own bound in
      // GET /api/audit/entities and the trail browser uses that; this is what it falls back to
      // before the API has answered, and for an API with discovery turned off.
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
