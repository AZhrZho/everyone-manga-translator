export type HostKind = 'Desktop' | 'Web'
export type ExecutionMode = 'Cached' | 'Replay' | 'Live'
export type ProjectSourceKind = 'Empty' | 'Folder' | 'Pdf'
export type ProjectWorkflowMode = 'Expert' | 'Quick'
export type ProviderEndpointType = 'OpenAIResponses' | 'OpenAIChatCompletions' | 'AnthropicMessages'
export type LocalTextDetectionEngine = 'Ctd' | 'Craft' | 'BrowserCraft'
export type TranslationInputResolution = 'Original' | 'Half' | 'Quarter'
export type ProjectExportFormat = 'Folder' | 'Zip' | 'Pdf'
export type ProjectExportResolution = 'Original' | 'Half' | 'Quarter'
export type JobStatus = 'Pending' | 'Running' | 'Stopped' | 'Completed' | 'Failed'
export type ProcessEventStatus = 'Pending' | 'Running' | 'Completed' | 'Failed'
export type PageReviewTag = 'Unreviewed' | 'Priority' | 'Accepted' | 'Rejected'

export interface SystemInfo {
  productName: string
  productVersion: string
  hostKind: HostKind
  runtimeVersion: string
  dataRoot: string
}

export interface AppSettings {
  theme: 'dark' | 'light'
  locale: 'zh-CN' | 'en-US'
  providers: ProviderSettings[]
  stages: StageModelSelection[]
  defaultFontId: string | null
  fallbackFontId: string | null
  fontLanguageProfiles: FontLanguageProfile[]
  fonts: FontInfo[]
  translationBatchConcurrency: number
  translationBatchSize: number
  precheckBatchConcurrency: number
  precheckBatchSize: number
  localTextDetectionEngine: LocalTextDetectionEngine
}

export interface FontLanguageProfile {
  targetLanguage: string
  allowedFontIds: string[]
  defaultFontId: string | null
}

export interface ProviderSettings {
  providerId: string
  name: string
  endpointType: ProviderEndpointType
  baseUrl: string
  apiKeyConfigured: boolean
  models: ModelSettings[]
}

export interface ModelSettings {
  modelId: string
  displayName: string
  supportsImageInput: boolean
}

export interface StageModelSelection {
  stageId: string
  displayName: string
  requiredModelType: string
  providerId: string | null
  modelId: string | null
}

export interface FontInfo {
  fontId: string
  displayName: string
  fileName: string
  source: string
  canRemove: boolean
}

export interface PageSummary {
  pageId: string
  index: number
  fileName: string
  pageType: string
  includedInWorkflow: boolean
}

export interface ProjectSummary {
  projectId: string
  name: string
  pageCount: number
  pages: PageSummary[]
}

export interface ProjectListItem {
  projectId: string
  name: string
  sourceKind: ProjectSourceKind
  pageCount: number
  locationLabel: string
  isAvailable: boolean
  createdAt: string
  updatedAt: string
  lastOpenedAt: string
}

export interface ProjectPage {
  pageId: string
  index: number
  fileName: string
  width: number
  height: number
  pageType: string
  includedInWorkflow: boolean
  reviewTag: PageReviewTag
  originalAssetUrl: string
  cleanAssetUrl: string
  resultAssetUrl: string
  hasAiResult: boolean
  hasUnversionedUserChanges: boolean
  revision: number
  currentVersionId: string | null
  currentLetteringVersionId: string | null
  currentBackgroundVersionId: string | null
  versionCount: number
}

export interface ProjectDetail {
  projectId: string
  name: string
  sourceKind: ProjectSourceKind
  revision: number
  autoSaveEnabled: boolean
  workflowMode: ProjectWorkflowMode
  targetLanguage: string
  enabledFontIds: string[]
  defaultFontId: string | null
  fallbackFontId: string | null
  precheckBatchSize: number
  precheckBatchConcurrency: number
  locationLabel: string
  pages: ProjectPage[]
  createdAt: string
  updatedAt: string
}

export type ProjectLocationKind = 'ProjectParent' | 'ExistingProject' | 'ImportFolder' | 'ImportPdf' | 'ImportFont'
export type PageVersionKind = 'Imported' | 'Ai' | 'User'
export type PageVersionTrack = 'Lettering' | 'Background'

