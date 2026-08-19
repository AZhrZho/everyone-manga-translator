import type { FontInfo, PageReviewTag, PageVersion, ProjectPage } from '../../../contracts'
import type { Locale } from '../../../i18n'
import type { LayoutDocument, LayoutManualOverride, LayoutSegment } from '../../editor/model/layout'
import type { ImageEditDocument } from '../../editor/model/imageEdit'
import { TranslationAnnotationPanel } from '../annotations/TranslationAnnotationPanel'
import { ImageEditInspector } from '../image-edit/ImageEditInspector'
import { LayoutInspector } from '../layout/LayoutInspector'
import type { WorkspaceMode } from '../../../shared/types/workspace'
import { TranslationVersionPanel } from './TranslationVersionPanel'

type TranslationInspectorProps = {
  locale: Locale
  mode: WorkspaceMode
  selectedPage: ProjectPage | undefined
  selectedSegment: LayoutSegment | null
  layout: LayoutDocument | null
  layoutFonts: FontInfo[]
  hasCopiedFormat: boolean
  imageEdit: ImageEditDocument | null
  annotationText: string
  annotationSaveState: 'saved' | 'saving' | 'error'
  exporting: boolean
  generating: boolean
  reviewTagBusy: boolean
  versions: PageVersion[]
  onCopyFormat: () => void
  onApplyFormat: () => void
  onUpdateSegment: (updater: (segment: LayoutSegment) => LayoutSegment, record?: boolean, override?: LayoutManualOverride) => void
  onAnnotationChange: (value: string) => void
  onAnnotationCommit: () => void
  onReviewTagChange: (tag: PageReviewTag) => void
  onRerunPage: () => void
  onActivateVersion: (version: PageVersion) => void
  onDeleteVersion: (version: PageVersion) => void
  onCreateVersion: () => void
}

export function TranslationInspector(props: TranslationInspectorProps) {
  const { locale, mode, selectedPage, selectedSegment, layout, layoutFonts, hasCopiedFormat, imageEdit, annotationText, annotationSaveState, exporting, generating, reviewTagBusy, versions } = props
  return <aside className="translation-inspector">
    {mode === 'layout' && selectedSegment && layout
      ? <LayoutInspector segment={selectedSegment} fonts={layoutFonts} hasCopiedFormat={hasCopiedFormat} onCopyFormat={props.onCopyFormat} onApplyFormat={props.onApplyFormat} onUpdate={props.onUpdateSegment}/>
      : mode === 'clean'
        ? <ImageEditInspector operationCount={imageEdit?.operations.length ?? 0}/>
        : null}
    {mode === 'preview' && selectedPage && <TranslationAnnotationPanel value={annotationText} reviewTag={selectedPage.reviewTag} saveState={annotationSaveState} onChange={props.onAnnotationChange} onCommit={props.onAnnotationCommit} onReject={() => props.onReviewTagChange('Rejected')} onRerun={props.onRerunPage} disabled={exporting || generating || !selectedPage.includedInWorkflow || !selectedPage.hasAiResult || reviewTagBusy} excluded={!selectedPage.includedInWorkflow} hasAiResult={selectedPage.hasAiResult}/>}
    <TranslationVersionPanel locale={locale} versions={versions} canCreate={selectedPage !== undefined} onActivate={props.onActivateVersion} onDelete={props.onDeleteVersion} onCreate={props.onCreateVersion}/>
  </aside>
}
