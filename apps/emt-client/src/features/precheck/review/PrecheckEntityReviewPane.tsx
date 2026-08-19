import { Save, Sparkles, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api } from '../../../api'
import type { PrecheckCharacterDetails, PrecheckReview, PrecheckTermDetails, ProjectDetail } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { readError } from '../../../shared/lib/errors'

export type PrecheckEntityReviewPaneProps = {
  project: ProjectDetail
  review: PrecheckReview | null
  onProjectChange: (project: ProjectDetail) => void
  onReviewChange: (review: PrecheckReview) => void
  onError: (error: string | null) => void
}

export function PrecheckEntityReviewPane({ project, review, onProjectChange, onReviewChange, onError }: PrecheckEntityReviewPaneProps) {
  const locale = readLocale()
  const [editingKey, setEditingKey] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [busyKey, setBusyKey] = useState<string | null>(null)

  async function updateItem(artifactName: 'characters.json' | 'glossary.json', itemKey: string, value: string | null, deleteItem = false, mergeIntoKey: string | null = null) {
    setBusyKey(itemKey)
    onError(null)
    try {
      const updated = await api.updatePrecheckItem(
        project.projectId,
        artifactName,
        itemKey,
        value,
        mergeIntoKey,
        deleteItem,
        project.revision,
      )
      onProjectChange(updated)
      onReviewChange(await api.getPrecheckReview(project.projectId))
      setEditingKey(null)
    } catch (reason) {
      onError(readError(reason))
    } finally {
      setBusyKey(null)
    }
  }

  if (!review) {
    return (
      <section className="content placeholder-pane">
        <Sparkles size={28}/>
        <h1>{locale === 'en-US' ? 'Story Bible' : 'Story Bible 审阅'}</h1>
        <p>{locale === 'en-US' ? 'Run preprocessing to generate the whole-book synopsis, translation voice, characters, terms, and continuity facts.' : '运行一次预处理后，这里会显示全书剧情摘要、翻译语气、角色、术语和连续性事实。'}</p>
      </section>
    )
  }

  return (
    <section className="content precheck-entity-content">
      <div className="content-header">
        <div>
          <h1>{locale === 'en-US' ? 'Story Bible' : 'Story Bible 审阅'}</h1>
          <p>{review.runId ? (locale === 'en-US' ? `Source: ${review.runId} · Changes are saved automatically.` : `来源：${review.runId} · 修改会自动保存到项目数据库。`) : (locale === 'en-US' ? 'No preprocessing results have been imported.' : '尚未导入预处理结果。')}</p>
        </div>
        <div className="autosave-indicator"><Save size={14}/>{locale === 'en-US' ? 'Characters and terms save independently' : '角色和术语独立保存'}</div>
      </div>
      {review.context
        ? (
            <section className="story-bible-summary">
              <div className="story-bible-copy">
                <div><span>{locale === 'en-US' ? 'Whole-book synopsis' : '全书剧情摘要'}</span><p>{review.context.overallSynopsis || (locale === 'en-US' ? 'No synopsis returned.' : '未生成剧情摘要。')}</p></div>
                {review.context.tone && <div><span>{locale === 'en-US' ? 'Tone' : '整体基调'}</span><p>{review.context.tone}</p></div>}
                {(review.context.themes?.length ?? 0) > 0 && <DetailList label={locale === 'en-US' ? 'Themes' : '主题'} values={review.context.themes ?? []}/>}
                <div><span>{locale === 'en-US' ? 'Translation voice' : '整体翻译语气'}</span><p>{review.context.overallVoice || (locale === 'en-US' ? 'No overall voice returned.' : '未生成整体语气。')}</p></div>
              </div>
              <dl className="story-bible-stats">
                <div><dt>{locale === 'en-US' ? 'Characters' : '角色'}</dt><dd>{review.context.characterCount}</dd></div>
                <div><dt>{locale === 'en-US' ? 'Terms' : '术语'}</dt><dd>{review.context.termCount}</dd></div>
                <div><dt>{locale === 'en-US' ? 'Continuity facts' : '连续性事实'}</dt><dd>{review.context.continuityFactCount}</dd></div>
              </dl>
              <div className="story-bible-guidance">
                <h2>{locale === 'en-US' ? 'Translation guidance' : '翻译指导'}</h2>
                <DetailList label={locale === 'en-US' ? 'Dialogue rules' : '对白规则'} values={review.context.dialogueRules ?? []}/>
                <DetailText label={locale === 'en-US' ? 'Honorific policy' : '敬称策略'} value={review.context.honorificPolicy ?? ''}/>
                <DetailText label={locale === 'en-US' ? 'Name policy' : '名称策略'} value={review.context.namePolicy ?? ''}/>
                <DetailList label={locale === 'en-US' ? 'Avoid' : '禁止倾向'} values={review.context.prohibitedTendencies ?? []}/>
              </div>
              {(review.context.continuityFacts?.length ?? 0) > 0 && (
                <div className="story-bible-continuity">
                  <h2>{locale === 'en-US' ? 'Continuity facts' : '连续性事实'}</h2>
                  <div className="story-bible-fact-list">
                    {(review.context.continuityFacts ?? []).map((item, index) => (
                      <article key={`${index}-${item.fact}`}>
                        <div className="story-bible-fact-head">
                          <b>{item.fact}</b>
                          <span>{locale === 'en-US' ? `Revealed on page ${item.revealedAtOrder}` : `第 ${item.revealedAtOrder} 页揭示`}</span>
                        </div>
                        {item.safeWordingBeforeReveal && <p><span>{locale === 'en-US' ? 'Safe wording before reveal' : '揭示前安全措辞'}</span>{item.safeWordingBeforeReveal}</p>}
                        {item.evidencePages.length > 0 && <small>{locale === 'en-US' ? 'Evidence' : '依据页'}：{item.evidencePages.join(', ')}</small>}
                      </article>
                    ))}
                  </div>
                </div>
              )}
              {review.context.unresolvedQuestions.length > 0 && (
                <div className="story-bible-questions">
                  <b>{locale === 'en-US' ? 'Unresolved questions' : '待确认事项'}</b>
                  <ul>{review.context.unresolvedQuestions.map((item, index) => <li key={`${index}-${item}`}>{item}</li>)}</ul>
                </div>
              )}
            </section>
          )
        : <div className="story-bible-legacy-note">{locale === 'en-US' ? 'This result predates the whole-book Story Bible format. Run live preprocessing to generate it.' : '当前结果来自旧版预处理；重新实时运行后可生成全书 Story Bible。'}</div>}
      <div className="entity-review-stack">
        <EntitySection
          title={locale === 'en-US' ? 'Characters' : '角色'}
          empty={locale === 'en-US' ? 'No characters were detected.' : '预处理结果中没有识别到角色。'}
          items={review.characters.map(item => ({ ...item, details: item.details ?? null, artifactName: 'characters.json' as const }))}
          editingKey={editingKey}
          draft={draft}
          busyKey={busyKey}
          onEdit={(key, value) => { setEditingKey(key); setDraft(value) }}
          onDraftChange={setDraft}
          onCancel={() => setEditingKey(null)}
          onSave={(item) => updateItem(item.artifactName, item.mergeKey, draft)}
          onDelete={(item) => updateItem(item.artifactName, item.mergeKey, null, true)}
          onMerge={(item, target) => updateItem(item.artifactName, item.mergeKey, item.value, false, target.mergeKey)}
        />
        <EntitySection
          title={locale === 'en-US' ? 'Glossary' : '术语表'}
          empty={locale === 'en-US' ? 'No glossary terms were detected.' : '预处理结果中没有识别到术语。'}
          items={review.terms.map(item => ({ ...item, details: item.details ?? null, artifactName: 'glossary.json' as const }))}
          editingKey={editingKey}
          draft={draft}
          busyKey={busyKey}
          onEdit={(key, value) => { setEditingKey(key); setDraft(value) }}
          onDraftChange={setDraft}
          onCancel={() => setEditingKey(null)}
          onSave={(item) => updateItem(item.artifactName, item.mergeKey, draft)}
          onDelete={(item) => updateItem(item.artifactName, item.mergeKey, null, true)}
          onMerge={(item, target) => updateItem(item.artifactName, item.mergeKey, item.value, false, target.mergeKey)}
        />
      </div>
    </section>
  )
}

