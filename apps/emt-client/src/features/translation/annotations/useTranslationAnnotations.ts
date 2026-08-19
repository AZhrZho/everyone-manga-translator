import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import { api } from '../../../api'
import { logBackgroundError, readError } from '../../../shared/lib/errors'

type TranslationAnnotationSaveState = 'saved' | 'saving' | 'error'

type UseTranslationAnnotationsOptions = {
  projectId: string
  selectedPageId: string | null
  fallbackPageId: string | null
  projectRevisionRef: RefObject<number>
  onError: (error: string | null) => void
}

export function useTranslationAnnotations({
  projectId,
  selectedPageId,
  fallbackPageId,
  projectRevisionRef,
  onError,
}: UseTranslationAnnotationsOptions) {
  const [annotationText, setAnnotationText] = useState('')
  const [annotationSaveState, setAnnotationSaveState] = useState<TranslationAnnotationSaveState>('saved')
  const annotationsRef = useRef<Record<string, string>>({})
  const annotationDraftsRef = useRef<Record<string, string>>({})
  const annotationTimersRef = useRef<Record<string, number>>({})
  const annotationsLoadedRef = useRef(false)

  useEffect(() => {
    annotationsLoadedRef.current = false
    let cancelled = false
    api.getTranslationAnnotations(projectId)
      .then(response => {
        if (cancelled) return
        annotationsRef.current = response.pages
        annotationDraftsRef.current = { ...response.pages }
        setAnnotationText(response.pages[selectedPageId ?? fallbackPageId ?? ''] ?? '')
        annotationsLoadedRef.current = true
        setAnnotationSaveState('saved')
      })
      .catch((reason: unknown) => {
        if (cancelled) return
        annotationsRef.current = {}
        setAnnotationSaveState('error')
        logBackgroundError('translation-annotations.load', reason, { projectId })
      })
    return () => { cancelled = true }
  // Annotation state is project-scoped; page switching only changes the selected draft below.
  // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, onError])

  useEffect(() => {
    if (!annotationsLoadedRef.current || !selectedPageId) return
    setAnnotationText(annotationDraftsRef.current[selectedPageId] ?? '')
  }, [selectedPageId])

  useEffect(() => {
    return () => {
      Object.values(annotationTimersRef.current).forEach(timer => window.clearTimeout(timer))
      annotationTimersRef.current = {}
    }
  }, [projectId])

  const persistAnnotation = useCallback(async (pageId: string, value: string) => {
    setAnnotationSaveState('saving')
    const mergeDrafts = (pages: Record<string, string>) => {
      const merged = { ...pages, ...annotationDraftsRef.current }
      if (!value.trim()) delete merged[pageId]
      else merged[pageId] = value
      return merged
    }

    try {
      let next = mergeDrafts(annotationsRef.current)
      let response
      try {
        response = await api.updateTranslationAnnotations(projectId, next, projectRevisionRef.current)
      } catch {
        const latest = await api.getTranslationAnnotations(projectId)
        projectRevisionRef.current = latest.projectRevision
        next = mergeDrafts(latest.pages)
        response = await api.updateTranslationAnnotations(projectId, next, latest.projectRevision)
      }
      projectRevisionRef.current = response.projectRevision
      annotationsRef.current = response.pages
      annotationDraftsRef.current = { ...annotationDraftsRef.current, ...response.pages }
      if (!value.trim()) delete annotationDraftsRef.current[pageId]
      setAnnotationSaveState('saved')
      return true
    } catch (reason) {
      setAnnotationSaveState('error')
      onError(readError(reason))
      return false
    }
  }, [onError, projectId, projectRevisionRef])

  const updateAnnotation = useCallback((value: string) => {
    if (!selectedPageId) return
    const pageId = selectedPageId
    setAnnotationText(value)
    annotationDraftsRef.current = { ...annotationDraftsRef.current, [pageId]: value }
    setAnnotationSaveState('saving')
    const previousTimer = annotationTimersRef.current[pageId]
    if (previousTimer) window.clearTimeout(previousTimer)
    annotationTimersRef.current[pageId] = window.setTimeout(() => {
      delete annotationTimersRef.current[pageId]
      void persistAnnotation(pageId, value)
    }, 650)
  }, [persistAnnotation, selectedPageId])

  const flushAnnotations = useCallback(async () => {
    const pendingPages = Object.keys(annotationTimersRef.current)
    if (pendingPages.length === 0) return true
    Object.values(annotationTimersRef.current).forEach(timer => window.clearTimeout(timer))
    annotationTimersRef.current = {}
    const pageId = selectedPageId ?? pendingPages[0]
    return persistAnnotation(pageId, annotationDraftsRef.current[pageId] ?? '')
  }, [persistAnnotation, selectedPageId])

  return {
    annotationText,
    annotationSaveState,
    updateAnnotation,
    flushAnnotations,
  }
}
