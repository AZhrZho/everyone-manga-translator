import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Image } from 'lucide-react'
import { api } from '../../../api'
import type {
  FontInfo,
  JobSnapshot,
  PageReviewTag,
  ProjectDetail,
  ProjectPage,
  PageVersion,
} from '../../../contracts'
import { dialogService } from '../../../dialogService'
import { readLocale } from '../../../i18n'
import { assetUrl } from '../../../shared/lib/assets'
import { isPresentableError, logBackgroundError, readError } from '../../../shared/lib/errors'
import type { PreviewLayer, ViewportState, WorkspaceMode } from '../../../shared/types/workspace'
import {
  clampBox,
  createSegment,
  parseLayout,
  readBasicTextFormat,
  segmentRenderKey,
  serializeLayout,
} from '../../editor/model/layout'
import type {
  BasicTextFormat,
  LayoutBox,
  LayoutDocument,
  LayoutHistory,
  LayoutManualOverride,
  LayoutSegment,
  LayoutSpec,
  TextClip,
} from '../../editor/model/layout'
import { createDefaultImageEdit, parseImageEdit, serializeImageEdit } from '../../editor/model/imageEdit'
import type {
  ImageBrushSettings,
  ImageEditDocument,
  ImageEditHistory,
  ImageEditOperation,
} from '../../editor/model/imageEdit'
import { useTranslationAnnotations } from '../annotations/useTranslationAnnotations'
import { ImageEditCanvas } from '../image-edit/ImageEditCanvas'
import { LayoutCanvas } from '../layout/LayoutCanvas'
import { PreviewPane } from '../preview/PreviewPane'
import { TranslationInspector } from './TranslationInspector'
import { TranslationPagesSidebar } from './TranslationPagesSidebar'
import { TranslationToolRail } from './TranslationToolRail'
import { TranslationWorkspaceToolbar } from './TranslationWorkspaceToolbar'
import { useTranslationWorkspaceKeyboard } from './useTranslationWorkspaceKeyboard'

export type TranslationWorkspaceProps = {
  active: boolean
  project: ProjectDetail
  jobs: JobSnapshot[]
  error: string | null
  onStart: (pageIndices?: number[]) => void
  onStop: () => void
  onResume: () => void
  onProjectChange: (project: ProjectDetail) => void
  onError: (error: string | null) => void
  onFlushReady: (flush: () => Promise<boolean>) => void
  editingLocked: boolean
}

