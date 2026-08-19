import { describe, expect, it } from 'vitest'
import { BrowserTextMaskDetector, MAXIMUM_BROWSER_MODEL_BYTES } from './client'
import type {
  BrowserInferenceWorkerRequest,
  BrowserInferenceWorkerResponse,
} from './contracts'

describe('BrowserTextMaskDetector worker lifecycle', () => {
  it('rejects an oversized model before reading or posting it', async () => {
    const worker = new FakeWorker()
    const detector = new BrowserTextMaskDetector(undefined, () => worker as unknown as Worker)
    const oversized = {
      name: 'too-large.onnx',
      size: MAXIMUM_BROWSER_MODEL_BYTES + 1,
      arrayBuffer: () => Promise.reject(new Error('must not read')),
    } as File

    await expect(detector.loadModel(oversized)).rejects.toThrow('512 MiB')
    expect(worker.requests).toHaveLength(0)
    await detector.dispose()
  })

  it('loads model bytes supplied by the desktop host', async () => {
    const worker = new FakeWorker()
    const detector = new BrowserTextMaskDetector(undefined, () => worker as unknown as Worker)
    const loading = detector.loadModelBytes(new Uint8Array([1, 2, 3]).buffer, 'craft.onnx', 'wasm')
    await waitForRequests(worker, 1)

    expect(worker.requests[0]).toMatchObject({ type: 'load-model', name: 'craft.onnx', preferredBackend: 'wasm' })
    worker.emit({
      id: 1,
      type: 'model-loaded',
      model: {
        name: 'craft.onnx',
        size: 3,
        sha256: 'abc',
        backend: 'wasm',
        loadMs: 1,
        fallbackReason: null,
      },
    })
    await expect(loading).resolves.toMatchObject({ name: 'craft.onnx', backend: 'wasm' })
    await detector.dispose()
  })

  it('terminates immediately on dispose and rejects every pending request', async () => {
    const worker = new FakeWorker()
    const fatalErrors: Error[] = []
    const detector = new BrowserTextMaskDetector(
      undefined,
      () => worker as unknown as Worker,
      error => fatalErrors.push(error),
    )
    const firstLoad = detector.loadModel(new File([new Uint8Array([1, 2, 3])], 'craft-a.onnx'))
    const secondLoad = detector.loadModel(new File([new Uint8Array([4, 5, 6])], 'craft-b.onnx'))
    await waitForRequests(worker, 2)

    await detector.dispose()

    await expect(firstLoad).rejects.toThrow('disposed')
    await expect(secondLoad).rejects.toThrow('disposed')
    expect(worker.terminated).toBe(true)
    expect(fatalErrors).toHaveLength(0)
    expect(worker.requests).toHaveLength(2)
    await expect(detector.detect('data:image/png;base64,AA==')).rejects.toThrow('disposed')
    await expect(detector.loadModel(new File([new Uint8Array([1])], 'craft.onnx')))
      .rejects.toThrow('disposed')
  })

  it.each([
    ['error', 'crashed'],
    ['messageerror', 'unreadable'],
  ] as const)('becomes permanently invalid after a worker %s', async (kind, expectedMessage) => {
    const worker = new FakeWorker()
    const fatalErrors: Error[] = []
    const detector = new BrowserTextMaskDetector(
      undefined,
      () => worker as unknown as Worker,
      error => fatalErrors.push(error),
    )
    const pending = detector.loadModel(new File([new Uint8Array([1])], 'craft.onnx'))
    await waitForRequests(worker, 1)

    if (kind === 'error') worker.emitError('worker crashed')
    else worker.emitMessageError()

    await expect(pending).rejects.toThrow(expectedMessage)
    await expect(detector.loadModel(new File([new Uint8Array([2])], 'craft.onnx')))
      .rejects.toThrow(expectedMessage)
    await expect(detector.detect('this URL must never be fetched'))
      .rejects.toThrow(expectedMessage)
    expect(worker.terminated).toBe(true)
    expect(worker.requests).toHaveLength(1)
    expect(fatalErrors.map(error => error.message)).toEqual([expect.stringContaining(expectedMessage)])

    const replacementWorker = new FakeWorker()
    const replacement = new BrowserTextMaskDetector(undefined, () => replacementWorker as unknown as Worker)
    const recovered = replacement.loadModel(new File([new Uint8Array([3])], 'craft.onnx'))
    await waitForRequests(replacementWorker, 1)
    replacementWorker.emit({
      id: 1,
      type: 'model-loaded',
      model: {
        name: 'craft.onnx',
        size: 1,
        sha256: 'abc',
        backend: 'wasm',
        loadMs: 1,
        fallbackReason: null,
      },
    })
    await expect(recovered).resolves.toMatchObject({ backend: 'wasm' })
    await replacement.dispose()
  })
})

class FakeWorker {
  onmessage: ((event: MessageEvent<BrowserInferenceWorkerResponse>) => void) | null = null
  onerror: ((event: ErrorEvent) => void) | null = null
  onmessageerror: ((event: MessageEvent) => void) | null = null
  readonly requests: BrowserInferenceWorkerRequest[] = []
  terminated = false

  postMessage(request: BrowserInferenceWorkerRequest): void {
    this.requests.push(request)
  }

  terminate(): void {
    this.terminated = true
  }

  emit(response: BrowserInferenceWorkerResponse): void {
    this.onmessage?.({ data: response } as MessageEvent<BrowserInferenceWorkerResponse>)
  }

  emitError(message: string): void {
    this.onerror?.({ message } as ErrorEvent)
  }

  emitMessageError(): void {
    this.onmessageerror?.({} as MessageEvent)
  }
}

async function waitForRequests(worker: FakeWorker, expected: number): Promise<void> {
  for (let attempt = 0; attempt < 10 && worker.requests.length < expected; attempt += 1) {
    await new Promise<void>((resolve) => setTimeout(resolve, 0))
  }
  expect(worker.requests).toHaveLength(expected)
}
