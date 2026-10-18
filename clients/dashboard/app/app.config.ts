export default defineAppConfig({
  ui: {
    // Indigo rather than the suite's usual green: this dashboard reads a record of what people
    // did, and it should not be mistaken at a glance for the media library.
    colors: {
      primary: 'indigo',
      neutral: 'zinc'
    }
  }
})
