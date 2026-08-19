import { useEffect, useRef, useState } from 'react'
import { Plus, Save, Trash2 } from 'lucide-react'
import { api } from '../../api'
import type {
  AppSettings,
  FontInfo,
  ModelSettings,
  ProjectDetail,
  ProviderSettings,
} from '../../contracts'
import { dialogService } from '../../dialogService'
import { emtHost } from '../../host'
import { localeLabels, t, type Locale } from '../../i18n'
import { readError } from '../../shared/lib/errors'
import { stageDisplayName } from '../../shared/lib/formatters'
import { ManagedFontSample } from '../../shared/ui/ManagedFontSample'
import { ProjectSettingsDialog } from '../projects/settings/ProjectSettingsDialog'

const providerDefaultBaseUrls: Record<ProviderSettings['endpointType'], string> = {
  OpenAIResponses: 'https://api.openai.com/v1',
  OpenAIChatCompletions: 'https://api.openai.com/v1',
  AnthropicMessages: 'https://api.anthropic.com/v1',
}

function normalizeUiSettings(settings: AppSettings): AppSettings {
  return settings.localTextDetectionEngine === 'BrowserCraft'
    ? { ...settings, localTextDetectionEngine: 'Craft' }
    : settings
}

function changeProviderEndpointType(provider: ProviderSettings, endpointType: ProviderSettings['endpointType']) {
  const normalizedBaseUrl = provider.baseUrl.trim().replace(/\/$/, '').toLowerCase()
  const knownDefaultUrls = new Set([
    'https://api.openai.com',
    'https://api.openai.com/v1',
    'https://api.openai.com/v1/responses',
    'https://api.openai.com/v1/chat/completions',
    'https://api.anthropic.com',
    'https://api.anthropic.com/v1',
    'https://api.anthropic.com/v1/messages',
  ])
  return {
    endpointType,
    ...(normalizedBaseUrl.length === 0 || knownDefaultUrls.has(normalizedBaseUrl)
      ? { baseUrl: providerDefaultBaseUrls[endpointType] }
      : {}),
  }
}

