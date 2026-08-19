import type { BrowserInferenceRegion } from './contracts'

export async function createMaskOverlayUrl(
  width: number,
  height: number,
  mask: Uint8Array,
  regions: BrowserInferenceRegion[],
) {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) throw new Error('Canvas 2D is unavailable.')
  const image = context.createImageData(width, height)
  for (let index = 0; index < mask.length; index++) {
    if (mask[index] === 0) continue
    const pixel = index * 4
    image.data[pixel] = 255
    image.data[pixel + 1] = 50
    image.data[pixel + 2] = 110
    image.data[pixel + 3] = 135
  }
  context.putImageData(image, 0, 0)
  context.strokeStyle = 'rgba(80, 220, 255, .95)'
  context.lineWidth = Math.max(1, Math.round(Math.max(width, height) / 700))
  for (const region of regions) {
    context.beginPath()
    region.polygon.forEach((point, index) => {
      if (index === 0) context.moveTo(point.x, point.y)
      else context.lineTo(point.x, point.y)
    })
    context.closePath()
    context.stroke()
  }
  const blob = await new Promise<Blob>((resolve, reject) =>
    canvas.toBlob(value => value ? resolve(value) : reject(new Error('Unable to encode mask preview.')), 'image/png'))
  return URL.createObjectURL(blob)
}

export async function downloadMaskPng(width: number, height: number, mask: Uint8Array, fileName: string) {
  downloadBlob(await encodeMaskPng(width, height, mask), fileName)
}

export async function encodeMaskPng(width: number, height: number, mask: Uint8Array): Promise<Blob> {
  if (!Number.isInteger(width) || width <= 0 || !Number.isInteger(height) || height <= 0) {
    throw new RangeError('Mask dimensions must be positive integers.')
  }
  if (mask.length !== width * height) {
    throw new RangeError('Mask pixel count does not match its dimensions.')
  }
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) throw new Error('Canvas 2D is unavailable.')
  const image = context.createImageData(width, height)
  for (let index = 0; index < mask.length; index++) {
    const value = mask[index]
    const pixel = index * 4
    image.data[pixel] = value
    image.data[pixel + 1] = value
    image.data[pixel + 2] = value
    image.data[pixel + 3] = 255
  }
  context.putImageData(image, 0, 0)
  return new Promise<Blob>((resolve, reject) =>
    canvas.toBlob(value => value ? resolve(value) : reject(new Error('Unable to encode mask PNG.')), 'image/png'))
}

export function downloadDetectionJson(value: unknown, fileName: string) {
  downloadBlob(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }), fileName)
}

function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 0)
}
