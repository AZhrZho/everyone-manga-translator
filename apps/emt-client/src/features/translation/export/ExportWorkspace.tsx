import { useEffect } from 'react'
import { Download, FolderOpen, RotateCcw, Sparkles } from 'lucide-react'
import type { JobSnapshot, ProjectDetail, ProjectExportFormat, ProjectExportResolution } from '../../../contracts'
import { readLocale } from '../../../i18n'
import { isPresentableError } from '../../../shared/lib/errors'
import type { WorkspaceActivity } from '../../../shared/types/workspace'
import { useTranslationExport } from './useTranslationExport'

export function ExportWorkspace({ active, project, jobs, error, onError, onClearFinishedJobs, onTaskCenterChange, onExportingChange }: {
  active: boolean
  project: ProjectDetail
  jobs: JobSnapshot[]
  error: string | null
  onError: (error: string | null) => void
  onClearFinishedJobs: () => Promise<number>
  onTaskCenterChange: (taskCenter: WorkspaceActivity['taskCenter'] | undefined) => void
  onExportingChange: (exporting: boolean) => void
}) {
  const locale = readLocale()
  const exportState = useTranslationExport({ project, jobs, onError, onClearFinished: onClearFinishedJobs, onTaskCenterChange })
  const includedPages = project.pages.filter(page => page.includedInWorkflow)
  const generatedPages = includedPages.filter(page => page.hasAiResult)
  const contentLabel = exportState.includeBackground && exportState.includeLettering
    ? (locale === 'en-US' ? 'Translated result' : '翻译结果')
    : exportState.includeBackground
      ? (locale === 'en-US' ? 'Background only' : '仅底图')
      : (locale === 'en-US' ? 'Lettering only' : '仅嵌字')

  useEffect(() => onExportingChange(exportState.exporting), [exportState.exporting, onExportingChange])

  return <main className="export-workspace" hidden={!active}>
    <header className="export-workspace-header"><div><h1>{locale === 'en-US' ? 'Export' : '导出'}</h1><p>{locale === 'en-US' ? 'Choose output layers, format, size and destination. Editing is locked while an export is running.' : '设置导出图层、格式、分辨率和路径。导出运行期间会锁定项目编辑。'}</p></div><div className="export-summary"><span>{locale === 'en-US' ? 'Pages' : '页面'}<b>{includedPages.length}</b></span><span>{locale === 'en-US' ? 'Translated' : '已翻译'}<b>{generatedPages.length}</b></span></div></header>
    {error && isPresentableError(error) && <div className="error-banner export-error">{error}</div>}
    <div className="export-workspace-body">
      <section className="export-settings-card">
        <div className="export-section-heading"><span>1</span><div><b>{locale === 'en-US' ? 'Output content' : '导出内容'}</b><small>{contentLabel}</small></div></div>
        <div className="export-layer-options">
          <label className={exportState.includeBackground ? 'selected' : ''}><input type="checkbox" checked={exportState.includeBackground} disabled={exportState.exporting || !exportState.includeLettering} onChange={event => exportState.setIncludeBackground(event.target.checked)}/><span><b>{locale === 'en-US' ? 'Background' : '底图'}</b><small>{locale === 'en-US' ? 'Cleaned page background and manual smart-clean repairs' : '自动清字结果与手动智能清除修复'}</small></span></label>
          <label className={exportState.includeLettering ? 'selected' : ''}><input type="checkbox" checked={exportState.includeLettering} disabled={exportState.exporting || !exportState.includeBackground} onChange={event => exportState.setIncludeLettering(event.target.checked)}/><span><b>{locale === 'en-US' ? 'Lettering' : '嵌字'}</b><small>{locale === 'en-US' ? 'Transparent offline-rendered lettering layer' : '由离线渲染器生成的透明嵌字层'}</small></span></label>
        </div>

        <div className="export-section-heading"><span>2</span><div><b>{locale === 'en-US' ? 'File settings' : '文件设置'}</b><small>{locale === 'en-US' ? 'Format and output resolution' : '格式与输出分辨率'}</small></div></div>
        <div className="export-form-grid"><label><span>{locale === 'en-US' ? 'Format' : '导出格式'}</span><select value={exportState.exportFormat} disabled={exportState.exporting} onChange={event => exportState.setExportFormat(event.target.value as ProjectExportFormat)}><option value="Folder">{locale === 'en-US' ? 'Image folder' : '图片文件夹'}</option><option value="Zip">ZIP</option><option value="Pdf">PDF</option></select></label><label><span>{locale === 'en-US' ? 'Resolution' : '导出分辨率'}</span><select value={exportState.exportResolution} disabled={exportState.exporting} onChange={event => exportState.setExportResolution(event.target.value as ProjectExportResolution)}><option value="Original">{locale === 'en-US' ? 'Original size' : '原始尺寸'}</option><option value="Half">1/2</option><option value="Quarter">1/4</option></select></label></div>

        <div className="export-section-heading"><span>3</span><div><b>{locale === 'en-US' ? 'Destination' : '导出路径'}</b><small>{exportState.usingDefaultDestination ? (locale === 'en-US' ? 'Project default' : '项目默认路径') : (locale === 'en-US' ? 'Custom destination' : '自定义路径')}</small></div></div>
        <div className="export-destination"><code title={exportState.destinationLabel}>{exportState.destinationLabel}</code><button onClick={() => void exportState.browseDestination()} disabled={exportState.exporting}><FolderOpen size={14}/>{locale === 'en-US' ? 'Browse' : '浏览'}</button>{!exportState.usingDefaultDestination && <button title={locale === 'en-US' ? 'Use project default' : '恢复项目默认路径'} onClick={exportState.resetDestination} disabled={exportState.exporting}><RotateCcw size={14}/></button>}</div>
      </section>

      <aside className="export-run-card">
        <div className="export-preview-icon"><Download size={28}/></div><h2>{contentLabel}</h2><dl><div><dt>{locale === 'en-US' ? 'Pages' : '页面'}</dt><dd>{includedPages.length}</dd></div><div><dt>{locale === 'en-US' ? 'Format' : '格式'}</dt><dd>{exportState.exportFormat}</dd></div><div><dt>{locale === 'en-US' ? 'Resolution' : '分辨率'}</dt><dd>{exportState.exportResolution === 'Original' ? (locale === 'en-US' ? 'Original' : '原图') : exportState.exportResolution === 'Half' ? '1/2' : '1/4'}</dd></div></dl>
        {exportState.exportJob && <><div className="progress-row"><span>{exportState.exporting ? (locale === 'en-US' ? 'Exporting' : '正在导出') : exportState.exportMessage ?? (locale === 'en-US' ? 'Last export' : '上次导出')}</span><b>{exportState.exportJob.progressPercent}%</b></div><div className="progress-track"><i style={{ width: `${exportState.exportJob.progressPercent}%` }}/></div></>}
        <button className="primary ai-action-button export-start-button" disabled={exportState.exporting || includedPages.length === 0} onClick={() => void exportState.exportProject()}><Sparkles size={15}/>{exportState.exporting ? (locale === 'en-US' ? 'Exporting…' : '正在导出…') : (locale === 'en-US' ? 'Start export' : '开始导出')}</button>
        {exportState.includeLettering && generatedPages.length < includedPages.length && <p className="export-warning">{locale === 'en-US' ? `${includedPages.length - generatedPages.length} selected pages do not have AI lettering yet; their lettering layer will be empty.` : `有 ${includedPages.length - generatedPages.length} 个已纳入页面尚无嵌字结果，导出的嵌字层将为空。`}</p>}
      </aside>
    </div>
  </main>
}