export interface PageVersion {
  versionId: string
  pageId: string
  sequence: number
  label: string
  kind: PageVersionKind
  track: PageVersionTrack
  parentVersionId: string | null
  isCurrent: boolean
  isWorkingCopy: boolean
  sourceAssetKey: string
  cleanAssetKey: string | null
  maskAssetKey: string | null
  renderedAssetKey: string | null
  layoutJson: string | null
  createdAt: string
}

export interface PageLayoutSnapshot {
  projectId: string
  pageId: string
  projectRevision: number
  pageRevision: number
  hasUnversionedUserChanges: boolean
  currentVersionId: string | null
  layoutJson: string | null
}

export interface PageImageEditSnapshot {
  projectId: string
  pageId: string
  projectRevision: number
  pageRevision: number
  hasUnversionedUserChanges: boolean
  currentVersionId: string | null
  operationsJson: string | null
  renderedAssetUrl: string
}

export interface UpdatePageImageEditResponse {
  project: ProjectDetail
  imageEdit: PageImageEditSnapshot
}

export interface TranslationAnnotationsResponse {
  projectId: string
  projectRevision: number
  pages: Record<string, string>
}

export interface ExportResult {
  format: ProjectExportFormat
  destinationPath: string
  pageCount: number
}

export interface PrecheckPageReview {
  pageId: string
  pageIndex: number
  pageType: string
  confidence: number
  evidence: string[]
}

export interface PrecheckCharacter {
  mergeKey: string
  value: string
  pageIndices: number[]
  evidence: string
  confidence: number
  details?: PrecheckCharacterDetails | null
}

export interface PrecheckCharacterDetails {
  originalName: string
  translatedName: string
  aliases: string[]
  role: string
  speechRules: string[]
  relationships: string[]
  validFromOrder: number
  validToOrder: number | null
  revealSensitive: boolean
  evidencePages: string[]
}

export interface PrecheckTerm {
  mergeKey: string
  value: string
  pageIndices: number[]
  evidence: string
  confidence: number
  details?: PrecheckTermDetails | null
}

export interface PrecheckTermDetails {
  source: string
  translation: string
  category: string
  usageRule: string
  validFromOrder: number
  validToOrder: number | null
  revealSensitive: boolean
  evidencePages: string[]
}

export interface PrecheckBatch {
  batchId: string
  ownerPageIndices: number[]
  priority: string
  estimatedCost: number
}

export interface PrecheckReview {
  runId: string | null
  projectRevision: number
  pages: PrecheckPageReview[]
  characters: PrecheckCharacter[]
  terms: PrecheckTerm[]
  batches: PrecheckBatch[]
  context: PrecheckContextSummary | null
}

export interface PrecheckContextSummary {
  overallSynopsis: string
  tone?: string
  themes?: string[]
  overallVoice: string
  dialogueRules?: string[]
  honorificPolicy?: string
  namePolicy?: string
  prohibitedTendencies?: string[]
  characterCount: number
  termCount: number
  continuityFactCount: number
  continuityFacts?: PrecheckContinuityFact[]
  unresolvedQuestions: string[]
}

export interface PrecheckContinuityFact {
  fact: string
  revealedAtOrder: number
  safeWordingBeforeReveal: string
  evidencePages: string[]
}

export interface PrecheckFixtureSummary {
  runId: string
  pageCount: number
  batchCount: number
  availableArtifacts: string[]
  lastModifiedAt: string
  isDefault: boolean
}

export interface ProcessEvent {
  eventId: string
  stage: string
  title: string
  requestSummary: string
  responseSummary: string | null
  detail: string | null
  status: ProcessEventStatus
  parallelGroup: string | null
  pageIndices: number[]
  updatedAt: string
}

export interface JobSnapshot {
  jobId: string
  projectId: string
  status: JobStatus
  executionMode: ExecutionMode
  fixtureRunId: string | null
  progressPercent: number
  events: ProcessEvent[]
  createdAt: string
  updatedAt: string
  translationSettings: {
    batchConcurrency: number
    batchSize: number
    inputResolution: TranslationInputResolution
  } | null
}
