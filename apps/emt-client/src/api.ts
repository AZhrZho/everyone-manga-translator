import type {
  ExecutionMode,
  AppSettings,
  ExportResult,
  JobSnapshot,
  PrecheckFixtureSummary,
  ProjectDetail,
  PageLayoutSnapshot,
  PageImageEditSnapshot,
  UpdatePageImageEditResponse,
  TranslationAnnotationsResponse,
  PageVersion,
  PrecheckReview,
  ProjectListItem,
  ProjectExportFormat,
  ProjectExportResolution,
  ProjectWorkflowMode,
  PageReviewTag,
  PageVersionTrack,
  SystemInfo,
} from './contracts'
import type {
  BrowserDetectionUploadMetadata,
  BrowserDetectionUploadResponse,
} from './features/translation/browser-inference/contracts'

export type ProjectSettingsInput = {
  workflowMode: ProjectWorkflowMode
  targetLanguage: string
  enabledFontIds: string[]
  defaultFontId: string | null
  fallbackFontId?: string | null
  precheckBatchSize?: number
  precheckBatchConcurrency?: number
}

export const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://127.0.0.1:5198'

export class ApiRequestError extends Error {
  readonly method: string
  readonly url: string
  readonly status: number
  readonly statusText: string
  readonly responseBody: string | null

  constructor(method: string, url: string, status: number, statusText: string, message: string, responseBody: string | null) {
    super(message)
    this.name = 'ApiRequestError'
    this.method = method
    this.url = url
    this.status = status
    this.statusText = statusText
    this.responseBody = responseBody
  }
}

async function createRequestError(response: Response, method: string, url: string) {
  const responseBody = await response.text().catch(() => '')
  let detail: string | null = null
  if (responseBody) {
    try {
      const problem = JSON.parse(responseBody) as { detail?: string; title?: string }
      detail = problem.detail ?? problem.title ?? null
    } catch {
      detail = responseBody.length <= 500 ? responseBody : null
    }
  }
  return new ApiRequestError(
    method,
    url,
    response.status,
    response.statusText,
    detail ?? `${response.status} ${response.statusText}`,
    responseBody || null,
  )
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const url = `${apiBaseUrl}${path}`
  const method = init?.method ?? 'GET'
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    throw await createRequestError(response, method, url)
  }
  return response.json() as Promise<T>
}

async function requestForm<T>(path: string, form: FormData): Promise<T> {
  const url = `${apiBaseUrl}${path}`
  const method = 'POST'
  const response = await fetch(url, { method, body: form })
  if (!response.ok) {
    throw await createRequestError(response, method, url)
  }
  return response.json() as Promise<T>
}

async function requestClip(path: string, init: RequestInit) {
  const url = `${apiBaseUrl}${path}`
  const method = init.method ?? 'GET'
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init.headers },
  })
  if (!response.ok) {
    throw await createRequestError(response, method, url)
  }
  const blob = await response.blob()
  return {
    blob,
    width: Number(response.headers.get('X-EMT-Clip-Width') ?? 0),
    height: Number(response.headers.get('X-EMT-Clip-Height') ?? 0),
    fingerprint: response.headers.get('X-EMT-Clip-Fingerprint') ?? '',
  }
}

