import type { FontInfo, FontLanguageProfile, ProjectWorkflowMode } from '../../../contracts'

export type ProjectDialogCommon = {
  name: string
  destinationToken: string
  destinationLabel: string
  workflowMode: ProjectWorkflowMode
  targetLanguage: string
  enabledFontIds: string[]
  defaultFontId: string | null
  fallbackFontId: string | null
  availableFonts: FontInfo[]
  fontLanguageProfiles: FontLanguageProfile[]
}

export type ProjectDialogState = ProjectDialogCommon & (
  | { kind: 'import-folder'; sourceToken: string; sourceLabel: string }
  | { kind: 'import-pdf'; sourceToken: string; sourceLabel: string; renderDpi: number }
)
