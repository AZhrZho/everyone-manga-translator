import { useCallback, useRef } from 'react'
import type { PointerEvent as ReactPointerEvent } from 'react'
import type { ProjectPage } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { clampBox } from '../../editor/model/layout'
import type { LayoutBox, LayoutDocument, LayoutSegment, LayoutSpec, TextClip } from '../../editor/model/layout'
import { assetUrl } from '../../../shared/lib/assets'
import type { ViewportState } from '../../../shared/types/workspace'
import { useViewportSurface } from '../../../shared/viewport/useViewportSurface'
import { useBufferedImageSource } from '../../../shared/viewport/useBufferedImageSource'

export function LayoutCanvas({ page, layout, clips, selectedSegmentId, viewport, spacePan, showOriginal, onViewportChange, onFitScaleChange, onSelect, onBoxCommit, onRotationCommit }: {
  page: ProjectPage
  layout: LayoutDocument
  clips: Record<string, TextClip>
  selectedSegmentId: string | null
  viewport: ViewportState
  spacePan: boolean
  showOriginal: boolean
  onViewportChange: (next: ViewportState) => void
  onFitScaleChange: (fitScale: number) => void
  onSelect: (segmentId: string) => void
  onBoxCommit: (segmentId: string, beforeBox: LayoutBox, afterBox: LayoutBox) => void
  onRotationCommit: (segmentId: string, beforeRotation: LayoutSpec['rotation'], afterAngle: number) => void
}) {
  type ResizeHandle = 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w' | 'nw'
  const surface = useViewportSurface(page, viewport, onViewportChange, onFitScaleChange, spacePan)
  const requestedBackgroundUrl = `${assetUrl(showOriginal ? page.originalAssetUrl : page.cleanAssetUrl)}?revision=${page.revision}`
  const ignoreImageError = useCallback(() => undefined, [])
  const displayedBackgroundUrl = useBufferedImageSource(page.pageId, requestedBackgroundUrl, ignoreImageError)
  const dragRef = useRef<{ id: string; mode: 'move' | 'resize' | 'rotate'; handle: ResizeHandle | null; startX: number; startY: number; box: LayoutBox; currentBox: LayoutBox; startRotation: LayoutSpec['rotation']; startAngle: number; currentAngle: number; centerX: number; centerY: number; element: HTMLDivElement; animationFrame: number | null } | null>(null)

  function paintDragPreview(drag: NonNullable<typeof dragRef.current>) {
    const box = drag.currentBox
    drag.element.style.left = `${box.x0 * surface.scale}px`
    drag.element.style.top = `${box.y0 * surface.scale}px`
    drag.element.style.width = `${(box.x1 - box.x0) * surface.scale}px`
    drag.element.style.height = `${(box.y1 - box.y0) * surface.scale}px`
    drag.element.style.transform = `rotate(${drag.currentAngle}deg)`
    const badge = drag.element.querySelector<HTMLElement>('.layout-angle-badge')
    if (badge) badge.textContent = `${Math.round(drag.currentAngle * 10) / 10}°`
    drag.animationFrame = null
  }

  function scheduleDragPreview(drag: NonNullable<typeof dragRef.current>) {
    if (drag.animationFrame !== null) return
    drag.animationFrame = window.requestAnimationFrame(() => {
      if (dragRef.current === drag) paintDragPreview(drag)
    })
  }

  function resizeBox(box: LayoutBox, handle: ResizeHandle, dx: number, dy: number): LayoutBox {
    const minimumWidth = 32
    const minimumHeight = 28
    let { x0, y0, x1, y1 } = box
    if (handle.includes('w')) x0 = Math.min(x1 - minimumWidth, Math.max(0, box.x0 + dx))
    if (handle.includes('e')) x1 = Math.max(x0 + minimumWidth, Math.min(page.width, box.x1 + dx))
    if (handle.includes('n')) y0 = Math.min(y1 - minimumHeight, Math.max(0, box.y0 + dy))
    if (handle.includes('s')) y1 = Math.max(y0 + minimumHeight, Math.min(page.height, box.y1 + dy))
    return { x0: Math.round(x0), y0: Math.round(y0), x1: Math.round(x1), y1: Math.round(y1) }
  }

  function beginDrag(event: ReactPointerEvent, segment: LayoutSegment, mode: 'move' | 'resize' | 'rotate', handle: ResizeHandle | null = null) {
    if (event.button !== 0 || spacePan) return
    event.preventDefault()
    event.stopPropagation()
    onSelect(segment.segment_id)
    const box = segment.layout.text_box
    const element = (event.currentTarget as HTMLElement).closest<HTMLDivElement>('.layout-text-box')
    if (!element) return
    const surfaceRect = (event.currentTarget as HTMLElement).closest('.layout-surface')?.getBoundingClientRect()
    const centerX = (surfaceRect?.left ?? 0) + (box.x0 + box.x1) * surface.scale / 2
    const centerY = (surfaceRect?.top ?? 0) + (box.y0 + box.y1) * surface.scale / 2
    const startAngle = segment.layout.rotation?.angle_deg ?? 0
    dragRef.current = { id: segment.segment_id, mode, handle, startX: event.clientX, startY: event.clientY, box: { ...box }, currentBox: { ...box }, startRotation: segment.layout.rotation ? { ...segment.layout.rotation } : null, startAngle, currentAngle: startAngle, centerX, centerY, element, animationFrame: null }
    const move = (moveEvent: PointerEvent) => {
      const drag = dragRef.current
      if (!drag) return
      const dx = (moveEvent.clientX - drag.startX) / surface.scale
      const dy = (moveEvent.clientY - drag.startY) / surface.scale
      if (drag.mode === 'rotate') {
        const startRadians = Math.atan2(drag.startY - drag.centerY, drag.startX - drag.centerX)
        const currentRadians = Math.atan2(moveEvent.clientY - drag.centerY, moveEvent.clientX - drag.centerX)
        const angle = drag.startAngle + (currentRadians - startRadians) * 180 / Math.PI
        drag.currentAngle = Math.round(angle * 10) / 10
        scheduleDragPreview(drag)
        return
      }
      drag.currentBox = drag.mode === 'move'
        ? clampBox({ x0: drag.box.x0 + dx, y0: drag.box.y0 + dy, x1: drag.box.x1 + dx, y1: drag.box.y1 + dy }, page)
        : resizeBox(drag.box, drag.handle ?? 'se', dx, dy)
      scheduleDragPreview(drag)
    }
    const end = () => {
      const completed = dragRef.current
      dragRef.current = null
      if (completed) {
        if (completed.animationFrame !== null) window.cancelAnimationFrame(completed.animationFrame)
        paintDragPreview(completed)
        if (completed.mode === 'rotate') {
          if (completed.currentAngle !== completed.startAngle) onRotationCommit(completed.id, completed.startRotation, completed.currentAngle)
        } else if (JSON.stringify(completed.currentBox) !== JSON.stringify(completed.box)) {
          onBoxCommit(completed.id, completed.box, completed.currentBox)
        }
      }
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', end)
      window.removeEventListener('pointercancel', end)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', end)
    window.addEventListener('pointercancel', end)
  }

  return (
    <div className={`layout-canvas-host ${spacePan ? 'space-pan' : ''}`} ref={surface.hostRef} onWheel={surface.onWheel} onPointerDown={surface.onPointerDown}>
      <div className="layout-surface viewport-stage" style={surface.stageStyle}>
        <img src={displayedBackgroundUrl} alt={page.fileName}/>
        {!showOriginal && layout.segments.map(segment => {
          const box = segment.layout.text_box
          const active = segment.segment_id === selectedSegmentId
          return <div key={segment.segment_id} className={`layout-text-box ${active ? 'active' : ''}`} style={{ left: box.x0 * surface.scale, top: box.y0 * surface.scale, width: (box.x1 - box.x0) * surface.scale, height: (box.y1 - box.y0) * surface.scale, transform: `rotate(${segment.layout.rotation?.angle_deg ?? 0}deg)` }} onPointerDown={event => beginDrag(event, segment, 'move')} onClick={() => onSelect(segment.segment_id)}>
            {clips[segment.segment_id]
              ? <img className="layout-text-clip" src={clips[segment.segment_id].url} alt="" draggable={false}/>
              : <span className="layout-clip-loading">{readLocale() === 'en-US' ? 'Rendering…' : '正在渲染…'}</span>}
            {active && <><button className="layout-rotate-handle" aria-label={readLocale() === 'en-US' ? 'Rotate text box' : '旋转文本框'} onPointerDown={event => beginDrag(event, segment, 'rotate')}/>{(['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw'] as ResizeHandle[]).map(handle => <button key={handle} className={`layout-resize-handle handle-${handle}`} aria-label={readLocale() === 'en-US' ? `Resize text box from ${handle}` : `从 ${handle} 方向调整文本框`} onPointerDown={event => beginDrag(event, segment, 'resize', handle)}/>)}<em className="layout-angle-badge">{Math.round((segment.layout.rotation?.angle_deg ?? 0) * 10) / 10}°</em></>}
          </div>
        })}
      </div>
    </div>
  )
}
