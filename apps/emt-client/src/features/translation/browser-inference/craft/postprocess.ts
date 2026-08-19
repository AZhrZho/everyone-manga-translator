import { labelConnectedComponents } from './connectedComponents'
import { buildCraftMask } from './mask'
import { resolveCraftOptions } from './options'
import type {
  CraftComponentDetection,
  CraftDetectionAnalysis,
  CraftOptions,
  CraftPoint,
  CraftPostprocessResult,
  CraftPreparedInput,
  CraftScoreMaps,
  CraftScoreTensor,
  CraftTextRegion,
} from './types'
import type { BrowserInferenceResult } from '../contracts'

/** Extracts text/link planes from either [1,H,W,2] or [1,2,H,W]. */
export function readCraftScoreMaps(tensor: CraftScoreTensor): CraftScoreMaps {
  if (tensor.dims.length !== 4 || tensor.dims[0] !== 1) {
    throw new RangeError('The CRAFT score output must have four dimensions and batch size one.')
  }
  const channelsLast = tensor.dims[3] === 2
  const channelsFirst = tensor.dims[1] === 2
  if (channelsLast === channelsFirst) {
    throw new RangeError('The CRAFT score output must contain exactly one two-channel dimension.')
  }

  const height = channelsLast ? tensor.dims[1] : tensor.dims[2]
  const width = channelsLast ? tensor.dims[2] : tensor.dims[3]
  if (!Number.isInteger(width) || width < 1 || !Number.isInteger(height) || height < 1) {
    throw new RangeError('The CRAFT score output has invalid spatial dimensions.')
  }
  const planeSize = width * height
  const expectedLength = planeSize * 2
  if (!Number.isSafeInteger(expectedLength) || tensor.data.length !== expectedLength) {
    throw new RangeError(`The CRAFT score output contains ${tensor.data.length} values; expected ${expectedLength}.`)
  }

  const text = new Float32Array(planeSize)
  const link = new Float32Array(planeSize)
  for (let index = 0; index < planeSize; index += 1) {
    if (channelsLast) {
      text[index] = tensor.data[index * 2]
      link[index] = tensor.data[index * 2 + 1]
    } else {
      text[index] = tensor.data[index]
      link[index] = tensor.data[planeSize + index]
    }
  }

  return {
    text,
    link,
    width,
    height,
    layout: channelsLast ? 'nhwc' : 'nchw',
  }
}

/** Removes score-map cells that correspond only to the padded model canvas. */
export function cropCraftScoreMaps(
  maps: CraftScoreMaps,
  contentWidth: number,
  contentHeight: number,
): CraftScoreMaps {
  validateMap(maps)
  if (!Number.isInteger(contentWidth) || contentWidth < 1
      || !Number.isInteger(contentHeight) || contentHeight < 1) {
    throw new RangeError('CRAFT content dimensions must be positive integers.')
  }
  const width = Math.min(maps.width, Math.ceil(contentWidth / 2))
  const height = Math.min(maps.height, Math.ceil(contentHeight / 2))
  if (width === maps.width && height === maps.height) {
    return maps
  }

  const text = new Float32Array(width * height)
  const link = new Float32Array(width * height)
  for (let y = 0; y < height; y += 1) {
    const sourceStart = y * maps.width
    const sourceEnd = sourceStart + width
    text.set(maps.text.subarray(sourceStart, sourceEnd), y * width)
    link.set(maps.link.subarray(sourceStart, sourceEnd), y * width)
  }
  return { text, link, width, height, layout: maps.layout }
}

