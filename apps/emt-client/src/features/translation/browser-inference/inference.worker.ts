/// <reference lib="webworker" />

import * as ort from 'onnxruntime-web/all'
import { prepareCraftInput } from './craft/preprocess'
import { processCraftOutput } from './craft/postprocess'
import type {
  BrowserInferenceBackend,
  BrowserInferenceResult,
  BrowserInferenceStatus,
  BrowserInferenceWorkerRequest,
  BrowserInferenceWorkerResponse,
} from './contracts'

const workerScope = self as DedicatedWorkerGlobalScope

let session: ort.InferenceSession | null = null
let modelBytes: Uint8Array | null = null
let modelSha256 = ''
let modelLoadMs = 0
let backend: BrowserInferenceBackend | null = null
let fallbackReason: string | null = null
let inputName = ''
let scoreOutputName = ''

ort.env.wasm.proxy = false
ort.env.wasm.simd = true
ort.env.wasm.numThreads = workerScope.crossOriginIsolated
  ? Math.min(4, Math.max(1, Math.ceil((navigator.hardwareConcurrency || 1) / 2)))
  : 1

let messageQueue = Promise.resolve()

workerScope.onmessage = event => {
  const request = event.data as BrowserInferenceWorkerRequest
  messageQueue = messageQueue.then(
    () => handleMessage(request),
    () => handleMessage(request),
  )
}

async function handleMessage(request: BrowserInferenceWorkerRequest) {
  try {
    if (request.type === 'load-model') {
      const model = await loadModel(request.id, new Uint8Array(request.model), request.name, request.preferredBackend)
      post({ id: request.id, type: 'model-loaded', model })
      return
    }
    if (request.type === 'detect') {
      const result = await detect(request.id, request.image, request.mediaType)
      post({ id: request.id, type: 'detected', result }, [result.mask.buffer])
      return
    }
    await bestEffortReleaseSession()
    modelBytes = null
    post({ id: request.id, type: 'disposed' })
  } catch (reason) {
    const error = reason instanceof Error ? reason : new Error(String(reason))
    status(request.id, 'failed', error.message)
    post({ id: request.id, type: 'error', message: error.message, stack: error.stack })
  }
}

async function loadModel(id: number, model: Uint8Array, name: string, preferredBackend: BrowserInferenceBackend) {
  status(id, 'loading-model', '正在验证并加载 CRAFT ONNX 模型。')
  await bestEffortReleaseSession()
  modelBytes = model
  try {
    modelSha256 = await sha256(model)
    fallbackReason = null
    const startedAt = performance.now()
    if (preferredBackend === 'webgpu') {
      try {
        await createSession('webgpu')
      } catch (reason) {
        fallbackReason = readableError(reason)
        status(id, 'loading-model', 'WebGPU 初始化失败，正在回退到 WASM。', 'wasm', fallbackReason)
        await bestEffortReleaseSession()
        await createSession('wasm')
      }
    } else {
      await createSession('wasm')
    }
    modelLoadMs = performance.now() - startedAt
    status(id, 'ready', `模型已由 ${backend?.toUpperCase()} 加载。`, backend, fallbackReason)
    return {
      name,
      size: model.byteLength,
      sha256: modelSha256,
      backend: backend!,
      loadMs: modelLoadMs,
      fallbackReason,
    }
  } catch (reason) {
    await bestEffortReleaseSession()
    modelBytes = null
    throw reason
  }
}

