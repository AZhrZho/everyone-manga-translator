export type CraftScoreLayout = 'nchw' | 'nhwc'

export type CraftTextOrientation = 'horizontal' | 'vertical'

export interface CraftPoint {
  x: number
  y: number
}

export interface CraftRgbaImage {
  readonly width: number
  readonly height: number
  readonly data: Uint8ClampedArray
}

export interface CraftOptions {
  canvasSize?: number
  magnificationRatio?: number
  textThreshold?: number
  lowTextThreshold?: number
  linkThreshold?: number
  glyphSeedThreshold?: number
  glyphCandidateThreshold?: number
  minimumComponentArea?: number
  minimumGlyphArea?: number
  maximumRegions?: number
  glyphMaskDilationRadius?: number
}

export interface ResolvedCraftOptions {
  readonly canvasSize: number
  readonly magnificationRatio: number
  readonly textThreshold: number
  readonly lowTextThreshold: number
  readonly linkThreshold: number
  readonly glyphSeedThreshold: number
  readonly glyphCandidateThreshold: number
  readonly minimumComponentArea: number
  readonly minimumGlyphArea: number
  readonly maximumRegions: number
  readonly glyphMaskDilationRadius: number
}

export interface CraftPreparedInput {
  /** Float32 RGB tensor in [1, 3, canvasHeight, canvasWidth] order. */
  readonly data: Float32Array
  readonly dimensions: readonly [1, 3, number, number]
  /** Compatibility aliases consumed by the inference worker. */
  readonly tensor: Float32Array
  readonly tensorShape: readonly [1, 3, number, number]
  readonly sourceWidth: number
  readonly sourceHeight: number
  readonly contentWidth: number
  readonly contentHeight: number
  readonly canvasWidth: number
  readonly canvasHeight: number
  readonly nominalScale: number
}

/** Structural subset of an ONNX Runtime Web float tensor. */
export interface CraftScoreTensor {
  readonly data: ArrayLike<number>
  readonly dims: readonly number[]
}

export interface CraftScoreMaps {
  readonly text: Float32Array
  readonly link: Float32Array
  readonly width: number
  readonly height: number
  readonly layout: CraftScoreLayout
}

export interface CraftConnectedComponent {
  readonly label: number
  readonly area: number
  readonly left: number
  readonly top: number
  readonly width: number
  readonly height: number
  readonly pixels: Uint32Array
}

export interface CraftConnectedComponents {
  readonly labels: Int32Array
  readonly components: readonly CraftConnectedComponent[]
  readonly width: number
  readonly height: number
}

export interface CraftComponentDetection {
  readonly componentLabel: number
  readonly componentPixels: Uint32Array
  readonly scoreBox: readonly [CraftPoint, CraftPoint, CraftPoint, CraftPoint]
  readonly confidence: number
}

export interface CraftDetectionAnalysis {
  readonly detections: readonly CraftComponentDetection[]
  readonly mapWidth: number
  readonly mapHeight: number
}

export interface CraftTextRegion {
  readonly x0: number
  readonly y0: number
  readonly x1: number
  readonly y1: number
  readonly language: 'unknown'
  readonly orientation: CraftTextOrientation
  readonly fontSize: number
  readonly confidence: number
  readonly polygon: readonly [CraftPoint, CraftPoint, CraftPoint, CraftPoint]
}

export interface CraftBinaryMask {
  readonly width: number
  readonly height: number
  /** Row-major pixels whose values are always either 0 or 255. */
  readonly pixels: Uint8Array
}

export interface CraftPostprocessResult {
  readonly regions: readonly CraftTextRegion[]
  readonly mask: CraftBinaryMask
  readonly scoreMapWidth: number
  readonly scoreMapHeight: number
}
