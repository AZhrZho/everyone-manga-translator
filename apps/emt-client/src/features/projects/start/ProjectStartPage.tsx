import { BookOpen, Clock3, FileText, FolderOpen, PanelsTopLeft, Trash2, X } from 'lucide-react'
import { useState } from 'react'
import type { ProjectListItem } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { formatDate, sourceKindText } from '../../../shared/lib/formatters'
import { isPresentableError } from '../../../shared/lib/errors'

export function ProjectStartPage({ projects, busy, error, onOpen, onImportFolder, onImportPdf, onOpenExisting, onRemove }: {
  projects: ProjectListItem[]
  busy: boolean
  error: string | null
  onOpen: (projectId: string) => void
  onImportFolder: () => void
  onImportPdf: () => void
  onOpenExisting: () => void
  onRemove: (projectId: string, deleteFiles: boolean) => Promise<boolean>
}) {
  const locale = readLocale()
  const [removeTarget, setRemoveTarget] = useState<ProjectListItem | null>(null)
  return (
    <main className="start-page">
      <section className="start-hero">
        <picture className="start-logo"><img className="logo-dark" src="/emt-logo-dark-transparent.svg" alt="EMT"/><img className="logo-light" src="/emt-logo-light-transparent.svg" alt="" aria-hidden="true"/></picture>
        <div>
          <h1>Everyone Manga Translator</h1>
          <p>{locale === 'en-US' ? 'A project-based workspace for translating, reviewing and lettering manga' : '项目化的漫画翻译、审阅与编辑工作区'}</p>
        </div>
      </section>

      {error && isPresentableError(error) && <div className="error-banner start-error">{error}</div>}

      <section className="start-actions">
        <button className="start-action primary" onClick={onImportFolder} disabled={busy}>
          <FolderOpen size={22}/>
          <span><b>{locale === 'en-US' ? 'Import manga folder' : '导入漫画文件夹'}</b><small>{locale === 'en-US' ? 'Copy images into a self-contained project' : '复制图片并创建可持久化项目'}</small></span>
        </button>
        <button className="start-action primary" onClick={onImportPdf} disabled={busy}>
          <FileText size={22}/>
          <span><b>{locale === 'en-US' ? 'Import manga PDF' : '导入 PDF 漫画'}</b><small>{locale === 'en-US' ? 'Rasterize pages at 200 DPI into the project' : '按固定 200 DPI 栅格化到项目目录'}</small></span>
        </button>
        <button className="start-action" onClick={onOpenExisting} disabled={busy}>
          <FolderOpen size={22}/>
          <span><b>{locale === 'en-US' ? 'Open existing EMT project' : '打开已有 EMT 项目'}</b><small>{locale === 'en-US' ? 'Choose a folder containing project.db' : '选择包含 project.db 的项目目录'}</small></span>
        </button>
      </section>

      <section className="recent-projects">
        <div className="section-title"><Clock3 size={15}/><h2>{locale === 'en-US' ? 'Recent projects' : '最近项目'}</h2></div>
        {projects.length === 0
          ? (
              <div className="no-projects">
                <PanelsTopLeft size={28}/>
                <p>{locale === 'en-US' ? 'No projects yet. Import a manga folder to get started.' : '还没有项目。导入一个漫画文件夹开始工作。'}</p>
              </div>
            )
          : (
              <div className="project-grid">
                {projects.map(project => <article className="project-card" key={project.projectId}>
                  <button className="project-card-open" onClick={() => onOpen(project.projectId)} disabled={busy}>
                    <div className="project-icon"><BookOpen size={19}/></div>
                    <div><b>{project.name}</b><span>{locale === 'en-US' ? `${project.pageCount} pages` : `${project.pageCount} 页`} · {sourceKindText(project.sourceKind)}</span><small>{locale === 'en-US' ? `Updated ${formatDate(project.updatedAt)}` : `更新于 ${formatDate(project.updatedAt)}`}</small></div>
                  </button>
                  <button className="project-card-remove" aria-label={locale === 'en-US' ? `Remove ${project.name}` : `移除项目 ${project.name}`} title={locale === 'en-US' ? 'Remove project' : '移除项目'} disabled={busy} onClick={() => setRemoveTarget(project)}><Trash2 size={14}/></button>
                </article>)}
              </div>
            )}
      </section>
      {removeTarget && <div className="modal-backdrop recent-remove-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) setRemoveTarget(null) }}>
        <section className="recent-remove-dialog" role="dialog" aria-modal="true" aria-labelledby="remove-project-title">
          <header><div><h2 id="remove-project-title">{locale === 'en-US' ? 'Remove project' : '删除项目'}</h2><p>{removeTarget.name}</p></div><button onClick={() => setRemoveTarget(null)} disabled={busy} aria-label={locale === 'en-US' ? 'Close' : '关闭'}><X size={16}/></button></header>
          <p>{locale === 'en-US' ? 'Choose whether to remove only the recent-project entry or permanently delete the complete project directory.' : '请选择仅从最近项目中移除，或永久删除整个项目目录。'}</p>
          <div className="recent-remove-options">
            <button onClick={async () => { if (await onRemove(removeTarget.projectId, false)) setRemoveTarget(null) }} disabled={busy}><span><b>{locale === 'en-US' ? 'Remove from list' : '仅移除项目'}</b><small>{locale === 'en-US' ? 'Keep all project files on disk.' : '保留磁盘上的全部项目文件，之后仍可重新打开。'}</small></span></button>
            <button className="danger" onClick={async () => { if (await onRemove(removeTarget.projectId, true)) setRemoveTarget(null) }} disabled={busy}><Trash2 size={16}/><span><b>{locale === 'en-US' ? 'Delete project files' : '物理删除项目'}</b><small>{locale === 'en-US' ? 'Permanently delete the self-contained project directory.' : '永久删除自包含项目目录，此操作不可恢复。'}</small></span></button>
          </div>
          <footer><button onClick={() => setRemoveTarget(null)} disabled={busy}>{locale === 'en-US' ? 'Cancel' : '取消'}</button></footer>
        </section>
      </div>}
    </main>
  )
}