type EntityItem = {
  artifactName: 'characters.json' | 'glossary.json'
  mergeKey: string
  value: string
  pageIndices: number[]
  evidence: string
  confidence: number
  details?: PrecheckCharacterDetails | PrecheckTermDetails | null
}

function EntitySection({ title, empty, items, editingKey, draft, busyKey, onEdit, onDraftChange, onCancel, onSave, onDelete, onMerge }: {
  title: string
  empty: string
  items: EntityItem[]
  editingKey: string | null
  draft: string
  busyKey: string | null
  onEdit: (key: string, value: string) => void
  onDraftChange: (value: string) => void
  onCancel: () => void
  onSave: (item: EntityItem) => void
  onDelete: (item: EntityItem) => void
  onMerge: (item: EntityItem, target: EntityItem) => void
}) {
  const locale = readLocale()
  const [mergeTargets, setMergeTargets] = useState<Record<string, string>>({})
  return (
    <section className="entity-section">
      <div className="entity-section-title"><b>{title}</b><span>{items.length}</span></div>
      {items.length === 0
        ? <div className="entity-empty">{empty}</div>
        : items.map(item => (
            <article className="entity-card" key={item.mergeKey}>
              <div className="entity-card-head">
                <code>{item.mergeKey}</code>
                <span>{Math.round(item.confidence * 100)}% · {locale === 'en-US' ? `Pages ${item.pageIndices.join(', ')}` : `第 ${item.pageIndices.join(', ')} 页`}</span>
              </div>
              {editingKey === item.mergeKey
                ? (
                    <div className="entity-edit-row">
                      <input value={draft} onChange={event => onDraftChange(event.target.value)} autoFocus/>
                      <button className="primary" onClick={() => onSave(item)} disabled={busyKey === item.mergeKey}>{locale === 'en-US' ? 'Save' : '保存'}</button>
                      <button onClick={onCancel} disabled={busyKey === item.mergeKey}>{locale === 'en-US' ? 'Cancel' : '取消'}</button>
                    </div>
                  )
                : <p className="entity-value">{item.value}</p>}
              {item.details && <EntityDetails item={item}/>}
              <small className="entity-evidence">{item.evidence}</small>
              <div className="entity-actions">
                <button onClick={() => onEdit(item.mergeKey, item.value)} disabled={busyKey !== null}><Save size={12}/>{locale === 'en-US' ? 'Rename' : '修改'}</button>
                <button onClick={() => onDelete(item)} disabled={busyKey !== null}><Trash2 size={12}/>{locale === 'en-US' ? 'Delete' : '删除'}</button>
                {items.length > 1 && <><select aria-label={locale === 'en-US' ? `Merge ${item.value} into` : `将 ${item.value} 合并到`} value={mergeTargets[item.mergeKey] ?? ''} onChange={event => setMergeTargets(current => ({ ...current, [item.mergeKey]: event.target.value }))} disabled={busyKey !== null}><option value="">{locale === 'en-US' ? 'Merge into…' : '合并到…'}</option>{items.filter(target => target.mergeKey !== item.mergeKey).map(target => <option value={target.mergeKey} key={target.mergeKey}>{target.value}</option>)}</select><button onClick={() => { const target = items.find(candidate => candidate.mergeKey === mergeTargets[item.mergeKey]); if (target) onMerge(item, target) }} disabled={busyKey !== null || !mergeTargets[item.mergeKey]}>{locale === 'en-US' ? 'Merge' : '合并'}</button></>}
              </div>
            </article>
          ))}
    </section>
  )
}

