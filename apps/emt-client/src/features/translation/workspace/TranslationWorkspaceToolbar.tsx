import { FileImage, PanelsTopLeft, Plus, Redo2, Trash2, Undo2 } from 'lucide-react'
import { t, type Locale } from '../../../i18n'
import type { PreviewLayer, ViewportState, WorkspaceMode } from '../../../shared/types/workspace'

type SaveState = 'saved' | 'dirty' | 'saving' | 'error'

type TranslationWorkspaceToolbarProps = {
  locale: Locale
  mode: WorkspaceMode
  split: boolean
  leftLayer: PreviewLayer
  rightLayer: PreviewLayer
  viewport: ViewportState
  showOriginalTemporarily: boolean
  canUndoLayout: boolean
  canRedoLayout: boolean
  hasSelectedSegment: boolean
  layoutSaveState: SaveState
  canUndoImageEdit: boolean
  canRedoImageEdit: boolean
  imageEditSize: number
  canResetImageEdit: boolean
  imageEditSaveState: SaveState
  canAddSegment: boolean
  onSplitToggle: () => void
  onLeftLayerChange: (layer: PreviewLayer) => void
  onRightLayerChange: (layer: PreviewLayer) => void
  onViewportChange: (viewport: ViewportState) => void
  onActualPixels: () => void
  onShowOriginalChange: (show: boolean) => void
  onUndoLayout: () => void
  onRedoLayout: () => void
  onDeleteSegment: () => void
  onUndoImageEdit: () => void
  onRedoImageEdit: () => void
  onImageEditSizeChange: (size: number) => void
  onResetImageEdit: () => void
  onAddSegment: () => void
}

export function TranslationWorkspaceToolbar(props: TranslationWorkspaceToolbarProps) {
  const { locale, mode, split, leftLayer, rightLayer, viewport, showOriginalTemporarily, canUndoLayout, canRedoLayout, hasSelectedSegment, layoutSaveState, canUndoImageEdit, canRedoImageEdit, imageEditSize, canResetImageEdit, imageEditSaveState, canAddSegment } = props
  const saveText = (state: SaveState) => t(locale, state === 'saving' ? '保存中…' : state === 'dirty' ? '有未保存修改' : state === 'error' ? '保存失败' : '所有修改已保存')
  return <div className="translation-toolbar">
    <span className="toolbar-title">{t(locale, mode === 'preview' ? '预览' : mode === 'layout' ? '文本布局' : '底图编辑')}</span>
    {mode === 'preview' && <div className={`preview-view-switcher ${split ? 'split' : 'single'}`}><select aria-label={locale === 'en-US' ? 'Left view type' : '左侧视图类型'} value={leftLayer} onChange={event => props.onLeftLayerChange(event.target.value as PreviewLayer)}><option value="original">{t(locale, '原图')}</option><option value="clean">{t(locale, '底图')}</option><option value="original-layout">{t(locale, '原图叠加渲染')}</option><option value="result">{t(locale, '底图叠加渲染（结果图）')}</option></select><button className={split ? 'active' : ''} onClick={props.onSplitToggle} title={locale === 'en-US' ? (split ? 'Switch to single view' : 'Switch to split view') : (split ? '切换为单视图' : '切换为分割视图')} aria-label={locale === 'en-US' ? (split ? 'Single view' : 'Split view') : (split ? '单视图' : '分割视图')}><PanelsTopLeft size={15}/></button>{split && <select aria-label={locale === 'en-US' ? 'Right view type' : '右侧视图类型'} value={rightLayer} onChange={event => props.onRightLayerChange(event.target.value as PreviewLayer)}><option value="original">{t(locale, '原图')}</option><option value="clean">{t(locale, '底图')}</option><option value="original-layout">{t(locale, '原图叠加渲染')}</option><option value="result">{t(locale, '底图叠加渲染（结果图）')}</option></select>}</div>}
    <div className="viewport-controls"><button title={locale === 'en-US' ? 'Zoom out (-)' : '缩小 (-)'} onClick={() => props.onViewportChange({ ...viewport, zoom: Math.max(0.2, viewport.zoom / 1.15) })}>−</button><span>{Math.round(viewport.zoom * 100)}%</span><button title={locale === 'en-US' ? 'Zoom in (+)' : '放大 (+)'} onClick={() => props.onViewportChange({ ...viewport, zoom: Math.min(8, viewport.zoom * 1.15) })}>＋</button><button title={locale === 'en-US' ? 'Fit window (Ctrl+0)' : '适合窗口 (Ctrl+0)'} onClick={() => props.onViewportChange({ zoom: 1, panX: 0, panY: 0 })}>{t(locale, '适合')}</button><button title={locale === 'en-US' ? 'Actual pixels (Ctrl+1)' : '实际像素 (Ctrl+1)'} onClick={props.onActualPixels}>1:1</button></div>
    {mode === 'layout' && <div className="layout-toolbar-actions"><button className="layout-add-segment" onClick={props.onAddSegment} disabled={!canAddSegment}><Plus size={14}/>{t(locale, '新建文本框')}</button><span className="toolbar-separator"/><button className={showOriginalTemporarily ? 'active' : ''} onPointerDown={() => props.onShowOriginalChange(true)} onPointerUp={() => props.onShowOriginalChange(false)} onPointerCancel={() => props.onShowOriginalChange(false)} onPointerLeave={() => props.onShowOriginalChange(false)} title={locale === 'en-US' ? 'Hold to show original and hide lettering (Shift)' : '按住查看原图并隐藏嵌字 (Shift)'}><FileImage size={14}/>{t(locale, '按住原图')}</button><button onClick={props.onUndoLayout} disabled={!canUndoLayout} title={t(locale, '撤销')}><Undo2 size={14}/></button><button onClick={props.onRedoLayout} disabled={!canRedoLayout} title={t(locale, '重做')}><Redo2 size={14}/></button><button onClick={props.onDeleteSegment} disabled={!hasSelectedSegment}><Trash2 size={14}/>{t(locale, '删除')}</button><span className={`layout-save-state ${layoutSaveState}`}>{saveText(layoutSaveState)}</span></div>}
    {mode === 'clean' && <div className="clean-toolbar-actions"><span className="clean-tool-label">{locale === 'en-US' ? 'Smart cleanup' : '智能清除'}</span><button onClick={props.onUndoImageEdit} disabled={!canUndoImageEdit} title={t(locale, '撤销')}><Undo2 size={14}/></button><button onClick={props.onRedoImageEdit} disabled={!canRedoImageEdit} title={t(locale, '重做')}><Redo2 size={14}/></button><label><span>{t(locale, '尺寸')}</span><input type="range" min="2" max="320" value={imageEditSize} onChange={event => props.onImageEditSizeChange(Number(event.target.value))}/><em>{imageEditSize}px</em></label><button onClick={props.onResetImageEdit} disabled={!canResetImageEdit}><Trash2 size={14}/>{locale === 'en-US' ? 'Clear repairs' : '清空本页修复'}</button><span className={`layout-save-state ${imageEditSaveState}`}>{saveText(imageEditSaveState)}</span></div>}
  </div>
}
