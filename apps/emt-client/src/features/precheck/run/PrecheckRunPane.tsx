import { ArrowRight, CircleStop, FileCheck2, Sparkles } from 'lucide-react'
import type { JobSnapshot, ProjectDetail } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { isPresentableError } from '../../../shared/lib/errors'
import { statusText } from '../../../shared/lib/formatters'
import type { TimelineEntry } from '../../../shared/types/workspace'
import { Message, ParallelMessage, ProcessMessage } from './PrecheckMessages'

export type PrecheckRunPaneProps = {
  project: ProjectDetail
  job: JobSnapshot | null
  timeline: TimelineEntry[]
  expanded: string | null
  error: string | null
  onExpandedChange: (eventId: string | null) => void
  onStart: () => void
  onStop: () => void
  onResume: () => void
  onRetry: () => void
  onReviewPages: () => void
  onReviewStory: () => void
  onContinue: () => void
}

export function PrecheckRunPane({ project, job, timeline, expanded, error, onExpandedChange, onStart, onStop, onResume, onRetry, onReviewPages, onReviewStory, onContinue }: PrecheckRunPaneProps) {
  const locale = readLocale()
  const running = job?.status === 'Running'
  return (
    <section className="content">
      <div className="content-header">
        <div>
          <h1>{locale === 'en-US' ? 'Run preprocessing' : '预处理运行'}</h1>
          <p>{locale === 'en-US' ? 'Classify pages in fixed parallel scans, then consolidate one whole-book Story Bible.' : '按固定连续批次并行识别页面类型，再一次性归并全书 Story Bible。'}</p>
        </div>
        <div className="job-actions">
          {!job && <button className="primary ai-action-button" onClick={onStart}><Sparkles size={14}/>{locale === 'en-US' ? 'Start' : '开始'}</button>}
          {job?.status === 'Running' && <button onClick={onStop}><CircleStop size={14}/>{locale === 'en-US' ? 'Stop' : '停止'}</button>}
          {job?.status === 'Stopped' && <button className="primary ai-action-button" onClick={onResume}><Sparkles size={14}/>{locale === 'en-US' ? 'Continue' : '继续'}</button>}
          {job?.status === 'Failed' && <button className="primary ai-action-button" onClick={onRetry}><Sparkles size={14}/>{locale === 'en-US' ? 'Retry failed parts' : '重试失败部分'}</button>}
          {job && <button className="ai-action-button" onClick={onStart} disabled={running}><Sparkles size={14}/>{locale === 'en-US' ? 'Run again' : '重新运行'}</button>}
        </div>
      </div>

      {error && isPresentableError(error) && <div className="error-banner">{error}</div>}

      {job?.status === 'Completed' && <section className="precheck-complete-guide"><div><FileCheck2 size={21}/><span><b>{locale === 'en-US' ? 'Preprocessing complete' : '预处理已完成'}</b><small>{locale === 'en-US' ? 'Review page routing and the Story Bible before starting translation.' : '建议先审阅页面纳入结果和 Story Bible，再进入翻译工作区。'}</small></span></div><div><button onClick={onReviewPages}>{locale === 'en-US' ? 'Review pages' : '审阅页面'}</button><button onClick={onReviewStory}>{locale === 'en-US' ? 'Review Story Bible' : '审阅 Story Bible'}</button><button className="primary ai-action-button" onClick={onContinue}>{locale === 'en-US' ? 'Go to translation' : '进入翻译工作区'}<ArrowRight size={14}/></button></div></section>}

      <div className="progress-row">
        <span>{job ? statusText(job.status) : (locale === 'en-US' ? 'Not started' : '尚未开始')}</span>
        <b>{job?.progressPercent ?? 0}%</b>
      </div>
      <div className="progress-track"><i style={{ width: `${job?.progressPercent ?? 0}%` }}/></div>

      <div className="conversation">
        {job && (
          <Message
            side="user"
            author={locale === 'en-US' ? 'You' : '你'}
            text={locale === 'en-US' ? `Start preprocessing ${project.name}.` : `开始预处理 ${project.name}。`}
          />
        )}

        {!job && (
          <div className="empty-state">
            <Sparkles size={24}/>
            <h2>{locale === 'en-US' ? 'Ready to preprocess' : '准备运行预处理'}</h2>
            <p>{locale === 'en-US' ? 'Progress will show fixed page scans, conservative page routing, and the final Story Bible merge.' : '这里将依次显示固定页面扫描、保守的页面纳入判断和最终 Story Bible 归并。'}</p>
          </div>
        )}

        {timeline.map(entry => entry.kind === 'event'
          ? (
              <ProcessMessage
                key={entry.event.eventId}
                event={entry.event}
                paused={job?.status === 'Stopped'}
                expanded={expanded === entry.event.eventId}
                onToggle={() => onExpandedChange(expanded === entry.event.eventId ? null : entry.event.eventId)}
              />
            )
          : (
              <ParallelMessage
                key={entry.groupId}
                batches={entry.events}
                paused={job?.status === 'Stopped'}
                expanded={expanded === entry.groupId}
                onToggle={() => onExpandedChange(expanded === entry.groupId ? null : entry.groupId)}
              />
            ))}

        {job?.status === 'Stopped' && (
          <Message side="assistant" author={locale === 'en-US' ? 'Preprocessing assistant' : '预处理助手'} text={locale === 'en-US' ? 'The user stopped this run. Completed intermediate results were kept and can be resumed later.' : '用户已停止当前任务。已完成的中间结果会被保留，可以随时继续。'}/>
        )}
        {job?.status === 'Failed' && (
          <Message side="assistant" author={locale === 'en-US' ? 'Preprocessing assistant' : '预处理助手'} text={locale === 'en-US' ? 'Preprocessing did not complete. Completed scans were kept; retrying will run only the failed or unfinished parts.' : '预处理未完成。已完成的扫描结果会保留；重试时只运行失败或尚未完成的部分。'}/>
        )}
      </div>
    </section>
  )
}