export function SettingsDialog({ theme, locale, project, onThemeChange, onLocaleChange, onClose, onSaved, onProjectSaved, onError }: {
  theme: 'dark' | 'light'
  locale: Locale
  project?: ProjectDetail | null
  onThemeChange: (theme: 'dark' | 'light') => void
  onLocaleChange: (locale: Locale) => void
  onClose: () => void
  onSaved: (settings: AppSettings) => void
  onProjectSaved?: (project: ProjectDetail) => void
  onError: (error: string | null) => void
}) {
  type EditableProvider = ProviderSettings & { apiKeyDraft: string; clearApiKey: boolean }
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [providers, setProviders] = useState<EditableProvider[]>([])
  const [tab, setTab] = useState<'project' | 'providers' | 'stages' | 'performance' | 'fonts'>(project ? 'project' : 'providers')
  const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null)
  const [fontFilter, setFontFilter] = useState('')
  const [selectedFontLanguage, setSelectedFontLanguage] = useState('zh-Hans')
  const [saving, setSaving] = useState(false)
  const initialThemeRef = useRef(theme)
  const initialLocaleRef = useRef(locale)
  const selectedProvider = providers.find(provider => provider.providerId === selectedProviderId) ?? providers[0] ?? null

  function cancelSettings() {
    onThemeChange(initialThemeRef.current)
    onLocaleChange(initialLocaleRef.current)
    onClose()
  }

  useEffect(() => {
    api.getSettings()
      .then(next => {
        const normalized = normalizeUiSettings(next)
        setSettings(normalized)
        setProviders(next.providers.map(provider => ({ ...provider, apiKeyDraft: '', clearApiKey: false })))
        setSelectedProviderId(next.providers[0]?.providerId ?? null)
        setSelectedFontLanguage(normalized.fontLanguageProfiles[0]?.targetLanguage ?? 'zh-Hans')
      })
      .catch((reason: unknown) => onError(readError(reason)))
  }, [onError])

  function updateProvider(providerId: string, patch: Partial<EditableProvider>) {
    setProviders(current => current.map(provider => {
      if (provider.providerId !== providerId) return provider
      if (!patch.endpointType) return { ...provider, ...patch }
      const { baseUrl: _discardedEndpointDefault, ...patchWithoutBaseUrl } = patch
      return {
        ...provider,
        ...patchWithoutBaseUrl,
        ...changeProviderEndpointType(provider, patch.endpointType),
      }
    }))
  }

  function updateFontLanguageProfile(targetLanguage: string, patch: { allowedFontIds?: string[]; defaultFontId?: string | null }) {
    if (!settings) return
    const normalized = targetLanguage.trim() || 'zh-Hans'
    const existing = settings.fontLanguageProfiles.find(profile => profile.targetLanguage.toLowerCase() === normalized.toLowerCase())
    const nextProfile = {
      targetLanguage: normalized,
      allowedFontIds: patch.allowedFontIds ?? existing?.allowedFontIds ?? [],
      defaultFontId: patch.defaultFontId !== undefined ? patch.defaultFontId : existing?.defaultFontId ?? null,
    }
    setSettings({
      ...settings,
      fontLanguageProfiles: existing
        ? settings.fontLanguageProfiles.map(profile => profile === existing ? nextProfile : profile)
        : [...settings.fontLanguageProfiles, nextProfile],
    })
  }

  function updateProviderModel(providerId: string, modelIndex: number, patch: Partial<ModelSettings>) {
    const previousModelId = providers.find(provider => provider.providerId === providerId)?.models[modelIndex]?.modelId
    setProviders(current => current.map(provider => provider.providerId === providerId
      ? { ...provider, models: provider.models.map((model, index) => index === modelIndex ? { ...model, ...patch } : model) }
      : provider))
    if (previousModelId && patch.modelId !== undefined && patch.modelId !== previousModelId) {
      setSettings(current => current
        ? { ...current, stages: current.stages.map(stage => stage.providerId === providerId && stage.modelId === previousModelId ? { ...stage, modelId: patch.modelId || null } : stage) }
        : current)
    }
  }

  function removeProviderModel(providerId: string, modelIndex: number) {
    const modelId = providers.find(provider => provider.providerId === providerId)?.models[modelIndex]?.modelId
    setProviders(current => current.map(provider => provider.providerId === providerId
      ? { ...provider, models: provider.models.filter((_, index) => index !== modelIndex) }
      : provider))
    setSettings(current => current
      ? { ...current, stages: current.stages.map(stage => stage.providerId === providerId && stage.modelId === modelId ? { ...stage, modelId: null } : stage) }
      : current)
  }

  function addProvider() {
    const providerId = `provider-${Date.now()}`
    const provider: EditableProvider = { providerId, name: locale === 'en-US' ? 'New provider' : '新 Provider', endpointType: 'OpenAIResponses', baseUrl: 'https://api.openai.com/v1', apiKeyConfigured: false, apiKeyDraft: '', clearApiKey: false, models: [] }
    setProviders(current => [...current, provider])
    setSelectedProviderId(providerId)
    setTab('providers')
  }

  function addModel() {
    if (!selectedProvider) return
    const modelId = `model-${Date.now()}`
    updateProvider(selectedProvider.providerId, { models: [...selectedProvider.models, { modelId, displayName: locale === 'en-US' ? 'New vision model' : '新图片模型', supportsImageInput: true }] })
  }

  async function removeProvider(providerId: string) {
    if (!await dialogService.confirm({
      title: locale === 'en-US' ? 'Delete provider' : '删除 Provider',
      message: locale === 'en-US' ? 'Delete this provider configuration?' : '确定要删除这个 Provider 配置吗？',
      detail: locale === 'en-US' ? 'Stage assignments that use it will be cleared.' : '使用它的工作流阶段配置也会被清除。',
      destructive: true,
      confirmLabel: locale === 'en-US' ? 'Delete' : '删除',
    })) return
    setProviders(current => current.filter(provider => provider.providerId !== providerId))
    setSettings(current => current
      ? { ...current, stages: current.stages.map(stage => stage.providerId === providerId ? { ...stage, providerId: null, modelId: null } : stage) }
      : current)
    setSelectedProviderId(current => current === providerId ? null : current)
  }

  async function save() {
    if (!settings) return
    setSaving(true)
    onError(null)
    try {
      const updated = await api.updateSettings({
        theme: settings.theme,
        locale: settings.locale,
        providers: providers.map(provider => ({ providerId: provider.providerId, name: provider.name, endpointType: provider.endpointType, baseUrl: provider.baseUrl, apiKey: provider.apiKeyDraft.trim() || null, clearApiKey: provider.clearApiKey, models: provider.models })),
        stages: settings.stages,
        defaultFontId: settings.defaultFontId,
        fallbackFontId: settings.fallbackFontId,
        fontLanguageProfiles: settings.fontLanguageProfiles,
        translationBatchConcurrency: settings.translationBatchConcurrency,
        translationBatchSize: settings.translationBatchSize,
        precheckBatchConcurrency: settings.precheckBatchConcurrency,
        precheckBatchSize: settings.precheckBatchSize,
        localTextDetectionEngine: settings.localTextDetectionEngine,
      })
      setSettings(updated)
      setProviders(updated.providers.map(provider => ({ ...provider, apiKeyDraft: '', clearApiKey: false })))
      onSaved(updated)
    } catch (reason) {
      onError(readError(reason))
    } finally {
      setSaving(false)
    }
  }

  async function importFonts() {
    if (!emtHost) {
      onError(locale === 'en-US' ? 'Local font import is not available in the web build. Use Settings in the desktop app.' : 'Web 版本暂不支持本地字体导入，请在桌面端设置中导入字体。')
      return
    }
    try {
      const grants = await emtHost.selectFontFiles()
      if (grants.length === 0) return
      setSettings(normalizeUiSettings(await api.importFonts(
        grants.map(grant => grant.token),
        selectedFontLanguage,
      )))
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function removeFont(font: FontInfo) {
    if (!font.canRemove || !await dialogService.confirm({
      title: locale === 'en-US' ? 'Remove font' : '删除字体',
      message: locale === 'en-US' ? `Remove imported font “${font.displayName}”?` : `确定要删除已导入字体“${font.displayName}”吗？`,
      destructive: true,
      confirmLabel: locale === 'en-US' ? 'Remove' : '删除',
    })) return
    try {
      setSettings(normalizeUiSettings(await api.removeFont(font.fontId)))
    } catch (reason) {
      onError(readError(reason))
    }
  }

  if (!settings) {
    return <div className="settings-backdrop"><section className="settings-dialog"><div className="settings-loading">{locale === 'en-US' ? 'Loading settings…' : '正在读取设置…'}</div></section></div>
  }

  const selectedFontProfile = settings.fontLanguageProfiles.find(
    profile => profile.targetLanguage.toLowerCase() === selectedFontLanguage.toLowerCase(),
  ) ?? { targetLanguage: selectedFontLanguage, allowedFontIds: [], defaultFontId: null }
  const visibleFonts = settings.fonts
    .filter(font => `${font.displayName} ${font.fileName}`.toLowerCase().includes(fontFilter.toLowerCase()))
    .sort((left, right) => {
      const availability = Number(selectedFontProfile.allowedFontIds.includes(right.fontId))
        - Number(selectedFontProfile.allowedFontIds.includes(left.fontId))
      return availability || left.displayName.localeCompare(right.displayName, locale)
    })
  return <div className="settings-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) cancelSettings() }}>
    <section className="settings-dialog" role="dialog" aria-label={t(locale, '设置')}>
      <header className="settings-header"><div><b>{t(locale, '设置')}</b><small>{locale === 'en-US' ? 'Models, fonts and interface preferences' : '模型、字体与界面偏好'}</small></div><button onClick={cancelSettings} aria-label={t(locale, '关闭')}>×</button></header>
      <div className="settings-body">
        <nav className="settings-nav">
          {project && <button className={tab === 'project' ? 'active' : ''} onClick={() => setTab('project')}>{locale === 'en-US' ? 'Project' : '当前项目'}</button>}
          <button className={tab === 'providers' ? 'active' : ''} onClick={() => setTab('providers')}>{locale === 'en-US' ? 'Providers & models' : 'Provider 与模型'}</button>
          <button className={tab === 'stages' ? 'active' : ''} onClick={() => setTab('stages')}>{t(locale, '阶段模型')}</button>
          <button className={tab === 'performance' ? 'active' : ''} onClick={() => setTab('performance')}>{locale === 'en-US' ? 'Tasks & performance' : '任务与性能'}</button>
          <button className={tab === 'fonts' ? 'active' : ''} onClick={() => setTab('fonts')}>{t(locale, '字体管理')}</button>
          <div className="settings-appearance">
            <label><span>{t(locale, '界面主题')}</span><select value={settings.theme} onChange={event => { const next = event.target.value as 'dark' | 'light'; setSettings({ ...settings, theme: next }); onThemeChange(next) }}><option value="dark">Dark</option><option value="light">Light</option></select></label>
            <label><span>{t(locale, '界面语言')}</span><select value={settings.locale} onChange={event => { const next = event.target.value as Locale; setSettings({ ...settings, locale: next }); onLocaleChange(next) }}>{Object.entries(localeLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
          </div>
        </nav>
        <div className="settings-content">
          {tab === 'project' && project && (
            <ProjectSettingsDialog
              project={project}
              locale={locale}
              embedded
              onClose={() => undefined}
              onSaved={updated => onProjectSaved?.(updated)}
              onError={onError}
            />
          )}
          {tab === 'providers' && <div className="settings-provider-grid">
            <div className="settings-provider-list"><div className="settings-section-title"><b>Providers</b><button onClick={addProvider}><Plus size={13}/>{t(locale, '添加')}</button></div>{providers.map(provider => <button key={provider.providerId} className={provider.providerId === selectedProvider?.providerId ? 'active' : ''} onClick={() => setSelectedProviderId(provider.providerId)}><span><b>{provider.name}</b><small>{provider.baseUrl}</small></span><em>{provider.apiKeyConfigured ? (locale === 'en-US' ? 'Key configured' : 'Key 已配置') : (locale === 'en-US' ? 'API key not configured' : '未配置 Key')}</em></button>)}{providers.length === 0 && <p className="settings-empty">{locale === 'en-US' ? 'No providers yet. Add one to assign models to workflow stages.' : '还没有 Provider。添加后可在阶段模型中分配。'}</p>}</div>
            {selectedProvider && <div className="settings-provider-form"><div className="settings-form-heading"><b>{t(locale, 'Provider 配置')}</b><button className="danger-link" onClick={() => removeProvider(selectedProvider.providerId)}>{t(locale, '删除')}</button></div><label><span>{locale === 'en-US' ? 'Name' : '名称'}</span><input value={selectedProvider.name} onChange={event => updateProvider(selectedProvider.providerId, { name: event.target.value })}/></label><label><span>{locale === 'en-US' ? 'Endpoint type' : '端点类型'}</span><select value={selectedProvider.endpointType} onChange={event => updateProvider(selectedProvider.providerId, { endpointType: event.target.value as ProviderSettings['endpointType'] })}><option value="OpenAIResponses">OpenAI Responses</option><option value="OpenAIChatCompletions">OpenAI Chat Completions</option><option value="AnthropicMessages">Claude / Anthropic Messages</option></select></label><label><span>Base URL</span><input value={selectedProvider.baseUrl} onChange={event => updateProvider(selectedProvider.providerId, { baseUrl: event.target.value })}/></label><small>{locale === 'en-US' ? 'You may enter the API root, /v1 root, or the full endpoint URL.' : '可填写 API 根地址、/v1 根地址，或完整端点 URL。'}</small><label><span>API Key</span><input type="password" placeholder={selectedProvider.apiKeyConfigured ? (locale === 'en-US' ? 'Configured (leave empty to keep)' : '已配置（留空保持不变）') : (locale === 'en-US' ? 'Enter API key' : '输入 API Key')} value={selectedProvider.apiKeyDraft} onChange={event => updateProvider(selectedProvider.providerId, { apiKeyDraft: event.target.value, clearApiKey: false })}/></label><label className="settings-check"><input type="checkbox" checked={selectedProvider.clearApiKey} onChange={event => updateProvider(selectedProvider.providerId, { clearApiKey: event.target.checked, apiKeyDraft: '' })}/><span>{locale === 'en-US' ? 'Clear saved API key' : '清除已保存的 API Key'}</span></label><div className="settings-section-title"><b>{locale === 'en-US' ? 'Models' : '模型列表'}</b><button onClick={addModel}><Plus size={13}/>{locale === 'en-US' ? 'Add model' : '添加模型'}</button></div><div className="settings-model-header"><span>{locale === 'en-US' ? 'Model ID' : '模型 ID'}</span><span>{locale === 'en-US' ? 'Display name' : '显示名称'}</span><span>{locale === 'en-US' ? 'Capabilities' : '能力'}</span><span/></div><div className="settings-model-list">{selectedProvider.models.map((model, modelIndex) => <div key={modelIndex} className="settings-model-row"><input value={model.modelId} aria-label="Model ID" onChange={event => updateProviderModel(selectedProvider.providerId, modelIndex, { modelId: event.target.value })}/><input value={model.displayName} aria-label={locale === 'en-US' ? 'Model display name' : '模型显示名'} onChange={event => updateProviderModel(selectedProvider.providerId, modelIndex, { displayName: event.target.value })}/><label title={locale === 'en-US' ? 'Model accepts image input' : '模型支持图片输入'}><input type="checkbox" checked={model.supportsImageInput} onChange={event => updateProviderModel(selectedProvider.providerId, modelIndex, { supportsImageInput: event.target.checked })}/>{locale === 'en-US' ? 'Supports image input' : '支持图片输入'}</label><button className="settings-model-remove" aria-label={`${locale === 'en-US' ? 'Delete model' : '删除模型'} ${model.displayName}`} title={locale === 'en-US' ? 'Delete model' : '删除模型'} onClick={() => removeProviderModel(selectedProvider.providerId, modelIndex)}><Trash2 size={13}/></button></div>)}{selectedProvider.models.length === 0 && <p className="settings-empty">{locale === 'en-US' ? 'Add at least one model that supports image input.' : '请添加至少一个支持图片输入的模型。'}</p>}</div></div>}
          </div>}
          {tab === 'stages' && <div className="settings-stage-list"><div className="settings-form-heading"><b>{locale === 'en-US' ? 'Workflow stage models' : '工作流阶段模型'}</b><small>{locale === 'en-US' ? 'Scanning and translation require image-capable models; preprocessing uses structured scan results.' : '扫描与翻译必须使用支持图片输入的模型；预处理读取结构化扫描结果。'}</small></div>{settings.stages.map((stage, index) => { const provider = providers.find(item => item.providerId === stage.providerId); const requiresImage = stage.requiredModelType === 'image-input'; const availableModels = provider?.models.filter(model => !requiresImage || model.supportsImageInput) ?? []; return <div className="settings-stage-row" key={stage.stageId}><div><b>{stageDisplayName(locale, stage.stageId, stage.displayName)}</b><small>{requiresImage ? (locale === 'en-US' ? 'Supports image input required' : '必须支持图片输入') : (locale === 'en-US' ? 'Text input' : '文本输入')}</small></div><select value={stage.providerId ?? ''} aria-label="Provider" onChange={event => { const providerId = event.target.value || null; const next = [...settings.stages]; next[index] = { ...stage, providerId, modelId: providers.find(item => item.providerId === providerId)?.models.find(model => !requiresImage || model.supportsImageInput)?.modelId ?? null }; setSettings({ ...settings, stages: next }) }}><option value="">{locale === 'en-US' ? 'No provider assigned' : '未分配 Provider'}</option>{providers.map(item => <option value={item.providerId} key={item.providerId}>{item.name}</option>)}</select><select value={stage.modelId ?? ''} aria-label={locale === 'en-US' ? 'Model' : '模型'} onChange={event => { const next = [...settings.stages]; next[index] = { ...stage, modelId: event.target.value || null }; setSettings({ ...settings, stages: next }) }}><option value="">{locale === 'en-US' ? 'No model assigned' : '未分配模型'}</option>{availableModels.map(model => <option value={model.modelId} key={model.modelId}>{model.displayName}</option>)}</select></div>})}</div>}
          {tab === 'performance' && <div className="settings-stage-list performance-settings"><div className="settings-form-heading"><div><b>{locale === 'en-US' ? 'Task execution' : '任务执行'}</b><small>{locale === 'en-US' ? 'New tasks snapshot these values when they start.' : '新任务启动时会固化这些参数；预处理与翻译参数互不影响。'}</small></div></div><h3>{locale === 'en-US' ? 'Preprocessing' : '专家预处理'}</h3><div className="settings-stage-row"><div><b>{locale === 'en-US' ? 'Concurrent scan batches' : '扫描批次并行度'}</b><small>{locale === 'en-US' ? 'Maximum preprocessing scan requests running at once.' : '预处理阶段同时运行的页面扫描请求数量。'}</small></div><input type="number" min={1} max={8} value={settings.precheckBatchConcurrency} onChange={event => setSettings({ ...settings, precheckBatchConcurrency: Math.max(1, Math.min(8, Number(event.target.value) || 1)) })}/></div><div className="settings-stage-row"><div><b>{locale === 'en-US' ? 'Pages per scan batch' : '每批扫描页数'}</b><small>{locale === 'en-US' ? 'Number of owner pages in each preprocessing scan request.' : '单次预处理扫描请求包含的主页面数量。'}</small></div><input type="number" min={1} max={24} value={settings.precheckBatchSize} onChange={event => setSettings({ ...settings, precheckBatchSize: Math.max(1, Math.min(24, Number(event.target.value) || 1)) })}/></div><h3>{locale === 'en-US' ? 'Translation' : '翻译'}</h3><div className="settings-stage-row"><div><b>{locale === 'en-US' ? 'Text detection and cleaning engine' : '文字检测与清字引擎'}</b><small>{locale === 'en-US' ? 'Choose the local detector used before cleaning.' : '选择清字前使用的本地文字检测器。'}</small></div><select value={settings.localTextDetectionEngine === 'BrowserCraft' ? 'Craft' : settings.localTextDetectionEngine} onChange={event => setSettings({ ...settings, localTextDetectionEngine: event.target.value as AppSettings['localTextDetectionEngine'] })}><option value="Ctd">CTD</option><option value="Craft">CRAFT</option></select></div><div className="settings-stage-row"><div><b>{locale === 'en-US' ? 'Concurrent translation batches' : '翻译批次并行度'}</b><small>{locale === 'en-US' ? 'Maximum model batch requests running at the same time.' : '同时运行的大模型翻译 Batch 请求数量。'}</small></div><input type="number" min={1} max={16} value={settings.translationBatchConcurrency} onChange={event => setSettings({ ...settings, translationBatchConcurrency: Math.max(1, Math.min(16, Number(event.target.value) || 1)) })}/></div><div className="settings-stage-row"><div><b>{locale === 'en-US' ? 'Pages per translation batch' : '每批翻译页数'}</b><small>{locale === 'en-US' ? 'Number of selected pages sent in one model request.' : '单次大模型翻译请求包含的已选页面数。'}</small></div><input type="number" min={1} max={16} value={settings.translationBatchSize} onChange={event => setSettings({ ...settings, translationBatchSize: Math.max(1, Math.min(16, Number(event.target.value) || 1)) })}/></div></div>}
          {tab === 'fonts' && (
            <div className="settings-fonts">
              <div className="settings-form-heading">
                <div>
                  <b>{t(locale, '字体管理')}</b>
                  <small>{locale === 'en-US' ? 'Maintain the application font library by target language. Project font selection and fallback are configured in each project.' : '按目标语言维护应用字体库；项目实际使用的字体与缺字回退字体在项目设置中配置。'}</small>
                </div>
                <button className="primary-small" onClick={() => void importFonts()}><Plus size={13}/>{locale === 'en-US' ? 'Import to this language' : '导入到当前语言'}</button>
              </div>
              <div className="settings-font-toolbar">
                <label><span>{locale === 'en-US' ? 'Target language' : '目标语言'}</span><select value={selectedFontLanguage} onChange={event => setSelectedFontLanguage(event.target.value)}><option value="zh-Hans">简体中文 / zh-Hans</option><option value="zh-Hant">繁体中文 / zh-Hant</option><option value="ja">日本語 / ja</option><option value="ko">한국어 / ko</option><option value="en-US">English / en-US</option></select></label>
                <label><span>{locale === 'en-US' ? 'Filter' : '筛选'}</span><input placeholder={locale === 'en-US' ? 'Font name or file' : '字体名称或文件名'} value={fontFilter} onChange={event => setFontFilter(event.target.value)}/></label>
              </div>
              <div className="font-library-summary">
                <span>{locale === 'en-US' ? 'Available in this language' : '当前语言可用'}</span>
                <b>{settings.fonts.filter(font => selectedFontProfile.allowedFontIds.includes(font.fontId)).length}</b>
                <small>{locale === 'en-US' ? 'Only these fonts can be selected by projects using this target language.' : '使用该目标语言的项目只能选择这里已加入的字体。'}</small>
              </div>
              <div className="settings-font-list font-library-list">
                {visibleFonts.map(font => {
                  const assigned = selectedFontProfile.allowedFontIds.includes(font.fontId)
                  return <div key={font.fontId} className={assigned ? 'font-library-row assigned' : 'font-library-row'}>
                    <ManagedFontSample font={font} locale={locale}/>
                    <div className="settings-font-meta">
                      <em className={assigned ? 'available' : ''}>{assigned ? (locale === 'en-US' ? 'Available' : '已加入') : (locale === 'en-US' ? 'Not added' : '未加入')}</em>
                      <button onClick={() => updateFontLanguageProfile(selectedFontLanguage, {
                        allowedFontIds: assigned
                          ? selectedFontProfile.allowedFontIds.filter(id => id !== font.fontId)
                          : [...selectedFontProfile.allowedFontIds, font.fontId],
                      })}>{assigned ? (locale === 'en-US' ? 'Remove from language' : '从当前语言移出') : (locale === 'en-US' ? 'Add to language' : '加入当前语言')}</button>
                      {font.canRemove && <button className="font-delete-button" title={locale === 'en-US' ? 'Delete imported font from the application' : '从应用中删除已导入字体'} aria-label={locale === 'en-US' ? `Delete ${font.displayName}` : `删除字体 ${font.displayName}`} onClick={() => void removeFont(font)}><Trash2 size={13}/></button>}
                    </div>
                  </div>
                })}
                {visibleFonts.length === 0 && <p className="settings-empty">{locale === 'en-US' ? 'No matching fonts.' : '没有匹配的字体。'}</p>}
              </div>
            </div>
          )}
        </div>
      </div>
      <footer className="settings-footer"><span>{locale === 'en-US' ? 'API keys stay in the local user-data directory and are encrypted at rest.' : 'API Key 仅写入本机用户数据目录，并使用应用数据保护密钥加密。'}</span><div><button onClick={cancelSettings}>{t(locale, '取消')}</button><button className="primary-small" onClick={() => void save()} disabled={saving}><Save size={13}/>{saving ? (locale === 'en-US' ? 'Saving…' : '保存中…') : t(locale, '保存设置')}</button></div></footer>
    </section>
  </div>
}
