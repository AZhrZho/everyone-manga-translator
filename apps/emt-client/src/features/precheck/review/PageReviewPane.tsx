import { useEffect, useState } from 'react'
import { Image, Redo2, Save, Undo2 } from 'lucide-react'
import { api } from '../../../api'
import type { PrecheckReview, ProjectDetail, ProjectPage } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { assetUrl } from '../../../shared/lib/assets'
import { isPresentableError, readError } from '../../../shared/lib/errors'
import type { ReviewCommandHistory } from '../model/review'

export function PageReviewPane({ project, review, error, onProjectChange, onError }: {
  project: ProjectDetail
  review: PrecheckReview | null
  error: string | null
  onProjectChange: (project: ProjectDetail) => void
  onError: (error: string | null) => void
}) {
  const locale = readLocale()
  const [selectedPageId, setSelectedPageId] = useState(project.pages[0]?.pageId ?? null)
  const [savingPageId, setSavingPageId] = useState<string | null>(null)
  const [historyByPage, setHistoryByPage] = useState<Record<string, ReviewCommandHistory>>({})
  const selectedPage = project.pages.find(page => page.pageId === selectedPageId) ?? project.pages[0]
  const selectedClassification = selectedPage
    ? review?.pages.find(page => page.pageId === selectedPage.pageId || page.pageIndex === selectedPage.index)
    : undefined

  useEffect(() => {
    if (selectedPageId && project.pages.some(page => page.pageId === selectedPageId)) return
    setSelectedPageId(project.pages[0]?.pageId ?? null)
  }, [project.pages, selectedPageId])

  useEffect(() => {
    // Page-type review is metadata only; it deliberately has no page-version lifecycle.
  }, [project.projectId, selectedPage?.pageId])

  async function saveReview(page: ProjectPage, pageType: string, includedInWorkflow: boolean) {
    setSavingPageId(page.pageId)
    onError(null)
    try {
      const updated = await api.updatePageReview(
        project.projectId,
        page.pageId,
        pageType,
        includedInWorkflow,
        project.revision,
      )
      onProjectChange(updated)
      setHistoryByPage(current => ({
        ...current,
        [page.pageId]: {
          undo: [...(current[page.pageId]?.undo ?? []), {
            before: { pageType: page.pageType, includedInWorkflow: page.includedInWorkflow },
            after: { pageType, includedInWorkflow },
          }],
          redo: [],
        },
      }))
    } catch (reason) {
      onError(readError(reason))
      try {
        onProjectChange(await api.getProject(project.projectId))
      } catch {
        // Keep the original error visible if reloading also fails.
      }
    } finally {
      setSavingPageId(null)
    }
  }

  async function undoReview() {
    if (!selectedPage) return
    const entry = historyByPage[selectedPage.pageId]
    const command = entry?.undo.at(-1)
    if (!command) return
    try {
      const updated = await api.updatePageReview(project.projectId, selectedPage.pageId, command.before.pageType, command.before.includedInWorkflow, project.revision)
      onProjectChange(updated)
      setHistoryByPage(current => ({
        ...current,
        [selectedPage.pageId]: { undo: entry.undo.slice(0, -1), redo: [...entry.redo, command] },
      }))
    } catch (reason) { onError(readError(reason)) }
  }

  async function redoReview() {
    if (!selectedPage) return
    const entry = historyByPage[selectedPage.pageId]
    const command = entry?.redo.at(-1)
    if (!command) return
    try {
      const updated = await api.updatePageReview(project.projectId, selectedPage.pageId, command.after.pageType, command.after.includedInWorkflow, project.revision)
      onProjectChange(updated)
      setHistoryByPage(current => ({
        ...current,
        [selectedPage.pageId]: { undo: [...entry.undo, command], redo: entry.redo.slice(0, -1) },
      }))
    } catch (reason) { onError(readError(reason)) }
  }

  return (
    <section className="content page-review-content">
        <div className="content-header review-header">
        <div>
          <h1>{locale === 'en-US' ? 'Page type review' : '页面类型审阅'}</h1>
          <p>{locale === 'en-US' ? 'Changes save immediately and determine whether each page enters the translation workflow.' : '修改会立即自动保存，并决定页面是否进入后续翻译流程。'}</p>
        </div>
        <div className="review-header-actions">
          <button onClick={undoReview} disabled={!selectedPage || !(historyByPage[selectedPage.pageId]?.undo.length)} title={locale === 'en-US' ? 'Undo this page’s review change' : '撤销当前页审阅修改'}><Undo2 size={14}/></button>
          <button onClick={redoReview} disabled={!selectedPage || !(historyByPage[selectedPage.pageId]?.redo.length)} title={locale === 'en-US' ? 'Redo this page’s review change' : '恢复当前页审阅修改'}><Redo2 size={14}/></button>
          <div className="autosave-indicator"><Save size={14}/>{locale === 'en-US' ? (savingPageId ? 'Saving…' : 'All changes saved') : (savingPageId ? '正在保存…' : '所有修改已保存')}</div>
        </div>
      </div>

      {error && isPresentableError(error) && <div className="error-banner">{error}</div>}

      {project.pages.length === 0
        ? (
            <div className="empty-state">
              <Image size={25}/>
              <h2>{locale === 'en-US' ? 'This project has no pages' : '项目中还没有页面'}</h2>
              <p>{locale === 'en-US' ? 'Create a new project by importing a folder or PDF.' : '返回项目页并从文件夹或 PDF 导入漫画。'}</p>
            </div>
          )
        : (
            <div className="page-review-workspace">
              <div className="review-page-list">
                {project.pages.map(page => (
                  <button
                    key={page.pageId}
                    className={page.pageId === selectedPage?.pageId ? 'active' : ''}
                    onClick={() => setSelectedPageId(page.pageId)}
                  >
                    <img src={assetUrl(page.originalAssetUrl)} alt="" loading="lazy"/>
                    <span><b>{page.index.toString().padStart(3, '0')}</b><small>{pageTypeLabel(page.pageType, locale)} · {page.fileName}</small></span>
                    {!page.includedInWorkflow && <i>{locale === 'en-US' ? 'Excluded' : '排除'}</i>}
                  </button>
                ))}
              </div>

              {selectedPage && (
                <div className="review-canvas-panel">
                  <div className="review-canvas">
                    <img src={assetUrl(selectedPage.originalAssetUrl)} alt={selectedPage.fileName}/>
                  </div>
                  <div className="review-properties">
                    <div className="property-title">
                      <b>{locale === 'en-US' ? 'Page' : '页面'} {selectedPage.index.toString().padStart(3, '0')}</b>
                      {selectedClassification && <span>{Math.round(selectedClassification.confidence * 100)}% {locale === 'en-US' ? 'confidence' : '置信度'}</span>}
                    </div>
                    <label>
                      <span>{locale === 'en-US' ? 'Page type' : '页面类型'}</span>
                      <select
                        value={normalizePageType(selectedPage.pageType)}
                        disabled={savingPageId !== null}
                        onChange={event => saveReview(selectedPage, event.target.value, selectedPage.includedInWorkflow)}
                      >
                        {pageTypeOptions.map(option => <option value={option.value} key={option.value}>{locale === 'en-US' ? option.en : option.zh}</option>)}
                      </select>
                    </label>
                    <label className="workflow-toggle">
                      <input
                        type="checkbox"
                        checked={selectedPage.includedInWorkflow}
                        disabled={savingPageId !== null}
                        onChange={event => saveReview(selectedPage, normalizePageType(selectedPage.pageType), event.target.checked)}
                      />
                      <span><b>{locale === 'en-US' ? 'Include in translation workflow' : '加入后续翻译流程'}</b><small>{locale === 'en-US' ? 'When off, AI translation and export skip this page.' : '关闭后，AI 翻译和导出会跳过此页。'}</small></span>
                    </label>
                    {selectedClassification
                      ? (
                          <div className="page-classification-evidence">
                            <b>{locale === 'en-US' ? 'Preprocessing evidence' : '预处理判断依据'}</b>
                            {selectedClassification.evidence.length > 0
                              ? <ul>{selectedClassification.evidence.map((item, index) => <li key={`${index}-${item}`}>{item}</li>)}</ul>
                              : <p>{locale === 'en-US' ? 'No evidence was returned; the page remains included conservatively.' : '模型未返回判断依据；该页面会保守地保持纳入。'}</p>}
                          </div>
                        )
                      : <div className="review-note">{locale === 'en-US' ? 'No live page classification is available yet. Run preprocessing to obtain confidence and evidence.' : '尚无实时页面分类结果。运行预处理后可查看置信度和判断依据。'}</div>}
                    <dl>
                      <div><dt>{locale === 'en-US' ? 'Source file' : '原文件'}</dt><dd>{selectedPage.fileName}</dd></div>
                      <div><dt>{locale === 'en-US' ? 'Dimensions' : '尺寸'}</dt><dd>{selectedPage.width || '—'} × {selectedPage.height || '—'}</dd></div>
                      <div><dt>{locale === 'en-US' ? 'Workflow' : '工作流'}</dt><dd>{selectedPage.includedInWorkflow ? (locale === 'en-US' ? 'Included' : '已加入') : (locale === 'en-US' ? 'Excluded' : '已排除')}</dd></div>
                    </dl>
                  </div>
                </div>
              )}
            </div>
          )}
    </section>
  )
}