async function detect(id: number, encodedImage: ArrayBuffer, mediaType: string): Promise<BrowserInferenceResult> {
  if (!session || !modelBytes || !backend) throw new Error('请先选择并加载 CRAFT ONNX 模型。')
  const totalStartedAt = performance.now()
  status(id, 'preprocessing', '正在浏览器 Worker 中解码和预处理页面。', backend, fallbackReason)
  const preprocessStartedAt = performance.now()
  const prepared = await prepareCraftInput(encodedImage, mediaType)
  const preprocessMs = performance.now() - preprocessStartedAt
  status(id, 'running', `正在使用 ${backend.toUpperCase()} 运行 CRAFT。`, backend, fallbackReason)
  const inferenceStartedAt = performance.now()
  let output: ort.Tensor
  try {
    output = await runSession(prepared.tensor, prepared.tensorShape)
  } catch (reason) {
    if (backend !== 'webgpu') throw reason
    const runFailure = readableError(reason)
    fallbackReason = `WebGPU 运行失败：${runFailure}`
    status(id, 'running', 'WebGPU 运行失败，正在用纯 WASM 会话重试。', 'wasm', fallbackReason)
    try {
      await releaseSession()
    } catch (releaseReason) {
      fallbackReason += `；释放 WebGPU 会话时也发生错误：${readableError(releaseReason)}`
    }
    await createSession('wasm')
    output = await runSession(prepared.tensor, prepared.tensorShape)
  }
  const inferenceMs = performance.now() - inferenceStartedAt
  status(id, 'postprocessing', '正在生成文本区域与二值 Mask。', backend, fallbackReason)
  const postprocessStartedAt = performance.now()
  if (!(output.data instanceof Float32Array)) throw new Error('CRAFT score output must be float32.')
  const processed = processCraftOutput(prepared, output.data, output.dims)
  const postprocessMs = performance.now() - postprocessStartedAt
  const result: BrowserInferenceResult = {
    ...processed,
    backend: backend!,
    runtimeVersion: 'onnxruntime-web/1.27.0',
    modelSha256,
    timings: {
      modelLoadMs,
      preprocessMs,
      inferenceMs,
      postprocessMs,
      totalMs: performance.now() - totalStartedAt,
    },
    fallbackReason,
  }
  status(id, 'completed', `完成：${result.regions.length} 个区域。`, backend, fallbackReason)
  return result
}

async function createSession(nextBackend: BrowserInferenceBackend) {
  if (!modelBytes) throw new Error('Model bytes are unavailable.')
  const options: ort.InferenceSession.SessionOptions = {
    executionProviders: nextBackend === 'webgpu'
      ? [{ name: 'webgpu', preferredLayout: 'NCHW' }]
      : ['wasm'],
    graphOptimizationLevel: 'all',
    executionMode: 'sequential',
  }
  session = await ort.InferenceSession.create(modelBytes, options)
  validateModelContract(session)
  backend = nextBackend
}

function validateModelContract(activeSession: ort.InferenceSession) {
  const inputs = activeSession.inputMetadata.filter(item =>
    item.isTensor && item.type === 'float32' && item.shape.length === 4 && item.shape[1] === 3)
  if (inputs.length !== 1) throw new Error('CRAFT 模型必须有且仅有一个三通道 float32 NCHW 输入。')
  const outputs = activeSession.outputMetadata.filter(item =>
    item.isTensor && item.type === 'float32' && item.shape.length === 4
      && ((item.shape[1] === 2 && item.shape[3] !== 2) || (item.shape[3] === 2 && item.shape[1] !== 2)))
  if (outputs.length !== 1) throw new Error('CRAFT 模型必须有且仅有一个静态双通道 score 输出。')
  inputName = inputs[0].name
  scoreOutputName = outputs[0].name
}

async function runSession(values: Float32Array, shape: readonly number[]) {
  if (!session) throw new Error('Inference session is unavailable.')
  const outputs = await session.run({ [inputName]: new ort.Tensor('float32', values, shape) }, [scoreOutputName])
  const output = outputs[scoreOutputName]
  if (!(output instanceof ort.Tensor)) throw new Error('CRAFT score output is missing.')
  return output
}

async function releaseSession() {
  if (!session) {
    backend = null
    inputName = ''
    scoreOutputName = ''
    return
  }
  const previous = session
  session = null
  backend = null
  inputName = ''
  scoreOutputName = ''
  await previous.release()
}

async function bestEffortReleaseSession() {
  try {
    await releaseSession()
  } catch {
    // The worker is replacing or discarding this session anyway. A failed
    // provider release must not prevent the independent WASM fallback.
  }
}

function status(
  id: number,
  stage: BrowserInferenceStatus['stage'],
  message: string,
  selectedBackend: BrowserInferenceBackend | null = backend,
  reason: string | null = fallbackReason,
) {
  post({ id, type: 'status', status: { stage, message, backend: selectedBackend, fallbackReason: reason } })
}

function post(message: BrowserInferenceWorkerResponse, transfer: Transferable[] = []) {
  workerScope.postMessage(message, transfer)
}

async function sha256(data: Uint8Array) {
  const digest = await crypto.subtle.digest('SHA-256', data.slice().buffer)
  return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('')
}

function readableError(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason)
}

export {}
