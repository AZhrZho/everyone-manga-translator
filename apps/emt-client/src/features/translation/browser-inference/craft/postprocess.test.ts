import { describe, expect, it } from 'vitest'
import { postprocessCraftOutput } from './postprocess'
import type { CraftPreparedInput } from './types'

describe('CRAFT browser postprocessing', () => {
  it('crops padded score cells and keeps the region and mask in page space', () => {
    const prepared = createPrepared(101, 77, 101, 77, 128, 96)
    const scores = new Float32Array(48 * 64 * 2)
    fill(scores, 64, 45, 15, 4, 4, 0, 0.9)
    fill(scores, 64, 58, 15, 4, 4, 0, 0.9)

    const result = postprocessCraftOutput({ data: scores, dims: [1, 48, 64, 2] }, prepared, {
      canvasSize: 128,
      magnificationRatio: 1,
      minimumComponentArea: 1,
      minimumGlyphArea: 1,
    })

    expect(result.regions).toHaveLength(1)
    expect(result.regions[0].x0).toBeGreaterThanOrEqual(80)
    const foreground = [...result.mask.pixels.entries()].filter(([, value]) => value !== 0)
    expect(foreground.length).toBeGreaterThan(0)
    for (const [index] of foreground) {
      const x = index % result.mask.width
      const y = Math.floor(index / result.mask.width)
      expect(x).toBeGreaterThanOrEqual(result.regions[0].x0)
      expect(x).toBeLessThan(result.regions[0].x1)
      expect(y).toBeGreaterThanOrEqual(result.regions[0].y0)
      expect(y).toBeLessThan(result.regions[0].y1)
    }
  })

  it('groups weak punctuation through affinity without masking affinity pixels', () => {
    const prepared = createPrepared(128, 64, 128, 64, 128, 64)
    const scores = new Float32Array(32 * 64 * 2)
    fill(scores, 64, 10, 10, 5, 10, 0, 0.9)
    fill(scores, 64, 22, 11, 2, 2, 0, 0.45)
    fill(scores, 64, 22, 15, 2, 2, 0, 0.45)
    fill(scores, 64, 15, 11, 7, 6, 1, 0.9)

    const result = postprocessCraftOutput({ data: scores, dims: [1, 32, 64, 2] }, prepared, {
      canvasSize: 128,
      magnificationRatio: 1,
      minimumComponentArea: 1,
      minimumGlyphArea: 1,
    })

    expect(result.regions).toHaveLength(1)
    expect(anyForeground(result.mask.pixels, result.mask.width, 43, 21, 7, 7)).toBe(true)
    expect(anyForeground(result.mask.pixels, result.mask.width, 43, 29, 7, 7)).toBe(true)
    expect(anyForeground(result.mask.pixels, result.mask.width, 34, 24, 7, 7)).toBe(false)
    expect(new Set(result.mask.pixels)).toEqual(new Set([0, 255]))
  })
})

function createPrepared(
  sourceWidth: number,
  sourceHeight: number,
  contentWidth: number,
  contentHeight: number,
  canvasWidth: number,
  canvasHeight: number,
): CraftPreparedInput {
  const tensor = new Float32Array(canvasWidth * canvasHeight * 3)
  const dimensions = [1, 3, canvasHeight, canvasWidth] as const
  return {
    data: tensor,
    dimensions,
    tensor,
    tensorShape: dimensions,
    sourceWidth,
    sourceHeight,
    contentWidth,
    contentHeight,
    canvasWidth,
    canvasHeight,
    nominalScale: 1,
  }
}

function fill(
  scores: Float32Array,
  mapWidth: number,
  x0: number,
  y0: number,
  width: number,
  height: number,
  channel: 0 | 1,
  score: number,
) {
  for (let y = y0; y < y0 + height; y += 1) {
    for (let x = x0; x < x0 + width; x += 1) {
      scores[(y * mapWidth + x) * 2 + channel] = score
    }
  }
}

function anyForeground(mask: Uint8Array, imageWidth: number, x0: number, y0: number, width: number, height: number) {
  for (let y = y0; y < y0 + height; y += 1) {
    for (let x = x0; x < x0 + width; x += 1) {
      if (mask[x + y * imageWidth] !== 0) return true
    }
  }
  return false
}
