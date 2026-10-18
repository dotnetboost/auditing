<script setup lang="ts">
import { CalendarDate, type DateValue } from '@internationalized/date'
import type { DateRange } from 'reka-ui'

/**
 * The window a trail is read over — `from` and `to` as `GET /api/audit` takes them.
 *
 * One control rather than two boxes, because a window is one question. Three ways to ask it, in
 * the order they are actually used: a preset for "what just happened", a calendar for "that week
 * in March", and two exact boxes for the forensic case where the minute matters.
 *
 * Both halves are kept in `datetime-local` wire format — `YYYY-MM-DDTHH:mm`, no zone — which is
 * what the exact inputs read and write, and what `useAuditTrail()` hands to `new Date(…)`, which
 * reads an unzoned string as local time. So a window means what it says on the operator's clock.
 */
const from = defineModel<string | undefined>('from')
const to = defineModel<string | undefined>('to')

const pad = (value: number) => String(value).padStart(2, '0')

const wire = (date: Date) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
  + `T${pad(date.getHours())}:${pad(date.getMinutes())}`

// Anything unparseable is treated as no bound at all rather than as a date in 1970 — these strings
// can arrive half-typed from the exact inputs.
const parse = (value: string | undefined) => {
  if (!value) return undefined
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? undefined : date
}

const startOfDay = (date: DateValue) => new Date(date.year, date.month - 1, date.day)

const calendarDate = (date: Date) =>
  new CalendarDate(date.getFullYear(), date.getMonth() + 1, date.getDate())

const sameDay = (a: DateValue | undefined, b: DateValue | undefined) =>
  a === b || (!!a && !!b && a.year === b.year && a.month === b.month && a.day === b.day)

const open = ref(false)

/**
 * Presets leave the end open on purpose. Pinning it to "now" would freeze the window at the moment
 * it was chosen, and the next refresh would show nothing recorded since — which is exactly what
 * somebody watching a live trail asked for the preset to avoid.
 */
const presets = [
  { label: 'Last hour', minutes: 60 },
  { label: 'Last 24 hours', minutes: 60 * 24 },
  { label: 'Last 7 days', minutes: 60 * 24 * 7 },
  { label: 'Last 30 days', minutes: 60 * 24 * 30 }
]

const since = (minutes: number) => {
  from.value = wire(new Date(Date.now() - minutes * 60_000))
  to.value = undefined
}

const midnight = (daysAgo: number) => {
  const date = new Date()
  date.setHours(0, 0, 0, 0)
  date.setDate(date.getDate() - daysAgo)
  return date
}

const today = () => {
  from.value = wire(midnight(0))
  to.value = undefined
}

// The only preset with a closed end, because yesterday has one.
const yesterday = () => {
  from.value = wire(midnight(1))
  to.value = wire(midnight(0))
}

const clear = () => {
  from.value = undefined
  to.value = undefined
}

// The last selectable day — a trail records what already happened. Re-read each time the popover
// opens rather than once at setup: a dashboard left running overnight would otherwise stop at
// yesterday.
// `shallowRef`, not `ref`: a deep ref would proxy the CalendarDate and strip the class the
// calendar type-checks against.
const latest = shallowRef(calendarDate(new Date()))

watch(open, (isOpen) => {
  if (isOpen) latest.value = calendarDate(new Date())
})

/**
 * What the calendar shows, as the days `from` and `to` fall on.
 *
 * `to` is exclusive, so the day it selects is the one before the instant itself: a window ending
 * at midnight on the 10th is the 9th highlighted, not the 10th. Stepping back by a millisecond
 * lands on the right day for a mid-day bound too.
 */
const days = (): DateRange => {
  const start = parse(from.value)
  const end = parse(to.value)

  return {
    start: start ? calendarDate(start) : undefined,
    end: end ? calendarDate(new Date(end.getTime() - 1)) : undefined
  }
}

/**
 * The calendar's own state, rather than a computed written straight back into the filters.
 *
 * A range takes two clicks, and the calendar holds "start chosen, waiting for the end" internally.
 * Writing the first click back through the model replaced that state with a fresh value and the
 * calendar forgot it was mid-selection — so the second click never landed as an end. Keeping it
 * here also means the trail is not re-read for half a window nobody asked a question with yet.
 */
const picked = shallowRef<DateRange | null>(days())

// Calendar → filters, once both ends are in.
watch(picked, (value) => {
  const start = value?.start
  const end = value?.end
  if (!start || !end) return

  // The calendar only knows about days, so an echo of a window that already starts at 16:15
  // arrives as "the 17th" and would be written back as midnight. Nothing is written unless the
  // days actually moved — otherwise every preset and every exact time collapses to 00:00.
  const current = days()
  if (sameDay(start, current.start) && sameDay(end, current.end)) return

  from.value = wire(startOfDay(start))
  // A day picked as the end is meant whole, and the API excludes its end — so the window closes at
  // the midnight *after* it. Without the extra day, picking the 9th would stop at the 9th's first
  // instant and hide everything that happened that day.
  to.value = wire(startOfDay(end.add({ days: 1 })))
})

