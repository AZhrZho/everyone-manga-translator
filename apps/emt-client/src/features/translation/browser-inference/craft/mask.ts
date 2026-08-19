import { visitNeighbors } from './connectedComponents'
import { resolveCraftOptions } from './options'
import type {
  CraftBinaryMask,
  CraftDetectionAnalysis,
  CraftOptions,
  CraftPreparedInput,
  CraftScoreMaps,
} from './types'

export const CRAFT_MASK_BINARY_THRESHOLD = 30

/** Builds a 0/255 page-space mask while ensuring affinity-only pixels stay out. */
export function buildCraftMask(
  maps: CraftScoreMaps,
  analysis: CraftDetectionAnalysis,
  prepared: CraftPreparedInput,
  options: CraftOptions = {},
): CraftBinaryMask {
  validateInputs(maps, analysis, prepared)
  const resolved = resolveCraftOptions(options)
  const heatmapMask = new Uint8Array(maps.width * maps.height)
  const componentMask = new Uint8Array(heatmapMask.length)

  const visited = new Int32Array(heatmapMask.length)
  visited.fill(-1)
  const queue = new Uint32Array(heatmapMask.length)
  for (let detectionIndex = 0; detectionIndex < analysis.detections.length; detectionIndex += 1) {
    const detection = analysis.detections[detectionIndex]
    for (const pixel of detection.componentPixels) {
      componentMask[pixel] = 1
    }
    let hasSeed = false
    for (const pixel of detection.componentPixels) {
      if (maps.text[pixel] > resolved.glyphSeedThreshold) {
        hasSeed = true
        break
      }
    }
    if (!hasSeed) {
      for (const pixel of detection.componentPixels) {
        componentMask[pixel] = 0
      }
      continue
    }

    for (const seed of detection.componentPixels) {
      if (visited[seed] === detectionIndex || !(maps.text[seed] > resolved.glyphCandidateThreshold)) {
        continue
      }
      let head = 0
      let tail = 0
      visited[seed] = detectionIndex
      queue[tail] = seed
      tail += 1
      while (head < tail) {
        const index = queue[head]
        head += 1
        const x = index % maps.width
        const y = Math.floor(index / maps.width)
        visitNeighbors(x, y, maps.width, maps.height, 8, (neighbor) => {
          if (componentMask[neighbor] !== 0
              && visited[neighbor] !== detectionIndex
              && maps.text[neighbor] > resolved.glyphCandidateThreshold) {
            visited[neighbor] = detectionIndex
            queue[tail] = neighbor
            tail += 1
          }
        })
      }
      if (tail >= resolved.minimumGlyphArea) {
        for (let index = 0; index < tail; index += 1) {
          heatmapMask[queue[index]] = 255
        }
      }
    }
    for (const pixel of detection.componentPixels) {
      componentMask[pixel] = 0
    }
  }

  const dilated = resolved.glyphMaskDilationRadius > 0
    ? dilateBinaryMaskEllipse(heatmapMask, maps.width, maps.height, resolved.glyphMaskDilationRadius)
    : heatmapMask
  const contentMask = resizeGrayscaleBilinear(
    dilated,
    maps.width,
    maps.height,
    prepared.contentWidth,
    prepared.contentHeight,
  )
  const pageMask = resizeGrayscaleBilinear(
    contentMask,
    prepared.contentWidth,
    prepared.contentHeight,
    prepared.sourceWidth,
    prepared.sourceHeight,
  )
  for (let index = 0; index < pageMask.length; index += 1) {
    pageMask[index] = pageMask[index] > CRAFT_MASK_BINARY_THRESHOLD ? 255 : 0
  }
  return { width: prepared.sourceWidth, height: prepared.sourceHeight, pixels: pageMask }
}

