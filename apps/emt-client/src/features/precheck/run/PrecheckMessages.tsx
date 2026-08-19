import { Check, Clock3, Sparkles } from 'lucide-react'
import type { ProcessEvent } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { eventStatusText } from '../../../shared/lib/formatters'

export function Message({ side, author, text }: { side: 'user' | 'assistant'; author: string; text: string }) {
  const locale = readLocale()
  return (
    <article className={`message ${side}`}>
      <div className="avatar">{side === 'user' ? (locale === 'en-US' ? 'You' : '你') : <Sparkles size={14}/>}</div>
      <div><div className="author">{author}</div><div className="bubble">{text}</div></div>
    </article>
  )
}

export function ProcessMessage({ event, paused, expanded, onToggle }: { event: ProcessEvent; paused: boolean; expanded: boolean; onToggle: () => void }) {
  if (event.status === 'Pending') return null
  const locale = readLocale()
  const pausedRunning = paused && event.status === 'Running'
  return (
    <article className="message assistant">
      <div className="avatar"><Sparkles size={14}/></div>
      <div>
        <div className="author">{locale === 'en-US' ? 'Preprocessing assistant' : '预处理助手'}<span>{stageLabel(event.stage, locale)}</span></div>
        <button className="process" onClick={onToggle}>
          <span className={`state ${pausedRunning ? 'paused' : event.status.toLowerCase()}`}>
            {event.status === 'Completed' ? <Check size={13}/> : pausedRunning ? <Clock3 size={13}/> : <span className="spinner"/>}
          </span>
          <span><b>{event.title}</b><small>{event.responseSummary ?? event.requestSummary}</small></span>
        </button>
        {expanded && (
          <div className="exchange">
            <div><label>{locale === 'en-US' ? 'Request' : '请求'}</label><p>{event.requestSummary}</p></div>
            <div><label>{locale === 'en-US' ? 'Response' : '响应'}</label><p>{event.responseSummary ?? (locale === 'en-US' ? 'Generating result…' : '正在生成结果…')}</p>{event.detail && <code>{event.detail}</code>}</div>
          </div>
        )}
      </div>
    </article>
  )
}

export function ParallelMessage({ batches, paused, expanded, onToggle }: { batches: ProcessEvent[]; paused: boolean; expanded: boolean; onToggle: () => void }) {
  if (!batches.some(item => item.status !== 'Pending')) return null
  const locale = readLocale()
  const completedCount = batches.filter(item => item.status === 'Completed').length
  const runningCount = batches.filter(item => item.status === 'Running').length
  return (
    <article className="message assistant">
      <div className="avatar"><Sparkles size={14}/></div>
      <div>
        <div className="author">{locale === 'en-US' ? 'Preprocessing assistant' : '预处理助手'}<span>{locale === 'en-US' ? 'Parallel page scans' : '并行页面扫描'}</span></div>
        <div className="parallel">
          <button className="parallel-title" onClick={onToggle}>
            <b>{locale === 'en-US' ? `Scan ${batches.length} fixed page batches in parallel` : `并行扫描 ${batches.length} 个固定页面批次`}</b>
            <span>{locale === 'en-US' ? (completedCount === batches.length ? 'All complete' : `${runningCount} running · ${completedCount} complete`) : (completedCount === batches.length ? '全部完成' : `${runningCount} 个运行中 · ${completedCount} 个完成`)}</span>
          </button>
          <div className="batch-grid">
            {batches.map(batch => (
              <div className="batch" key={batch.eventId}>
                <div><b>{batch.eventId}</b><span className={paused && batch.status === 'Running' ? 'paused' : batch.status.toLowerCase()}>{paused && batch.status === 'Running' ? (locale === 'en-US' ? 'Paused' : '已暂停') : eventStatusText(batch.status)}</span></div>
                <small>{locale === 'en-US' ? `Pages ${batch.pageIndices.join(', ')}` : `页面 ${batch.pageIndices.join('、')}`}</small>
                {expanded && <p>{batch.responseSummary ?? batch.requestSummary}</p>}
              </div>
            ))}
          </div>
        </div>
      </div>
    </article>
  )
}

function stageLabel(stage: string, locale: 'zh-CN' | 'en-US') {
  const labels: Record<string, [string, string]> = {
    manifest: ['页面清单', 'Page manifest'],
    'scan-plan': ['扫描计划', 'Scan plan'],
    'page-scan': ['页面扫描', 'Page scan'],
    'page-routing': ['页面路由', 'Page routing'],
    'translation-context': ['Story Bible', 'Story Bible'],
  }
  const label = labels[stage]
  return label ? label[locale === 'en-US' ? 1 : 0] : stage
}
