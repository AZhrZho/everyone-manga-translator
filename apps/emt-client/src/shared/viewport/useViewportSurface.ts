import { useEffect, useRef, useState } from 'react'
import type { PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react'
import type { ProjectPage } from '../../contracts'
import type { ViewportState } from '../types/workspace'

export function useViewportSurface(
  page: ProjectPage,
  viewport: ViewportState,
  onViewportChange: (next: ViewportState) => void,
  onFitScaleChange: (fitScale: number) => void,
  panEnabled: boolean,
) {
  const hostRef = useRef<HTMLDivElement | null>(null)
  const [fitScale, setFitScale] = useState(1)
  const panRef = useRef<{ startX: number; startY: number; panX: number; panY: number } | null>(null)

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    const update = () => {
      const rect = host.getBoundingClientRect()
      const next = Math.max(0.05, Math.min((rect.width - 32) / page.width, (rect.height - 52) / page.height, 1))
      setFitScale(next)
      onFitScaleChange(next)
    }
    update()
    const observer = new ResizeObserver(update)
    observer.observe(host)
    return () => observer.disconnect()
  }, [onFitScaleChange, page.height, page.width])

  function onWheel(event: ReactWheelEvent<HTMLDivElement>) {
    event.preventDefault()
    const factor = event.deltaY < 0 ? 1.12 : 1 / 1.12
    const nextZoom = Math.max(0.2, Math.min(8, viewport.zoom * factor))
    const rect = event.currentTarget.getBoundingClientRect()
    const currentScale = fitScale * viewport.zoom
    const nextScale = fitScale * nextZoom
    const centerX = rect.width / 2
    const centerY = rect.height / 2
    const worldX = (event.clientX - rect.left - centerX - viewport.panX) / Math.max(0.01, currentScale)
    const worldY = (event.clientY - rect.top - centerY - viewport.panY) / Math.max(0.01, currentScale)
    onViewportChange({
      zoom: nextZoom,
      panX: event.clientX - rect.left - centerX - worldX * nextScale,
      panY: event.clientY - rect.top - centerY - worldY * nextScale,
    })
  }

  function beginPan(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 1 && !(event.button === 0 && panEnabled)) return
    event.preventDefault()
    panRef.current = { startX: event.clientX, startY: event.clientY, panX: viewport.panX, panY: viewport.panY }
    const move = (moveEvent: PointerEvent) => {
      const start = panRef.current
      if (!start) return
      onViewportChange({ zoom: viewport.zoom, panX: start.panX + moveEvent.clientX - start.startX, panY: start.panY + moveEvent.clientY - start.startY })
    }
    const end = () => {
      panRef.current = null
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', end)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', end)
  }

  const scale = fitScale * viewport.zoom
  return {
    hostRef,
    scale,
    fitScale,
    stageStyle: {
      width: Math.max(1, page.width * scale),
      height: Math.max(1, page.height * scale),
      transform: `translate(${viewport.panX}px, ${viewport.panY}px)`,
    },
    onWheel,
    onPointerDown: beginPan,
  }
}
