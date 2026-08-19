import { useEffect, useRef } from 'react'
import type { PageReviewTag, ProjectPage } from '../../../contracts'
import type { ViewportState, WorkspaceMode } from '../../../shared/types/workspace'

type TranslationWorkspaceKeyboardOptions = {
  active: boolean
  mode: WorkspaceMode
  selectedPageId: string | null
  selectedPage: ProjectPage | undefined
  visiblePages: ProjectPage[]
  selectedSegmentId: string | null
  viewport: ViewportState
  onShowOriginalChange: (visible: boolean) => void
  onSpacePanChange: (enabled: boolean) => void
  onDeleteSelectedSegment: () => void
  onNudgeSelectedSegment: (dx: number, dy: number) => void
  onSelectPage: (pageId: string) => void | Promise<void>
  onReviewPage: (page: ProjectPage, reviewTag: PageReviewTag) => void | Promise<void>
  onUndoLayout: () => void
  onUndoImageEdit: () => void
  onRedoLayout: () => void
  onRedoImageEdit: () => void
  onViewportChange: (viewport: ViewportState) => void
  onActualPixels: () => void
  onAdjustBrushSize: (factor: number) => void
  onClearSelection: () => void
}

export function useTranslationWorkspaceKeyboard(options: TranslationWorkspaceKeyboardOptions) {
  const optionsRef = useRef(options)
  optionsRef.current = options

  useEffect(() => {
    if (!options.active) return

    const isInteractiveTarget = (target: EventTarget | null) => {
      if (!(target instanceof Element)) return false
      return Boolean(target.closest('input, textarea, select, button, a[href], [contenteditable]:not([contenteditable="false"]), [role="button"], [role="link"], [role="textbox"], [role="dialog"]'))
    }

    const onKeyDown = (event: KeyboardEvent) => {
      const current = optionsRef.current
      if (!current.active || event.defaultPrevented || event.isComposing || document.querySelector('[role="dialog"], .modal-backdrop') || isInteractiveTarget(event.target)) return

      if (event.code === 'Shift' && current.mode === 'layout') {
        current.onShowOriginalChange(true)
        return
      }
      if (event.code === 'Space') {
        event.preventDefault()
        current.onSpacePanChange(true)
        return
      }
      if (current.mode === 'layout' && current.selectedSegmentId && ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Delete'].includes(event.key)) {
        event.preventDefault()
        if (event.key === 'Delete') {
          current.onDeleteSelectedSegment()
          return
        }
        const amount = event.shiftKey ? 10 : 1
        const dx = event.key === 'ArrowLeft' ? -amount : event.key === 'ArrowRight' ? amount : 0
        const dy = event.key === 'ArrowUp' ? -amount : event.key === 'ArrowDown' ? amount : 0
        current.onNudgeSelectedSegment(dx, dy)
        return
      }

      const navigateBackward = event.key === 'PageUp' || event.key === 'ArrowLeft'
      const navigateForward = event.key === 'PageDown' || event.key === 'ArrowRight'
      if (!event.altKey && !event.ctrlKey && !event.metaKey && (navigateBackward || navigateForward)) {
        event.preventDefault()
        const index = current.visiblePages.findIndex(page => page.pageId === current.selectedPageId)
        let nextPage: ProjectPage | undefined
        if (index >= 0) {
          const nextIndex = navigateBackward
            ? Math.max(0, index - 1)
            : Math.min(current.visiblePages.length - 1, index + 1)
          nextPage = current.visiblePages[nextIndex]
        } else if (navigateBackward) {
          nextPage = [...current.visiblePages].reverse().find(page => !current.selectedPage || page.index < current.selectedPage.index)
            ?? current.visiblePages[0]
        } else {
          nextPage = current.visiblePages.find(page => !current.selectedPage || page.index > current.selectedPage.index)
            ?? current.visiblePages.at(-1)
        }
        if (nextPage) void current.onSelectPage(nextPage.pageId)
        return
      }
      if (!event.altKey && !event.ctrlKey && !event.metaKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
        event.preventDefault()
        if (!event.repeat && current.selectedPage?.includedInWorkflow && current.selectedPage.hasAiResult) {
          void current.onReviewPage(current.selectedPage, event.key === 'ArrowUp' ? 'Accepted' : 'Rejected')
        }
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
        event.preventDefault()
        if (current.mode === 'layout') current.onUndoLayout()
        else if (current.mode === 'clean') current.onUndoImageEdit()
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
        event.preventDefault()
        if (current.mode === 'layout') current.onRedoLayout()
        else if (current.mode === 'clean') current.onRedoImageEdit()
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key === '0') {
        event.preventDefault()
        current.onViewportChange({ zoom: 1, panX: 0, panY: 0 })
        return
      }
      if ((event.ctrlKey || event.metaKey) && event.key === '1') {
        event.preventDefault()
        current.onActualPixels()
        return
      }
      if (event.key === '+' || event.key === '=') {
        event.preventDefault()
        current.onViewportChange({ ...current.viewport, zoom: Math.min(8, current.viewport.zoom * 1.15) })
        return
      }
      if (event.key === '-' || event.key === '_') {
        event.preventDefault()
        current.onViewportChange({ ...current.viewport, zoom: Math.max(0.2, current.viewport.zoom / 1.15) })
        return
      }
      if (current.mode === 'clean' && (event.key === '[' || event.key === ']')) {
        event.preventDefault()
        current.onAdjustBrushSize(event.key === ']' ? 1.18 : 1 / 1.18)
        return
      }
      if (event.key === 'Escape') current.onClearSelection()
    }

    const onKeyUp = (event: KeyboardEvent) => {
      const current = optionsRef.current
      if (event.code === 'Space') current.onSpacePanChange(false)
      if (event.code === 'Shift') current.onShowOriginalChange(false)
    }

    window.addEventListener('keydown', onKeyDown)
    window.addEventListener('keyup', onKeyUp)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('keyup', onKeyUp)
    }
  }, [options.active])
}
