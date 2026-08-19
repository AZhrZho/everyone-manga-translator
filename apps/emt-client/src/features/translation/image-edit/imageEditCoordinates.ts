import type { ProjectPage } from '../../../contracts'
import type { ImageEditPoint } from '../../editor/model/imageEdit'

export function pagePointFromClient(
  canvas: HTMLCanvasElement,
  clientX: number,
  clientY: number,
  page: Pick<ProjectPage, 'width' | 'height'>,
): ImageEditPoint {
  const rect = canvas.getBoundingClientRect()
  const contentWidth = canvasContentWidth(canvas, rect)
  const contentHeight = canvasContentHeight(canvas, rect)
  const contentLeft = rect.left + canvas.clientLeft
  const contentTop = rect.top + canvas.clientTop
  return {
    x: Math.max(0, Math.min(page.width, (clientX - contentLeft) * page.width / Math.max(1, contentWidth))),
    y: Math.max(0, Math.min(page.height, (clientY - contentTop) * page.height / Math.max(1, contentHeight))),
  }
}

export function canvasPageScale(canvas: HTMLCanvasElement, pageWidth: number) {
  return canvasContentWidth(canvas, canvas.getBoundingClientRect()) / Math.max(1, pageWidth)
}

function canvasContentWidth(canvas: HTMLCanvasElement, rect: DOMRect) {
  return Math.max(1, rect.width - canvas.clientLeft - Math.max(0, canvas.offsetWidth - canvas.clientWidth - canvas.clientLeft))
}

function canvasContentHeight(canvas: HTMLCanvasElement, rect: DOMRect) {
  return Math.max(1, rect.height - canvas.clientTop - Math.max(0, canvas.offsetHeight - canvas.clientHeight - canvas.clientTop))
}
