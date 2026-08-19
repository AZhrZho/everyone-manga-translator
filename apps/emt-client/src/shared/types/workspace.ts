import type { JobSnapshot, ProcessEvent } from '../../contracts'

export type TimelineEntry =
  | { kind: 'event'; event: ProcessEvent }
  | { kind: 'parallel'; groupId: string; events: ProcessEvent[] }

export type ReviewPane = 'run' | 'pages' | 'characters'
export type WorkspaceStage = 'precheck' | 'selection' | 'translation' | 'export'
export type WorkspaceMode = 'preview' | 'clean' | 'layout'
export type PreviewLayer = 'original' | 'clean' | 'original-layout' | 'result'
export type ViewportState = { zoom: number; panX: number; panY: number }

export type WorkspaceActivity = {
  active: boolean
  label: string
  progressPercent: number
  taskCenter?: {
    jobs: JobSnapshot[]
    exportJob: JobSnapshot | null
    exportMessage: string | null
    onStopExport: () => Promise<boolean>
    onClearFinished?: () => Promise<number>
  }
}
