import { MousePointer2, Paintbrush, Type } from 'lucide-react'
import type { ProjectPage } from '../../../contracts'
import { t, type Locale } from '../../../i18n'
import type { WorkspaceMode } from '../../../shared/types/workspace'

type TranslationToolRailProps = {
  locale: Locale
  mode: WorkspaceMode
  selectedPage: ProjectPage | undefined
  generating: boolean
  editTransitionBusy: boolean
  onModeChange: (mode: WorkspaceMode) => void
}

export function TranslationToolRail({ locale, mode, selectedPage, generating, editTransitionBusy, onModeChange }: TranslationToolRailProps) {
  const generatingTitle = locale === 'en-US' ? 'Available after this page finishes generating' : '页面生成完成后可用'
  const requiresTranslationTitle = locale === 'en-US' ? 'Run AI translation for this page first' : '请先完成当前页的 AI 翻译'
  const editingUnavailable = !selectedPage?.hasAiResult || generating
  return <aside className="translation-tool-rail">
    <button className={mode === 'preview' ? 'active' : ''} title={t(locale, '预览')} aria-label={t(locale, '预览')} disabled={editTransitionBusy} onClick={() => onModeChange('preview')}><MousePointer2 size={16}/><span>{t(locale, '预览')}</span></button>
    <button className={mode === 'layout' ? 'active' : ''} title={generating ? generatingTitle : !selectedPage?.hasAiResult ? requiresTranslationTitle : t(locale, '文本布局')} aria-label={t(locale, '文本布局')} disabled={editTransitionBusy || editingUnavailable} onClick={() => onModeChange('layout')}><Type size={16}/><span>{locale === 'en-US' ? 'Text' : '文字'}</span></button>
    <button className={mode === 'clean' ? 'active' : ''} title={generating ? generatingTitle : !selectedPage?.hasAiResult ? requiresTranslationTitle : t(locale, '底图编辑')} aria-label={t(locale, '底图编辑')} disabled={editTransitionBusy || editingUnavailable} onClick={() => onModeChange('clean')}><Paintbrush size={16}/><span>{locale === 'en-US' ? 'Clean' : '底图'}</span></button>
  </aside>
}