// Filters → calendar, for the presets, the exact boxes, Clear, and a window arriving from a link.
watch([from, to], () => {
  picked.value = days()
})

const sameYear = (date: Date) => date.getFullYear() === new Date().getFullYear()

const day = (date: Date) =>
  date.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    year: sameYear(date) ? undefined : 'numeric'
  })

const stamp = (date: Date) =>
  `${day(date)}, ${date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}`

const isMidnight = (date: Date) => date.getHours() === 0 && date.getMinutes() === 0

/**
 * What the button says. Derived from the two values rather than remembered from the preset that set
 * them: "Last 24 hours" chosen an hour ago is no longer the last 24 hours, and a label that keeps
 * claiming it would be describing a window nobody is looking at.
 */
const label = computed(() => {
  const start = parse(from.value)
  const end = parse(to.value)

  if (!start && !end) return 'Any time'
  if (start && !end) return `Since ${isMidnight(start) ? day(start) : stamp(start)}`
  if (!start && end) return `Before ${isMidnight(end) ? day(end) : stamp(end)}`

  // Both whole days: say it the way it was picked — the inclusive last day, not the exclusive
  // instant one millisecond into the next one.
  if (isMidnight(start!) && isMidnight(end!)) {
    const last = new Date(end!.getTime() - 1)
    return day(start!) === day(last) ? day(start!) : `${day(start!)} – ${day(last)}`
  }

  return `${stamp(start!)} → ${stamp(end!)}`
})

const set = computed(() => Boolean(from.value || to.value))
</script>

<template>
  <UPopover v-model:open="open" :content="{ align: 'start' }">
    <UButton
      icon="i-lucide-calendar-range"
      :color="set ? 'primary' : 'neutral'"
      :variant="set ? 'subtle' : 'outline'"
      trailing-icon="i-lucide-chevron-down"
      class="w-full"
    >
      <!-- Grows so the label sits against the leading icon rather than floating mid-button, and
           truncates rather than stretching the column when an exact window is spelled out. -->
      <span class="grow text-left truncate">{{ label }}</span>
    </UButton>

    <template #content>
      <div class="flex flex-col sm:flex-row divide-y sm:divide-y-0 sm:divide-x divide-default">
        <div class="flex flex-col gap-1 p-2 sm:w-40">
          <UButton
            v-for="preset in presets"
            :key="preset.label"
            color="neutral"
            variant="ghost"
            size="sm"
            class="justify-start"
            @click="since(preset.minutes)"
          >
            {{ preset.label }}
          </UButton>

          <UButton
            color="neutral"
            variant="ghost"
            size="sm"
            class="justify-start"
            @click="today()"
          >
            Today
          </UButton>
          <UButton
            color="neutral"
            variant="ghost"
            size="sm"
            class="justify-start"
            @click="yesterday()"
          >
            Yesterday
          </UButton>

          <USeparator class="my-1" />

          <UButton
            color="neutral"
            variant="ghost"
            size="sm"
            icon="i-lucide-x"
            class="justify-start"
            :disabled="!set"
            @click="clear()"
          >
            Any time
          </UButton>
        </div>

        <div class="p-2 space-y-2">
          <UCalendar
            v-model="picked"
            range
            fixed-weeks
            :max-value="latest"
            class="p-0"
          />

          <USeparator />

          <!--
            The exact window, for when the minute is the point. Shown rather than hidden behind a
            second click: a calendar cannot express "between 14:02 and 14:06", and that is the
            question somebody reading a trail during an incident is holding.

            Stacked below `sm`: a `datetime-local` box asks for about 220px of its own, and two of
            them side by side are wider than a phone — which pushed the calendar's last column off
            the screen rather than wrapping it.
          -->
          <div class="flex flex-col sm:flex-row sm:items-end gap-2">
            <UFormField label="From" size="xs" class="flex-1 min-w-0">
              <UInput
                v-model="from"
                type="datetime-local"
                size="sm"
                class="w-full min-w-0"
              />
            </UFormField>

            <UFormField label="To" size="xs" class="flex-1 min-w-0">
              <UInput
                v-model="to"
                type="datetime-local"
                size="sm"
                class="w-full min-w-0"
              />
            </UFormField>
          </div>

          <p class="text-xs text-muted max-w-72">
            A window includes its start and excludes its end, so consecutive ranges never
            double-count. Picking whole days in the calendar includes the last one.
          </p>
        </div>
      </div>
    </template>
  </UPopover>
</template>
