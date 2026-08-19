import { resolveCraftOptions } from './options'
import type { CraftOptions, CraftPreparedInput, CraftRgbaImage } from './types'

const RGB_MEAN: readonly [number, number, number] = [0.485, 0.456, 0.406]
const RGB_DEVIATION: readonly [number, number, number] = [0.229, 0.224, 0.225]

export function roundUpTo32(value: number): number {
  if (!Number.isInteger(value) || value < 1) {
    throw new RangeError('The CRAFT content dimension must be a positive integer.')
  }
  return Math.ceil(value / 32) * 32
}

/**
 * Resizes an RGBA browser image, pads it to a multiple-of-32 canvas, and
 * produces the normalized RGB NCHW tensor expected by the CRAFT ONNX graph.
 */
export function prepareCraftInput(
  image: ImageData | CraftRgbaImage,
  options?: CraftOptions,
): CraftPreparedInput
export function prepareCraftInput(
  encodedImage: ArrayBuffer,
  mediaType: string,
  options?: CraftOptions,
): Promise<CraftPreparedInput>
export function prepareCraftInput(
  image: ImageData | CraftRgbaImage | ArrayBuffer,
  optionsOrMediaType: CraftOptions | string = {},
  encodedOptions: CraftOptions = {},
): CraftPreparedInput | Promise<CraftPreparedInput> {
  if (image instanceof ArrayBuffer) {
    if (typeof optionsOrMediaType !== 'string') {
      throw new TypeError('The media type is required when preparing encoded CRAFT image bytes.')
    }
    return decodeRgbaImage(image, optionsOrMediaType)
      .then((decoded) => prepareRgbaImage(decoded, encodedOptions))
  }
  if (typeof optionsOrMediaType === 'string') {
    throw new TypeError('CRAFT options must be an object for an already-decoded RGBA image.')
  }
  return prepareRgbaImage(image, optionsOrMediaType)
}

function prepareRgbaImage(
  image: ImageData | CraftRgbaImage,
  options: CraftOptions,
): CraftPreparedInput {
  validateImage(image)
  const resolved = resolveCraftOptions(options)
  const longestSide = Math.max(image.width, image.height)
  const targetSize = Math.min(resolved.magnificationRatio * longestSide, resolved.canvasSize)
  const nominalScale = targetSize / longestSide
  const contentWidth = Math.max(1, Math.trunc(image.width * nominalScale))
  const contentHeight = Math.max(1, Math.trunc(image.height * nominalScale))
  const canvasWidth = roundUpTo32(contentWidth)
  const canvasHeight = roundUpTo32(contentHeight)
  const planeSize = checkedPixelCount(canvasWidth, canvasHeight)
  const tensorData = new Float32Array(checkedValueCount(planeSize, 3))

  for (let channel = 0; channel < 3; channel += 1) {
    tensorData.fill(
      (0 - RGB_MEAN[channel]) / RGB_DEVIATION[channel],
      channel * planeSize,
      (channel + 1) * planeSize,
    )
  }

  const xSamples = createLinearSamples(image.width, contentWidth)
  const ySamples = createLinearSamples(image.height, contentHeight)
  for (let targetY = 0; targetY < contentHeight; targetY += 1) {
    const ySample = ySamples[targetY]
    const sourceRow0 = ySample.lower * image.width
    const sourceRow1 = ySample.upper * image.width
    const targetRow = targetY * canvasWidth
    for (let targetX = 0; targetX < contentWidth; targetX += 1) {
      const xSample = xSamples[targetX]
      const source00 = (sourceRow0 + xSample.lower) * 4
      const source01 = (sourceRow0 + xSample.upper) * 4
      const source10 = (sourceRow1 + xSample.lower) * 4
      const source11 = (sourceRow1 + xSample.upper) * 4
      const destination = targetRow + targetX
      for (let channel = 0; channel < 3; channel += 1) {
        const top = lerp(image.data[source00 + channel], image.data[source01 + channel], xSample.weight)
        const bottom = lerp(image.data[source10 + channel], image.data[source11 + channel], xSample.weight)
        // OpenCV resizes the decoded CV_8UC3 image into another byte Mat.
        // Match that contract by saturating/rounding before normalization.
        const byteValue = saturateByte(lerp(top, bottom, ySample.weight))
        tensorData[channel * planeSize + destination] = (byteValue / 255 - RGB_MEAN[channel]) / RGB_DEVIATION[channel]
      }
    }
  }

  const dimensions = [1, 3, canvasHeight, canvasWidth] as const
  return {
    data: tensorData,
    dimensions,
    tensor: tensorData,
    tensorShape: dimensions,
    sourceWidth: image.width,
    sourceHeight: image.height,
    contentWidth,
    contentHeight,
    canvasWidth,
    canvasHeight,
    nominalScale,
  }
}

async function decodeRgbaImage(encodedImage: ArrayBuffer, mediaType: string): Promise<CraftRgbaImage> {
  if (encodedImage.byteLength === 0) {
    throw new RangeError('CRAFT encoded image bytes must not be empty.')
  }
  const bitmap = await createImageBitmap(new Blob([encodedImage], { type: mediaType }))
  try {
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
    const context = canvas.getContext('2d', { willReadFrequently: true })
    if (context === null) {
      throw new Error('Canvas 2D is unavailable for CRAFT image decoding.')
    }
    context.drawImage(bitmap, 0, 0)
    const decoded = context.getImageData(0, 0, bitmap.width, bitmap.height)
    return { width: decoded.width, height: decoded.height, data: decoded.data }
  } finally {
    bitmap.close()
  }
}

interface LinearSample {
  readonly lower: number
  readonly upper: number
  readonly weight: number
}

function createLinearSamples(sourceLength: number, targetLength: number): readonly LinearSample[] {
  const samples = new Array<LinearSample>(targetLength)
  const scale = sourceLength / targetLength
  for (let target = 0; target < targetLength; target += 1) {
    const coordinate = (target + 0.5) * scale - 0.5
    const lower = Math.floor(coordinate)
    samples[target] = {
      lower: clamp(lower, 0, sourceLength - 1),
      upper: clamp(lower + 1, 0, sourceLength - 1),
      weight: clamp(coordinate - lower, 0, 1),
    }
  }
  return samples
}

function validateImage(image: ImageData | CraftRgbaImage): void {
  if (!Number.isInteger(image.width) || image.width < 1
      || !Number.isInteger(image.height) || image.height < 1) {
    throw new RangeError('CRAFT input image dimensions must be positive integers.')
  }
  const expectedLength = checkedValueCount(checkedPixelCount(image.width, image.height), 4)
  if (image.data.length !== expectedLength) {
    throw new RangeError(`CRAFT RGBA input contains ${image.data.length} values; expected ${expectedLength}.`)
  }
}

function checkedPixelCount(width: number, height: number): number {
  const count = width * height
  if (!Number.isSafeInteger(count)) {
    throw new RangeError('CRAFT image dimensions are too large.')
  }
  return count
}

function checkedValueCount(pixelCount: number, channels: number): number {
  const count = pixelCount * channels
  if (!Number.isSafeInteger(count)) {
    throw new RangeError('CRAFT tensor dimensions are too large.')
  }
  return count
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value))
}

function lerp(first: number, second: number, weight: number): number {
  return first + (second - first) * weight
}

function saturateByte(value: number): number {
  return clamp(Math.round(value), 0, 255)
}
