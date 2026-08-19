import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { ArrowLeft, BookOpen, Download, FileImage, Play, Sparkles } from 'lucide-react'
import { api, apiBaseUrl } from '../../api'
import type { JobSnapshot, PrecheckReview, ProjectDetail } from '../../contracts'
import { dialogService } from '../../dialogService'
import { readLocale, t } from '../../i18n'
import { logBackgroundError, readError } from '../../shared/lib/errors'
import { statusText } from '../../shared/lib/formatters'
import type { ReviewPane, WorkspaceActivity, WorkspaceStage } from '../../shared/types/workspace'
import { buildTimeline } from '../jobs/model/timeline'
import { PrecheckEntityReviewPane } from '../precheck/review/PrecheckEntityReviewPane'
import { PageReviewPane } from '../precheck/review/PageReviewPane'
import { PrecheckRunPane } from '../precheck/run/PrecheckRunPane'
import { QuickPageSelection } from '../translation/selection/QuickPageSelection'
import { TranslationWorkspace } from '../translation/workspace/TranslationWorkspace'
import { ExportWorkspace } from '../translation/export/ExportWorkspace'

export type ProjectWorkspaceProps = {
  project: ProjectDetail
  onProjectChange: (project: ProjectDetail) => void
  onCloseProject: () => void
  onError: (error: string | null) => void
  error: string | null
  onFlushReady: (flush: () => Promise<boolean>) => void
  onActivityChange: (activity: WorkspaceActivity | null) => void
}

const visibleActivityPageLimit = 4
const activityPagePreviewCount = 3

function formatActivityPages(pageIndices: number[], locale: 'zh-CN' | 'en-US') {
  const pages = [...new Set(pageIndices)].sort((left, right) => left - right)
  if (pages.length === 0) return ''
  if (pages.length <= visibleActivityPageLimit) {
    return locale === 'en-US'
      ? ` · ${pages.length === 1 ? 'page' : 'pages'} ${pages.join(', ')}`
      : ` · 第 ${pages.join('、')} 页`
  }

  const preview = pages.slice(0, activityPagePreviewCount)
  return locale === 'en-US'
    ? ` · pages ${preview.join(', ')} and others (${pages.length} total)`
    : ` · 第 ${preview.join('、')} 等 ${pages.length} 页`
}

