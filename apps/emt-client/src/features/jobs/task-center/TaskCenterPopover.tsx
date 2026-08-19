import { useEffect, useMemo, useState } from 'react'
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleStop,
  Clock3,
  ListChecks,
  Trash2,
  XCircle,
} from 'lucide-react'
import type { JobSnapshot, ProcessEvent } from '../../../contracts'
import { dialogService } from '../../../dialogService'
import { readLocale } from '../../../i18n'
import { statusText } from '../../../shared/lib/formatters'
import { formatTaskDuration } from './formatTaskDuration'

const activeStatuses = new Set<JobSnapshot['status']>(['Pending', 'Running'])
const finishedStatuses = new Set<JobSnapshot['status']>(['Completed', 'Failed'])

function jobDuration(job: JobSnapshot, now: number) {
  const start = new Date(job.createdAt).getTime()
  const end = activeStatuses.has(job.status) ? now : new Date(job.updatedAt).getTime()
  return formatTaskDuration(Math.max(0, end - start), readLocale())
}

function eventSummary(events: ProcessEvent[]) {
  const completed = events.filter(event => event.status === 'Completed').length
  const failed = events.filter(event => event.status === 'Failed').length
  const running = events.filter(event => event.status === 'Running').length
  return { completed, failed, running, total: events.length }
}

function taskName(job: JobSnapshot, locale: 'zh-CN' | 'en-US') {
  if (job.jobId.startsWith('translation-')) return locale === 'en-US' ? 'Translation' : '翻译'
  if (job.jobId.startsWith('export-')) return locale === 'en-US' ? 'Export' : '导出'
  return locale === 'en-US' ? 'Expert preprocessing' : '专家预处理'
}

function latestEventText(job: JobSnapshot) {
  const event = job.events.find(item => item.status === 'Running')
    ?? job.events.find(item => item.status === 'Failed')
    ?? [...job.events].reverse().find(item => item.status === 'Completed')
  return event?.responseSummary ?? event?.requestSummary ?? '—'
}

function JobCard({ job, now, defaultExpanded = false }: {
  job: JobSnapshot
  now: number
  defaultExpanded?: boolean
}) {
  const locale = readLocale()
  const active = activeStatuses.has(job.status)
  const [expanded, setExpanded] = useState(defaultExpanded)
  const summary = eventSummary(job.events)
  const visibleEvents = expanded ? job.events : []
  const statusClass = job.status.toLowerCase()
  return <article className={`task-center-job ${statusClass}`}>
    <button className="task-center-job-summary" onClick={() => setExpanded(value => !value)} aria-expanded={expanded}>
      <span className="task-center-job-icon">
        {job.status === 'Completed' ? <CheckCircle2 size={16}/>
          : job.status === 'Failed' ? <XCircle size={16}/>
            : <Clock3 size={16}/>}
      </span>
      <span className="task-center-job-main">
        <span className="task-center-job-title"><b>{taskName(job, locale)}</b><em>{statusText(job.status)}</em></span>
        <small>{latestEventText(job)}</small>
        <span className="task-center-job-meta">
          <span>{locale === 'en-US' ? 'Elapsed' : '耗时'} <b>{jobDuration(job, now)}</b></span>
          <span>{locale === 'en-US'
            ? `${summary.completed}/${summary.total} steps${summary.failed ? ` · ${summary.failed} failed` : ''}`
            : `${summary.completed}/${summary.total} 项${summary.failed ? ` · ${summary.failed} 项失败` : ''}`}</span>
        </span>
      </span>
      <span className="task-center-job-progress"><b>{job.progressPercent}%</b>{expanded ? <ChevronDown size={14}/> : <ChevronRight size={14}/>}</span>
    </button>
    {active && <div className="progress-track"><i style={{ width: `${job.progressPercent}%` }}/></div>}
    {visibleEvents.length > 0 && <div className={`task-center-events ${expanded ? 'expanded' : 'compact'}`}>
      {visibleEvents.map(event => <div key={`${job.jobId}-${event.eventId}`} className={event.status.toLowerCase()}>
        <i/><span><b>{event.title}</b><small>{event.responseSummary ?? event.requestSummary}</small></span>
      </div>)}
    </div>}
  </article>
}