export function detectCraftRegions(
  maps: CraftScoreMaps,
  options: CraftOptions = {},
): CraftDetectionAnalysis {
  validateMap(maps)
  const resolved = resolveCraftOptions(options)
  const combined = new Uint8Array(maps.width * maps.height)
  for (let index = 0; index < combined.length; index += 1) {
    if (maps.text[index] > resolved.lowTextThreshold || maps.link[index] > resolved.linkThreshold) {
      combined[index] = 1
    }
  }

  const connected = labelConnectedComponents(combined, maps.width, maps.height, 4)
  const detections: CraftComponentDetection[] = []
  for (const component of connected.components) {
    if (detections.length >= resolved.maximumRegions) {
      break
    }
    if (component.area < resolved.minimumComponentArea) {
      continue
    }

    let peak = Number.NEGATIVE_INFINITY
    for (const index of component.pixels) {
      peak = Math.max(peak, maps.text[index])
    }
    if (peak < resolved.textThreshold) {
      continue
    }

    const iterations = Math.trunc(
      Math.sqrt(
        component.area * Math.min(component.width, component.height)
        / (component.width * component.height),
      ) * 2,
    )
    const points = dilateTextSegment(
      component.pixels,
      maps.text,
      component.left,
      component.top,
      component.width,
      component.height,
      maps.width,
      maps.height,
      resolved.lowTextThreshold,
      iterations,
    )
    if (points.length < 3) {
      continue
    }

    let scoreBox = minimumAreaRectangle(points)
    const sideA = distance(scoreBox[0], scoreBox[1])
    const sideB = distance(scoreBox[1], scoreBox[2])
    if (Math.abs(1 - Math.max(sideA, sideB) / (Math.min(sideA, sideB) + 1e-5)) <= 0.1) {
      scoreBox = axisAlignedRectangle(points, true)
    }
    detections.push({
      componentLabel: component.label,
      componentPixels: component.pixels,
      scoreBox: rotateToSmallestCoordinateSum(scoreBox),
      confidence: clamp(peak, 0, 1),
    })
  }

  return { detections, mapWidth: maps.width, mapHeight: maps.height }
}

/** Complete CRAFT browser postprocessing, including page-space regions and mask. */
export function postprocessCraftOutput(
  tensor: CraftScoreTensor,
  prepared: CraftPreparedInput,
  options: CraftOptions = {},
): CraftPostprocessResult {
  validatePreparedInput(prepared)
  const maps = cropCraftScoreMaps(
    readCraftScoreMaps(tensor),
    prepared.contentWidth,
    prepared.contentHeight,
  )
  const analysis = detectCraftRegions(maps, options)
  const regions = analysis.detections.map((detection) => mapDetectionToRegion(
    detection,
    analysis.mapWidth,
    analysis.mapHeight,
    prepared.sourceWidth,
    prepared.sourceHeight,
  ))
  const mask = buildCraftMask(maps, analysis, prepared, options)
  return {
    regions,
    mask,
    scoreMapWidth: maps.width,
    scoreMapHeight: maps.height,
  }
}

/** Worker-facing compatibility adapter around the stable structural API. */
export function processCraftOutput(
  prepared: CraftPreparedInput,
  scores: Float32Array,
  dimensions: readonly number[],
  options: CraftOptions = {},
): Pick<BrowserInferenceResult, 'width' | 'height' | 'regions' | 'mask'> {
  const processed = postprocessCraftOutput({ data: scores, dims: dimensions }, prepared, options)
  return {
    width: processed.mask.width,
    height: processed.mask.height,
    mask: processed.mask.pixels,
    regions: processed.regions.map((region) => ({
      x0: region.x0,
      y0: region.y0,
      x1: region.x1,
      y1: region.y1,
      orientation: region.orientation === 'vertical' ? 'Vertical' : 'Horizontal',
      fontSize: region.fontSize,
      confidence: region.confidence,
      polygon: region.polygon.map((point) => ({ x: point.x, y: point.y })),
    })),
  }
}

export function mapScoreCoordinate(coordinate: number, mapLength: number, imageLength: number): number {
  if (!Number.isFinite(coordinate)) {
    throw new RangeError('CRAFT score coordinate must be finite.')
  }
  if (!Number.isInteger(mapLength) || mapLength < 1
      || !Number.isInteger(imageLength) || imageLength < 1) {
    throw new RangeError('CRAFT coordinate dimensions must be positive integers.')
  }
  return clamp(roundToEven(coordinate / mapLength * imageLength), 0, imageLength - 1)
}

