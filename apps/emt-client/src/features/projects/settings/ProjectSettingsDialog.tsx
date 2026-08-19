import { useEffect, useState } from 'react'
import { api } from '../../../api'
import type { FontInfo, FontLanguageProfile, ProjectDetail } from '../../../contracts'
import type { Locale } from '../../../i18n'
import { readError } from '../../../shared/lib/errors'
import { ManagedFontSample } from '../../../shared/ui/ManagedFontSample'

export function ProjectSettingsDialog({ project, locale, onClose, onSaved, onError, embedded = false }: {
  project: ProjectDetail
  locale: Locale
  onClose: () => void
  onSaved: (project: ProjectDetail) => void
  onError: (message: string | null) => void
  embedded?: boolean
}) {
  const workflowMode = project.workflowMode
  const [targetLanguage, setTargetLanguage] = useState(project.targetLanguage)
  const [enabledFontIds, setEnabledFontIds] = useState(project.enabledFontIds)
  const [defaultFontId, setDefaultFontId] = useState(project.defaultFontId)
  const [fallbackFontId, setFallbackFontId] = useState(project.fallbackFontId)
  const [fonts, setFonts] = useState<FontInfo[]>([])
  const [fontLanguageProfiles, setFontLanguageProfiles] = useState<FontLanguageProfile[]>([])
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    api.getSettings().then(settings => {
      setFonts(settings.fonts)
      setFontLanguageProfiles(settings.fontLanguageProfiles)
      const profile = settings.fontLanguageProfiles.find(
        item => item.targetLanguage.toLowerCase() === project.targetLanguage.toLowerCase(),
      )
      const allowedFontIds = profile?.allowedFontIds ?? []
      const retainedFontIds = project.enabledFontIds.filter(fontId => allowedFontIds.includes(fontId))
      const nextEnabledFontIds = retainedFontIds.length > 0 ? retainedFontIds : allowedFontIds
      setEnabledFontIds(nextEnabledFontIds)
      setDefaultFontId(project.defaultFontId && nextEnabledFontIds.includes(project.defaultFontId)
        ? project.defaultFontId
        : profile?.defaultFontId && nextEnabledFontIds.includes(profile.defaultFontId)
          ? profile.defaultFontId
          : nextEnabledFontIds[0] ?? null)
      setFallbackFontId(project.fallbackFontId && nextEnabledFontIds.includes(project.fallbackFontId)
        ? project.fallbackFontId
        : null)
    }).catch(reason => onError(readError(reason)))
  }, [
    onError,
    project.defaultFontId,
    project.enabledFontIds,
    project.fallbackFontId,
    project.projectId,
    project.targetLanguage,
  ])

  const languageProfile = fontLanguageProfiles.find(
    profile => profile.targetLanguage.toLowerCase() === targetLanguage.toLowerCase(),
  )
  const availableFontIds = new Set(languageProfile?.allowedFontIds ?? [])
  const availableFonts = fonts.filter(font => availableFontIds.has(font.fontId))

  function changeTargetLanguage(nextLanguage: string) {
    const profile = fontLanguageProfiles.find(
      item => item.targetLanguage.toLowerCase() === nextLanguage.toLowerCase(),
    )
    const allowedFontIds = profile?.allowedFontIds ?? []
    const retainedFontIds = enabledFontIds.filter(fontId => allowedFontIds.includes(fontId))
    const nextEnabledFontIds = retainedFontIds.length > 0 ? retainedFontIds : allowedFontIds
    setTargetLanguage(nextLanguage)
    setEnabledFontIds(nextEnabledFontIds)
    setDefaultFontId(current => current && nextEnabledFontIds.includes(current)
      ? current
      : profile?.defaultFontId && nextEnabledFontIds.includes(profile.defaultFontId)
        ? profile.defaultFontId
        : nextEnabledFontIds[0] ?? null)
    setFallbackFontId(current => current && nextEnabledFontIds.includes(current) ? current : null)
  }

  async function save() {
    setSaving(true)
    try {
      onSaved(await api.updateProjectSettings(project.projectId, {
        workflowMode,
        targetLanguage,
        enabledFontIds,
        defaultFontId,
        fallbackFontId,
      }, project.revision))
    } catch (reason) {
      onError(readError(reason))
    } finally {
      setSaving(false)
    }
  }

  const content = (
      <section className={`project-dialog project-settings-dialog ${embedded ? 'project-settings-embedded' : ''}`}>
        <h2>{locale === 'en-US' ? 'Project settings' : '项目设置'}</h2>
        <div className="project-mode-choice project-mode-locked">
          <span>{locale === 'en-US' ? 'Workflow mode' : '工作流模式'}</span>
          <div>
            <div className={workflowMode === 'Quick' ? 'active' : ''}><b>{locale === 'en-US' ? 'Quick' : '快速模式'}</b><small>{locale === 'en-US' ? 'Page-isolated translation without preprocessing.' : '不运行预处理，逐页独立完成翻译嵌字。'}</small></div>
            <div className={workflowMode === 'Expert' ? 'active' : ''}><b>{locale === 'en-US' ? 'Expert' : '专家模式'}</b><small>{locale === 'en-US' ? 'Uses preprocessing and book-wide context.' : '使用预处理与全书上下文。'}</small></div>
          </div>
          <p>{locale === 'en-US' ? 'Workflow mode is fixed when the project is created.' : '工作流模式在项目建立时确定，建立后不可切换。'}</p>
        </div>
        <label>
          <span>{locale === 'en-US' ? 'Target language' : '目标语言'}</span>
          <select value={targetLanguage} onChange={event => changeTargetLanguage(event.target.value)}>
            <option value="zh-Hans">简体中文</option>
            <option value="zh-Hant">繁體中文</option>
            <option value="ja">日本語</option>
            <option value="ko">한국어</option>
            <option value="en-US">English</option>
          </select>
        </label>
        <details className="project-font-choice" open>
          <summary>{locale === 'en-US' ? `Project fonts (${enabledFontIds.length})` : `项目字体（${enabledFontIds.length}）`}</summary>
          <div className="project-font-list">{availableFonts.map(font => (
            <label key={font.fontId}>
              <input type="checkbox" checked={enabledFontIds.includes(font.fontId)} onChange={event => {
                const next = event.target.checked ? [...enabledFontIds, font.fontId] : enabledFontIds.filter(id => id !== font.fontId)
                setEnabledFontIds(next)
                if (!next.includes(defaultFontId ?? '')) setDefaultFontId(next[0] ?? null)
                if (!next.includes(fallbackFontId ?? '')) setFallbackFontId(null)
              }}/>
              <ManagedFontSample font={font} locale={locale}/>
            </label>
          ))}</div>
          {availableFonts.length === 0 && <p className="project-font-empty">{locale === 'en-US' ? 'No fonts are available for this language. Add fonts in application settings first.' : '该目标语言的字体库为空，请先在应用设置中添加字体。'}</p>}
          <label className="project-fallback-font"><span>{locale === 'en-US' ? 'Missing glyph fallback' : '缺字回退字体'}</span><select value={fallbackFontId ?? ''} onChange={event => setFallbackFontId(event.target.value || null)}><option value="">{locale === 'en-US' ? 'No fallback font' : '不使用回退字体'}</option>{availableFonts.filter(font => enabledFontIds.includes(font.fontId)).map(font => <option value={font.fontId} key={font.fontId}>{font.displayName}</option>)}</select></label>
        </details>
        <div className="dialog-actions">
          {!embedded && <button onClick={onClose} disabled={saving}>{locale === 'en-US' ? 'Cancel' : '取消'}</button>}
          <button className="primary" onClick={() => void save()} disabled={saving || enabledFontIds.length === 0}>{saving ? (locale === 'en-US' ? 'Saving…' : '保存中…') : (locale === 'en-US' ? 'Save project settings' : '保存项目设置')}</button>
        </div>
      </section>
  )
  if (embedded) return content
  return <div className="modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !saving) onClose() }}>{content}</div>
}
