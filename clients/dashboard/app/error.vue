<script setup lang="ts">
import type { NuxtError } from '#app'

const props = defineProps<{
  error: NuxtError
}>()

// This page catches everything, not just a missing route, so the title has to read the status —
// announcing a 500 as "Page not found" sends somebody looking for a bad link instead of a broken
// dashboard. `UError` already shows the status and its message in the body; this is the tab.
const notFound = computed(() => props.error.statusCode === 404)

const title = computed(() =>
  notFound.value ? 'Page not found' : `Something went wrong (${props.error.statusCode ?? 500})`)

const description = computed(() =>
  notFound.value
    ? 'We are sorry but this page could not be found.'
    : props.error.statusMessage || 'The dashboard could not render this page.')

useSeoMeta({ title, description })

useHead({
  htmlAttrs: {
    lang: 'en'
  }
})
</script>

<template>
  <UApp>
    <UError :error="error" />
  </UApp>
</template>