export function TaskCenterPopover({ jobs, exportJob, exportMessage, onStopExport, onClearFinished, onClose }: {
  jobs: JobSnapshot[]
  exportJob: JobSnapshot | null
  exportMessage: string | null
  onStopExport: () => Promise<boolean>
  onClearFinished: () => Promise<number>
  onClose: () => void
}) {
  const locale = readLocale()
  const allJobs = useMemo(() => {
    const candidates = [...jobs, ...(exportJob ? [exportJob] : [])]
    return candidates
      .filter((job, index) => candidates.findIndex(item => item.jobId === job.jobId) === index)
      .sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime())
  }, [exportJob, jobs])
  const activeJobs = allJobs.filter(job => activeStatuses.has(job.status))
  const historyJobs = allJobs.filter(job => !activeStatuses.has(job.status))
  const clearableCount = historyJobs.filter(job => finishedStatuses.has(job.status)).length
  const [historyOpen, setHistoryOpen] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (activeJobs.length === 0) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [activeJobs.length])

  async function clearFinished() {
    if (clearableCount === 0 || clearing) return
    if (!await dialogService.confirm({
      title: locale === 'en-US' ? 'Clear task history' : '清理任务历史',
      message: locale === 'en-US'
        ? `Delete ${clearableCount} completed or failed task records?`
        : `删除 ${clearableCount} 条已完成或失败的任务记录？`,
      detail: locale === 'en-US'
        ? 'Stopped and running tasks will be kept. Generated project results are not deleted.'
        : '运行中和已停止任务会保留，已生成的项目结果不会被删除。',
      destructive: true,
      confirmLabel: locale === 'en-US' ? 'Clear history' : '清理历史',
    })) return
    setClearing(true)
    try {
      if (await onClearFinished() > 0) setHistoryOpen(false)
    } finally {
      setClearing(false)
    }
  }

  return <section className="task-center-popover" role="dialog" aria-label={locale === 'en-US' ? 'Task center' : '任务中心'}>
    <div className="task-center-head">
      <div><b>{locale === 'en-US' ? 'Task center' : '任务中心'}</b><span>{activeJobs.length > 0
        ? (locale === 'en-US' ? `${activeJobs.length} active` : `${activeJobs.length} 个任务进行中`)
        : (locale === 'en-US' ? 'No running tasks' : '暂无运行中的任务')}</span></div>
      <div className="task-center-head-actions">
        {clearableCount > 0 && <button disabled={clearing} onClick={() => void clearFinished()} title={locale === 'en-US' ? 'Clear completed and failed tasks' : '清理已完成和失败任务'}><Trash2 size={14}/><span>{locale === 'en-US' ? 'Clear' : '清理'}</span></button>}
        <button onClick={onClose} title={locale === 'en-US' ? 'Collapse task center' : '收起任务中心'}><ChevronDown size={14}/><span>{locale === 'en-US' ? 'Collapse' : '收起'}</span></button>
      </div>
    </div>
    {exportMessage && !exportJob && <div className="task-center-complete"><CheckCircle2 size={15}/><span>{exportMessage}</span></div>}
    {activeJobs.length > 0 && <div className="task-center-section">
      <div className="task-center-section-title"><span><Clock3 size={14}/>{locale === 'en-US' ? 'Active' : '当前任务'}</span></div>
      {activeJobs.map(job => <div key={job.jobId} className="task-center-active-row">
        <JobCard job={job} now={now} defaultExpanded={job.status === 'Failed'}/>
        {job.jobId === exportJob?.jobId && activeStatuses.has(job.status) && <button className="task-center-stop" onClick={() => void onStopExport()}><CircleStop size={14}/>{locale === 'en-US' ? 'Stop export' : '停止导出'}</button>}
      </div>)}
    </div>}
    {historyJobs.length > 0 && <div className="task-center-history">
      <button className="task-center-history-toggle" onClick={() => setHistoryOpen(value => !value)} aria-expanded={historyOpen}>
        <span><ListChecks size={14}/>{locale === 'en-US' ? `History (${historyJobs.length})` : `历史任务（${historyJobs.length}）`}</span>
        {historyOpen ? <ChevronDown size={14}/> : <ChevronRight size={14}/>}
      </button>
      {historyOpen && <div className="task-center-history-list">{historyJobs.map(job => <JobCard key={job.jobId} job={job} now={now}/>)}</div>}
    </div>}
    {allJobs.length === 0 && !exportMessage ? <div className="task-center-empty">{locale === 'en-US' ? 'New preprocessing, translation, and export tasks will appear here.' : '新的预处理、翻译和导出任务会显示在这里。'}</div> : null}
  </section>
}