const pageTypeOptions = [
  { value: 'story', zh: '故事正文', en: 'Story' },
  { value: 'bonus_story', zh: '番外故事', en: 'Bonus story' },
  { value: 'cover', zh: '封面', en: 'Cover' },
  { value: 'title_page', zh: '标题页', en: 'Title page' },
  { value: 'table_of_contents', zh: '目录', en: 'Table of contents' },
  { value: 'credits', zh: '版权 / 制作信息', en: 'Credits / production info' },
  { value: 'advertisement', zh: '广告', en: 'Advertisement' },
  { value: 'blank', zh: '空白页', en: 'Blank page' },
  { value: 'illustration', zh: '插画', en: 'Illustration' },
  { value: 'unknown', zh: '未识别', en: 'Unknown' },
] as const

function pageTypeLabel(pageType: string, locale: 'zh-CN' | 'en-US') {
  const option = pageTypeOptions.find(item => item.value === normalizePageType(pageType))
  if (option) return locale === 'en-US' ? option.en : option.zh
  return pageType
}

function normalizePageType(pageType: string) {
  switch (pageType) {
    case 'manga':
    case 'spread':
      return 'story'
    case 'chapter_title':
      return 'title_page'
    case 'omake':
      return 'bonus_story'
    case 'front_cover':
    case 'back_cover':
    case 'inside_cover':
      return 'cover'
    case 'copyright':
    case 'afterword':
      return 'credits'
    default:
      return pageTypeOptions.some(item => item.value === pageType) ? pageType : 'unknown'
  }
}