/** OpenCV-style center-sampled bilinear resize with a byte result. */
export function resizeGrayscaleBilinear(
  source: Uint8Array,
  sourceWidth: number,
  sourceHeight: number,
  targetWidth: number,
  targetHeight: number,
): Uint8Array {
  validateRaster(source, sourceWidth, sourceHeight)
  if (!Number.isInteger(targetWidth) || targetWidth < 1
      || !Number.isInteger(targetHeight) || targetHeight < 1) {
    throw new RangeError('CRAFT mask target dimensions must be positive integers.')
  }
  if (sourceWidth === targetWidth && sourceHeight === targetHeight) {
    return source.slice()
  }

  const result = new Uint8Array(targetWidth * targetHeight)
  const xScale = sourceWidth / targetWidth
  const yScale = sourceHeight / targetHeight
  for (let targetY = 0; targetY < targetHeight; targetY += 1) {
    const sourceY = (targetY + 0.5) * yScale - 0.5
    const y0 = Math.floor(sourceY)
    const y1 = y0 + 1
    const clampedY0 = clamp(y0, 0, sourceHeight - 1)
    const clampedY1 = clamp(y1, 0, sourceHeight - 1)
    const yWeight = clamp(sourceY - y0, 0, 1)
    for (let targetX = 0; targetX < targetWidth; targetX += 1) {
      const sourceX = (targetX + 0.5) * xScale - 0.5
      const x0 = Math.floor(sourceX)
      const x1 = x0 + 1
      const clampedX0 = clamp(x0, 0, sourceWidth - 1)
      const clampedX1 = clamp(x1, 0, sourceWidth - 1)
      const xWeight = clamp(sourceX - x0, 0, 1)
      const top = lerp(
        source[clampedY0 * sourceWidth + clampedX0],
        source[clampedY0 * sourceWidth + clampedX1],
        xWeight,
      )
      const bottom = lerp(
        source[clampedY1 * sourceWidth + clampedX0],
        source[clampedY1 * sourceWidth + clampedX1],
        xWeight,
      )
      result[targetY * targetWidth + targetX] = clamp(roundToEven(lerp(top, bottom, yWeight)), 0, 255)
    }
  }
  return result
}

export function dilateBinaryMaskEllipse(
  source: Uint8Array,
  width: number,
  height: number,
  radius: number,
): Uint8Array {
  validateRaster(source, width, height)
  if (!Number.isInteger(radius) || radius < 0) {
    throw new RangeError('CRAFT mask dilation radius must be a non-negative integer.')
  }
  if (radius === 0) {
    return source.slice()
  }

  const result = new Uint8Array(source.length)
  const rowExtents = createEllipseRowExtents(radius)
  for (let index = 0; index < source.length; index += 1) {
    if (source[index] === 0) {
      continue
    }
    const sourceX = index % width
    const sourceY = Math.floor(index / width)
    for (let offsetY = -radius; offsetY <= radius; offsetY += 1) {
      const targetY = sourceY + offsetY
      if (targetY < 0 || targetY >= height) {
        continue
      }
      const extent = rowExtents[offsetY + radius]
      const startX = Math.max(0, sourceX - extent)
      const endX = Math.min(width - 1, sourceX + extent)
      result.fill(255, targetY * width + startX, targetY * width + endX + 1)
    }
  }
  return result
}

function createEllipseRowExtents(radius: number): readonly number[] {
  const extents = new Array<number>(radius * 2 + 1)
  const squaredRadius = radius * radius
  for (let offsetY = -radius; offsetY <= radius; offsetY += 1) {
    const normalized = (squaredRadius - offsetY * offsetY) / squaredRadius
    extents[offsetY + radius] = roundToEven(radius * Math.sqrt(Math.max(0, normalized)))
  }
  return extents
}

function validateInputs(
  maps: CraftScoreMaps,
  analysis: CraftDetectionAnalysis,
  prepared: CraftPreparedInput,
): void {
  if (maps.width !== analysis.mapWidth || maps.height !== analysis.mapHeight) {
    throw new RangeError('CRAFT detection analysis does not match the supplied score maps.')
  }
  validateRaster(new Uint8Array(maps.text.length), maps.width, maps.height)
  if (maps.link.length !== maps.text.length) {
    throw new RangeError('CRAFT text and link score maps must have equal lengths.')
  }
  if (prepared.contentWidth < 1 || prepared.contentHeight < 1
      || prepared.sourceWidth < 1 || prepared.sourceHeight < 1) {
    throw new RangeError('CRAFT prepared-input dimensions must be positive.')
  }
  for (const detection of analysis.detections) {
    for (const pixel of detection.componentPixels) {
      if (pixel >= maps.text.length) {
        throw new RangeError('CRAFT detection contains a component pixel outside the score map.')
      }
    }
  }
}

function validateRaster(source: Uint8Array, width: number, height: number): void {
  if (!Number.isInteger(width) || width < 1 || !Number.isInteger(height) || height < 1
      || source.length !== width * height) {
    throw new RangeError('CRAFT raster dimensions do not match its pixel buffer.')
  }
}

function roundToEven(value: number): number {
  const lower = Math.floor(value)
  const fraction = value - lower
  if (fraction < 0.5) {
    return lower
  }
  if (fraction > 0.5) {
    return lower + 1
  }
  return lower % 2 === 0 ? lower : lower + 1
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value))
}

function lerp(first: number, second: number, weight: number): number {
  return first + (second - first) * weight
}
