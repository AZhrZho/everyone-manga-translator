import { useCallback } from 'react'
import { RefreshCw } from 'lucide-react'
import type { ProjectPage } from '../../../contracts'
import { readLocale, t } from '../../../i18n'
import { assetUrl } from '../../../shared/lib/assets'
import { logBackgroundError } from '../../../shared/lib/errors'
import type { PreviewLayer, ViewportState } from '../../../shared/types/workspace'
import { useViewportSurface } from '../../../shared/viewport/useViewportSurface'
import { useBufferedImageSource } from '../../../shared/viewport/useBufferedImageSource'
import type { LayoutDocument, TextClip } from '../../editor/model/layout'

export function PreviewPane({ page, layer, layout, clips, viewport, spacePan, refreshNonce, generating, onViewportChange, onFitScaleChange }: {
  page: ProjectPage
  layer: PreviewLayer
  layout: LayoutDocument | null
  clips: Record<string, TextClip>
  viewport: ViewportState
  spacePan: boolean
  refreshNonce: number
  generating: boolean
  onViewportChange: (next: ViewportState) => void
  onFitScaleChange: (fitScale: number) => void
}) {
  const locale = readLocale()
  const surface = useViewportSurface(page, viewport, onViewportChange, onFitScaleChange, spacePan)
  const hasLayoutOverlay = layer === 'original-layout'
  const showGeneratingOverlay = generating && (layer === 'original-layout' || layer === 'result')
  const imageUrl = layer === 'result'
    ? page.resultAssetUrl
    : layer === 'clean'
      ? page.cleanAssetUrl
      : page.originalAssetUrl
  const resolvedImageUrl = `${assetUrl(imageUrl)}?revision=${page.revision}&refresh=${refreshNonce}`
  const reportLoadError = useCallback(() => logBackgroundError('preview-image.load', new Error(`Unable to load ${layer} preview image.`), {
    pageId: page.pageId,
    pageIndex: page.index,
    fileName: page.fileName,
    layer,
    url: resolvedImageUrl,
    revision: page.revision,
    refreshNonce,
  }), [layer, page.fileName, page.index, page.pageId, page.revision, refreshNonce, resolvedImageUrl])
  const displayedImageUrl = useBufferedImageSource(page.pageId, resolvedImageUrl, reportLoadError)
  return (
    <div className="preview-pane">
      <div className="preview-pane-head"><span>{t(locale, '页面')} {page.index.toString().padStart(3, '0')}</span></div>
      <div className={`preview-canvas ${spacePan ? 'space-pan' : ''}`} ref={surface.hostRef} onWheel={surface.onWheel} onPointerDown={surface.onPointerDown}><div className="preview-image-stage viewport-stage" style={surface.stageStyle}><img src={displayedImageUrl} alt={page.fileName}/>{hasLayoutOverlay && layout?.segments.map(segment => {
        const box = segment.layout.text_box
        const clip = clips[segment.segment_id]
        return clip ? <img className="preview-layout-clip" key={segment.segment_id} src={clip.url} alt="" style={{ left: box.x0 * surface.scale, top: box.y0 * surface.scale, width: (box.x1 - box.x0) * surface.scale, height: (box.y1 - box.y0) * surface.scale, transform: `rotate(${segment.layout.rotation?.angle_deg ?? 0}deg)` }}/> : null
      })}</div>{showGeneratingOverlay && <div className="page-generating-overlay" role="status" aria-live="polite"><RefreshCw className="page-generating-spinner" size={28}/><b>{locale === 'en-US' ? 'Generating' : '正在生成'}</b><span>{locale === 'en-US' ? 'The preview will update automatically when this page is ready.' : '本页完成后预览会自动更新'}</span></div>}</div>
    </div>
  )
}
