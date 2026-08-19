import { Sparkles } from 'lucide-react'
import type { PageReviewTag } from '../../../contracts'
import { readLocale, t } from '../../../i18n'

export function TranslationAnnotationPanel({ value, reviewTag, saveState, onChange, onCommit, onReject, onRerun, disabled, excluded, hasAiResult }: {
  value: string
  reviewTag: PageReviewTag
  saveState: 'saved' | 'saving' | 'error'
  onChange: (value: string) => void
  onCommit: () => void
  onReject: () => void
  onRerun: () => void
  disabled: boolean
  excluded: boolean
  hasAiResult: boolean
}) {
  const locale = readLocale()
  return <section className="translation-annotation-panel">
    <div className="translation-side-title"><b>{t(locale, '页面批注')}</b><span>{t(locale, saveState === 'saving' ? '保存中…' : saveState === 'error' ? '保存失败' : '已保存')}</span></div>
    <div className="translation-review-shortcuts"><span><kbd>←</kbd><kbd>→</kbd>{locale === 'en-US' ? 'Previous / next page' : '上一页 / 下一页'}</span><span><kbd>↑</kbd>{locale === 'en-US' ? 'Accept' : '接受'}</span><span><kbd>↓</kbd>{locale === 'en-US' ? 'Reject' : '驳回'}</span></div>
    <textarea value={value} onChange={event => onChange(event.target.value)} onBlur={onCommit} maxLength={4000} placeholder={locale === 'en-US' ? 'Tell AI what needs attention on this page…' : '告诉 AI 这页需要注意什么…'} rows={4} disabled={disabled}/>
    <small>{excluded ? (locale === 'en-US' ? 'This page is excluded from translation. Include it during page selection or preprocessing before running AI.' : '该页面已排除，需先在选页或预处理阶段重新纳入，才能运行 AI。') : !hasAiResult ? (locale === 'en-US' ? 'Run the first generation before adding review annotations or regenerating this page.' : '请先完成本页的首次生成，之后才可添加校对批注并重新生成。') : (locale === 'en-US' ? 'Annotations and manually adjusted lettering constraints are sent together when regenerating.' : '重新生成时，批注与用户手调过的嵌字约束会一起传给 AI。')}</small>
    <div className="annotation-actions"><button className="annotation-reject-button" onClick={onReject} disabled={disabled || reviewTag === 'Rejected'}>{reviewTag === 'Rejected' ? t(locale, '已驳回') : t(locale, '驳回')}</button><button className="annotation-rerun-button ai-action-button" onClick={onRerun} disabled={disabled}><Sparkles size={13}/>{t(locale, '立即重新生成')}</button></div>
  </section>
}
