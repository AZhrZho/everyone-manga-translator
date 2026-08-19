import { describe, expect, it } from 'vitest'
import { prepareCraftInput } from './preprocess'

describe('CRAFT browser preprocessing', () => {
  it('quantizes bilinear RGB values to CV_8UC3 bytes before normalization', () => {
    const prepared = prepareCraftInput({
      width: 2,
      height: 1,
      data: Uint8ClampedArray.from([
        0, 0, 0, 255,
        255, 2, 5, 255,
      ]),
    }, {
      canvasSize: 32,
      magnificationRatio: 1.5,
    })

    expect(prepared.contentWidth).toBe(3)
    expect(prepared.contentHeight).toBe(1)
    const planeSize = prepared.canvasWidth * prepared.canvasHeight
    expect(denormalizeByte(prepared.data[1], 0.485, 0.229)).toBe(128)
    expect(denormalizeByte(prepared.data[planeSize + 1], 0.456, 0.224)).toBe(1)
    expect(denormalizeByte(prepared.data[planeSize * 2 + 1], 0.406, 0.225)).toBe(3)
  })

  it('keeps black byte padding normalized separately from resized content', () => {
    const prepared = prepareCraftInput({
      width: 1,
      height: 1,
      data: Uint8ClampedArray.from([255, 255, 255, 255]),
    }, {
      canvasSize: 32,
      magnificationRatio: 1,
    })

    expect(denormalizeByte(prepared.data[0], 0.485, 0.229)).toBe(255)
    expect(Math.abs(denormalizeByte(prepared.data[1], 0.485, 0.229))).toBe(0)
  })
})

function denormalizeByte(value: number, mean: number, deviation: number): number {
  return Math.round((value * deviation + mean) * 255)
}