export function TranslationWorkspace({ active, project, jobs, error, onStart, onStop, onResume, onProjectChange, onError, onFlushReady, editingLocked }: TranslationWorkspaceProps) {
  const locale = readLocale()
  const [selectedPageId, setSelectedPageId] = useState(project.pages[0]?.pageId ?? null)
  const [mode, setMode] = useState<WorkspaceMode>('preview')
  const [split, setSplit] = useState(true)
  const [showOriginalTemporarily, setShowOriginalTemporarily] = useState(false)
  const [formatClipboard, setFormatClipboard] = useState<BasicTextFormat | null>(null)
  const [leftLayer, setLeftLayer] = useState<PreviewLayer>('original')
  const [rightLayer, setRightLayer] = useState<PreviewLayer>('result')
  const [versions, setVersions] = useState<PageVersion[]>([])
  const [layout, setLayout] = useState<LayoutDocument | null>(null)
  const [textClips, setTextClips] = useState<Record<string, TextClip>>({})
  const [layoutLoading, setLayoutLoading] = useState(false)
  const [selectedSegmentId, setSelectedSegmentId] = useState<string | null>(null)
  const [layoutFonts, setLayoutFonts] = useState<FontInfo[]>([])
  const [historyByPage, setHistoryByPage] = useState<Record<string, LayoutHistory>>({})
  const [saveState, setSaveState] = useState<'saved' | 'dirty' | 'saving' | 'error'>('saved')
  const [imageEdit, setImageEdit] = useState<ImageEditDocument | null>(null)
  const [imageEditBaseUrl, setImageEditBaseUrl] = useState<string | null>(null)
  const [imageEditLoading, setImageEditLoading] = useState(false)
  const [imageEditSize, setImageEditSize] = useState(36)
  const imageBrushSettings: ImageBrushSettings = { spacing: 18 }
  const [imageEditHistoryByPage, setImageEditHistoryByPage] = useState<Record<string, ImageEditHistory>>({})
  const [imageEditSaveState, setImageEditSaveState] = useState<'saved' | 'dirty' | 'saving' | 'error'>('saved')
  const [editTransitionBusy, setEditTransitionBusy] = useState(false)
  const [reviewFilter, setReviewFilter] = useState<'All' | PageReviewTag>('All')
  const [reviewTagBusyPageId, setReviewTagBusyPageId] = useState<string | null>(null)
  const [viewportByPage, setViewportByPage] = useState<Record<string, ViewportState>>({})
  const [spacePan, setSpacePan] = useState(false)
  const [previewRefreshNonce, setPreviewRefreshNonce] = useState(0)
  const selectedPage = project.pages.find(page => page.pageId === selectedPageId) ?? project.pages[0]
  const layoutRef = useRef<LayoutDocument | null>(null)
  const textClipsRef = useRef<Record<string, TextClip>>({})
  const clipRequestSequenceRef = useRef<Record<string, number>>({})
  const clipGenerationRef = useRef(0)
  const imageEditRef = useRef<ImageEditDocument | null>(null)
  const imageEditSnapshotRef = useRef<{ pageId: string; pageRevision: number } | null>(null)
  const layoutSnapshotRef = useRef<{ projectId: string; pageId: string; letteringVersionId: string | null } | null>(null)
  const projectRevisionRef = useRef(project.revision)
  const savingRef = useRef<Promise<boolean> | null>(null)
  const imageEditSavingRef = useRef<Promise<boolean> | null>(null)
  const flushAllEditsRef = useRef<() => Promise<boolean>>(async () => true)
  const activeFitScaleRef = useRef(1)
  const pendingClipRendersRef = useRef<Set<Promise<void>>>(new Set())
  const completedTranslationPagesByJobRef = useRef<Record<string, Set<number>>>({})
  const pageRowRefsRef = useRef<Map<string, HTMLDivElement>>(new Map())
  const activePageKeyRef = useRef(`${project.projectId}:${selectedPage?.pageId ?? ''}`)
  activePageKeyRef.current = `${project.projectId}:${selectedPage?.pageId ?? ''}`
  const viewport = selectedPage ? viewportByPage[selectedPage.pageId] ?? { zoom: 1, panX: 0, panY: 0 } : { zoom: 1, panX: 0, panY: 0 }
  const updateViewport = useCallback((next: ViewportState) => {
    if (!selectedPageId) return
    setViewportByPage(current => ({ ...current, [selectedPageId]: next }))
  }, [selectedPageId])
  const reportFitScale = useCallback((fitScale: number) => { activeFitScaleRef.current = fitScale }, [])
  const ignoreFitScale = useCallback(() => undefined, [])
  const exporting = editingLocked
  const jobRefreshSignature = useMemo(
    () => jobs.map(item => `${item.jobId}:${item.updatedAt}`).join('|'),
    [jobs],
  )
  const activeTranslationJobs = useMemo(
    () => jobs.filter(item => item.status === 'Pending' || item.status === 'Running'),
    [jobs],
  )
  const runningTranslation = activeTranslationJobs.length > 0
  const runningTranslationEvents = useMemo(
    () => activeTranslationJobs.flatMap(item => item.events.filter(event => event.status === 'Running')),
    [activeTranslationJobs],
  )
  const generatingPageIndices = useMemo(
    () => new Set(runningTranslationEvents.flatMap(event => event.pageIndices)),
    [runningTranslationEvents],
  )
  const visiblePages = useMemo(
    () => project.pages.filter(page => reviewFilter === 'All' || page.reviewTag === reviewFilter),
    [project.pages, reviewFilter],
  )
  const {
    annotationText,
    annotationSaveState,
    updateAnnotation,
    flushAnnotations,
  } = useTranslationAnnotations({
    projectId: project.projectId,
    selectedPageId,
    fallbackPageId: project.pages[0]?.pageId ?? null,
    projectRevisionRef,
    onError,
  })

  useEffect(() => { layoutRef.current = layout }, [layout])
  useEffect(() => { textClipsRef.current = textClips }, [textClips])
  useEffect(() => { imageEditRef.current = imageEdit }, [imageEdit])
  useEffect(() => { projectRevisionRef.current = project.revision }, [project.revision])
  useEffect(() => {
    if (selectedPageId && project.pages.some(page => page.pageId === selectedPageId)) return
    setSelectedPageId(project.pages[0]?.pageId ?? null)
  }, [project.pages, selectedPageId])

  useEffect(() => {
    clipGenerationRef.current += 1
    for (const clip of Object.values(textClipsRef.current)) URL.revokeObjectURL(clip.url)
    textClipsRef.current = {}
    setTextClips({})
    clipRequestSequenceRef.current = {}
  }, [project.projectId, selectedPageId])

  useEffect(() => () => {
    for (const clip of Object.values(textClipsRef.current)) URL.revokeObjectURL(clip.url)
  }, [])

  useEffect(() => {
    if (!active || !selectedPageId) return
    const frame = window.requestAnimationFrame(() => {
      pageRowRefsRef.current.get(selectedPageId)?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    })
    return () => window.cancelAnimationFrame(frame)
  }, [active, reviewFilter, selectedPageId])

  useEffect(() => {
    onFlushReady(() => flushAllEditsRef.current())
    return () => onFlushReady(async () => true)
  }, [onFlushReady, project.projectId])

  useTranslationWorkspaceKeyboard({
    active,
    mode,
    selectedPageId,
    selectedPage,
    visiblePages,
    selectedSegmentId,
    viewport,
    onShowOriginalChange: setShowOriginalTemporarily,
    onSpacePanChange: setSpacePan,
    onDeleteSelectedSegment: deleteSegment,
    onNudgeSelectedSegment: nudgeSelectedSegment,
    onSelectPage: selectWorkspacePage,
    onReviewPage: updateReviewTag,
    onUndoLayout: undoLayout,
    onUndoImageEdit: undoImageEdit,
    onRedoLayout: redoLayout,
    onRedoImageEdit: redoImageEdit,
    onViewportChange: updateViewport,
    onActualPixels: () => {
      const fit = Math.max(0.05, activeFitScaleRef.current)
      updateViewport({ zoom: Math.min(8, 1 / fit), panX: 0, panY: 0 })
    },
    onAdjustBrushSize: factor => setImageEditSize(current => Math.max(2, Math.min(320, Math.round(current * factor)))),
    onClearSelection: () => setSelectedSegmentId(null),
  })

  useEffect(() => {
    const clearTemporaryOriginal = () => setShowOriginalTemporarily(false)
    window.addEventListener('blur', clearTemporaryOriginal)
    return () => window.removeEventListener('blur', clearTemporaryOriginal)
  }, [])

  useEffect(() => {
    if (!selectedPage) {
      setVersions([])
      return
    }
    const projectId = project.projectId
    const pageId = selectedPage.pageId
    let cancelled = false
    api.listPageVersions(projectId, pageId)
      .then(items => { if (!cancelled) setVersions(items) })
      .catch((reason: unknown) => {
        if (!cancelled) logBackgroundError('page-versions.load', reason, { projectId, pageId })
      })
    return () => { cancelled = true }
  // The page object is intentionally omitted: project updates change its identity after every autosave.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [project.projectId, selectedPage?.pageId, selectedPage?.versionCount, onError])

  useEffect(() => {
    if (!selectedPage) return
    const projectId = project.projectId
    const page = selectedPage
    const pageId = page.pageId
    const letteringVersionId = page.currentLetteringVersionId
    if (layoutSnapshotRef.current?.projectId === projectId
        && layoutSnapshotRef.current.pageId === pageId
        && layoutSnapshotRef.current.letteringVersionId === letteringVersionId
        && layoutRef.current) {
      return
    }
    let cancelled = false
    const replacingAnotherPage = layoutSnapshotRef.current?.pageId !== pageId || !layoutRef.current
    if (replacingAnotherPage) setLayoutLoading(true)
    setSaveState('saved')
    api.getPageLayout(projectId, pageId)
      .then(snapshot => {
        if (cancelled) return
        const next = parseLayout(snapshot.layoutJson, page)
        layoutSnapshotRef.current = { projectId, pageId, letteringVersionId }
        setLayout(next)
        setSelectedSegmentId(current => next.segments.some(segment => segment.segment_id === current) ? current : next.segments[0]?.segment_id ?? null)
      })
      .catch((reason: unknown) => {
        if (!cancelled) logBackgroundError('page-layout.load-editor', reason, { projectId, pageId })
      })
      .finally(() => { if (!cancelled) setLayoutLoading(false) })
    return () => { cancelled = true }
  // The selected page identity is represented by pageId; project autosaves must not recreate the editor.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [project.projectId, selectedPage?.pageId, selectedPage?.currentLetteringVersionId, onError])

  useEffect(() => {
    if (mode !== 'layout') return
    api.getSettings().then(settings => setLayoutFonts(
      settings.fonts.filter(font => project.enabledFontIds.includes(font.fontId)),
    )).catch(() => setLayoutFonts([]))
  }, [mode, project.enabledFontIds])

  useEffect(() => {
    if (!selectedPage || !layout || (mode !== 'preview' && mode !== 'layout')) return
    const liveIds = new Set(layout.segments.map(segment => segment.segment_id))
    setTextClips(current => {
      let changed = false
      const next: Record<string, TextClip> = {}
      for (const [segmentId, clip] of Object.entries(current)) {
        if (liveIds.has(segmentId)) next[segmentId] = clip
        else {
          changed = true
          URL.revokeObjectURL(clip.url)
        }
      }
      return changed ? next : current
    })

    const timer = window.setTimeout(() => {
      const generation = clipGenerationRef.current
      const projectId = project.projectId
      const pageId = selectedPage.pageId
      const layoutJson = serializeLayout(layout)
      for (const segment of layout.segments) {
        const renderKey = segmentRenderKey(segment)
        if (textClipsRef.current[segment.segment_id]?.renderKey === renderKey) continue
        const sequence = (clipRequestSequenceRef.current[segment.segment_id] ?? 0) + 1
        clipRequestSequenceRef.current[segment.segment_id] = sequence
        let operation: Promise<void>
        operation = api.renderTextClip(projectId, pageId, layoutJson, segment.segment_id)
          .then(result => {
            if (clipGenerationRef.current !== generation || clipRequestSequenceRef.current[segment.segment_id] !== sequence) return
            const url = URL.createObjectURL(result.blob)
            setTextClips(current => {
              const previous = current[segment.segment_id]
              if (previous) URL.revokeObjectURL(previous.url)
              return {
                ...current,
                [segment.segment_id]: {
                  url,
                  width: result.width,
                  height: result.height,
                  fingerprint: result.fingerprint,
                  renderKey,
                },
              }
            })
          })
          .catch((reason: unknown) => logBackgroundError('text-clip.render', reason, {
            projectId,
            pageId,
            segmentId: segment.segment_id,
          }))
          .finally(() => pendingClipRendersRef.current.delete(operation))
        pendingClipRendersRef.current.add(operation)
      }
    }, 180)
    return () => window.clearTimeout(timer)
  }, [layout, mode, onError, project.projectId, selectedPage])

  useEffect(() => {
    if (!selectedPage) return
    const projectId = project.projectId
    const pageId = selectedPage.pageId
    const pageRevision = selectedPage.revision
    if (imageEditSnapshotRef.current?.pageId === pageId
        && imageEditSnapshotRef.current.pageRevision === pageRevision
        && imageEditRef.current) {
      return
    }
    let cancelled = false
    const replacingAnotherPage = imageEditSnapshotRef.current?.pageId !== pageId || !imageEditRef.current
    if (replacingAnotherPage) setImageEditLoading(true)
    setImageEditSaveState('saved')
    api.getPageImageEdit(projectId, pageId)
      .then(snapshot => {
        if (cancelled) return
        imageEditSnapshotRef.current = { pageId, pageRevision: snapshot.pageRevision }
        setImageEdit(parseImageEdit(snapshot.operationsJson))
        setImageEditBaseUrl(assetUrl(snapshot.renderedAssetUrl))
      })
      .catch((reason: unknown) => {
        if (!cancelled) logBackgroundError('page-image-edit.load', reason, { projectId, pageId })
      })
      .finally(() => { if (!cancelled) setImageEditLoading(false) })
    return () => { cancelled = true }
  // Background state is prefetched for the selected page so mode switches never need a loading pass.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [project.projectId, selectedPage?.pageId, selectedPage?.revision, onError])

  useEffect(() => {
    if (!jobRefreshSignature) return
    const projectId = project.projectId
    let cancelled = false
    api.getProject(projectId)
      .then(updated => {
        if (cancelled || updated.projectId !== projectId || updated.revision < projectRevisionRef.current) return
        projectRevisionRef.current = updated.revision
        onProjectChange(updated)
      })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [jobRefreshSignature, project.projectId, onProjectChange])

  useEffect(() => {
    const currentJobIds = new Set(jobs.map(item => item.jobId))
    for (const jobId of Object.keys(completedTranslationPagesByJobRef.current)) {
      if (!currentJobIds.has(jobId)) delete completedTranslationPagesByJobRef.current[jobId]
    }
    const newlyCompleted: number[] = []
    for (const translationJob of jobs) {
      const completed = new Set(translationJob.events
        .filter(event => event.status === 'Completed')
        .flatMap(event => event.pageIndices))
      const observed = completedTranslationPagesByJobRef.current[translationJob.jobId]
      if (observed) newlyCompleted.push(...[...completed].filter(pageIndex => !observed.has(pageIndex)))
      completedTranslationPagesByJobRef.current[translationJob.jobId] = completed
    }
    if (!selectedPage || !newlyCompleted.includes(selectedPage.index)) return

    const projectId = project.projectId
    const page = selectedPage

    let disposed = false
    void (async () => {
      try {
        const updatedProject = await api.getProject(projectId)
        if (disposed) return
        projectRevisionRef.current = updatedProject.revision
        onProjectChange(updatedProject)
        const updatedPage = updatedProject.pages.find(item => item.pageId === page.pageId) ?? page
        const [layoutSnapshot, nextVersions] = await Promise.all([
          api.getPageLayout(projectId, page.pageId),
          api.listPageVersions(projectId, page.pageId),
        ])
        if (disposed) return
        for (const clip of Object.values(textClipsRef.current)) URL.revokeObjectURL(clip.url)
        textClipsRef.current = {}
        clipRequestSequenceRef.current = {}
        setTextClips({})
        const nextLayout = parseLayout(layoutSnapshot.layoutJson, updatedPage)
        setLayout(nextLayout)
        setSelectedSegmentId(current => nextLayout.segments.some(segment => segment.segment_id === current) ? current : nextLayout.segments[0]?.segment_id ?? null)
        setVersions(nextVersions)
        if (mode === 'clean') {
          const snapshot = await api.getPageImageEdit(projectId, page.pageId)
          if (disposed) return
          imageEditSnapshotRef.current = { pageId: page.pageId, pageRevision: snapshot.pageRevision }
          setImageEdit(parseImageEdit(snapshot.operationsJson))
          setImageEditBaseUrl(assetUrl(snapshot.renderedAssetUrl))
          setImageEditSaveState('saved')
        }
        setPreviewRefreshNonce(value => value + 1)
      } catch (reason) {
        if (!disposed) logBackgroundError('completed-page.refresh', reason, {
          projectId,
          pageId: page.pageId,
          pageIndex: page.index,
        })
      }
    })()
    return () => { disposed = true }
  }, [jobs, mode, onError, onProjectChange, project.projectId, selectedPage])

  useEffect(() => {
    if (!selectedPage || !generatingPageIndices.has(selectedPage.index) || mode === 'preview') return
    setMode('preview')
    setShowOriginalTemporarily(false)
  }, [generatingPageIndices, mode, selectedPage])

  useEffect(() => {
    if (mode !== 'layout' || !layout || saveState !== 'dirty' || !selectedPage) return
    const timer = window.setTimeout(() => { void persistLayout(selectedPage.pageId, layout) }, 650)
    return () => window.clearTimeout(timer)
  // persistLayout is deliberately kept out so typing does not reset the debounce timer on every render.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [layout, mode, saveState, selectedPage?.pageId])

  useEffect(() => {
    if (mode !== 'clean' || !imageEdit || imageEditSaveState !== 'dirty' || !selectedPage) return
    const timer = window.setTimeout(() => { void persistImageEdit(selectedPage.pageId, imageEdit) }, 50)
    return () => window.clearTimeout(timer)
  // persistImageEdit is deliberately kept out so drawing does not reset the debounce timer on every render.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [imageEdit, imageEditSaveState, mode, selectedPage?.pageId])

  async function persistLayout(pageId: string, document: LayoutDocument) {
    if (savingRef.current) await savingRef.current
    const latestDocument = selectedPage?.pageId === pageId ? layoutRef.current ?? document : document
    const serialized = serializeLayout(latestDocument)
    const operation = (async () => {
      setSaveState('saving')
      onError(null)
      try {
        const previousVersionCount = project.pages.find(page => page.pageId === pageId)?.versionCount
        const updated = await api.updatePageLayout(project.projectId, pageId, serialized, projectRevisionRef.current)
        projectRevisionRef.current = updated.revision
        onProjectChange(updated)
        if (updated.pages.find(page => page.pageId === pageId)?.versionCount !== previousVersionCount) {
          setVersions(await api.listPageVersions(project.projectId, pageId))
        }
        setPreviewRefreshNonce(value => value + 1)
        if (selectedPage?.pageId === pageId) {
          const snapshot = await api.getPageImageEdit(project.projectId, pageId)
          imageEditSnapshotRef.current = { pageId, pageRevision: snapshot.pageRevision }
          setImageEdit(parseImageEdit(snapshot.operationsJson))
          setImageEditBaseUrl(assetUrl(snapshot.renderedAssetUrl))
          setImageEditSaveState('saved')
          setImageEditHistoryByPage(current => ({ ...current, [pageId]: { undo: [], redo: [] } }))
        }
        setSaveState(layoutRef.current && serializeLayout(layoutRef.current) === serialized ? 'saved' : 'dirty')
        return true
      } catch (reason) {
        setSaveState('error')
        onError(readError(reason))
        return false
      }
    })()
    savingRef.current = operation
    try {
      return await operation
    } finally {
      if (savingRef.current === operation) savingRef.current = null
    }
  }

  async function persistImageEdit(pageId: string, document: ImageEditDocument) {
    if (imageEditSavingRef.current) await imageEditSavingRef.current
    const latestDocument = selectedPage?.pageId === pageId ? imageEditRef.current ?? document : document
    const serialized = serializeImageEdit(latestDocument)
    const operation = (async () => {
      setImageEditSaveState('saving')
      onError(null)
      try {
        const previousVersionCount = project.pages.find(page => page.pageId === pageId)?.versionCount
        const pageBeforeSave = project.pages.find(page => page.pageId === pageId)
        const snapshotBeforeSave = imageEditSnapshotRef.current?.pageId === pageId
          ? imageEditSnapshotRef.current
          : {
              pageId,
              pageRevision: pageBeforeSave?.revision ?? 0,
            }
        const result = await api.updatePageImageEdit(
          project.projectId,
          pageId,
          serialized,
          snapshotBeforeSave.pageRevision,
        )
        const updated = result.project
        const snapshot = result.imageEdit
        projectRevisionRef.current = updated.revision
        imageEditSnapshotRef.current = { pageId, pageRevision: snapshot.pageRevision }
        onProjectChange(updated)
        if (updated.pages.find(page => page.pageId === pageId)?.versionCount !== previousVersionCount) {
          setVersions(await api.listPageVersions(project.projectId, pageId))
        }
        if (activePageKeyRef.current === `${project.projectId}:${pageId}`) {
          setImageEditBaseUrl(`${assetUrl(snapshot.renderedAssetUrl)}?pageRevision=${snapshot.pageRevision}`)
          setPreviewRefreshNonce(value => value + 1)
        }
        setImageEditSaveState(imageEditRef.current && serializeImageEdit(imageEditRef.current) === serialized ? 'saved' : 'dirty')
        return true
      } catch (reason) {
        setImageEditSaveState('error')
        onError(readError(reason))
        return false
      }
    })()
    imageEditSavingRef.current = operation
    try {
      return await operation
    } finally {
      if (imageEditSavingRef.current === operation) imageEditSavingRef.current = null
    }
  }

  function applyLayout(next: LayoutDocument, before = layout, record = true) {
    if (record && before && serializeLayout(before) !== serializeLayout(next) && selectedPage) {
      setHistoryByPage(current => {
        const history = current[selectedPage.pageId] ?? { undo: [], redo: [] }
        return { ...current, [selectedPage.pageId]: { undo: [...history.undo, serializeLayout(before)].slice(-80), redo: [] } }
      })
    }
    setLayout(next)
    setSaveState('dirty')
  }

  function updateSegment(segmentId: string, updater: (segment: LayoutSegment) => LayoutSegment, record = true, override?: LayoutManualOverride) {
    if (!layout) return
    const next = { ...layout, segments: layout.segments.map(segment => {
      if (segment.segment_id !== segmentId) return segment
      const updated = updater(segment)
      return override ? markSegmentOverride(updated, override) : updated
    }) }
    applyLayout(next, layout, record)
  }

  function markSegmentOverride(segment: LayoutSegment, override: LayoutManualOverride) {
    const current = (segment.user_overrides as Record<string, unknown> | undefined) ?? {}
    return { ...segment, user_overrides: { ...current, [override]: true } }
  }

  function updateSegmentBox(segmentId: string, box: LayoutBox, record = false, override?: LayoutManualOverride) {
    updateSegment(segmentId, segment => {
      const next = { ...segment, layout: { ...segment.layout, text_box: box } }
      return override ? markSegmentOverride(next, override) : next
    }, record)
  }

  function nudgeSelectedSegment(dx: number, dy: number) {
    if (!selectedPage || !selectedSegmentId) return
    const segment = layoutRef.current?.segments.find(item => item.segment_id === selectedSegmentId)
    if (!segment) return
    updateSegmentBox(selectedSegmentId, clampBox({
      x0: segment.layout.text_box.x0 + dx,
      y0: segment.layout.text_box.y0 + dy,
      x1: segment.layout.text_box.x1 + dx,
      y1: segment.layout.text_box.y1 + dy,
    }, selectedPage), true, 'text_box')
  }

  function commitSegmentBox(segmentId: string, beforeBox: LayoutBox, afterBox: LayoutBox) {
    const current = layoutRef.current
    if (!current) return
    const before = {
      ...current,
      segments: current.segments.map(segment => segment.segment_id === segmentId
        ? { ...segment, layout: { ...segment.layout, text_box: beforeBox } }
        : segment),
    }
    const next = {
      ...current,
      segments: current.segments.map(segment => {
        if (segment.segment_id !== segmentId) return segment
        const advanced = segment.layout.advanced
        const beforeWidth = Math.max(1, beforeBox.x1 - beforeBox.x0)
        const beforeHeight = Math.max(1, beforeBox.y1 - beforeBox.y0)
        const scaleX = (afterBox.x1 - afterBox.x0) / beforeWidth
        const scaleY = (afterBox.y1 - afterBox.y0) / beforeHeight
        const placementGroups = advanced?.placement_groups?.map(group => ({
          ...group,
          text_box: {
            x0: Math.round(afterBox.x0 + (group.text_box.x0 - beforeBox.x0) * scaleX),
            y0: Math.round(afterBox.y0 + (group.text_box.y0 - beforeBox.y0) * scaleY),
            x1: Math.round(afterBox.x0 + (group.text_box.x1 - beforeBox.x0) * scaleX),
            y1: Math.round(afterBox.y0 + (group.text_box.y1 - beforeBox.y0) * scaleY),
          },
        }))
        const nextLayout = {
          ...segment.layout,
          text_box: afterBox,
          ...(advanced && placementGroups ? { advanced: { ...advanced, placement_groups: placementGroups } } : {}),
        }
        return markSegmentOverride({ ...segment, layout: nextLayout }, 'text_box')
      }),
    }
    applyLayout(next, before)
  }

  function commitSegmentRotation(segmentId: string, beforeRotation: LayoutSpec['rotation'], afterAngle: number) {
    const current = layoutRef.current
    if (!current) return
    const before = {
      ...current,
      segments: current.segments.map(segment => segment.segment_id === segmentId
        ? { ...segment, layout: { ...segment.layout, rotation: beforeRotation ? { ...beforeRotation } : null } }
        : segment),
    }
    const next = {
      ...current,
      segments: current.segments.map(segment => {
        if (segment.segment_id !== segmentId) return segment
        const box = segment.layout.text_box
        return markSegmentOverride({
          ...segment,
          layout: {
            ...segment.layout,
            rotation: { center_x_px: (box.x1 - box.x0) / 2, center_y_px: (box.y1 - box.y0) / 2, angle_deg: afterAngle },
          },
        }, 'rotation')
      }),
    }
    applyLayout(next, before)
  }

  function undoLayout() {
    if (!selectedPage || !layout) return
    const history = historyByPage[selectedPage.pageId] ?? { undo: [], redo: [] }
    const previous = history.undo.at(-1)
    if (!previous) return
    setHistoryByPage(current => ({ ...current, [selectedPage.pageId]: { undo: history.undo.slice(0, -1), redo: [...history.redo, serializeLayout(layout)] } }))
    setLayout(JSON.parse(previous) as LayoutDocument)
    setSaveState('dirty')
  }

  function redoLayout() {
    if (!selectedPage || !layout) return
    const history = historyByPage[selectedPage.pageId] ?? { undo: [], redo: [] }
    const next = history.redo.at(-1)
    if (!next) return
    setHistoryByPage(current => ({ ...current, [selectedPage.pageId]: { undo: [...history.undo, serializeLayout(layout)], redo: history.redo.slice(0, -1) } }))
    setLayout(JSON.parse(next) as LayoutDocument)
    setSaveState('dirty')
  }

  function addSegment() {
    if (!layout || !selectedPage) return
    const scale = Math.max(0.01, activeFitScaleRef.current * viewport.zoom)
    const visualCenter = {
      x: selectedPage.width / 2 - viewport.panX / scale,
      y: selectedPage.height / 2 - viewport.panY / scale,
    }
    const created = createSegment(selectedPage, layout.segments.length + 1, '', visualCenter)
    const nextSegment = {
      ...created,
      layout: { ...created.layout, font_id: project.defaultFontId ?? layoutFonts[0]?.fontId ?? 'default' },
      user_overrides: {
        user_created: true,
        translated_text: true,
        text_box: true,
        rotation: true,
        basic_format: true,
        flow: true,
        advanced_layout: true,
      },
    }
    applyLayout({ ...layout, segments: [...layout.segments, nextSegment] }, layout)
    setSelectedSegmentId(nextSegment.segment_id)
  }

  function deleteSegment() {
    if (!layout || !selectedSegmentId) return
    const selected = layout.segments.find(segment => segment.segment_id === selectedSegmentId)
    const deleted = selected?.user_overrides && (selected.user_overrides as Record<string, unknown>).user_created
      ? layout.user_deleted_segment_ids ?? []
      : [...new Set([...(layout.user_deleted_segment_ids ?? []), selectedSegmentId])]
    const next = { ...layout, segments: layout.segments.filter(segment => segment.segment_id !== selectedSegmentId), user_deleted_segment_ids: deleted }
    applyLayout(next, layout)
    setSelectedSegmentId(next.segments[0]?.segment_id ?? null)
  }

  function applyImageEdit(next: ImageEditDocument, before = imageEdit, record = true) {
    if (record && before && serializeImageEdit(before) !== serializeImageEdit(next) && selectedPage) {
      setImageEditHistoryByPage(current => {
        const history = current[selectedPage.pageId] ?? { undo: [], redo: [] }
        return { ...current, [selectedPage.pageId]: { undo: [...history.undo, serializeImageEdit(before)].slice(-80), redo: [] } }
      })
    }
    setImageEdit(next)
    setImageEditSaveState('dirty')
  }

  function appendImageEditOperation(operation: ImageEditOperation) {
    if (!imageEdit) return
    applyImageEdit({ ...imageEdit, operations: [...imageEdit.operations, operation] }, imageEdit)
  }

  function undoImageEdit() {
    if (!selectedPage || !imageEdit) return
    const history = imageEditHistoryByPage[selectedPage.pageId] ?? { undo: [], redo: [] }
    const previous = history.undo.at(-1)
    if (!previous) return
    setImageEditHistoryByPage(current => ({ ...current, [selectedPage.pageId]: { undo: history.undo.slice(0, -1), redo: [...history.redo, serializeImageEdit(imageEdit)] } }))
    setImageEdit(JSON.parse(previous) as ImageEditDocument)
    setImageEditSaveState('dirty')
  }

  function redoImageEdit() {
    if (!selectedPage || !imageEdit) return
    const history = imageEditHistoryByPage[selectedPage.pageId] ?? { undo: [], redo: [] }
    const next = history.redo.at(-1)
    if (!next) return
    setImageEditHistoryByPage(current => ({ ...current, [selectedPage.pageId]: { undo: [...history.undo, serializeImageEdit(imageEdit)], redo: history.redo.slice(0, -1) } }))
    setImageEdit(JSON.parse(next) as ImageEditDocument)
    setImageEditSaveState('dirty')
  }

  function resetImageEdit() {
    if (!imageEdit || imageEdit.operations.length === 0) return
    applyImageEdit(createDefaultImageEdit(), imageEdit)
  }

  async function activateVersion(version: PageVersion) {
    if (!selectedPage || version.isCurrent) return
    const projectId = project.projectId
    const page = selectedPage
    const pageKey = `${projectId}:${page.pageId}`
    let expectedRevision = projectRevisionRef.current
    try {
      if (!await flushActiveEditor()) return
      expectedRevision = projectRevisionRef.current
      const updated = await api.activatePageVersion(projectId, page.pageId, version.versionId, expectedRevision)
      projectRevisionRef.current = updated.revision
      onProjectChange(updated)
      if (activePageKeyRef.current !== pageKey) return
      const nextVersions = await api.listPageVersions(projectId, page.pageId)
      if (activePageKeyRef.current !== pageKey) return
      setVersions(nextVersions)
      if (version.track === 'Lettering') {
        const snapshot = await api.getPageLayout(projectId, page.pageId)
        if (activePageKeyRef.current !== pageKey) return
        const next = parseLayout(snapshot.layoutJson, page)
        setLayout(next)
        setSelectedSegmentId(next.segments[0]?.segment_id ?? null)
        setSaveState('saved')
      }
      if (version.track === 'Lettering' && mode === 'clean') {
        const snapshot = await api.getPageImageEdit(projectId, page.pageId)
        if (activePageKeyRef.current !== pageKey) return
        imageEditSnapshotRef.current = { pageId: page.pageId, pageRevision: snapshot.pageRevision }
        setImageEdit(parseImageEdit(snapshot.operationsJson))
        setImageEditBaseUrl(assetUrl(snapshot.renderedAssetUrl))
        setImageEditSaveState('saved')
        setImageEditHistoryByPage(current => ({ ...current, [page.pageId]: { undo: [], redo: [] } }))
      }
      setPreviewRefreshNonce(value => value + 1)
    } catch (reason) { onError(readError(reason)) }
  }

  async function deleteVersion(version: PageVersion) {
    if (!selectedPage || version.isCurrent || versions.filter(item => item.track === version.track).length <= 1) return
    if (!await dialogService.confirm({
      title: locale === 'en-US' ? 'Delete version' : '删除版本',
      message: locale === 'en-US' ? `Delete “${version.label}”?` : `确定要删除“${version.label}”吗？`,
      detail: locale === 'en-US' ? 'Image assets used by this version will not be removed automatically.' : '该版本使用的图片资源不会自动删除。',
      destructive: true,
      confirmLabel: locale === 'en-US' ? 'Delete version' : '删除版本',
    })) return
    const projectId = project.projectId
    const pageId = selectedPage.pageId
    const pageKey = `${projectId}:${pageId}`
    try {
      if (!await flushActiveEditor()) return
      const updated = await api.deletePageVersion(projectId, pageId, version.versionId, projectRevisionRef.current)
      projectRevisionRef.current = updated.revision
      onProjectChange(updated)
      if (activePageKeyRef.current !== pageKey) return
      const nextVersions = await api.listPageVersions(projectId, pageId)
      if (activePageKeyRef.current === pageKey) setVersions(nextVersions)
    } catch (reason) { onError(readError(reason)) }
  }

  async function createUserVersion() {
    if (!selectedPage) return
    const projectId = project.projectId
    const pageId = selectedPage.pageId
    const pageKey = `${projectId}:${pageId}`
    if (!await flushActiveEditor()) return
    try {
      const trackVersions = versions.filter(version => version.track === 'Lettering')
      const manualVersionNumber = trackVersions.filter(version => version.kind === 'User' && !version.isWorkingCopy).length + 1
      const label = locale === 'en-US' ? `Manual edit ${manualVersionNumber}` : `手动编辑 ${manualVersionNumber}`
      const updated = await api.createPageVersion(projectId, pageId, label, projectRevisionRef.current, 'User', 'Lettering')
      projectRevisionRef.current = updated.revision
      onProjectChange(updated)
      if (activePageKeyRef.current !== pageKey) return
      const nextVersions = await api.listPageVersions(projectId, pageId)
      if (activePageKeyRef.current !== pageKey) return
      setVersions(nextVersions)
      setSaveState('saved')
      setImageEditSaveState('saved')
    } catch (reason) { onError(readError(reason)) }
  }

  async function flushActiveEditor() {
    if (!selectedPage) return true
    if (mode === 'layout' && layoutRef.current && saveState !== 'saved') {
      return persistLayout(selectedPage.pageId, layoutRef.current)
    }
    if (mode === 'clean' && imageEditRef.current && imageEditSaveState !== 'saved') {
      return persistImageEdit(selectedPage.pageId, imageEditRef.current)
    }
    return true
  }

  async function flushAllEdits() {
    if (!await flushActiveEditor()) return false
    return flushAnnotations()
  }

  flushAllEditsRef.current = flushAllEdits

  async function flushPendingClipRenders() {
    if (mode !== 'layout') return
    await new Promise(resolve => window.setTimeout(resolve, 210))
    await Promise.all([...pendingClipRendersRef.current])
  }

  async function switchWorkspaceMode(nextMode: WorkspaceMode) {
    if (nextMode === mode || editTransitionBusy) return
    if (nextMode !== 'preview' && selectedPage && !selectedPage.hasAiResult) {
      onError(locale === 'en-US' ? 'Run AI translation for this page before adjusting lettering or repairing its background.' : '请先完成当前页的 AI 翻译，再调整嵌字或修复底图。')
      return
    }
    if (nextMode !== 'preview' && selectedPage && generatingPageIndices.has(selectedPage.index)) {
      onError(locale === 'en-US' ? 'This page is being generated. Background and text editing will be available when it finishes.' : '该页面正在生成，完成后才能进入底图或文字编辑。')
      return
    }
    setEditTransitionBusy(true)
    try {
      if (!await flushActiveEditor()) return
      if (nextMode === 'preview') {
        await flushPendingClipRenders()
      }
      setMode(nextMode)
      setShowOriginalTemporarily(false)
    } finally {
      setEditTransitionBusy(false)
    }
  }

  async function selectWorkspacePage(pageId: string) {
    if (pageId === selectedPageId || editTransitionBusy) return
    setEditTransitionBusy(true)
    try {
      if (!await flushActiveEditor()) return
      const nextPage = project.pages.find(page => page.pageId === pageId)
      if (nextPage && generatingPageIndices.has(nextPage.index) && mode !== 'preview') {
        setMode('preview')
        setShowOriginalTemporarily(false)
      }
      setSelectedPageId(pageId)
    } finally {
      setEditTransitionBusy(false)
    }
  }

  async function runTranslation() {
    const pendingPages = project.pages.filter(page => page.includedInWorkflow && !page.hasAiResult)
    if (pendingPages.length === 0 || exporting || runningTranslation) return
    const hadLocalPendingEdit = saveState !== 'saved' || imageEditSaveState !== 'saved'
    if (!await flushAllEdits()) return
    const hasPendingEdit = hadLocalPendingEdit || project.pages.some(page => page.hasUnversionedUserChanges)
    if (hasPendingEdit && !await dialogService.confirm({
      title: locale === 'en-US' ? 'Preserve manual edits?' : '保留手动编辑？',
      message: locale === 'en-US' ? 'This project has unversioned edits. Rerunning will preserve them in a protected version before the new AI result becomes current.' : '当前项目有未固化的用户编辑。重新运行前会先将它们保存为保护版本，再切换到新的 AI 结果。',
      confirmLabel: locale === 'en-US' ? 'Rerun AI' : '重新运行 AI',
    })) return
    onStart(pendingPages.map(page => page.index))
  }

  async function rerunPage(page: ProjectPage) {
    if (exporting || generatingPageIndices.has(page.index) || !page.includedInWorkflow || !page.hasAiResult) return
    if (!await flushAllEdits()) return
    onStart([page.index])
  }

  async function rerunRejectedPages() {
    const rejected = project.pages.filter(page => page.reviewTag === 'Rejected' && page.includedInWorkflow && page.hasAiResult)
    if (rejected.length === 0 || exporting) return
    if (!await flushAllEdits()) return
    onStart(rejected.map(page => page.index))
  }

  async function updateReviewTag(page: ProjectPage, reviewTag: PageReviewTag) {
    if (reviewTagBusyPageId || exporting || !page.includedInWorkflow || !page.hasAiResult) return
    setReviewTagBusyPageId(page.pageId)
    onError(null)
    try {
      const updated = await api.updatePageReviewTag(project.projectId, page.pageId, reviewTag, projectRevisionRef.current)
      projectRevisionRef.current = updated.revision
      onProjectChange(updated)
    } catch (reason) {
      onError(readError(reason))
    } finally {
      setReviewTagBusyPageId(null)
    }
  }

  function copySegmentFormat(segment: LayoutSegment) {
    setFormatClipboard(readBasicTextFormat(segment.layout))
  }

  function applyCopiedFormat(segmentId: string) {
    if (!formatClipboard) return
    updateSegment(segmentId, segment => ({
      ...segment,
      layout: {
        ...segment.layout,
        ...formatClipboard,
        stroke: { ...formatClipboard.stroke },
      },
    }), true, 'basic_format')
  }

  const selectedSegment = layout?.segments.find(segment => segment.segment_id === selectedSegmentId) ?? null
  const pendingTranslationPages = project.pages.filter(page => page.includedInWorkflow && !page.hasAiResult)
  const generatedPageCount = project.pages.filter(page => page.includedInWorkflow && page.hasAiResult).length
  const hasStoppedTranslation = jobs.some(item => item.status === 'Stopped')
  const history = selectedPage ? historyByPage[selectedPage.pageId] ?? { undo: [], redo: [] } : { undo: [], redo: [] }
  const imageHistory = selectedPage ? imageEditHistoryByPage[selectedPage.pageId] ?? { undo: [], redo: [] } : { undo: [], redo: [] }

  return (
    <main className={`translation-layout ${exporting ? 'export-locked' : ''}`} hidden={!active} aria-busy={exporting}>
      <TranslationPagesSidebar
        locale={locale}
        pages={project.pages}
        visiblePages={visiblePages}
        selectedPageId={selectedPage?.pageId ?? null}
        generatingPageIndices={generatingPageIndices}
        reviewFilter={reviewFilter}
        exporting={exporting}
        runningTranslation={runningTranslation}
        pendingTranslationPageCount={pendingTranslationPages.length}
        generatedPageCount={generatedPageCount}
        hasStoppedTranslation={hasStoppedTranslation}
        editTransitionBusy={editTransitionBusy}
        reviewTagBusyPageId={reviewTagBusyPageId}
        onReviewFilterChange={setReviewFilter}
        onRunTranslation={() => void runTranslation()}
        onStop={onStop}
        onResume={onResume}
        onRerunRejectedPages={() => void rerunRejectedPages()}
        onSelectPage={pageId => void selectWorkspacePage(pageId)}
        onReviewTagChange={(page, reviewTag) => void updateReviewTag(page, reviewTag)}
        onPageRowRef={(pageId, element) => { if (element) pageRowRefsRef.current.set(pageId, element); else pageRowRefsRef.current.delete(pageId) }}
      />

      <section className="translation-center">
        <TranslationWorkspaceToolbar locale={locale} mode={mode} split={split} leftLayer={leftLayer} rightLayer={rightLayer} viewport={viewport} showOriginalTemporarily={showOriginalTemporarily} canUndoLayout={history.undo.length > 0} canRedoLayout={history.redo.length > 0} hasSelectedSegment={selectedSegment !== null} layoutSaveState={saveState} canUndoImageEdit={imageHistory.undo.length > 0} canRedoImageEdit={imageHistory.redo.length > 0} imageEditSize={imageEditSize} canResetImageEdit={Boolean(imageEdit?.operations.length)} imageEditSaveState={imageEditSaveState} canAddSegment={Boolean(selectedPage && layout && !layoutLoading)} onSplitToggle={() => setSplit(value => !value)} onLeftLayerChange={setLeftLayer} onRightLayerChange={setRightLayer} onViewportChange={updateViewport} onActualPixels={() => updateViewport({ zoom: Math.min(8, 1 / Math.max(0.05, activeFitScaleRef.current)), panX: 0, panY: 0 })} onShowOriginalChange={setShowOriginalTemporarily} onUndoLayout={undoLayout} onRedoLayout={redoLayout} onDeleteSegment={deleteSegment} onUndoImageEdit={undoImageEdit} onRedoImageEdit={redoImageEdit} onImageEditSizeChange={setImageEditSize} onResetImageEdit={resetImageEdit} onAddSegment={addSegment}/>
        {error && isPresentableError(error) && <div className="error-banner translation-error">{error}</div>}
        <div className="translation-editor-shell">
          <TranslationToolRail locale={locale} mode={mode} selectedPage={selectedPage} generating={Boolean(selectedPage && generatingPageIndices.has(selectedPage.index))} editTransitionBusy={editTransitionBusy} onModeChange={nextMode => void switchWorkspaceMode(nextMode)}/>
          <div className={`preview-workspace mode-${mode} ${split && mode === 'preview' ? 'split' : ''}`}>
          {selectedPage ? <>
            <div className={`translation-canvas-layer translation-preview-layer ${mode === 'preview' ? 'active' : ''}`} aria-hidden={mode !== 'preview'}>
              <PreviewPane page={selectedPage} layer={leftLayer} layout={layout} clips={textClips} viewport={viewport} spacePan={spacePan} refreshNonce={previewRefreshNonce} generating={generatingPageIndices.has(selectedPage.index)} onViewportChange={updateViewport} onFitScaleChange={mode === 'preview' ? reportFitScale : ignoreFitScale}/>
              {split && (
                <PreviewPane page={selectedPage} layer={rightLayer} layout={layout} clips={textClips} viewport={viewport} spacePan={spacePan} refreshNonce={previewRefreshNonce} generating={generatingPageIndices.has(selectedPage.index)} onViewportChange={updateViewport} onFitScaleChange={mode === 'preview' ? reportFitScale : ignoreFitScale}/>
              )}
              {split && <div className="split-divider" aria-hidden="true"/>}
            </div>
            <div className={`translation-canvas-layer translation-layout-layer ${mode === 'layout' ? 'active' : ''}`} aria-hidden={mode !== 'layout'}>
              {layoutLoading || !layout
                ? <div className="empty-state">{locale === 'en-US' ? 'Loading this page’s layout…' : '正在加载当前页布局…'}</div>
                : <LayoutCanvas page={selectedPage} layout={layout} clips={textClips} selectedSegmentId={selectedSegmentId} viewport={viewport} spacePan={spacePan} showOriginal={showOriginalTemporarily} onViewportChange={updateViewport} onFitScaleChange={mode === 'layout' ? reportFitScale : ignoreFitScale} onSelect={setSelectedSegmentId} onBoxCommit={commitSegmentBox} onRotationCommit={commitSegmentRotation}/>}
            </div>
            <div className={`translation-canvas-layer translation-clean-layer ${mode === 'clean' ? 'active' : ''}`} aria-hidden={mode !== 'clean'}>
              {imageEditLoading || !imageEdit
                ? <div className="empty-state">{locale === 'en-US' ? 'Loading this page’s background edits…' : '正在加载当前页底图编辑记录…'}</div>
                : <ImageEditCanvas page={selectedPage} baseAssetUrl={imageEditBaseUrl ?? assetUrl(selectedPage.cleanAssetUrl)} size={imageEditSize} brush={imageBrushSettings} viewport={viewport} spacePan={spacePan} onViewportChange={updateViewport} onFitScaleChange={mode === 'clean' ? reportFitScale : ignoreFitScale} onCommit={appendImageEditOperation}/>}
            </div>
          </> : <div className="empty-state"><Image size={25}/><h2>{locale === 'en-US' ? 'This project has no pages' : '项目中还没有页面'}</h2></div>}
          </div>
        </div>
      </section>

      <TranslationInspector
        locale={locale}
        mode={mode}
        selectedPage={selectedPage}
        selectedSegment={selectedSegment}
        layout={layout}
        layoutFonts={layoutFonts}
        hasCopiedFormat={formatClipboard !== null}
        imageEdit={imageEdit}
        annotationText={annotationText}
        annotationSaveState={annotationSaveState}
        exporting={exporting}
        generating={Boolean(selectedPage && generatingPageIndices.has(selectedPage.index))}
        reviewTagBusy={reviewTagBusyPageId === selectedPage?.pageId}
        versions={versions}
        onCopyFormat={() => { if (selectedSegment) copySegmentFormat(selectedSegment) }}
        onApplyFormat={() => { if (selectedSegment) applyCopiedFormat(selectedSegment.segment_id) }}
        onUpdateSegment={(updater, record = true, override) => { if (selectedSegment) updateSegment(selectedSegment.segment_id, updater, record, override) }}
        onAnnotationChange={updateAnnotation}
        onAnnotationCommit={() => void flushAnnotations()}
        onReviewTagChange={tag => { if (selectedPage) void updateReviewTag(selectedPage, tag) }}
        onRerunPage={() => { if (selectedPage) void rerunPage(selectedPage) }}
        onActivateVersion={version => void activateVersion(version)}
        onDeleteVersion={version => void deleteVersion(version)}
        onCreateVersion={() => void createUserVersion()}
      />
    </main>
  )
}
