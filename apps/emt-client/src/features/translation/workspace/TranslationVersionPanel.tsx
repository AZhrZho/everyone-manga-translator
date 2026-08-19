import { Plus } from 'lucide-react'
import type { PageVersion } from '../../../contracts'
import { t, type Locale } from '../../../i18n'
import { formatDate, versionKindText } from '../../../shared/lib/formatters'

type TranslationVersionPanelProps = {
  locale: Locale
  versions: PageVersion[]
  canCreate: boolean
  onActivate: (version: PageVersion) => void
  onDelete: (version: PageVersion) => void
  onCreate: () => void
}

export function TranslationVersionPanel({ locale, versions, canCreate, onActivate, onDelete, onCreate }: TranslationVersionPanelProps) {
  const trackVersions = versions.filter(version => version.track === 'Lettering')
  return <div className="translation-version-list">
    <div className="translation-side-title"><b>{t(locale, '当前页版本')}</b><span>{locale === 'en-US' ? 'Lettering' : '嵌字'}</span></div>
    <section className="version-track" data-track="lettering">
      <div className="version-track-head"><span><b>{locale === 'en-US' ? 'Lettering versions' : '嵌字版本'}</b><small>{locale === 'en-US' ? 'Text, layout and lettering render' : '文本、布局与嵌字渲染'}</small></span><em>{trackVersions.length}</em></div>
      <div className="version-track-list">
        {trackVersions.map(version => (
          <div className={version.isCurrent ? 'current' : ''} key={version.versionId}>
            <span><b>{version.label}</b><small>{versionKindText(version.kind)} · {formatDate(version.createdAt)}</small></span>
            {version.isCurrent
              ? <em>{version.isWorkingCopy ? (locale === 'en-US' ? 'Editing' : '编辑中') : (locale === 'en-US' ? 'Current' : '当前')}</em>
              : <span className="version-row-actions"><button onClick={() => onActivate(version)}>{locale === 'en-US' ? 'Switch' : '切换'}</button><button onClick={() => onDelete(version)} disabled={trackVersions.length <= 1}>{t(locale, '删除')}</button></span>}
          </div>
        ))}
        {trackVersions.length === 0 && <p>{locale === 'en-US' ? 'No lettering versions yet.' : '尚无嵌字版本。'}</p>}
      </div>
      {canCreate && <button className="version-create-button" onClick={onCreate}><Plus size={12}/>{locale === 'en-US' ? 'Save lettering version' : '固化当前嵌字'}</button>}
    </section>
  </div>
}