function mapDetectionToRegion(
  detection: CraftComponentDetection,
  mapWidth: number,
  mapHeight: number,
  imageWidth: number,
  imageHeight: number,
): CraftTextRegion {
  const polygon = detection.scoreBox.map((point) => ({
    x: mapScoreCoordinate(point.x, mapWidth, imageWidth),
    y: mapScoreCoordinate(point.y, mapHeight, imageHeight),
  })) as [CraftPoint, CraftPoint, CraftPoint, CraftPoint]
  const x0 = Math.min(...polygon.map((point) => point.x))
  const y0 = Math.min(...polygon.map((point) => point.y))
  const x1 = Math.min(imageWidth, Math.max(...polygon.map((point) => point.x)) + 1)
  const y1 = Math.min(imageHeight, Math.max(...polygon.map((point) => point.y)) + 1)
  const orientation = y1 - y0 > x1 - x0 ? 'vertical' : 'horizontal'
  return {
    x0,
    y0,
    x1: Math.max(x0 + 1, x1),
    y1: Math.max(y0 + 1, y1),
    language: 'unknown',
    orientation,
    fontSize: Math.max(1, orientation === 'vertical' ? x1 - x0 : y1 - y0),
    confidence: detection.confidence,
    polygon,
  }
}

function dilateTextSegment(
  componentPixels: Uint32Array,
  text: Float32Array,
  left: number,
  top: number,
  componentWidth: number,
  componentHeight: number,
  mapWidth: number,
  mapHeight: number,
  lowTextThreshold: number,
  iterations: number,
): CraftPoint[] {
  const roiLeft = clamp(left - iterations, 0, mapWidth)
  const roiTop = clamp(top - iterations, 0, mapHeight)
  const roiRight = clamp(left + componentWidth + iterations + 1, roiLeft, mapWidth)
  const roiBottom = clamp(top + componentHeight + iterations + 1, roiTop, mapHeight)
  const roiWidth = roiRight - roiLeft
  const roiHeight = roiBottom - roiTop
  const dilated = new Uint8Array(roiWidth * roiHeight)
  const kernelSize = 1 + iterations
  const anchor = Math.floor(kernelSize / 2)
  const negativeExtent = kernelSize - 1 - anchor

  for (const index of componentPixels) {
    if (!(text[index] > lowTextThreshold)) {
      continue
    }
    const sourceX = index % mapWidth
    const sourceY = Math.floor(index / mapWidth)
    const startX = Math.max(roiLeft, sourceX - negativeExtent)
    const endX = Math.min(roiRight - 1, sourceX + anchor)
    const startY = Math.max(roiTop, sourceY - negativeExtent)
    const endY = Math.min(roiBottom - 1, sourceY + anchor)
    for (let y = startY; y <= endY; y += 1) {
      const row = (y - roiTop) * roiWidth
      dilated.fill(1, row + startX - roiLeft, row + endX - roiLeft + 1)
    }
  }

  const points: CraftPoint[] = []
  for (let localY = 0; localY < roiHeight; localY += 1) {
    for (let localX = 0; localX < roiWidth; localX += 1) {
      if (dilated[localY * roiWidth + localX] !== 0) {
        points.push({ x: roiLeft + localX, y: roiTop + localY })
      }
    }
  }
  return points
}

function minimumAreaRectangle(points: readonly CraftPoint[]): [CraftPoint, CraftPoint, CraftPoint, CraftPoint] {
  const hull = convexHull(points)
  if (hull.length < 3) {
    return axisAlignedRectangle(points, false)
  }

  let bestArea = Number.POSITIVE_INFINITY
  let bestRectangle: [CraftPoint, CraftPoint, CraftPoint, CraftPoint] | undefined
  for (let index = 0; index < hull.length; index += 1) {
    const first = hull[index]
    const second = hull[(index + 1) % hull.length]
    const angle = Math.atan2(second.y - first.y, second.x - first.x)
    const cosine = Math.cos(angle)
    const sine = Math.sin(angle)
    let minimumU = Number.POSITIVE_INFINITY
    let maximumU = Number.NEGATIVE_INFINITY
    let minimumV = Number.POSITIVE_INFINITY
    let maximumV = Number.NEGATIVE_INFINITY
    for (const point of hull) {
      const u = point.x * cosine + point.y * sine
      const v = -point.x * sine + point.y * cosine
      minimumU = Math.min(minimumU, u)
      maximumU = Math.max(maximumU, u)
      minimumV = Math.min(minimumV, v)
      maximumV = Math.max(maximumV, v)
    }
    const area = (maximumU - minimumU) * (maximumV - minimumV)
    if (area < bestArea) {
      bestArea = area
      bestRectangle = [
        inverseRotate(minimumU, minimumV, cosine, sine),
        inverseRotate(maximumU, minimumV, cosine, sine),
        inverseRotate(maximumU, maximumV, cosine, sine),
        inverseRotate(minimumU, maximumV, cosine, sine),
      ]
    }
  }
  return bestRectangle ?? axisAlignedRectangle(points, false)
}