function EntityDetails({ item }: { item: EntityItem }) {
  return 'originalName' in item.details!
    ? <CharacterDetails details={item.details}/>
    : <TermDetails details={item.details!}/>
}

function CharacterDetails({ details }: { details: PrecheckCharacterDetails }) {
  const locale = readLocale()
  return (
    <div className="entity-details">
      <div className="entity-detail-summary">
        <DetailText label={locale === 'en-US' ? 'Original name' : '原名'} value={details.originalName}/>
        <DetailText label={locale === 'en-US' ? 'Translated name' : '当前译名'} value={details.translatedName}/>
        <DetailText label={locale === 'en-US' ? 'Role' : '身份 / 定位'} value={details.role}/>
        <DetailText label={locale === 'en-US' ? 'Valid pages' : '有效页范围'} value={formatPageRange(details.validFromOrder, details.validToOrder, locale)}/>
      </div>
      <DetailList label={locale === 'en-US' ? 'Aliases' : '别名'} values={details.aliases}/>
      <DetailList label={locale === 'en-US' ? 'Speech rules' : '说话规则'} values={details.speechRules}/>
      <DetailList label={locale === 'en-US' ? 'Relationships' : '关系'} values={details.relationships}/>
      <DetailFlags revealSensitive={details.revealSensitive} evidencePages={details.evidencePages}/>
    </div>
  )
}

