import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react'
import { Cpu, Download, FlaskConical, Gauge, RefreshCw, Upload } from 'lucide-react'
import { api } from '../../../api'
import type { ProjectPage } from '../../../contracts'
import { emtHost } from '../../../host'
import { assetUrl } from '../../../shared/lib/assets'
import { BrowserTextMaskDetector } from './client'
import type {
  BrowserInferenceBackend,
  BrowserInferenceDetectedResult,
  BrowserInferenceModelInfo,
  BrowserInferenceStatus,
  BrowserDetectionUploadResponse,
} from './contracts'
import { CRAFT_ALGORITHM_VERSION, createCraftOptionsFingerprint } from './craft'
import { createMaskOverlayUrl, downloadDetectionJson, downloadMaskPng, encodeMaskPng } from './maskPreview'
import { detectBrowserInferenceCapabilities } from './runtimeCapabilities'

export type BrowserInferencePanelHandle = {
  preparePages: (pages: readonly ProjectPage[]) => Promise<void>
}

export const BrowserInferencePanel = forwardRef<BrowserInferencePanelHandle, {
  projectId: string
  page: ProjectPage
  imageUrl: string
  locale: 'zh-CN' | 'en-US'
  disabled: boolean
  onError: (message: string | null) => void
}>(({ projectId, page, imageUrl, locale, disabled, onError }, ref) => {
  const capabilities = useMemo(detectBrowserInferenceCapabilities, [])
  const [preferredBackend, setPreferredBackend] = useState<BrowserInferenceBackend>('webgpu')
  const [model, setModel] = useState<BrowserInferenceModelInfo | null>(null)
  const [status, setStatus] = useState<BrowserInferenceStatus>({
    stage: 'idle', message: locale === 'en-US' ? 'The CRAFT model will load automatically when needed.' : 'CRAFT 模型将在需要时自动加载。', backend: null, fallbackReason: null,
  })
  const [result, setResult] = useState<BrowserInferenceDetectedResult | null>(null)
  const [overlayUrl, setOverlayUrl] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [saving, setSaving] = useState(false)
  const [savedArtifact, setSavedArtifact] = useState<BrowserDetectionUploadResponse | null>(null)
  const [batchProgress, setBatchProgress] = useState<{ completed: number; total: number; pageIndex: number } | null>(null)
  const detectorRef = useRef<BrowserTextMaskDetector | null>(null)
  const modelRef = useRef<BrowserInferenceModelInfo | null>(null)
  const modelLoadPromiseRef = useRef<Promise<BrowserInferenceModelInfo> | null>(null)
  const operationInProgressRef = useRef(false)
  const detailsRef = useRef<HTMLDetailsElement | null>(null)
  const overlayUrlRef = useRef<string | null>(null)
  const generationRef = useRef(0)
  const pageContextKeyRef = useRef(`${projectId}\n${page.pageId}`)
  const localeRef = useRef(locale)
  const onErrorRef = useRef(onError)
  const pageContextRef = useRef({ projectId, pageId: page.pageId })
  localeRef.current = locale
  onErrorRef.current = onError
  pageContextRef.current = { projectId, pageId: page.pageId }

  const clearResult = useCallback(() => {
    setResult(null)
    setSavedArtifact(null)
    setOverlayUrl(current => {
      if (current) URL.revokeObjectURL(current)
      overlayUrlRef.current = null
      return null
    })
  }, [])

  const createDetector = useCallback(() => {
    let detector: BrowserTextMaskDetector
    detector = new BrowserTextMaskDetector(
      nextStatus => {
        if (detectorRef.current === detector) setStatus(nextStatus)
      },
      undefined,
      reason => {
        if (detectorRef.current !== detector) return
        detectorRef.current = null
        modelRef.current = null
        modelLoadPromiseRef.current = null
        generationRef.current += 1
        setModel(null)
        clearResult()
        setBusy(false)
        setSaving(false)
        const message = localeRef.current === 'en-US'
          ? `Browser inference runtime stopped: ${reason.message}`
          : `浏览器推理运行时已停止：${reason.message}`
        setStatus({ stage: 'failed', message, backend: null, fallbackReason: null })
        onErrorRef.current(message)
      },
    )
    detectorRef.current = detector
    return detector
  }, [clearResult])

  const restartRuntime = useCallback(() => {
    generationRef.current += 1
    void detectorRef.current?.dispose()
    detectorRef.current = null
    modelRef.current = null
    modelLoadPromiseRef.current = null
    setModel(null)
    clearResult()
    setBusy(false)
    onErrorRef.current(null)
    createDetector()
    setStatus({
      stage: 'idle',
      message: localeRef.current === 'en-US'
        ? 'Runtime restarted. The model will reload automatically when needed.'
        : '运行时已重建，模型将在需要时自动重新加载。',
      backend: null,
      fallbackReason: null,
    })
  }, [clearResult, createDetector])

  useEffect(() => {
    createDetector()
    return () => {
      const detector = detectorRef.current
      detectorRef.current = null
      if (overlayUrlRef.current) URL.revokeObjectURL(overlayUrlRef.current)
      void detector?.dispose()
    }
  }, [createDetector])

  useEffect(() => {
    const contextKey = `${projectId}\n${page.pageId}`
    if (pageContextKeyRef.current === contextKey) return
    pageContextKeyRef.current = contextKey
    clearResult()
    onErrorRef.current(null)
  }, [clearResult, page.pageId, projectId])

  const ensureModelLoaded = useCallback(async () => {
    const loaded = modelRef.current
    if (loaded && detectorRef.current?.isUsable) return loaded
    if (modelLoadPromiseRef.current) return modelLoadPromiseRef.current
    if (!emtHost?.loadLocalModel) {
      throw new Error(localeRef.current === 'en-US'
        ? 'Automatic Browser CRAFT model loading is unavailable in this host.'
        : '当前宿主不支持自动加载浏览器 CRAFT 模型。')
    }
    const detector = detectorRef.current?.isUsable
      ? detectorRef.current
      : createDetector()
    const generation = generationRef.current
    setModel(null)
    modelRef.current = null
    clearResult()
    setStatus({
      stage: 'loading-model',
      message: localeRef.current === 'en-US'
        ? 'Loading the installed CRAFT model…'
        : '正在自动加载已安装的 CRAFT 模型…',
      backend: null,
      fallbackReason: null,
    })
    const loading = (async () => {
      const localModel = await emtHost.loadLocalModel('craft')
      const bytes = normalizeModelBytes(localModel.bytes)
      const next = await detector.loadModelBytes(bytes, localModel.name, preferredBackend)
      if (generation !== generationRef.current) throw new Error(localeRef.current === 'en-US' ? 'Model loading was cancelled.' : '模型加载已取消。')
      modelRef.current = next
      setModel(next)
      setResult(null)
      return next
    })()
    modelLoadPromiseRef.current = loading
    try {
      return await loading
    } catch (reason) {
      if (generation === generationRef.current) {
        setStatus({ stage: 'failed', message: readError(reason), backend: null, fallbackReason: null })
      }
      throw reason
    } finally {
      if (modelLoadPromiseRef.current === loading) modelLoadPromiseRef.current = null
    }
  }, [clearResult, createDetector, preferredBackend])

  async function run() {
    if (busy || saving || disabled || operationInProgressRef.current) return
    operationInProgressRef.current = true
    const generation = ++generationRef.current
    const target = pageContextRef.current
    setBusy(true)
    onError(null)
    try {
      await ensureModelLoaded()
      const detector = detectorRef.current
      if (!detector?.isUsable) throw new Error(localeRef.current === 'en-US' ? 'Browser inference runtime is unavailable.' : '浏览器推理运行时不可用。')
      const next = await detector.detect(imageUrl)
      if (generation !== generationRef.current || !isCurrentPage(target)) return
      setResult(next)
      setSavedArtifact(null)
      const nextOverlay = await createMaskOverlayUrl(next.width, next.height, next.mask, next.regions)
      if (generation !== generationRef.current || !isCurrentPage(target)) {
        URL.revokeObjectURL(nextOverlay)
        return
      }
      setOverlayUrl(current => { if (current) URL.revokeObjectURL(current); overlayUrlRef.current = nextOverlay; return nextOverlay })
    } catch (reason) {
      if (generation === generationRef.current && isCurrentPage(target)) onError(readError(reason))
    } finally {
      operationInProgressRef.current = false
      if (generation === generationRef.current) setBusy(false)
    }
  }

  async function saveToProject() {
    if (!result || saving || busy || disabled) return
    const generation = generationRef.current
    const target = pageContextRef.current
    setSaving(true)
    onError(null)
    try {
      const [mask, optionsFingerprint] = await Promise.all([
        encodeMaskPng(result.width, result.height, result.mask),
        createCraftOptionsFingerprint(),
      ])
      const saved = await api.saveBrowserDetection(target.projectId, target.pageId, {
        schemaVersion: '1.0',
        detectorId: 'craft',
        modelSha256: result.modelSha256,
        sourceSha256: result.sourceSha256,
        algorithmVersion: CRAFT_ALGORITHM_VERSION,
        optionsFingerprint,
        width: result.width,
        height: result.height,
        backend: result.backend,
        runtimeVersion: result.runtimeVersion,
        regions: result.regions,
      }, mask)
      if (generation === generationRef.current && isCurrentPage(target)) {
        setSavedArtifact(saved)
      }
    } catch (reason) {
      if (generation === generationRef.current && isCurrentPage(target)) {
        onError(readError(reason))
      }
    } finally {
      if (generation === generationRef.current) setSaving(false)
    }
  }

  useImperativeHandle(ref, () => ({
    preparePages: async pages => {
      if (operationInProgressRef.current) {
        throw new Error(localeRef.current === 'en-US'
          ? 'Browser CRAFT is already processing pages.'
          : '浏览器 CRAFT 正在处理页面，请等待当前操作完成。')
      }

      const targets = pages.filter(item => item.includedInWorkflow)
      if (targets.length === 0) return
      operationInProgressRef.current = true
      const generation = ++generationRef.current
      detailsRef.current?.setAttribute('open', '')
      clearResult()
      setBusy(true)
      setBatchProgress({ completed: 0, total: targets.length, pageIndex: targets[0].index })
      onErrorRef.current(null)
      try {
        const loadedModel = await ensureModelLoaded()
        const detector = detectorRef.current
        if (!detector?.isUsable) throw new Error(localeRef.current === 'en-US' ? 'Browser inference runtime is unavailable.' : '浏览器推理运行时不可用。')
        const optionsFingerprint = await createCraftOptionsFingerprint()
        for (let index = 0; index < targets.length; index++) {
          const target = targets[index]
          setBatchProgress({ completed: index, total: targets.length, pageIndex: target.index })
          const detected = await detector.detect(assetUrl(target.originalAssetUrl))
          await saveDetectionResult(projectId, target.pageId, detected, optionsFingerprint)
          setBatchProgress({ completed: index + 1, total: targets.length, pageIndex: target.index })
        }
        if (generation === generationRef.current) {
          setStatus({
            stage: 'completed',
            message: localeRef.current === 'en-US'
              ? `Prepared ${targets.length} pages. Starting translation…`
              : `已准备 ${targets.length} 页浏览器检测产物，正在启动翻译…`,
            backend: loadedModel.backend,
            fallbackReason: loadedModel.fallbackReason,
          })
        }
      } finally {
        operationInProgressRef.current = false
        if (generation === generationRef.current) {
          setBusy(false)
          setBatchProgress(null)
        }
      }
    },
  }), [clearResult, ensureModelLoaded, projectId])

  function isCurrentPage(target: { projectId: string; pageId: string }) {
    return target.projectId === pageContextRef.current.projectId
      && target.pageId === pageContextRef.current.pageId
  }

  const t = (zh: string, en: string) => locale === 'en-US' ? en : zh
  return <details ref={detailsRef} className="browser-inference-panel">
    <summary><FlaskConical size={14}/><span><b>{t('浏览器本地推理实验', 'Browser local inference lab')}</b><small>{t('CRAFT · WebGPU 优先，WASM 自动回退', 'CRAFT · WebGPU first with automatic WASM fallback')}</small></span><em>Experimental</em></summary>
    <div className="browser-inference-body">
      <p>{t('CRAFT 模型会在首次使用时从应用模型目录自动加载，只在当前应用的 Worker 内读取，不会上传。运行翻译时会自动检测并保存所需页面的 Mask 与区域信息。', 'CRAFT loads automatically from the application model directory on first use, stays inside this app worker, and is never uploaded. Starting translation automatically detects and saves the required masks and regions.')}</p>
      <div className="browser-inference-controls">
        <label><span>{t('首选后端（切换后将在下次使用时自动重载）', 'Preferred backend (reloads automatically on next use)')}</span><select value={preferredBackend} disabled={busy || saving || disabled} onChange={event => { generationRef.current += 1; void detectorRef.current?.dispose(); detectorRef.current = null; modelRef.current = null; modelLoadPromiseRef.current = null; setPreferredBackend(event.target.value as BrowserInferenceBackend); setModel(null); clearResult(); setStatus({ stage: 'idle', message: t('模型将在下次使用时为新后端自动加载。', 'The model will load automatically for the new backend on next use.'), backend: null, fallbackReason: null }) }}><option value="webgpu">WebGPU → WASM</option><option value="wasm">WASM only</option></select></label>
        <div className="browser-model-auto"><span>{t('模型', 'Model')}</span><b>{model ? model.name : t('按需自动加载', 'Automatic on demand')}</b></div>
        <button className="primary-small" disabled={busy || saving || disabled} onClick={() => void run()}><Gauge size={14}/>{busy ? t('运行中…', 'Running…') : t('检测当前页', 'Detect current page')}</button>
        {!detectorRef.current && <button disabled={saving || disabled} onClick={restartRuntime}><RefreshCw size={14}/>{t('重建推理运行时', 'Restart inference runtime')}</button>}
      </div>
      <div className="browser-inference-capabilities">
        <span className={capabilities.webGpuAvailable ? 'available' : 'unavailable'}><Cpu size={13}/>WebGPU {capabilities.webGpuAvailable ? '✓' : '—'}</span>
        <span>WASM {capabilities.wasmAvailable ? '✓' : '—'}</span>
        <span>{t('线程', 'Threads')} {capabilities.wasmThreads}</span>
        <span>{capabilities.crossOriginIsolated ? 'crossOriginIsolated' : t('非隔离模式', 'non-isolated')}</span>
      </div>
      <div className={`browser-inference-status ${status.stage}`}><b>{status.backend?.toUpperCase() ?? '—'}</b><span>{status.message}</span></div>
      {batchProgress && <div className="browser-inference-batch-progress"><progress value={batchProgress.completed} max={batchProgress.total}/><span>{t(`正在准备第 ${batchProgress.pageIndex} 页 · ${batchProgress.completed}/${batchProgress.total}`, `Preparing page ${batchProgress.pageIndex} · ${batchProgress.completed}/${batchProgress.total}`)}</span></div>}
      {status.fallbackReason && <small className="browser-inference-fallback">{t('回退原因：', 'Fallback: ')}{status.fallbackReason}</small>}
      {model && <dl className="browser-inference-metrics"><div><dt>Model</dt><dd>{(model.size / 1024 / 1024).toFixed(1)} MB</dd></div><div><dt>SHA-256</dt><dd title={model.sha256}>{model.sha256.slice(0, 16)}</dd></div><div><dt>{t('加载', 'Load')}</dt><dd>{model.loadMs.toFixed(0)} ms</dd></div></dl>}
      {result && <>
        <dl className="browser-inference-metrics"><div><dt>{t('区域', 'Regions')}</dt><dd>{result.regions.length}</dd></div><div><dt>{t('推理', 'Inference')}</dt><dd>{result.timings.inferenceMs.toFixed(0)} ms</dd></div><div><dt>{t('后处理', 'Postprocess')}</dt><dd>{result.timings.postprocessMs.toFixed(0)} ms</dd></div><div><dt>{t('总计', 'Total')}</dt><dd>{result.timings.totalMs.toFixed(0)} ms</dd></div></dl>
        {overlayUrl && <div className="browser-inference-preview"><img src={imageUrl} alt=""/><img className="mask-overlay" src={overlayUrl} alt={t('Mask 叠加预览', 'Mask overlay preview')}/></div>}
        <div className="browser-inference-downloads"><button onClick={() => void downloadMaskPng(result.width, result.height, result.mask, `${page.fileName}.craft-mask.png`)}><Download size={13}/>{t('下载 Mask', 'Download mask')}</button><button onClick={() => downloadDetectionJson({ ...result, mask: undefined }, `${page.fileName}.craft-detection.json`)}><Download size={13}/>{t('下载区域 JSON', 'Download regions JSON')}</button></div>
        <button className="primary-small browser-inference-save" disabled={saving || busy || disabled} onClick={() => void saveToProject()}><Upload size={13}/>{saving ? t('正在保存并生成底图…', 'Saving and generating clean image…') : t('保存到项目', 'Save to project')}</button>
        {savedArtifact && <small className="browser-inference-save-note saved">{t(`已保存 ${savedArtifact.regionCount} 个区域，可由后续翻译工作流复用。`, `Saved ${savedArtifact.regionCount} regions for the translation workflow.`)}</small>}
      </>}
    </div>
  </details>
})

async function saveDetectionResult(
  projectId: string,
  pageId: string,
  result: BrowserInferenceDetectedResult,
  optionsFingerprint: string,
) {
  const mask = await encodeMaskPng(result.width, result.height, result.mask)
  return api.saveBrowserDetection(projectId, pageId, {
    schemaVersion: '1.0',
    detectorId: 'craft',
    modelSha256: result.modelSha256,
    sourceSha256: result.sourceSha256,
    algorithmVersion: CRAFT_ALGORITHM_VERSION,
    optionsFingerprint,
    width: result.width,
    height: result.height,
    backend: result.backend,
    runtimeVersion: result.runtimeVersion,
    regions: result.regions,
  }, mask)
}

function normalizeModelBytes(bytes: ArrayBuffer | Uint8Array) {
  if (bytes instanceof ArrayBuffer) return bytes
  return bytes.slice().buffer
}

function readError(reason: unknown) {
  return reason instanceof Error ? reason.message : String(reason)
}