function convexHull(points: readonly CraftPoint[]): readonly CraftPoint[] {
  const sorted = points
    .map((point) => ({ x: point.x, y: point.y }))
    .sort((first, second) => first.x - second.x || first.y - second.y)
  const unique = sorted.filter((point, index) => index === 0
    || point.x !== sorted[index - 1].x
    || point.y !== sorted[index - 1].y)
  if (unique.length <= 1) {
    return unique
  }

  const lower: CraftPoint[] = []
  for (const point of unique) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], point) <= 0) {
      lower.pop()
    }
    lower.push(point)
  }
  const upper: CraftPoint[] = []
  for (let index = unique.length - 1; index >= 0; index -= 1) {
    const point = unique[index]
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], point) <= 0) {
      upper.pop()
    }
    upper.push(point)
  }
  lower.pop()
  upper.pop()
  return lower.concat(upper)
}

function axisAlignedRectangle(
  points: readonly CraftPoint[],
  useExclusiveMaximum: boolean,
): [CraftPoint, CraftPoint, CraftPoint, CraftPoint] {
  let left = Number.POSITIVE_INFINITY
  let right = Number.NEGATIVE_INFINITY
  let top = Number.POSITIVE_INFINITY
  let bottom = Number.NEGATIVE_INFINITY
  for (const point of points) {
    left = Math.min(left, point.x)
    right = Math.max(right, point.x)
    top = Math.min(top, point.y)
    bottom = Math.max(bottom, point.y)
  }
  if (useExclusiveMaximum) {
    right += 1
    bottom += 1
  }
  return [
    { x: left, y: top },
    { x: right, y: top },
    { x: right, y: bottom },
    { x: left, y: bottom },
  ]
}

function rotateToSmallestCoordinateSum(
  points: readonly [CraftPoint, CraftPoint, CraftPoint, CraftPoint],
): [CraftPoint, CraftPoint, CraftPoint, CraftPoint] {
  let start = 0
  for (let index = 1; index < points.length; index += 1) {
    if (points[index].x + points[index].y < points[start].x + points[start].y) {
      start = index
    }
  }
  return [
    points[start],
    points[(start + 1) % 4],
    points[(start + 2) % 4],
    points[(start + 3) % 4],
  ]
}

function inverseRotate(u: number, v: number, cosine: number, sine: number): CraftPoint {
  return {
    x: u * cosine - v * sine,
    y: u * sine + v * cosine,
  }
}

function cross(origin: CraftPoint, first: CraftPoint, second: CraftPoint): number {
  return (first.x - origin.x) * (second.y - origin.y)
    - (first.y - origin.y) * (second.x - origin.x)
}

function distance(first: CraftPoint, second: CraftPoint): number {
  return Math.hypot(first.x - second.x, first.y - second.y)
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

function validateMap(maps: CraftScoreMaps): void {
  if (!Number.isInteger(maps.width) || maps.width < 1
      || !Number.isInteger(maps.height) || maps.height < 1
      || maps.text.length !== maps.width * maps.height
      || maps.link.length !== maps.width * maps.height) {
    throw new RangeError('CRAFT score-map dimensions do not match their planes.')
  }
}

function validatePreparedInput(prepared: CraftPreparedInput): void {
  if (prepared.dimensions[0] !== 1
      || prepared.dimensions[1] !== 3
      || prepared.dimensions[2] !== prepared.canvasHeight
      || prepared.dimensions[3] !== prepared.canvasWidth
      || prepared.data.length !== prepared.canvasWidth * prepared.canvasHeight * 3) {
    throw new RangeError('CRAFT prepared-input tensor does not match its declared dimensions.')
  }
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value))
}