export function ProjectWorkspace({ project, onProjectChange, onCloseProject, onError, error, onFlushReady, onActivityChange }: ProjectWorkspaceProps) {
  const locale = readLocale()
  const [pane, setPane] = useState<ReviewPane>('run')
  const [stage, setStage] = useState<WorkspaceStage>(project.workflowMode === 'Quick' ? 'selection' : 'precheck')
  const [job, setJob] = useState<JobSnapshot | null>(null)
  const [translationJobs, setTranslationJobs] = useState<JobSnapshot[]>([])
  const [expanded, setExpanded] = useState<string | null>(null)
  const [review, setReview] = useState<PrecheckReview | null>(null)
  const [stageTransitionBusy, setStageTransitionBusy] = useState(false)
  const [activitySummary, setActivitySummary] = useState<Omit<WorkspaceActivity, 'taskCenter'> | null>(null)
  const [workspaceTaskCenter, setWorkspaceTaskCenter] = useState<WorkspaceActivity['taskCenter'] | undefined>(undefined)
  const [exportActive, setExportActive] = useState(false)
  const translationFlushRef = useRef<() => Promise<boolean>>(async () => true)
  const activeProjectIdRef = useRef<string | null>(project.projectId)
  activeProjectIdRef.current = project.projectId
  const registerTranslationFlush = useCallback((flush: () => Promise<boolean>) => {
    translationFlushRef.current = flush
  }, [])
  const clearFinishedJobs = useCallback(async () => {
    try {
      const result = await api.clearFinishedJobs(project.projectId)
      setJob(current => current?.status === 'Completed' || current?.status === 'Failed' ? null : current)
      setTranslationJobs(current => current.filter(item => item.status !== 'Completed' && item.status !== 'Failed'))
      return result.deletedCount
    } catch (reason) {
      onError(readError(reason))
      return 0
    }
  }, [onError, project.projectId])

  useEffect(() => {
    const projectId = project.projectId
    activeProjectIdRef.current = projectId
    return () => {
      if (activeProjectIdRef.current === projectId) activeProjectIdRef.current = null
    }
  }, [project.projectId])

  useEffect(() => {
    onFlushReady(() => translationFlushRef.current())
    return () => onFlushReady(async () => true)
  }, [onFlushReady, project.projectId])

  useEffect(() => {
    const projectId = project.projectId
    let cancelled = false
    setJob(null)
    setTranslationJobs([])
    setWorkspaceTaskCenter(undefined)
    setStage(project.workflowMode === 'Quick' ? 'selection' : 'precheck')
    setPane('run')
    api.listProjectJobs(projectId)
      .then(items => {
        if (cancelled) return
        const latest = items[0] ?? null
        setJob(latest)
      })
      .catch((reason: unknown) => logBackgroundError('project-jobs.load', reason, {
        projectId,
      }))
    return () => { cancelled = true }
  }, [project.projectId, project.workflowMode, onError])

  useEffect(() => {
    const projectId = project.projectId
    let cancelled = false
    api.listTranslationJobs(projectId)
      .then(items => {
        if (cancelled) return
        setTranslationJobs(items)
        if (project.workflowMode === 'Quick' && items.length > 0) setStage('translation')
      })
      .catch(() => { if (!cancelled) setTranslationJobs([]) })
    return () => { cancelled = true }
  }, [project.projectId, project.workflowMode])

  useEffect(() => {
    const projectId = project.projectId
    let cancelled = false
    setReview(null)
    if (project.workflowMode === 'Quick') return
    api.getPrecheckReview(projectId)
      .then(next => { if (!cancelled) setReview(next) })
      .catch(() => { if (!cancelled) setReview(null) })
    return () => { cancelled = true }
  }, [project.projectId, project.workflowMode])

  const jobSubscriptionSignature = useMemo(
    () => [job?.jobId, ...translationJobs.map(item => item.jobId)]
      .filter((value): value is string => Boolean(value))
      .join('|'),
    [job?.jobId, translationJobs],
  )

  useEffect(() => {
    const jobIds = jobSubscriptionSignature.split('|').filter(Boolean)
    if (jobIds.length === 0) return

    let disposed = false
    let refreshTimer: number | null = null
    let refreshReview = false
    const scheduleProjectRefresh = (includeReview: boolean) => {
      refreshReview ||= includeReview
      if (refreshTimer !== null) window.clearTimeout(refreshTimer)
      refreshTimer = window.setTimeout(() => {
        const shouldRefreshReview = refreshReview
        refreshReview = false
        const projectId = project.projectId
        void api.getProject(projectId).then(updated => {
          if (!disposed && activeProjectIdRef.current === projectId && updated.projectId === projectId) onProjectChange(updated)
        }).catch(() => undefined)
        if (shouldRefreshReview) {
          void api.getPrecheckReview(projectId).then(updated => {
            if (!disposed && activeProjectIdRef.current === projectId) setReview(updated)
          }).catch(() => undefined)
        }
      }, 120)
    }

    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/jobs`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('JobUpdated', (snapshot: JobSnapshot) => {
      if (snapshot.jobId.startsWith('translation-')) {
        setTranslationJobs(current => {
          const index = current.findIndex(item => item.jobId === snapshot.jobId)
          if (index < 0) return [snapshot, ...current]
          const next = [...current]
          next[index] = snapshot
          return next
        })
        scheduleProjectRefresh(false)
      } else {
        setJob(snapshot)
        scheduleProjectRefresh(snapshot.status === 'Completed')
      }
    })
    const start = connection.start()
      .then(async () => {
        await Promise.all(jobIds.map(id => connection.invoke('Subscribe', id)))
        const latestSnapshots = await Promise.all(jobIds.map(id => api.getJob(id)))
        if (disposed) return
        for (const snapshot of latestSnapshots) {
          if (snapshot.jobId.startsWith('translation-')) {
            setTranslationJobs(current => {
              const index = current.findIndex(item => item.jobId === snapshot.jobId)
              if (index < 0) return [snapshot, ...current]
              const next = [...current]
              next[index] = snapshot
              return next
            })
          } else {
            setJob(snapshot)
          }
        }
      })
      .catch((reason: unknown) => {
        if (!disposed) logBackgroundError('job-updates.connect', reason, {
          projectId: project.projectId,
          jobIds,
        })
      })

    return () => {
      disposed = true
      if (refreshTimer !== null) window.clearTimeout(refreshTimer)
      // SignalR rejects stop() while start() is still pending.  Wait for the
      // current start attempt to settle so asynchronous job-list loading can
      // replace subscriptions without surfacing a false connection error.
      void start.then(() => connection.stop())
    }
  }, [jobSubscriptionSignature, project.projectId, onProjectChange, onError])

  const timeline = useMemo(() => buildTimeline(job?.events ?? []), [job?.events])
  const latestTranslationJob = translationJobs[0] ?? null
  const activeTranslationJobs = useMemo(
    () => translationJobs.filter(item => item.status === 'Pending' || item.status === 'Running'),
    [translationJobs],
  )
  useEffect(() => {
    if (activeTranslationJobs.length > 0) {
      const events = activeTranslationJobs.flatMap(item => item.events)
      const runningPages = [...new Set(events
        .filter(event => event.status === 'Running')
        .flatMap(event => event.pageIndices))]
      const progressPercent = events.length === 0
        ? 0
        : Math.round(events.filter(event => event.status === 'Completed').length * 100 / events.length)
      setActivitySummary({
        active: true,
        label: locale === 'en-US'
          ? `Generating translation${formatActivityPages(runningPages, locale)}`
          : `正在生成翻译${formatActivityPages(runningPages, locale)}`,
        progressPercent,
      })
      return
    }
    if (job?.status !== 'Pending' && job?.status !== 'Running') {
      setActivitySummary(null)
      return
    }
    const runningEvent = job.events.find(event => event.status === 'Running')
    const pageLabel = formatActivityPages(runningEvent?.pageIndices ?? [], locale)
    setActivitySummary({
      active: true,
      label: `${locale === 'en-US' ? 'Preprocessing' : '正在预处理'}${pageLabel}`,
      progressPercent: job.progressPercent,
    })
  }, [activeTranslationJobs, job, locale])

  useEffect(() => {
    const taskCenterJobs = [
      ...(job ? [job] : []),
      ...(workspaceTaskCenter?.jobs ?? []),
    ].filter((item, index, items) => items.findIndex(candidate => candidate.jobId === item.jobId) === index)
    const taskCenter = taskCenterJobs.length > 0 || workspaceTaskCenter?.exportJob || workspaceTaskCenter?.exportMessage
      ? {
          jobs: taskCenterJobs,
          exportJob: workspaceTaskCenter?.exportJob ?? null,
          exportMessage: workspaceTaskCenter?.exportMessage ?? null,
          onStopExport: workspaceTaskCenter?.onStopExport ?? (async () => true),
          onClearFinished: workspaceTaskCenter?.onClearFinished ?? clearFinishedJobs,
        }
      : undefined
    const activeExport = taskCenter?.exportJob?.status === 'Pending' || taskCenter?.exportJob?.status === 'Running'
    const fallbackActivity = activeExport
      ? {
          active: true,
          label: locale === 'en-US' ? 'Exporting project' : '正在导出项目',
          progressPercent: taskCenter?.exportJob?.progressPercent ?? 0,
        }
      : null
    const status = fallbackActivity ?? activitySummary
    if (!status && !taskCenter) {
      onActivityChange(null)
      return
    }
    onActivityChange({
      active: status?.active ?? false,
      label: status?.label ?? (locale === 'en-US' ? 'Task center' : '任务中心'),
      progressPercent: status?.progressPercent ?? 0,
      taskCenter,
    })
    return () => onActivityChange(null)
  }, [activitySummary, clearFinishedJobs, job, locale, onActivityChange, workspaceTaskCenter])

  async function startJob() {
    const projectId = project.projectId
    onError(null)
    setExpanded(null)
    try {
      const snapshot = await api.startPrecheck(projectId, 'Live')
      if (activeProjectIdRef.current !== projectId) return
      setJob(snapshot)
      const updatedProject = await api.getProject(projectId)
      if (activeProjectIdRef.current !== projectId || updatedProject.projectId !== projectId) return
      onProjectChange(updatedProject)
      const updatedReview = await api.getPrecheckReview(projectId)
      if (activeProjectIdRef.current === projectId) setReview(updatedReview)
    } catch (reason) {
      if (activeProjectIdRef.current === projectId) onError(readError(reason))
    }
  }

  async function stopJob() {
    if (!job) return true
    try {
      setJob(await api.stopJob(job.jobId))
      return true
    } catch (reason) {
      onError(readError(reason))
      return false
    }
  }

  async function resumeJob() {
    if (!job) return
    try {
      setJob(await api.resumeJob(job.jobId))
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function retryPrecheckJob() {
    if (!job) return
    try {
      setJob(await api.retryPrecheckJob(job.jobId))
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function startTranslation(pageIndices?: number[]) {
    onError(null)
    try {
      const snapshot = await api.startTranslation(project.projectId, 'Live', undefined, pageIndices)
      setTranslationJobs(current => [snapshot, ...current.filter(item => item.jobId !== snapshot.jobId)])
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function stopTranslation() {
    const activeJobs = translationJobs.filter(item => item.status === 'Pending' || item.status === 'Running')
    if (activeJobs.length === 0) return true
    try {
      const stopped = await Promise.all(activeJobs.map(item => api.stopJob(item.jobId)))
      setTranslationJobs(current => current.map(item => stopped.find(snapshot => snapshot.jobId === item.jobId) ?? item))
      return true
    } catch (reason) {
      onError(readError(reason))
      return false
    }
  }

  async function resumeTranslation() {
    const stoppedJobs = translationJobs.filter(item => item.status === 'Stopped')
    if (stoppedJobs.length === 0) return
    try {
      const resumed = await Promise.all(stoppedJobs.map(item => api.resumeJob(item.jobId)))
      setTranslationJobs(current => current.map(item => resumed.find(snapshot => snapshot.jobId === item.jobId) ?? item))
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function switchStage(nextStage: WorkspaceStage) {
    if (project.workflowMode === 'Quick' && nextStage === 'precheck') return
    if (project.workflowMode === 'Expert' && nextStage === 'selection') return
    if (nextStage === stage || stageTransitionBusy || exportActive) return
    setStageTransitionBusy(true)
    try {
      // Translation editors debounce their writes.  Flush before leaving the
      // workspace so a stage switch never strands a layout, image edit, or
      // annotation in memory.
      if (stage === 'translation' && !await translationFlushRef.current()) return
      setStage(nextStage)
    } finally {
      setStageTransitionBusy(false)
    }
  }

  async function closeCurrentProject() {
    if (!await translationFlushRef.current()) return
    let activeExports: JobSnapshot[] = []
    try {
      activeExports = (await api.listExportJobs(project.projectId))
        .filter(item => item.status === 'Pending' || item.status === 'Running')
    } catch {
      // Project close can still proceed for precheck/translation jobs if export history cannot be loaded.
    }
    const activeJobs = [job, ...activeTranslationJobs, ...activeExports].filter((item): item is JobSnapshot => item?.status === 'Pending' || item?.status === 'Running')
    if (activeJobs.length > 0) {
      if (!await dialogService.confirm({
        title: locale === 'en-US' ? 'Tasks still running' : '任务仍在运行',
        message: locale === 'en-US'
          ? `${activeJobs.length} ${activeJobs.length === 1 ? 'task is' : 'tasks are'} still running. Close the project and stop them?`
          : `当前有 ${activeJobs.length} 个任务正在运行。关闭项目会停止这些任务，是否继续？`,
        detail: locale === 'en-US' ? 'Completed batches and intermediate results will be kept.' : '已完成的批次和中间结果会被保留。',
        destructive: true,
        confirmLabel: locale === 'en-US' ? 'Stop and close' : '停止并关闭',
      })) return
      const stopResults = await Promise.all([
        job?.status === 'Pending' || job?.status === 'Running' ? stopJob() : Promise.resolve(true),
        activeTranslationJobs.length > 0 ? stopTranslation() : Promise.resolve(true),
        ...activeExports.map(item => api.stopJob(item.jobId).then(() => true).catch(reason => { onError(readError(reason)); return false })),
      ])
      if (!stopResults.every(Boolean)) return
    }
    onCloseProject()
  }

  return (
    <>
      <div className="workspace-tabs">
        {project.workflowMode === 'Expert' && <button className={stage === 'precheck' ? 'active' : ''} disabled={stageTransitionBusy || exportActive} onClick={() => void switchStage('precheck')}><Sparkles size={15}/>{locale === 'en-US' ? 'Expert preprocessing' : '专家预处理'}</button>}
        {project.workflowMode === 'Quick' && <button className={stage === 'selection' ? 'active' : ''} disabled={stageTransitionBusy || exportActive} onClick={() => void switchStage('selection')}><FileImage size={15}/>{locale === 'en-US' ? 'Select pages' : '选择页面'}</button>}
        <button className={stage === 'translation' ? 'active' : ''} disabled={stageTransitionBusy || exportActive} onClick={() => void switchStage('translation')}><BookOpen size={15}/>{t(locale, '翻译工作区')}</button>
        <button className={stage === 'export' ? 'active' : ''} disabled={stageTransitionBusy} onClick={() => void switchStage('export')}><Download size={15}/>{locale === 'en-US' ? 'Export' : '导出'}</button>
        <span className="project-workflow-badge">{project.workflowMode === 'Quick' ? (locale === 'en-US' ? 'Quick mode' : '快速模式') : (locale === 'en-US' ? 'Expert mode' : '专家模式')}</span>
        <button className="project-switch" onClick={() => void closeCurrentProject()}><ArrowLeft size={14}/>{project.name}</button>
      </div>

      <main className="layout" hidden={stage !== 'precheck'}>
        <aside className="navigation">
          <h2>{locale === 'en-US' ? 'Expert preprocessing' : '专家预处理'}</h2>
          <button className={pane === 'run' ? 'active' : ''} onClick={() => setPane('run')}><Play size={15}/>{t(locale, '运行过程')}</button>
          <div className="nav-label">{locale === 'en-US' ? 'Review results' : '审阅结果'}</div>
          <button className={pane === 'pages' ? 'active' : ''} onClick={() => setPane('pages')}><FileImage size={15}/>{locale === 'en-US' ? 'Page routing' : '页面纳入'}</button>
          <button className={pane === 'characters' ? 'active' : ''} onClick={() => setPane('characters')}><Sparkles size={15}/>{locale === 'en-US' ? 'Story Bible' : 'Story Bible'}</button>
          <div className="project-pages">{locale === 'en-US' ? `${project.pages.length} pages` : `${project.pages.length} 个页面`}</div>
        </aside>

        {pane === 'run' && (
          <PrecheckRunPane
            project={project}
            job={job}
            timeline={timeline}
            expanded={expanded}
            error={error}
            onExpandedChange={setExpanded}
            onStart={startJob}
            onStop={stopJob}
            onResume={resumeJob}
            onRetry={retryPrecheckJob}
            onReviewPages={() => setPane('pages')}
            onReviewStory={() => setPane('characters')}
            onContinue={() => void switchStage('translation')}
          />
        )}

        {pane === 'pages' && (
          <PageReviewPane
            project={project}
            review={review}
            error={error}
            onProjectChange={onProjectChange}
            onError={onError}
          />
        )}

        {pane === 'characters' && (
          <PrecheckEntityReviewPane
            project={project}
            review={review}
            onProjectChange={onProjectChange}
            onReviewChange={setReview}
            onError={onError}
          />
        )}

        <aside className="details">
          <h2>{t(locale, '项目状态')}</h2>
          <dl>
            <div><dt>{locale === 'en-US' ? 'Project' : '项目'}</dt><dd>{project.name}</dd></div>
            <div><dt>{locale === 'en-US' ? 'Pages' : '页面'}</dt><dd>{project.pages.length}</dd></div>
            <div><dt>{locale === 'en-US' ? 'Job' : '任务'}</dt><dd>{job ? statusText(job.status) : (locale === 'en-US' ? 'Not created' : '未创建')}</dd></div>
          </dl>
        </aside>
      </main>
      {project.workflowMode === 'Quick' && (
        <QuickPageSelection
          active={stage === 'selection'}
          project={project}
          job={activeTranslationJobs[0] ?? latestTranslationJob}
          error={error}
          onProjectChange={onProjectChange}
          onError={onError}
          onContinue={() => void switchStage('translation')}
        />
      )}
      <TranslationWorkspace
        active={stage === 'translation'}
        project={project}
        jobs={translationJobs}
        error={error}
        onStart={startTranslation}
        onStop={stopTranslation}
        onResume={resumeTranslation}
        onProjectChange={onProjectChange}
        onError={onError}
        onFlushReady={registerTranslationFlush}
        editingLocked={exportActive}
      />
      <ExportWorkspace active={stage === 'export'} project={project} jobs={translationJobs} error={error} onError={onError} onClearFinishedJobs={clearFinishedJobs} onTaskCenterChange={setWorkspaceTaskCenter} onExportingChange={setExportActive}/>
    </>
  )
}
