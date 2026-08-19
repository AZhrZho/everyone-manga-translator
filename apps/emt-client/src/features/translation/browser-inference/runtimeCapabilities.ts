import type { BrowserInferenceCapabilities } from './contracts'

export function detectBrowserInferenceCapabilities(): BrowserInferenceCapabilities {
  const hardwareConcurrency = Math.max(1, navigator.hardwareConcurrency || 1)
  const isolated = globalThis.crossOriginIsolated === true
  return {
    secureContext: globalThis.isSecureContext,
    webGpuAvailable: globalThis.isSecureContext && 'gpu' in navigator,
    wasmAvailable: typeof WebAssembly === 'object',
    crossOriginIsolated: isolated,
    wasmThreads: isolated ? Math.min(4, Math.max(1, Math.ceil(hardwareConcurrency / 2))) : 1,
  }
}
