import type { ProcessEvent } from '../../../contracts'
import type { TimelineEntry } from '../../../shared/types/workspace'

export function buildTimeline(events: ProcessEvent[]): TimelineEntry[] {
  const result: TimelineEntry[] = []
  const grouped = new Set<string>()
  for (const event of events) {
    if (!event.parallelGroup) {
      result.push({ kind: 'event', event })
      continue
    }
    if (grouped.has(event.parallelGroup)) continue
    grouped.add(event.parallelGroup)
    result.push({ kind: 'parallel', groupId: event.parallelGroup, events: events.filter(candidate => candidate.parallelGroup === event.parallelGroup) })
  }
  return result
}
