import { useState } from 'react'
import { FolderOpen } from 'lucide-react'
import type { Locale } from '../../../i18n'
import type { ProjectDialogState } from '../model/projectDialog'
import { ManagedFontSample } from '../../../shared/ui/ManagedFontSample'

export function ProjectDialog({ state, busy, locale, onChange, onSelectSource, onSelectDestination, onCancel, onSubmit }: {
  state: ProjectDialogState
  busy: boolean
  locale: Locale
  onChange: (state: ProjectDialogState) => void
  onSelectSource: () => Promise<void>
  onSelectDestination: () => Promise<void>
  onCancel: () => void
  onSubmit: () => void
}) {
  const [selecting, setSelecting] = useState<'source' | 'destination' | null>(null)
  const languageProfile = state.fontLanguageProfiles.find(
    profile => profile.targetLanguage.toLowerCase() === state.targetLanguage.toLowerCase(),
  )
  const availableFontIds = new Set(languageProfile?.allowedFontIds ?? [])
  const availableFonts = state.availableFonts.filter(font => availableFontIds.has(font.fontId))
  const canSubmit = Boolean(
    state.name.trim()
    && state.destinationToken
    && state.enabledFontIds.length > 0
    && state.sourceToken)

  function changeTargetLanguage(targetLanguage: string) {
    const profile = state.fontLanguageProfiles.find(
      item => item.targetLanguage.toLowerCase() === targetLanguage.toLowerCase(),
    )
    const allowedFontIds = profile?.allowedFontIds ?? []
    const retainedFontIds = state.enabledFontIds.filter(fontId => allowedFontIds.includes(fontId))
    const enabledFontIds = retainedFontIds.length > 0 ? retainedFontIds : allowedFontIds
    const defaultFontId = state.defaultFontId && enabledFontIds.includes(state.defaultFontId)
      ? state.defaultFontId
      : profile?.defaultFontId && enabledFontIds.includes(profile.defaultFontId)
        ? profile.defaultFontId
        : enabledFontIds[0] ?? null
    onChange({
      ...state,
      targetLanguage,
      enabledFontIds,
      defaultFontId,
      fallbackFontId: state.fallbackFontId && enabledFontIds.includes(state.fallbackFontId)
        ? state.fallbackFontId
        : null,
    })
  }

  async function selectPath(kind: 'source' | 'destination') {
    if (busy || selecting) return
    setSelecting(kind)
    try {
      if (kind === 'source') await onSelectSource()
      else await onSelectDestination()
    } finally {
      setSelecting(null)
    }
  }

  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={event => {
      if (event.target === event.currentTarget && !busy) onCancel()
    }}>
      <form className="project-dialog project-import-dialog" onSubmit={event => { event.preventDefault(); if (canSubmit) onSubmit() }}>
        <header className="project-dialog-header">
          <h2>{locale === 'en-US' ? (state.kind === 'import-pdf' ? 'Import PDF manga' : 'Import manga folder') : (state.kind === 'import-pdf' ? '导入 PDF 漫画' : '导入漫画文件夹')}</h2>
          <p>{locale === 'en-US'
            ? 'Choose the source and project location here, then configure the project before importing.'
            : '在此统一选择导入来源和项目保存位置，然后配置项目。'}</p>
        </header>
        <div className="project-dialog-body">
          <section className="project-dialog-section project-source-section">
              <h3>{locale === 'en-US' ? 'Import source' : '导入来源'}</h3>
              <label className="project-location-field">
                <span>{locale === 'en-US' ? (state.kind === 'import-pdf' ? 'Source PDF' : 'Source folder') : (state.kind === 'import-pdf' ? '来源 PDF' : '来源文件夹')}</span>
                <div>
                  <input value={state.sourceLabel} placeholder={locale === 'en-US' ? 'No source selected' : '尚未选择来源'} readOnly title={state.sourceLabel}/>
                  <button type="button" onClick={() => void selectPath('source')} disabled={busy || selecting !== null}><FolderOpen size={14}/>{selecting === 'source' ? (locale === 'en-US' ? 'Selecting…' : '选择中…') : (locale === 'en-US' ? 'Browse…' : '浏览…')}</button>
                </div>
              </label>
              {state.kind === 'import-pdf' && (
                <label>
                  <span>{locale === 'en-US' ? 'Render DPI' : '渲染 DPI'}</span>
                  <input type="number" min={72} max={600} step={1} value={state.renderDpi} onChange={event => onChange({ ...state, renderDpi: Number(event.target.value) || 200 })} disabled={busy}/>
                </label>
              )}
          </section>

          <section className="project-dialog-section">
            <h3>{locale === 'en-US' ? 'Project configuration' : '项目配置'}</h3>
            <label>
              <span>{locale === 'en-US' ? 'Project name' : '项目名称'}</span>
              <input value={state.name} placeholder={locale === 'en-US' ? 'Enter a project name' : '输入项目名称'} onChange={event => onChange({ ...state, name: event.target.value })} disabled={busy}/>
            </label>
            <label className="project-location-field">
              <span>{locale === 'en-US' ? 'Project location' : '项目保存位置'}</span>
              <div>
                <input value={state.destinationLabel} placeholder={locale === 'en-US' ? 'No location selected' : '尚未选择保存位置'} readOnly title={state.destinationLabel}/>
                <button type="button" onClick={() => void selectPath('destination')} disabled={busy || selecting !== null}><FolderOpen size={14}/>{selecting === 'destination' ? (locale === 'en-US' ? 'Selecting…' : '选择中…') : (locale === 'en-US' ? 'Browse…' : '浏览…')}</button>
              </div>
            </label>
            <div className="project-mode-choice">
              <span>{locale === 'en-US' ? 'Workflow mode' : '工作流模式'}</span>
              <div>
                <button type="button" className={state.workflowMode === 'Quick' ? 'active' : ''} onClick={() => onChange({ ...state, workflowMode: 'Quick' })} disabled={busy}><b>{locale === 'en-US' ? 'Quick' : '快速模式'}</b><small>{locale === 'en-US' ? 'Translate and letter each page directly. No preprocessing.' : '逐页直接检测、翻译、清字和嵌字，不运行预处理。'}</small></button>
                <button type="button" className={state.workflowMode === 'Expert' ? 'active' : ''} onClick={() => onChange({ ...state, workflowMode: 'Expert' })} disabled={busy}><b>{locale === 'en-US' ? 'Expert' : '专家模式'}</b><small>{locale === 'en-US' ? 'Preprocess the whole book first for consistent names and context.' : '先完成全书预处理，获得角色、术语和跨页上下文。'}</small></button>
              </div>
            </div>
            <label>
              <span>{locale === 'en-US' ? 'Target language' : '目标语言'}</span>
              <select value={state.targetLanguage} onChange={event => changeTargetLanguage(event.target.value)} disabled={busy}><option value="zh-Hans">简体中文</option><option value="zh-Hant">繁體中文</option><option value="ja">日本語</option><option value="ko">한국어</option><option value="en-US">English</option></select>
            </label>
          </section>

          <details className="project-font-choice" open>
            <summary>{locale === 'en-US' ? `Project fonts (${state.enabledFontIds.length})` : `项目字体（${state.enabledFontIds.length}）`}</summary>
            <div className="project-font-list">{availableFonts.map(font => <label key={font.fontId}><input type="checkbox" checked={state.enabledFontIds.includes(font.fontId)} onChange={event => { const enabledFontIds = event.target.checked ? [...state.enabledFontIds, font.fontId] : state.enabledFontIds.filter(id => id !== font.fontId); onChange({ ...state, enabledFontIds, defaultFontId: enabledFontIds.includes(state.defaultFontId ?? '') ? state.defaultFontId : enabledFontIds[0] ?? null, fallbackFontId: enabledFontIds.includes(state.fallbackFontId ?? '') ? state.fallbackFontId : null }) }}/><ManagedFontSample font={font} locale={locale}/></label>)}</div>
            {availableFonts.length === 0 && <p className="project-font-empty">{locale === 'en-US' ? 'No fonts are available for this language. Add fonts in Settings first.' : '该目标语言的字体库为空，请先在设置中添加字体。'}</p>}
            <label className="project-fallback-font"><span>{locale === 'en-US' ? 'Missing glyph fallback' : '缺字回退字体'}</span><select value={state.fallbackFontId ?? ''} onChange={event => onChange({ ...state, fallbackFontId: event.target.value || null })}><option value="">{locale === 'en-US' ? 'No fallback' : '不使用回退字体'}</option>{availableFonts.filter(font => state.enabledFontIds.includes(font.fontId)).map(font => <option key={font.fontId} value={font.fontId}>{font.displayName}</option>)}</select></label>
          </details>

          <p className="project-import-note">{locale === 'en-US'
          ? state.kind === 'import-pdf'
              ? 'The source PDF and rasterized pages are copied into the project, so the original PDF can later be moved.'
              : 'Source images are copied into the project. The original folder can be moved or removed after import.'
          : state.kind === 'import-pdf'
              ? 'PDF 原文件和栅格化页面会一起保存，后续不依赖外部 PDF 路径。'
              : '原图会复制到所选项目目录。导入完成后，外部源文件夹可以移动或删除。'}</p>
        </div>
        <div className="dialog-actions">
          <button type="button" onClick={onCancel} disabled={busy}>{locale === 'en-US' ? 'Cancel' : '取消'}</button>
          <button type="submit" className="primary" disabled={busy || selecting !== null || !canSubmit}>
            {locale === 'en-US' ? (busy ? 'Working…' : 'Import') : (busy ? '处理中…' : '导入')}
          </button>
        </div>
      </form>
    </div>
  )
}
