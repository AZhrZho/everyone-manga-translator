export type BrowserInferenceBackend = 'webgpu' | 'wasm'

export type BrowserInferenceStage =
  | 'idle'
  | 'loading-model'
  | 'ready'
  | 'preprocessing'
  | 'running'
  | 'postprocessing'
  | 'completed'
  | 'failed'

export interface BrowserInferenceCapabilities {
  secureContext: boolean
  webGpuAvailable: boolean
  wasmAvailable: boolean
  crossOriginIsolated: boolean
  wasmThreads: number
}

export interface BrowserInferencePoint {
  x: number
  y: number
}

export interface BrowserInferenceRegion {
  x0: number
  y0: number
  x1: number
  y1: number
  orientation: 'Horizontal' | 'Vertical'
  fontSize: number
  confidence: number
  polygon: BrowserInferencePoint[]
}

export interface BrowserInferenceTimings {
  modelLoadMs: number
  preprocessMs: number
  inferenceMs: number
  postprocessMs: number
  totalMs: number
}

export interface BrowserInferenceResult {
  width: number
  height: number
  regions: BrowserInferenceRegion[]
  mask: Uint8Array
  backend: BrowserInferenceBackend
  runtimeVersion: string
  modelSha256: string
  timings: BrowserInferenceTimings
  fallbackReason: string | null
}

export interface BrowserInferenceDetectedResult extends BrowserInferenceResult {
  sourceSha256: string
}

export interface BrowserDetectionUploadMetadata {
  schemaVersion: '1.0'
  detectorId: 'craft'
  modelSha256: string
  width: number
  height: number
  backend: BrowserInferenceBackend
  runtimeVersion: string
  regions: BrowserInferenceRegion[]
  sourceSha256: string
  algorithmVersion: 'craft-onnx-clean-mask-v2'
  optionsFingerprint: string
}

export interface BrowserDetectionUploadResponse {
  detectorId: string
  detectorCacheKey: string
  modelSha256: string
  sourceSha256: string
  algorithmVersion: string
  optionsFingerprint: string
  maskAssetKey: string
  cleanAssetKey: string
  regionCount: number
  fingerprintSha256: string
  provenance: string
}

export interface BrowserInferenceStatus {
  stage: BrowserInferenceStage
  message: string
  backend: BrowserInferenceBackend | null
  fallbackReason: string | null
}

export interface BrowserInferenceModelInfo {
  name: string
  size: number
  sha256: string
  backend: BrowserInferenceBackend
  loadMs: number
  fallbackReason: string | null
}

export type BrowserInferenceWorkerRequest =
  | { id: number; type: 'load-model'; model: ArrayBuffer; name: string; preferredBackend: BrowserInferenceBackend }
  | { id: number; type: 'detect'; image: ArrayBuffer; mediaType: string }
  | { id: number; type: 'dispose' }

export type BrowserInferenceWorkerResponse =
  | { id: number; type: 'status'; status: BrowserInferenceStatus }
  | { id: number; type: 'model-loaded'; model: BrowserInferenceModelInfo }
  | { id: number; type: 'detected'; result: BrowserInferenceResult }
  | { id: number; type: 'disposed' }
  | { id: number; type: 'error'; message: string; stack?: string }
