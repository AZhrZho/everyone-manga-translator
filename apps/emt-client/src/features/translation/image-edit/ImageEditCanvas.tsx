import { useEffect, useRef } from 'react'
import type { PointerEvent as ReactPointerEvent } from 'react'
import type { ProjectPage } from '../../../contracts'
import { readLocale } from '../../../i18n'
import type { ViewportState } from '../../../shared/types/workspace'
import { useViewportSurface } from '../../../shared/viewport/useViewportSurface'
import type { ImageBrushSettings, ImageEditOperation, ImageEditPoint } from '../../editor/model/imageEdit'
import { canvasPageScale, pagePointFromClient } from './imageEditCoordinates'

export function ImageEditCanvas({ page, baseAssetUrl, size, brush, viewport, spacePan, onViewportChange, onFitScaleChange, onCommit }: {
  page: ProjectPage
  baseAssetUrl: string
  size: number
  brush: ImageBrushSettings
  viewport: ViewportState
  spacePan: boolean
  onViewportChange: (next: ViewportState) => void
  onFitScaleChange: (fitScale: number) => void
  onCommit: (operation: ImageEditOperation) => void
}) {
  const locale = readLocale()
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const strokeCanvasRef = useRef<HTMLCanvasElement | null>(null)
  const surface = useViewportSurface(page, viewport, onViewportChange, onFitScaleChange, spacePan)
  const draftRef = useRef<ImageEditOperation | null>(null)
  const frameRef = useRef<number | null>(null)
  const cursorRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    let cancelled = false
    const image = new window.Image()
    image.crossOrigin = 'anonymous'
    image.onload = () => {
      if (cancelled || !canvasRef.current) return
      const canvas = canvasRef.current
      const context = canvas.getContext('2d')
      if (!context) return
      context.clearRect(0, 0, page.width, page.height)
      context.drawImage(image, 0, 0, page.width, page.height)
    }
    image.src = withRevision(baseAssetUrl, page.revision)
    return () => { cancelled = true }
  }, [baseAssetUrl, page.height, page.pageId, page.revision, page.width])

  useEffect(() => () => { if (frameRef.current !== null) cancelAnimationFrame(frameRef.current) }, [])

  function scheduleStrokePreview() {
    if (frameRef.current !== null) return
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = null
      drawStrokePreview(strokeCanvasRef.current, draftRef.current)
    })
  }

  function pointFromEvent(event: ReactPointerEvent<HTMLCanvasElement>): ImageEditPoint {
    return pagePointFromClient(event.currentTarget, event.clientX, event.clientY, page)
  }

  function beginStroke(event: ReactPointerEvent<HTMLCanvasElement>) {
    if (event.button !== 0 || spacePan) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    draftRef.current = {
      operation_id: `image-repair-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      tool: 'inpaint',
      size_px: size,
      points: [pointFromEvent(event)],
      spacing: brush.spacing,
    }
    clearStrokePreview(strokeCanvasRef.current, page)
    scheduleStrokePreview()
  }

  function extendStroke(event: ReactPointerEvent<HTMLCanvasElement>) {
    const current = draftRef.current
    if (!current || !event.currentTarget.hasPointerCapture(event.pointerId)) return
    const point = pointFromEvent(event)
    const previous = current.points.at(-1)
    if (previous && Math.hypot(point.x - previous.x, point.y - previous.y) < 1) return
    current.points.push(point)
    scheduleStrokePreview()
  }

  function endStroke(event: ReactPointerEvent<HTMLCanvasElement>) {
    const completed = draftRef.current
    if (!completed) return
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
    draftRef.current = null
    if (frameRef.current !== null) cancelAnimationFrame(frameRef.current)
    frameRef.current = null
    clearStrokePreview(strokeCanvasRef.current, page)
    onCommit(completed)
  }

  function updateCursor(event: ReactPointerEvent<HTMLCanvasElement>) {
    const cursor = cursorRef.current
    const host = surface.hostRef.current
    if (!cursor || !host) return
    const hostRect = host.getBoundingClientRect()
    const displayScale = canvasPageScale(event.currentTarget, page.width)
    cursor.style.display = 'block'
    cursor.style.left = `${event.clientX - hostRect.left}px`
    cursor.style.top = `${event.clientY - hostRect.top}px`
    cursor.style.width = `${Math.max(2, size * displayScale)}px`
    cursor.style.height = `${Math.max(2, size * displayScale)}px`
  }

  return <div className={`image-edit-canvas-host ${spacePan ? 'space-pan' : ''}`} ref={surface.hostRef} onWheel={surface.onWheel} onPointerDown={surface.onPointerDown}>
    <div className="image-edit-stage viewport-stage" style={surface.stageStyle}>
      <canvas className="image-edit-main-canvas" ref={canvasRef} width={page.width} height={page.height}/>
      <canvas
        className="image-edit-stroke-overlay"
        ref={strokeCanvasRef}
        width={page.width}
        height={page.height}
        aria-label={locale === 'en-US' ? 'Smart background cleanup canvas' : '智能清除底图画布'}
        onPointerDown={beginStroke}
        onPointerMove={event => { updateCursor(event); extendStroke(event) }}
        onPointerUp={endStroke}
        onPointerCancel={endStroke}
        onPointerEnter={updateCursor}
        onPointerLeave={() => { if (cursorRef.current) cursorRef.current.style.display = 'none' }}
      />
    </div>
    <div className="image-edit-brush-cursor" ref={cursorRef}/>
    <div className="image-edit-canvas-badge">{locale === 'en-US' ? 'Smart cleanup' : '智能清除'} · {size}px</div>
  </div>
}

function withRevision(url: string, revision: number) {
  return `${url}${url.includes('?') ? '&' : '?'}revision=${revision}`
}

function clearStrokePreview(canvas: HTMLCanvasElement | null, page: ProjectPage) {
  if (!canvas) return
  canvas.getContext('2d')?.clearRect(0, 0, page.width, page.height)
  canvas.dataset.renderedPointCount = '0'
}

function drawStrokePreview(canvas: HTMLCanvasElement | null, operation: ImageEditOperation | null) {
  if (!canvas || !operation || operation.points.length === 0) return
  const context = canvas.getContext('2d')
  if (!context) return
  const previousCount = Math.max(0, Math.min(operation.points.length, Number(canvas.dataset.renderedPointCount ?? 0)))
  const points = operation.points.slice(Math.max(0, previousCount - 1))
  context.save()
  context.strokeStyle = '#f2a65a'
  context.fillStyle = '#f2a65a'
  context.globalAlpha = .38
  context.lineWidth = Math.max(1, operation.size_px)
  context.lineCap = 'round'
  context.lineJoin = 'round'
  if (points.length === 1 && previousCount === 0) {
    context.beginPath()
    context.arc(points[0].x, points[0].y, operation.size_px / 2, 0, Math.PI * 2)
    context.fill()
  } else if (points.length > 1) {
    context.beginPath()
    context.moveTo(points[0].x, points[0].y)
    for (const point of points.slice(1)) context.lineTo(point.x, point.y)
    context.stroke()
  }
  context.restore()
  canvas.dataset.renderedPointCount = String(operation.points.length)
}
