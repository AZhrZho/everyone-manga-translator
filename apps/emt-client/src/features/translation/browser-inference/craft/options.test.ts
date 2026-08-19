import { describe, expect, it } from 'vitest'
import { CRAFT_ALGORITHM_VERSION, createCraftOptionsFingerprint } from './options'

describe('CRAFT browser detector contract', () => {
  it('matches the server fingerprint for the default detector options', async () => {
    expect(CRAFT_ALGORITHM_VERSION).toBe('craft-onnx-clean-mask-v2')
    await expect(createCraftOptionsFingerprint()).resolves.toBe(
      'c8b3a2001261c46411dd6dcaaf825f81a7d262e06a4d0683ff1a8c12bbd0216d',
    )
  })
})
