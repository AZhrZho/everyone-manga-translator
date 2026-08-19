import type { CraftOptions, ResolvedCraftOptions } from './types'

export const CRAFT_ALGORITHM_VERSION = 'craft-onnx-clean-mask-v2' as const

export const DEFAULT_CRAFT_OPTIONS: Readonly<ResolvedCraftOptions> = Object.freeze({
  canvasSize: 1280,
  magnificationRatio: 1.5,
  textThreshold: 0.7,
  lowTextThreshold: 0.4,
  linkThreshold: 0.4,
  glyphSeedThreshold: 0.55,
  glyphCandidateThreshold: 0.25,
  minimumComponentArea: 10,
  minimumGlyphArea: 2,
  maximumRegions: 1000,
  glyphMaskDilationRadius: 0,
})

export function resolveCraftOptions(options: CraftOptions = {}): ResolvedCraftOptions {
  const resolved: ResolvedCraftOptions = {
    canvasSize: options.canvasSize ?? DEFAULT_CRAFT_OPTIONS.canvasSize,
    magnificationRatio: options.magnificationRatio ?? DEFAULT_CRAFT_OPTIONS.magnificationRatio,
    textThreshold: options.textThreshold ?? DEFAULT_CRAFT_OPTIONS.textThreshold,
    lowTextThreshold: options.lowTextThreshold ?? DEFAULT_CRAFT_OPTIONS.lowTextThreshold,
    linkThreshold: options.linkThreshold ?? DEFAULT_CRAFT_OPTIONS.linkThreshold,
    glyphSeedThreshold: options.glyphSeedThreshold ?? DEFAULT_CRAFT_OPTIONS.glyphSeedThreshold,
    glyphCandidateThreshold: options.glyphCandidateThreshold ?? DEFAULT_CRAFT_OPTIONS.glyphCandidateThreshold,
    minimumComponentArea: options.minimumComponentArea ?? DEFAULT_CRAFT_OPTIONS.minimumComponentArea,
    minimumGlyphArea: options.minimumGlyphArea ?? DEFAULT_CRAFT_OPTIONS.minimumGlyphArea,
    maximumRegions: options.maximumRegions ?? DEFAULT_CRAFT_OPTIONS.maximumRegions,
    glyphMaskDilationRadius: options.glyphMaskDilationRadius ?? DEFAULT_CRAFT_OPTIONS.glyphMaskDilationRadius,
  }

  if (!Number.isInteger(resolved.canvasSize) || resolved.canvasSize < 32 || resolved.canvasSize % 32 !== 0) {
    throw new RangeError('CRAFT canvasSize must be an integer multiple of 32.')
  }
  if (!Number.isFinite(resolved.magnificationRatio)
      || resolved.magnificationRatio <= 0
      || resolved.magnificationRatio > 8) {
    throw new RangeError('CRAFT magnificationRatio must be greater than 0 and at most 8.')
  }

  validateProbability(resolved.textThreshold, 'textThreshold')
  validateProbability(resolved.lowTextThreshold, 'lowTextThreshold')
  validateProbability(resolved.linkThreshold, 'linkThreshold')
  validateProbability(resolved.glyphSeedThreshold, 'glyphSeedThreshold')
  validateProbability(resolved.glyphCandidateThreshold, 'glyphCandidateThreshold')
  if (resolved.lowTextThreshold > resolved.textThreshold) {
    throw new RangeError('CRAFT lowTextThreshold cannot exceed textThreshold.')
  }
  if (resolved.glyphCandidateThreshold > resolved.glyphSeedThreshold) {
    throw new RangeError('CRAFT glyphCandidateThreshold cannot exceed glyphSeedThreshold.')
  }

  validatePositiveInteger(resolved.minimumComponentArea, 'minimumComponentArea')
  validatePositiveInteger(resolved.minimumGlyphArea, 'minimumGlyphArea')
  validatePositiveInteger(resolved.maximumRegions, 'maximumRegions')
  if (!Number.isInteger(resolved.glyphMaskDilationRadius)
      || resolved.glyphMaskDilationRadius < 0
      || resolved.glyphMaskDilationRadius > 4) {
    throw new RangeError('CRAFT glyphMaskDilationRadius must be an integer from 0 through 4.')
  }

  return resolved
}

export async function createCraftOptionsFingerprint(options: CraftOptions = {}): Promise<string> {
  const material = `${CRAFT_ALGORITHM_VERSION}\n${JSON.stringify(resolveCraftOptions(options))}`
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(material))
  return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('')
}

function validateProbability(value: number, name: string): void {
  if (!Number.isFinite(value) || value < 0 || value > 1) {
    throw new RangeError(`CRAFT ${name} must be between 0 and 1.`)
  }
}

function validatePositiveInteger(value: number, name: string): void {
  if (!Number.isInteger(value) || value < 1) {
    throw new RangeError(`CRAFT ${name} must be a positive integer.`)
  }
}