function TermDetails({ details }: { details: PrecheckTermDetails }) {
  const locale = readLocale()
  return (
    <div className="entity-details">
      <div className="entity-detail-summary">
        <DetailText label={locale === 'en-US' ? 'Source' : '原文'} value={details.source}/>
        <DetailText label={locale === 'en-US' ? 'Translation' : '当前译文'} value={details.translation}/>
        <DetailText label={locale === 'en-US' ? 'Category' : '类别'} value={details.category}/>
        <DetailText label={locale === 'en-US' ? 'Valid pages' : '有效页范围'} value={formatPageRange(details.validFromOrder, details.validToOrder, locale)}/>
      </div>
      <DetailText label={locale === 'en-US' ? 'Usage rule' : '使用规则'} value={details.usageRule}/>
      <DetailFlags revealSensitive={details.revealSensitive} evidencePages={details.evidencePages}/>
    </div>
  )
}

function DetailFlags({ revealSensitive, evidencePages }: { revealSensitive: boolean; evidencePages: string[] }) {
  const locale = readLocale()
  return (
    <div className="entity-detail-flags">
      {revealSensitive && <span className="entity-spoiler-flag">{locale === 'en-US' ? 'Reveal-sensitive' : '涉及剧情揭示'}</span>}
      {evidencePages.length > 0 && <span>{locale === 'en-US' ? 'Evidence pages' : '依据页'}：{evidencePages.join(', ')}</span>}
    </div>
  )
}

function DetailText({ label, value }: { label: string; value: string }) {
  if (!value) return null
  return <div className="detail-field"><span>{label}</span><p>{value}</p></div>
}

function DetailList({ label, values }: { label: string; values: string[] }) {
  if (values.length === 0) return null
  return <div className="detail-field"><span>{label}</span><ul>{values.map((value, index) => <li key={`${index}-${value}`}>{value}</li>)}</ul></div>
}

function formatPageRange(from: number, to: number | null, locale: string) {
  if (to === null) return locale === 'en-US' ? `Page ${from} onward` : `第 ${from} 页起`
  if (from === to) return locale === 'en-US' ? `Page ${from}` : `第 ${from} 页`
  return locale === 'en-US' ? `Pages ${from}–${to}` : `第 ${from}–${to} 页`
}
