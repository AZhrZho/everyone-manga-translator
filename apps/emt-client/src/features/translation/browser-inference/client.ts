import type {
  BrowserInferenceBackend,
  BrowserInferenceDetectedResult,
  BrowserInferenceModelInfo,
  BrowserInferenceResult,
  BrowserInferenceStatus,
  BrowserInferenceWorkerRequest,
  BrowserInferenceWorkerResponse,
} from './contracts'

type PendingRequest = {
  resolve: (value: BrowserInferenceModelInfo | BrowserInferenceResult | undefined) => void
  reject: (reason: Error) => void
}

type WorkerRequestWithoutId =
  | Omit<Extract<BrowserInferenceWorkerRequest, { type: 'load-model' }>, 'id'>
  | Omit<Extract<BrowserInferenceWorkerRequest, { type: 'detect' }>, 'id'>
  | Omit<Extract<BrowserInferenceWorkerRequest, { type: 'dispose' }>, 'id'>

export type BrowserInferenceWorkerFactory = () => Worker
export const MAXIMUM_BROWSER_MODEL_BYTES = 512 * 1024 * 1024

export class BrowserTextMaskDetector {
  private readonly worker: Worker
  private readonly pending = new Map<number, PendingRequest>()
  private requestId = 0
  private terminalError: Error | null = null
  private readonly onStatus?: (status: BrowserInferenceStatus) => void
  private readonly onFatalError?: (error: Error) => void

  constructor(
    onStatus?: (status: BrowserInferenceStatus) => void,
    createWorker: BrowserInferenceWorkerFactory = createBrowserInferenceWorker,
    onFatalError?: (error: Error) => void,
  ) {
    this.onStatus = onStatus
    this.onFatalError = onFatalError
    this.worker = createWorker()
    this.worker.onmessage = event => this.handleMessage(event.data as BrowserInferenceWorkerResponse)
    this.worker.onerror = event => {
      this.failWorker(new Error(event.message || 'Browser inference worker failed.'))
    }
    this.worker.onmessageerror = () => {
      this.failWorker(new Error('Browser inference worker returned an unreadable message.'))
    }
  }

  async loadModel(file: File, preferredBackend: BrowserInferenceBackend = 'webgpu') {
    this.throwIfUnavailable()
    if (file.size > MAXIMUM_BROWSER_MODEL_BYTES) {
      throw new Error('The browser inference model must not exceed 512 MiB.')
    }
    const model = await file.arrayBuffer()
    return this.loadModelBytes(model, file.name, preferredBackend)
  }

  async loadModelBytes(
    model: ArrayBuffer,
    name: string,
    preferredBackend: BrowserInferenceBackend = 'webgpu',
  ) {
    this.throwIfUnavailable()
    if (model.byteLength > MAXIMUM_BROWSER_MODEL_BYTES) {
      throw new Error('The browser inference model must not exceed 512 MiB.')
    }
    return this.request<BrowserInferenceModelInfo>(
      { type: 'load-model', model, name, preferredBackend },
      [model],
    )
  }

  async detect(imageUrl: string): Promise<BrowserInferenceDetectedResult> {
    this.throwIfUnavailable()
    const response = await fetch(imageUrl)
    if (!response.ok) throw new Error(`Unable to read the project page (${response.status}).`)
    const mediaType = response.headers.get('content-type')?.split(';')[0] || 'image/png'
    const image = await response.arrayBuffer()
    const sourceSha256 = await sha256(image)
    const result = await this.request<BrowserInferenceResult>({ type: 'detect', image, mediaType }, [image])
    return { ...result, sourceSha256 }
  }

  async dispose() {
    if (this.terminalError) return
    this.failWorker(new Error('Browser inference worker was disposed.'), false)
  }

  get isUsable() {
    return this.terminalError === null
  }

  private request<T extends BrowserInferenceModelInfo | BrowserInferenceResult | undefined>(
    value: WorkerRequestWithoutId,
    transfer: Transferable[] = [],
  ): Promise<T> {
    if (this.terminalError) return Promise.reject(this.terminalError)
    const id = ++this.requestId
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, {
        resolve: result => resolve(result as T),
        reject,
      })
      try {
        this.worker.postMessage({ ...value, id } as BrowserInferenceWorkerRequest, transfer)
      } catch (reason) {
        const error = reason instanceof Error ? reason : new Error(String(reason))
        this.pending.delete(id)
        reject(error)
      }
    })
  }

  private throwIfUnavailable(): void {
    if (this.terminalError) throw this.terminalError
  }

  private handleMessage(message: BrowserInferenceWorkerResponse) {
    if (message.type === 'status') {
      this.onStatus?.(message.status)
      return
    }
    const pending = this.pending.get(message.id)
    if (!pending) return
    this.pending.delete(message.id)
    if (message.type === 'error') {
      const error = new Error(message.message)
      if (message.stack) error.stack = message.stack
      pending.reject(error)
      return
    }
    if (message.type === 'model-loaded') pending.resolve(message.model)
    else if (message.type === 'detected') pending.resolve(message.result)
    else pending.resolve(undefined)
  }

  private failWorker(error: Error, notify = true) {
    if (this.terminalError) return
    this.terminalError = error
    this.worker.onmessage = null
    this.worker.onerror = null
    this.worker.onmessageerror = null
    this.worker.terminate()
    for (const request of this.pending.values()) request.reject(error)
    this.pending.clear()
    if (notify) this.onFatalError?.(error)
  }
}

function createBrowserInferenceWorker(): Worker {
  return new Worker(new URL('./inference.worker.ts', import.meta.url), {
    type: 'module',
    name: 'emt-browser-inference',
  })
}

async function sha256(data: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', data)
  return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('')
}
