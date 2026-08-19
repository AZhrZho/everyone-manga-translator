import { CircleStop, Sparkles } from 'lucide-react'
import type { PageReviewTag, ProjectPage } from '../../../contracts'
import { t, type Locale } from '../../../i18n'
import { assetUrl } from '../../../shared/lib/assets'

type TranslationPagesSidebarProps = {
  locale: Locale
  pages: readonly ProjectPage[]
  visiblePages: readonly ProjectPage[]
  selectedPageId: string | null
  generatingPageIndices: ReadonlySet<number>
  reviewFilter: 'All' | PageReviewTag
  exporting: boolean
  runningTranslation: boolean
  pendingTranslationPageCount: number
  generatedPageCount: number
  hasStoppedTranslation: boolean
  editTransitionBusy: boolean
  reviewTagBusyPageId: string | null
  onReviewFilterChange: (filter: 'All' | PageReviewTag) => void
  onRunTranslation: () => void
  onStop: () => void
  onResume: () => void
  onRerunRejectedPages: () => void
  onSelectPage: (pageId: string) => void
  onReviewTagChange: (page: ProjectPage, reviewTag: PageReviewTag) => void
  onPageRowRef: (pageId: string, element: HTMLDivElement | null) => void
}

export function TranslationPagesSidebar({
  locale,
  pages,
  visiblePages,
  selectedPageId,
  generatingPageIndices,
  reviewFilter,
  exporting,
  runningTranslation,
  pendingTranslationPageCount,
  generatedPageCount,
  hasStoppedTranslation,
  editTransitionBusy,
  reviewTagBusyPageId,
  onReviewFilterChange,
  onRunTranslation,
  onStop,
  onResume,
  onRerunRejectedPages,
  onSelectPage,
  onReviewTagChange,
  onPageRowRef,
}: TranslationPagesSidebarProps) {
  return (
    <aside className="translation-pages">
      <div className="translation-pages-fixed">
        <div className="translation-side-title"><b>{locale === 'en-US' ? 'Pages' : '页面'}</b><span>{visiblePages.length}/{pages.length}</span></div>
        <div className="translation-workflow-command">
          <div className="translation-workflow-run">
            {!runningTranslation && pendingTranslationPageCount > 0 && <button className="primary ai-action-button" onClick={onRunTranslation} disabled={exporting}><Sparkles size={14}/>{generatedPageCount === 0 ? (locale === 'en-US' ? 'Run translation' : '运行翻译') : (locale === 'en-US' ? `Generate remaining ${pendingTranslationPageCount} pages` : `生成剩余 ${pendingTranslationPageCount} 页`)}</button>}
            {runningTranslation && <button onClick={onStop} disabled={exporting}><CircleStop size={14}/>{locale === 'en-US' ? 'Stop all translation tasks' : '停止全部翻译任务'}</button>}
            {!runningTranslation && hasStoppedTranslation && <button className="primary ai-action-button" onClick={onResume} disabled={exporting}><Sparkles size={14}/>{locale === 'en-US' ? 'Continue translation' : '继续翻译'}</button>}
            {!runningTranslation && pages.some(page => page.hasAiResult) && <button className="ai-action-button" onClick={onRerunRejectedPages} disabled={exporting || !pages.some(page => page.reviewTag === 'Rejected' && page.includedInWorkflow && page.hasAiResult)}><Sparkles size={14}/>{t(locale, '重新运行已驳回页面')}</button>}
          </div>
        </div>
        <div className="translation-page-filters">
          <select aria-label={locale === 'en-US' ? 'Filter by review status' : '校对状态筛选'} value={reviewFilter} onChange={event => onReviewFilterChange(event.target.value as 'All' | PageReviewTag)} disabled={exporting}>
            <option value="All">{t(locale, '全部状态')}</option>
            <option value="Priority">{t(locale, '优先校对')}</option>
            <option value="Unreviewed">{t(locale, '未审阅')}</option>
            <option value="Accepted">{t(locale, '已接受')}</option>
            <option value="Rejected">{t(locale, '已驳回')}</option>
          </select>
        </div>
      </div>
      <div className="translation-page-list">
        {visiblePages.map(page => (
          <div ref={element => onPageRowRef(page.pageId, element)} className={`translation-page-row page-review-${page.reviewTag.toLowerCase()} ${!page.includedInWorkflow ? 'page-excluded' : ''} ${page.pageId === selectedPageId ? 'active' : ''} ${generatingPageIndices.has(page.index) ? 'page-generating' : ''}`} key={page.pageId}>
            <button disabled={editTransitionBusy || exporting} onClick={() => onSelectPage(page.pageId)}>
              <img src={assetUrl(page.originalAssetUrl)} alt=""/>
              <span><b>{page.index.toString().padStart(3, '0')}</b><small>{page.fileName}</small><i className={!page.includedInWorkflow ? 'excluded' : page.hasAiResult ? 'done' : generatingPageIndices.has(page.index) ? 'generating' : ''}>{!page.includedInWorkflow ? (locale === 'en-US' ? 'Excluded' : '已排除') : generatingPageIndices.has(page.index) ? (locale === 'en-US' ? 'Generating' : '正在生成') : page.hasAiResult ? (locale === 'en-US' ? 'Done' : '完成') : (locale === 'en-US' ? 'Not generated' : '未生成')}</i></span>
            </button>
            {!page.includedInWorkflow
              ? <span className="translation-page-excluded-status" title={locale === 'en-US' ? 'Excluded during page selection or preprocessing. This state is locked in the translation workspace.' : '该页面已在选页或预处理阶段排除，翻译工作区中不可修改。'}>{locale === 'en-US' ? 'Excluded' : '已排除'}</span>
              : <select aria-label={locale === 'en-US' ? `Review status for page ${page.index}` : `第 ${page.index} 页校对状态`} value={page.reviewTag} disabled={exporting || !page.hasAiResult || reviewTagBusyPageId === page.pageId} title={!page.hasAiResult ? (locale === 'en-US' ? 'Review status is available after the first generation.' : '首次生成完成后才可标记校对状态。') : undefined} onChange={event => onReviewTagChange(page, event.target.value as PageReviewTag)}>
                  <option value="Unreviewed">{locale === 'en-US' ? 'Unreviewed' : '未校对'}</option><option value="Priority">{locale === 'en-US' ? 'Priority review' : '优先校对'}</option><option value="Accepted">{locale === 'en-US' ? 'Accepted' : '接受'}</option><option value="Rejected">{locale === 'en-US' ? 'Rejected' : '驳回'}</option>
                </select>}
          </div>
        ))}
      </div>
    </aside>
  )
}
