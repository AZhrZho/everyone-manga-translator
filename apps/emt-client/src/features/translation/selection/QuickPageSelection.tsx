import { useCallback, useEffect, useRef, useState } from 'react'
import { BookOpen, CircleOff, Image } from 'lucide-react'
import { api } from '../../../api'
import type { JobSnapshot, ProjectDetail, ProjectPage } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { assetUrl } from '../../../shared/lib/assets'
import { isPresentableError, readError } from '../../../shared/lib/errors'
import type { ViewportState } from '../../../shared/types/workspace'
import { useViewportSurface } from '../../../shared/viewport/useViewportSurface'

export function QuickPageSelection({ active, project, job, error, onProjectChange, onError, onContinue }: {
  active: boolean
  project: ProjectDetail
  job: JobSnapshot | null
  error: string | null
  onProjectChange: (project: ProjectDetail) => void
  onError: (error: string | null) => void
  onContinue: () => void
}) {
  const locale = readLocale()
  const [selectedPageId, setSelectedPageId] = useState(project.pages[0]?.pageId ?? null)
  const [savingPageId, setSavingPageId] = useState<string | null>(null)
  const gridRef = useRef<HTMLDivElement | null>(null)
  const selectedPage = project.pages.find(page => page.pageId === selectedPageId) ?? project.pages[0]
  const includedCount = project.pages.filter(page => page.includedInWorkflow).length
  const selectionLocked = job?.status === 'Pending' || job?.status === 'Running'

  const togglePage = useCallback(async (page: ProjectPage, includedInWorkflow: boolean) => {
    setSavingPageId(page.pageId)
    onError(null)
    try {
      onProjectChange(await api.updatePageReview(
        project.projectId,
        page.pageId,
        page.pageType,
        includedInWorkflow,
        project.revision,
      ))
    } catch (reason) {
      onError(readError(reason))
      try {
        onProjectChange(await api.getProject(project.projectId))
      } catch {
        // Preserve the original save error when refreshing also fails.
      }
    } finally {
      setSavingPageId(null)
    }
  }, [onError, onProjectChange, project.projectId, project.revision])

  useEffect(() => {
    if (selectedPageId && project.pages.some(page => page.pageId === selectedPageId)) return
    setSelectedPageId(project.pages[0]?.pageId ?? null)
  }, [project.pages, selectedPageId])

  useEffect(() => {
    if (!active) return
    const isInteractiveTarget = (target: EventTarget | null) => {
      const element = target as HTMLElement | null
      return Boolean(element?.closest('input, textarea, select, button, a, [contenteditable="true"], [role="dialog"]'))
    }
    const selectOffsetPage = (offset: number) => {
      if (!selectedPage) return
      const currentIndex = project.pages.findIndex(page => page.pageId === selectedPage.pageId)
      const next = project.pages[Math.max(0, Math.min(project.pages.length - 1, currentIndex + offset))]
      if (!next || next.pageId === selectedPage.pageId) return
      setSelectedPageId(next.pageId)
      window.requestAnimationFrame(() => {
        gridRef.current?.querySelector<HTMLElement>(`[data-page-id="${CSS.escape(next.pageId)}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
      })
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (isInteractiveTarget(event.target)) return
      if (event.code === 'PageDown' || event.code === 'ArrowRight') {
        event.preventDefault()
        selectOffsetPage(1)
      } else if (event.code === 'PageUp' || event.code === 'ArrowLeft') {
        event.preventDefault()
        selectOffsetPage(-1)
      } else if (event.code === 'Space' && !event.repeat && selectedPage && !selectionLocked && savingPageId === null) {
        event.preventDefault()
        void togglePage(selectedPage, !selectedPage.includedInWorkflow)
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [active, project.pages, savingPageId, selectedPage, selectionLocked, togglePage])

  return (
    <main className="quick-page-selection" hidden={!active}>
      <div className="quick-selection-header">
        <div>
          <h1>{locale === 'en-US' ? 'Select pages to translate' : '选择要翻译的页面'}</h1>
          <p>{selectionLocked ? (locale === 'en-US' ? 'The current translation task has locked page selection. Stop or finish it before changing the next run.' : '当前翻译任务已锁定页面选择；停止或完成任务后才能修改下一次运行范围。') : (locale === 'en-US' ? 'Click a thumbnail to preview it. Only the checkbox changes whether a page enters automatic translation.' : '点击缩略图只切换右侧预览；只有点击复选框才会改变页面是否进入自动翻译。')}</p>
        </div>
        <div className="quick-selection-shortcuts"><span><kbd>PageUp</kbd><kbd>PageDown</kbd><kbd>←</kbd><kbd>→</kbd>{locale === 'en-US' ? 'Switch page' : '切换页面'}</span><span><kbd>Space</kbd>{locale === 'en-US' ? 'Toggle selection' : '切换选择'}</span></div>
        <div className="quick-selection-actions">
          <span>{locale === 'en-US' ? `${includedCount} of ${project.pages.length} selected` : `已选择 ${includedCount} / ${project.pages.length} 页`}</span>
          <button className="primary" onClick={onContinue} disabled={includedCount === 0 || savingPageId !== null}><BookOpen size={14}/>{selectionLocked ? (locale === 'en-US' ? 'Back to running task' : '返回运行中的任务') : (locale === 'en-US' ? 'Continue to translation' : '进入翻译工作区')}</button>
        </div>
      </div>
      {error && isPresentableError(error) && <div className="error-banner quick-selection-error">{error}</div>}
      {project.pages.length === 0
        ? <div className="empty-state"><Image size={25}/><h2>{locale === 'en-US' ? 'This project has no pages' : '项目中还没有页面'}</h2></div>
        : <div className="quick-selection-workspace">
            <div className="quick-selection-grid" ref={gridRef} aria-label={locale === 'en-US' ? 'Project pages' : '项目页面'}>
              {project.pages.map(page => (
                <article data-page-id={page.pageId} className={`quick-page-tile ${page.pageId === selectedPage?.pageId ? 'active' : ''} ${page.includedInWorkflow ? 'included' : 'excluded'}`} key={page.pageId}>
                  <button className="quick-page-preview-button" onClick={() => setSelectedPageId(page.pageId)} aria-label={locale === 'en-US' ? `Preview page ${page.index}` : `预览第 ${page.index} 页`}>
                    <span className="quick-page-thumbnail">
                      <img src={assetUrl(page.originalAssetUrl)} alt="" loading="lazy"/>
                      {!page.includedInWorkflow && <span className="quick-page-excluded-overlay" aria-hidden="true"><CircleOff size={20}/><b>{locale === 'en-US' ? 'Excluded' : '已排除'}</b></span>}
                    </span>
                    <span><b>{page.index.toString().padStart(3, '0')}</b><small>{page.fileName}</small></span>
                  </button>
                  <label className="quick-page-checkbox" title={locale === 'en-US' ? 'Include in automatic translation' : '加入自动翻译'}>
                    <input type="checkbox" checked={page.includedInWorkflow} disabled={selectionLocked || savingPageId !== null} onChange={event => void togglePage(page, event.target.checked)}/>
                    <span>{page.includedInWorkflow ? (locale === 'en-US' ? 'Selected' : '已选择') : (locale === 'en-US' ? 'Excluded' : '已排除')}</span>
                  </label>
                </article>
              ))}
            </div>
            <section className="quick-selection-preview">
              {selectedPage && <>
                <div className="quick-selection-preview-head"><span>{locale === 'en-US' ? 'Preview' : '页面预览'}</span><label className="quick-preview-selection-toggle"><input type="checkbox" checked={selectedPage.includedInWorkflow} disabled={selectionLocked || savingPageId !== null} onChange={event => void togglePage(selectedPage, event.target.checked)}/><span>{selectedPage.includedInWorkflow ? (locale === 'en-US' ? 'Selected for translation' : '已选择进入翻译') : (locale === 'en-US' ? 'Excluded from translation' : '已排除')}</span></label><b>{locale === 'en-US' ? `Page ${selectedPage.index}` : `第 ${selectedPage.index} 页`}</b></div>
                <QuickSelectionPreview page={selectedPage}/>
                <div className="quick-selection-preview-meta"><span>{selectedPage.fileName}</span><span>{selectedPage.width} × {selectedPage.height}</span><strong className={selectedPage.includedInWorkflow ? 'included' : 'excluded'}>{selectedPage.includedInWorkflow ? (locale === 'en-US' ? 'Will be translated' : '将进入自动翻译') : (locale === 'en-US' ? 'Excluded from translation' : '不会进入自动翻译')}</strong></div>
              </>}
            </section>
          </div>}
    </main>
  )
}

export function QuickSelectionPreview({ page }: { page: ProjectPage }) {
  const locale = readLocale()
  const [viewport, setViewport] = useState<ViewportState>({ zoom: 1, panX: 0, panY: 0 })
  const fitScaleRef = useRef(1)
  const surface = useViewportSurface(page, viewport, setViewport, fitScale => { fitScaleRef.current = fitScale }, true)
  useEffect(() => setViewport({ zoom: 1, panX: 0, panY: 0 }), [page.pageId])
  return <div className="quick-selection-preview-viewport">
    <div className="quick-preview-viewport-controls">
      <button title={locale === 'en-US' ? 'Zoom out' : '缩小'} onClick={() => setViewport(current => ({ ...current, zoom: Math.max(.2, current.zoom / 1.15) }))}>−</button>
      <span>{Math.round(viewport.zoom * 100)}%</span>
      <button title={locale === 'en-US' ? 'Zoom in' : '放大'} onClick={() => setViewport(current => ({ ...current, zoom: Math.min(8, current.zoom * 1.15) }))}>＋</button>
      <button title={locale === 'en-US' ? 'Fit window' : '适合窗口'} onClick={() => setViewport({ zoom: 1, panX: 0, panY: 0 })}>{locale === 'en-US' ? 'Fit' : '适合'}</button>
      <button title={locale === 'en-US' ? 'Actual pixels' : '实际像素'} onClick={() => setViewport({ zoom: Math.min(8, 1 / Math.max(.05, fitScaleRef.current)), panX: 0, panY: 0 })}>1:1</button>
    </div>
    <div className="quick-selection-preview-canvas pannable" ref={surface.hostRef} onWheel={surface.onWheel} onPointerDown={surface.onPointerDown} title={locale === 'en-US' ? 'Drag to pan · Scroll to zoom' : '拖动平移 · 滚轮缩放'}><div className={`quick-selection-preview-stage viewport-stage ${page.includedInWorkflow ? '' : 'excluded'}`} style={surface.stageStyle}><img src={assetUrl(page.originalAssetUrl)} alt={page.fileName}/>{!page.includedInWorkflow && <div className="quick-preview-excluded-overlay" aria-hidden="true"><CircleOff size={38}/><b>{locale === 'en-US' ? 'Excluded' : '已排除'}</b><span>{locale === 'en-US' ? 'Will not enter automatic translation' : '不会进入自动翻译'}</span></div>}</div></div>
  </div>
}