export const api = {
  getSystemInfo: () => request<SystemInfo>('/api/system/info'),
  listProjects: () => request<ProjectListItem[]>('/api/projects'),
  removeProject: async (projectId: string, deleteFiles: boolean) => {
    const url = `${apiBaseUrl}/api/projects/${encodeURIComponent(projectId)}?deleteFiles=${deleteFiles}`
    const response = await fetch(url, { method: 'DELETE' })
    if (!response.ok) throw await createRequestError(response, 'DELETE', url)
  },
  getProject: (projectId: string) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}`),
  saveBrowserDetection: (
    projectId: string,
    pageId: string,
    metadata: BrowserDetectionUploadMetadata,
    mask: Blob,
  ) => {
    const form = new FormData()
    form.append('metadata', JSON.stringify(metadata))
    form.append('mask', mask, `${pageId}.craft-mask.png`)
    return requestForm<BrowserDetectionUploadResponse>(
      `/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/browser-detection`,
      form,
    )
  },
  createProject: (name: string, destinationToken: string, settings?: ProjectSettingsInput) => request<ProjectDetail>('/api/projects', {
    method: 'POST',
    body: JSON.stringify({ name, destinationToken, ...settings }),
  }),
  openProjectFromLocation: (locationToken: string) => request<ProjectDetail>('/api/projects/open', {
    method: 'POST',
    body: JSON.stringify({ locationToken }),
  }),
  importFolderProject: (name: string, destinationToken: string, sourceToken: string, settings?: ProjectSettingsInput) => request<ProjectDetail>('/api/projects/import-folder', {
    method: 'POST',
    body: JSON.stringify({ name, destinationToken, sourceToken, ...settings }),
  }),
  importPdfProject: (name: string, destinationToken: string, sourceToken: string, renderDpi = 200, settings?: ProjectSettingsInput) => request<ProjectDetail>('/api/projects/import-pdf', {
    method: 'POST',
    body: JSON.stringify({ name, destinationToken, sourceToken, renderDpi, ...settings }),
  }),
  exportProject: (projectId: string, destinationToken: string, format: ProjectExportFormat) => request<ExportResult>(`/api/projects/${encodeURIComponent(projectId)}/export`, {
    method: 'POST',
    body: JSON.stringify({ destinationToken, format }),
  }),
  startExport: (projectId: string, destinationToken: string | null, format: ProjectExportFormat, includeBackground: boolean, includeLettering: boolean, resolution: ProjectExportResolution) => request<JobSnapshot>('/api/jobs/export', {
    method: 'POST',
    body: JSON.stringify({ projectId, destinationToken, format, includeBackground, includeLettering, resolution }),
  }),
  getJob: (jobId: string) => request<JobSnapshot>(`/api/jobs/${encodeURIComponent(jobId)}`),
  listPageVersions: (projectId: string, pageId: string) => request<PageVersion[]>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/versions`),
  getPageLayout: (projectId: string, pageId: string) => request<PageLayoutSnapshot>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/layout`),
  updatePageLayout: (projectId: string, pageId: string, layoutJson: string, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/layout`, {
    method: 'PATCH',
    body: JSON.stringify({ layoutJson, expectedRevision }),
  }),
  renderTextClip: (projectId: string, pageId: string, layoutJson: string, segmentId: string) => requestClip(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/text-clips/render`, {
    method: 'POST',
    body: JSON.stringify({ layoutJson, segmentId }),
  }),
  getPageImageEdit: (projectId: string, pageId: string) => request<PageImageEditSnapshot>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/image-edit`),
  updatePageImageEdit: (projectId: string, pageId: string, operationsJson: string, expectedPageRevision: number) => request<UpdatePageImageEditResponse>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/image-edit`, {
    method: 'PATCH',
    body: JSON.stringify({ operationsJson, expectedPageRevision }),
  }),
  getTranslationAnnotations: (projectId: string) => request<TranslationAnnotationsResponse>(`/api/projects/${encodeURIComponent(projectId)}/translation-annotations`),
  updateTranslationAnnotations: (projectId: string, pages: Record<string, string>, expectedRevision: number) => request<TranslationAnnotationsResponse>(`/api/projects/${encodeURIComponent(projectId)}/translation-annotations`, {
    method: 'PATCH',
    body: JSON.stringify({ pages, expectedRevision }),
  }),
  createPageVersion: (projectId: string, pageId: string, label: string, expectedRevision: number, kind: PageVersion['kind'] = 'User', track: PageVersionTrack = 'Lettering') => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/versions`, {
    method: 'POST',
    body: JSON.stringify({ label, expectedRevision, kind, track }),
  }),
  activatePageVersion: (projectId: string, pageId: string, versionId: string, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/versions/${encodeURIComponent(versionId)}/activate?expectedRevision=${expectedRevision}`, {
    method: 'POST',
  }),
  deletePageVersion: (projectId: string, pageId: string, versionId: string, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/versions/${encodeURIComponent(versionId)}?expectedRevision=${expectedRevision}`, {
    method: 'DELETE',
  }),
  getPrecheckReview: (projectId: string) => request<PrecheckReview>(`/api/projects/${encodeURIComponent(projectId)}/precheck-review`),
  updatePrecheckItem: (projectId: string, artifactName: 'characters.json' | 'glossary.json', itemKey: string, value: string | null, mergeIntoKey: string | null, deleteItem: boolean, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/precheck/${encodeURIComponent(artifactName)}`, {
    method: 'PATCH',
    body: JSON.stringify({ itemKey, value, mergeIntoKey, delete: deleteItem, expectedRevision }),
  }),
  renameProject: (projectId: string, name: string, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}`, {
    method: 'PATCH',
    body: JSON.stringify({ name, expectedRevision }),
  }),
  updateProjectSettings: (projectId: string, settings: ProjectSettingsInput, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/settings`, {
    method: 'PATCH',
    body: JSON.stringify({ ...settings, expectedRevision }),
  }),
  updatePageReview: (
    projectId: string,
    pageId: string,
    pageType: string,
    includedInWorkflow: boolean,
    expectedRevision: number,
  ) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/review`, {
    method: 'PATCH',
    body: JSON.stringify({ pageType, includedInWorkflow, expectedRevision }),
  }),
  updatePageReviewTag: (projectId: string, pageId: string, reviewTag: PageReviewTag, expectedRevision: number) => request<ProjectDetail>(`/api/projects/${encodeURIComponent(projectId)}/pages/${encodeURIComponent(pageId)}/review-tag`, {
    method: 'PATCH',
    body: JSON.stringify({ reviewTag, expectedRevision }),
  }),
  getPrecheckFixtures: () => request<PrecheckFixtureSummary[]>('/api/precheck/fixtures'),
  startPrecheck: (projectId: string, executionMode: ExecutionMode = 'Live', fixtureRunId?: string) => request<JobSnapshot>('/api/jobs/precheck', {
    method: 'POST',
    body: JSON.stringify({ projectId, executionMode, fixtureRunId }),
  }),
  listProjectJobs: (projectId: string) => request<JobSnapshot[]>(`/api/projects/${encodeURIComponent(projectId)}/jobs`),
  startTranslation: (projectId: string, executionMode: ExecutionMode = 'Live', fixtureRunId?: string, pageIndices?: number[]) => request<JobSnapshot>('/api/jobs/translation', {
    method: 'POST',
    body: JSON.stringify({ projectId, executionMode, fixtureRunId, pageIndices }),
  }),
  listTranslationJobs: (projectId: string) => request<JobSnapshot[]>(`/api/projects/${encodeURIComponent(projectId)}/translation-jobs`),
  listExportJobs: (projectId: string) => request<JobSnapshot[]>(`/api/projects/${encodeURIComponent(projectId)}/export-jobs`),
  clearFinishedJobs: (projectId: string) => request<{ deletedCount: number }>(`/api/projects/${encodeURIComponent(projectId)}/jobs/finished`, { method: 'DELETE' }),
  stopJob: (jobId: string) => request<JobSnapshot>(`/api/jobs/${jobId}/stop`, { method: 'POST' }),
  resumeJob: (jobId: string) => request<JobSnapshot>(`/api/jobs/${jobId}/resume`, { method: 'POST' }),
  retryPrecheckJob: (jobId: string) => request<JobSnapshot>(`/api/jobs/${jobId}/retry`, { method: 'POST' }),
  getSettings: () => request<AppSettings>('/api/settings'),
  updateSettings: (settings: { theme: 'dark' | 'light'; locale: 'zh-CN' | 'en-US'; providers: unknown[]; stages: unknown[]; defaultFontId: string | null; fallbackFontId: string | null; fontLanguageProfiles: AppSettings['fontLanguageProfiles']; translationBatchConcurrency: number; translationBatchSize: number; precheckBatchConcurrency: number; precheckBatchSize: number; localTextDetectionEngine: AppSettings['localTextDetectionEngine'] }) => request<AppSettings>('/api/settings', {
    method: 'PATCH',
    body: JSON.stringify(settings),
  }),
  importFonts: (sourceTokens: string[], targetLanguage: string) => request<AppSettings>('/api/settings/fonts/import', {
    method: 'POST',
    body: JSON.stringify({ sourceTokens, targetLanguage }),
  }),
  removeFont: (fontId: string) => request<AppSettings>(`/api/settings/fonts/${encodeURIComponent(fontId)}`, { method: 'DELETE' }),
}
