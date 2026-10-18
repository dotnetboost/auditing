/** A timestamp as the local date and time, to the second. */
export function formatDateTime(value: string): string {
  return new Date(value).toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  })
}

/** How long ago, in words — "4 minutes ago". */
export function formatRelative(value: string): string {
  const then = new Date(value).getTime()
  const seconds = Math.round((Date.now() - then) / 1000)

  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['second', 60],
    ['minute', 60],
    ['hour', 24],
    ['day', 30],
    ['month', 12],
    ['year', Number.POSITIVE_INFINITY]
  ]

  let value_ = -seconds
  for (const [unit, size] of units) {
    if (Math.abs(value_) < size) {
      return new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(Math.round(value_), unit)
    }
    value_ /= size
  }

  return formatDateTime(value)
}

/**
 * What to show for a recorded value.
 *
 * Three different absences, and conflating them is the easiest way for a trail to mislead:
 * a redacted value was withheld on purpose, a null was never stored, and an empty string was
 * stored and is empty.
 */
export function formatValue(value: string | null, isRedacted: boolean): string {
  if (isRedacted) return '•••••'
  if (value === null) return '—'
  if (value === '') return '(empty)'
  return value
}

/** A correlation id, shortened for a table but still recognisable. */
export function shortId(value: string): string {
  return value.length <= 12 ? value : `${value.slice(0, 8)}…${value.slice(-4)}`
}

/** An actor, or an honest label for one that was never captured. */
export function formatActor(actor: string | null): string {
  return actor ?? 'not captured'
}
