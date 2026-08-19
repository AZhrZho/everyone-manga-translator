import { useEffect, useRef, useState } from 'react'
import {
  ChevronDown,
  ChevronUp,
  Clock3,
  RefreshCw,
  Settings as SettingsIcon,
} from 'lucide-react'
import { api } from './api'
import { DialogProvider } from './DialogProvider'
import { dialogService } from './dialogService'
import { SettingsDialog } from './features/settings/SettingsDialog'
import { ProjectDialog } from './features/projects/start/ProjectDialog'
import { ProjectStartPage } from './features/projects/start/ProjectStartPage'
import type { ProjectDialogCommon, ProjectDialogState } from './features/projects/model/projectDialog'
import { TaskCenterPopover } from './features/jobs/task-center/TaskCenterPopover'
import { ProjectWorkspace } from './features/workspace/ProjectWorkspace'
import { readError } from './shared/lib/errors'
import { emtHost } from './host'
import { readLocale, t, writeLocale, type Locale } from './i18n'
import type {
  ProjectDetail,
  ProjectListItem,
  SystemInfo,
} from './contracts'
import type { WorkspaceActivity } from './shared/types/workspace'
import './App.css'

function App() {
  const [system, setSystem] = useState<SystemInfo | null>(null)
  const [projects, setProjects] = useState<ProjectListItem[]>([])
  const [activeProject, setActiveProject] = useState<ProjectDetail | null>(null)
  const [workspaceActivity, setWorkspaceActivity] = useState<WorkspaceActivity | null>(null)
  const [taskCenterOpen, setTaskCenterOpen] = useState(false)
  const [dialog, setDialog] = useState<ProjectDialogState | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [theme, setTheme] = useState<'dark' | 'light'>('light')
  const [locale, setLocale] = useState<Locale>(() => readLocale())
  const [windowMaximized, setWindowMaximized] = useState(false)
  const projectFlushRef = useRef<() => Promise<boolean>>(async () => true)

  useEffect(() => {
    Promise.all([api.getSystemInfo(), api.listProjects()])
      .then(([systemInfo, projectItems]) => {
        setSystem(systemInfo)
        setProjects(projectItems)
      })
      .catch((reason: unknown) => setError(readError(reason)))
  }, [])

  useEffect(() => {
    setTaskCenterOpen(false)
  }, [activeProject?.projectId])

  useEffect(() => {
    api.getSettings().then(settings => {
      setTheme(settings.theme)
      setLocale(settings.locale)
      writeLocale(settings.locale)
    }).catch(() => undefined)
  }, [])

  useEffect(() => {
    const host = emtHost
    if (!host) return
    host.setBeforeCloseHandler(() => projectFlushRef.current())
    host.setDialogHandler(async request => {
      const options = {
        title: request.title,
        message: request.message,
        detail: request.detail,
        confirmLabel: request.confirmLabel,
        cancelLabel: request.cancelLabel,
        destructive: request.destructive,
      }
      if (request.kind === 'alert') {
        await dialogService.alert(options)
        return true
      }
      return dialogService.confirm(options)
    })
    host.setWindowStateHandler(setWindowMaximized)
    void host.isMaximized().then(setWindowMaximized).catch(() => undefined)
    return () => {
      host.setBeforeCloseHandler(async () => true)
      host.setDialogHandler(async () => false)
      host.setWindowStateHandler(() => undefined)
    }
  }, [])

  async function refreshProjects() {
    setProjects(await api.listProjects())
  }

  async function openProject(projectId: string) {
    setError(null)
    setBusy(true)
    try {
      setActiveProject(await api.getProject(projectId))
    } catch (reason) {
      setError(readError(reason))
    } finally {
      setBusy(false)
    }
  }

  async function openExistingProject() {
    if (!emtHost) {
      setError(locale === 'en-US' ? 'Opening a local project directory is not available in the web build. Use the desktop app.' : 'Web 版本暂未接入项目目录上传，请使用桌面端打开项目。')
      return
    }

    try {
      const grant = await emtHost.selectExistingProject()
      if (!grant) return
      setBusy(true)
      setActiveProject(await api.openProjectFromLocation(grant.token))
      await refreshProjects()
    } catch (reason) {
      setError(readError(reason))
    } finally {
      setBusy(false)
    }
  }

  async function projectDialogDefaults(): Promise<Pick<ProjectDialogCommon, 'workflowMode' | 'targetLanguage' | 'enabledFontIds' | 'defaultFontId' | 'fallbackFontId' | 'availableFonts' | 'fontLanguageProfiles'>> {
    const settings = await api.getSettings()
    const targetLanguage = 'zh-Hans'
    const profile = settings.fontLanguageProfiles.find(item => item.targetLanguage.toLowerCase() === targetLanguage.toLowerCase())
    const enabledFontIds = profile?.allowedFontIds ?? []
    return {
      workflowMode: 'Expert',
      targetLanguage,
      enabledFontIds,
      defaultFontId: profile?.defaultFontId && enabledFontIds.includes(profile.defaultFontId)
        ? profile.defaultFontId
        : enabledFontIds[0] ?? null,
      fallbackFontId: null,
      availableFonts: settings.fonts,
      fontLanguageProfiles: settings.fontLanguageProfiles,
    }
  }

  async function beginFolderImport() {
    if (!emtHost) {
      setError(locale === 'en-US' ? 'Folder upload is not available in the web build. Use the desktop app.' : '当前 Web 构建尚未接入文件上传。请使用桌面端导入文件夹。')
      return
    }

    const defaults = await projectDialogDefaults()
    setDialog({
      kind: 'import-folder',
      name: '',
      sourceToken: '',
      sourceLabel: '',
      destinationToken: '',
      destinationLabel: '',
      ...defaults,
    })
  }

  async function beginPdfImport() {
    if (!emtHost) {
      setError(locale === 'en-US' ? 'PDF upload is not available in the web build. Use the desktop app.' : 'Web 版本暂未接入 PDF 上传，请使用桌面端导入 PDF。')
      return
    }

    const defaults = await projectDialogDefaults()
    setDialog({
      kind: 'import-pdf',
      name: '',
      sourceToken: '',
      sourceLabel: '',
      destinationToken: '',
      destinationLabel: '',
      renderDpi: 200,
      ...defaults,
    })
  }

  async function selectProjectDialogSource() {
    if (!dialog || !emtHost) return
    try {
      const grant = dialog.kind === 'import-pdf'
        ? await emtHost.selectImportPdf()
        : await emtHost.selectImportFolder()
      if (!grant) return
      setDialog(current => {
        if (!current || current.kind !== dialog.kind) return current
        const suggestedName = grant.displayName.replace(/\.pdf$/i, '')
        return {
          ...current,
          sourceToken: grant.token,
          sourceLabel: grant.displayPath ?? grant.displayName,
          name: current.name.trim() ? current.name : suggestedName,
        }
      })
    } catch (reason) {
      setError(readError(reason))
    }
  }

  async function selectProjectDialogDestination() {
    if (!dialog || !emtHost) return
    try {
      const grant = await emtHost.selectProjectParent()
      if (!grant) return
      setDialog(current => current ? {
        ...current,
        destinationToken: grant.token,
        destinationLabel: grant.displayPath ?? grant.displayName,
      } : current)
    } catch (reason) {
      setError(readError(reason))
    }
  }

  async function submitProjectDialog() {
    if (!dialog
        || !dialog.name.trim()
        || !dialog.destinationToken
        || dialog.enabledFontIds.length === 0
        || !dialog.sourceToken) return
    setBusy(true)
    setError(null)
    try {
      const projectSettings = {
        workflowMode: dialog.workflowMode,
        targetLanguage: dialog.targetLanguage,
        enabledFontIds: dialog.enabledFontIds,
        defaultFontId: dialog.defaultFontId,
        fallbackFontId: dialog.fallbackFontId,
      }
      const project = dialog.kind === 'import-folder'
        ? await api.importFolderProject(dialog.name.trim(), dialog.destinationToken, dialog.sourceToken, projectSettings)
        : await api.importPdfProject(dialog.name.trim(), dialog.destinationToken, dialog.sourceToken, dialog.renderDpi, projectSettings)
      setDialog(null)
      setActiveProject(project)
      await refreshProjects()
    } catch (reason) {
      setError(readError(reason))
    } finally {
      setBusy(false)
    }
  }

  async function closeProject() {
    projectFlushRef.current = async () => true
    setTaskCenterOpen(false)
    setWorkspaceActivity(null)
    setActiveProject(null)
    setError(null)
    await refreshProjects().catch((reason: unknown) => setError(readError(reason)))
  }

  async function removeRecentProject(projectId: string, deleteFiles: boolean) {
    setBusy(true)
    setError(null)
    try {
      await api.removeProject(projectId, deleteFiles)
      await refreshProjects()
      return true
    } catch (reason) {
      setError(readError(reason))
      return false
    } finally {
      setBusy(false)
    }
  }

  function changeLocale(next: Locale) {
    setLocale(next)
    writeLocale(next)
    void emtHost?.setLocale(next)
  }

  const projectSettingsLocked = workspaceActivity?.taskCenter?.exportJob?.status === 'Pending'
    || workspaceActivity?.taskCenter?.exportJob?.status === 'Running'

  return (
    <DialogProvider theme={theme} locale={locale}>
      <div className={`app-shell theme-${theme} locale-${locale} ${windowMaximized && emtHost?.platform === 'win32' ? 'window-maximized' : ''}`}>
      <header className="titlebar" role="banner">
        <div className="titlebar-drag-region" aria-hidden="true" />
        <img className="brand-logo" src={theme === 'dark' ? '/emt-logo-dark-transparent.svg' : '/emt-logo-light-transparent.svg'} alt="EMT" />
        <strong>EMT Studio</strong>
        <span className="product-name">Everyone Manga Translator</span>
        <button className="titlebar-settings" onClick={() => setSettingsOpen(true)} title={t(locale, '设置')}><SettingsIcon size={14}/><span>{t(locale, '设置')}</span></button>
        <div className="window-controls" aria-label={locale === 'en-US' ? 'Window controls' : '窗口控制'}>
          <button className="window-control" onClick={() => emtHost?.minimize()} aria-label={locale === 'en-US' ? 'Minimize' : '最小化'}><span className="window-icon window-minimize-icon"/></button>
          <button className="window-control" onClick={() => emtHost?.maximize()} aria-label={locale === 'en-US' ? (windowMaximized ? 'Restore' : 'Maximize') : (windowMaximized ? '还原' : '最大化')}><span className={`window-icon ${windowMaximized ? 'window-restore-icon' : 'window-maximize-icon'}`}/></button>
          <button className="window-control close" onClick={() => emtHost?.close()} aria-label={locale === 'en-US' ? 'Close' : '关闭'}><span className="window-icon window-close-icon"/></button>
        </div>
      </header>

      {activeProject
        ? (
            <ProjectWorkspace
              project={activeProject}
              onProjectChange={setActiveProject}
              onCloseProject={closeProject}
              onError={setError}
              error={error}
              onFlushReady={flush => { projectFlushRef.current = flush }}
              onActivityChange={setWorkspaceActivity}
            />
          )
        : (
            <ProjectStartPage
              projects={projects}
              busy={busy}
              error={error}
              onOpen={openProject}
              onImportFolder={beginFolderImport}
              onImportPdf={beginPdfImport}
              onOpenExisting={openExistingProject}
              onRemove={removeRecentProject}
            />
          )}

      {activeProject && taskCenterOpen && workspaceActivity?.taskCenter && (
        <TaskCenterPopover
          jobs={workspaceActivity.taskCenter.jobs}
          exportJob={workspaceActivity.taskCenter.exportJob}
          exportMessage={workspaceActivity.taskCenter.exportMessage}
          onStopExport={workspaceActivity.taskCenter.onStopExport}
          onClearFinished={workspaceActivity.taskCenter.onClearFinished ?? (async () => 0)}
          onClose={() => setTaskCenterOpen(false)}
        />
      )}
      <footer className={`statusbar ${workspaceActivity?.active ? 'statusbar-busy' : ''} ${taskCenterOpen ? 'task-center-expanded' : ''}`}>
        <span className="statusbar-runtime">{system ? (locale === 'en-US' ? 'Ready' : '就绪') : (locale === 'en-US' ? 'Connecting…' : '正在连接…')}</span>
        {activeProject && <span className="statusbar-project">{activeProject.name} · {locale === 'en-US' ? 'Autosave on' : '自动保存已开启'}</span>}
        {activeProject && workspaceActivity?.taskCenter
          ? <button className="statusbar-task" onClick={() => setTaskCenterOpen(value => !value)} title={locale === 'en-US' ? (taskCenterOpen ? 'Collapse task center' : 'Expand task center') : (taskCenterOpen ? '收起任务中心' : '展开任务中心')} aria-expanded={taskCenterOpen}><Clock3 size={12}/>{workspaceActivity.active && <RefreshCw className="status-spinner" size={12}/>}<b>{workspaceActivity.active ? workspaceActivity.label : (locale === 'en-US' ? 'Task center' : '任务中心')}</b>{workspaceActivity.active ? <><span>{workspaceActivity.progressPercent}%</span><i><em style={{ width: `${workspaceActivity.progressPercent}%` }}/></i></> : <span className="statusbar-task-idle">{locale === 'en-US' ? 'Idle' : '空闲'}</span>}{taskCenterOpen ? <ChevronDown className="statusbar-task-chevron" size={13}/> : <ChevronUp className="statusbar-task-chevron" size={13}/>}</button>
          : <span className="statusbar-ready">{activeProject ? (locale === 'en-US' ? 'No active tasks' : '暂无运行中的任务') : (locale === 'en-US' ? 'Ready' : '就绪')}</span>}
      </footer>

      {dialog && (
        <ProjectDialog
          state={dialog}
          busy={busy}
          locale={locale}
          onChange={setDialog}
          onSelectSource={selectProjectDialogSource}
          onSelectDestination={selectProjectDialogDestination}
          onCancel={() => setDialog(null)}
          onSubmit={submitProjectDialog}
        />
      )}
      {settingsOpen && <SettingsDialog theme={theme} locale={locale} project={projectSettingsLocked ? null : activeProject} onThemeChange={setTheme} onLocaleChange={changeLocale} onClose={() => setSettingsOpen(false)} onSaved={settings => { setTheme(settings.theme); setLocale(settings.locale); writeLocale(settings.locale); void emtHost?.setLocale(settings.locale); setSettingsOpen(false) }} onProjectSaved={setActiveProject} onError={setError}/>}
      </div>
    </DialogProvider>
  )
}


export default App
