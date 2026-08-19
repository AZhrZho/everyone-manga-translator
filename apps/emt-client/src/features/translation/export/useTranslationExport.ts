import { useCallback, useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { api, apiBaseUrl } from '../../../api'
import type { JobSnapshot, ProjectDetail, ProjectExportFormat, ProjectExportResolution } from '../../../contracts'
import { emtHost } from '../../../host'
import { readLocale } from '../../../i18n'
import { logBackgroundError, readError } from '../../../shared/lib/errors'
import type { WorkspaceActivity } from '../../../shared/types/workspace'

type UseTranslationExportOptions = {
  project: ProjectDetail
  jobs: JobSnapshot[]
  onError: (error: string | null) => void
  onClearFinished: () => Promise<number>
  onTaskCenterChange: (taskCenter: WorkspaceActivity['taskCenter'] | undefined) => void
}

export function useTranslationExport({ project, jobs, onError, onClearFinished, onTaskCenterChange }: UseTranslationExportOptions) {
  const [exportFormat, setExportFormat] = useState<ProjectExportFormat>('Folder')
  const [exportResolution, setExportResolution] = useState<ProjectExportResolution>('Original')
  const [includeBackground, setIncludeBackground] = useState(true)
  const [includeLettering, setIncludeLettering] = useState(true)
  const [destinationToken, setDestinationToken] = useState<string | null>(null)
  const [destinationLabel, setDestinationLabel] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)
  const [exportJob, setExportJob] = useState<JobSnapshot | null>(null)
  const [exportMessage, setExportMessage] = useState<string | null>(null)
  const stopExportRef = useRef<() => Promise<boolean>>(async () => true)
  const exportJobId = exportJob?.jobId ?? null
  const languageName = project.targetLanguage || 'translated'
  const pathSeparator = project.locationLabel.includes('\\') ? '\\' : '/'
  const defaultDestinationLabel = `${project.locationLabel.replace(/[\\/]$/, '')}${pathSeparator}output${pathSeparator}${languageName}${exportFormat === 'Zip' ? '.zip' : exportFormat === 'Pdf' ? '.pdf' : ''}`

  useEffect(() => {
    setDestinationToken(null)
    setDestinationLabel(null)
  }, [exportFormat, project.projectId])

  useEffect(() => {
    const projectId = project.projectId
    let cancelled = false
    api.listExportJobs(projectId)
      .then(items => {
        if (cancelled) return
        const latest = items[0] ?? null
        setExportJob(latest)
        const activeExport = latest?.status === 'Pending' || latest?.status === 'Running'
        setExporting(activeExport)
      })
      .catch((reason: unknown) => {
        if (!cancelled) logBackgroundError('export-jobs.load', reason, { projectId })
      })
    return () => { cancelled = true }
  }, [project.projectId])

  useEffect(() => {
    if (!exportJobId) return
    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/jobs`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()
    let disposed = false
    connection.on('JobUpdated', (snapshot: JobSnapshot) => {
      if (snapshot.jobId !== exportJobId) return
      setExportJob(snapshot)
      if (snapshot.status === 'Completed') {
        setExporting(false)
        setExportMessage(snapshot.events[0]?.responseSummary ?? (readLocale() === 'en-US' ? 'Export complete' : '导出完成'))
      } else if (snapshot.status === 'Failed') {
        setExporting(false)
        onError(snapshot.events[0]?.detail ?? (readLocale() === 'en-US' ? 'Export failed' : '导出失败'))
      }
    })
    const start = connection.start()
      .then(async () => {
        await connection.invoke('Subscribe', exportJobId)
        const latest = await api.getJob(exportJobId)
        if (!disposed) {
          setExportJob(latest)
          if (latest.status === 'Completed') {
            setExporting(false)
            setExportMessage(latest.events[0]?.responseSummary ?? (readLocale() === 'en-US' ? 'Export complete' : '导出完成'))
          } else if (latest.status === 'Failed') {
            setExporting(false)
            onError(latest.events[0]?.detail ?? (readLocale() === 'en-US' ? 'Export failed' : '导出失败'))
          }
        }
      })
      .catch((reason: unknown) => {
        if (!disposed) logBackgroundError('export-job-updates.connect', reason, {
          projectId: project.projectId,
          jobId: exportJobId,
        })
      })
    return () => { disposed = true; void start.then(() => connection.stop()) }
  }, [exportJobId, onError, project.projectId])

  async function browseDestination() {
    const locale = readLocale()
    if (!emtHost) {
      onError(locale === 'en-US' ? 'Choosing a local export destination is not available in the web build. Use the desktop app.' : 'Web 版本暂未接入下载目标，请在桌面端导出。')
      return
    }
    try {
      const safeName = project.name.replace(/[<>:"/\\|?*]+/g, '_') || 'emt-export'
      const grant = exportFormat === 'Folder'
        ? await emtHost.selectExportFolder()
        : exportFormat === 'Zip'
          ? await emtHost.selectExportZip(safeName)
          : await emtHost.selectExportPdf(safeName)
      if (!grant) {
        return
      }
      setDestinationToken(grant.token)
      setDestinationLabel(grant.displayPath ?? grant.displayName)
    } catch (reason) {
      onError(readError(reason))
    }
  }

  async function exportProject() {
    if (!includeBackground && !includeLettering) return
    setExporting(true)
    setExportMessage(null)
    onError(null)
    try {
      const snapshot = await api.startExport(project.projectId, destinationToken, exportFormat, includeBackground, includeLettering, exportResolution)
      setExportJob(snapshot)
    } catch (reason) {
      onError(readError(reason))
      setExporting(false)
    }
  }

  async function stopExport() {
    if (!exportJob || (exportJob.status !== 'Pending' && exportJob.status !== 'Running')) return true
    try {
      const stopped = await api.stopJob(exportJob.jobId)
      setExportJob(stopped)
      setExporting(false)
      return true
    } catch (reason) {
      onError(readError(reason))
      return false
    }
  }

  stopExportRef.current = stopExport

  const clearFinished = useCallback(async () => {
    const deletedCount = await onClearFinished()
    setExportJob(current => current?.status === 'Completed' || current?.status === 'Failed' ? null : current)
    setExportMessage(null)
    return deletedCount
  }, [onClearFinished])

  useEffect(() => {
    const hasTaskData = jobs.length > 0 || exportJob !== null || exportMessage !== null
    onTaskCenterChange(hasTaskData ? {
      jobs,
      exportJob,
      exportMessage,
      onStopExport: () => stopExportRef.current(),
      onClearFinished: clearFinished,
    } : undefined)
    return () => onTaskCenterChange(undefined)
  }, [clearFinished, jobs, exportJob, exportMessage, onTaskCenterChange])

  return {
    exportFormat,
    setExportFormat,
    exportResolution,
    setExportResolution,
    includeBackground,
    setIncludeBackground,
    includeLettering,
    setIncludeLettering,
    destinationLabel: destinationLabel ?? defaultDestinationLabel,
    usingDefaultDestination: destinationToken === null,
    browseDestination,
    resetDestination: () => { setDestinationToken(null); setDestinationLabel(null) },
    exporting,
    exportJob,
    exportMessage,
    exportProject,
  }
}
