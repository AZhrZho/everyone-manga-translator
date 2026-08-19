import type { JobSnapshot, PageVersion, ProcessEvent, ProjectListItem } from '../../contracts'
import { readLocale, type Locale } from '../../i18n'

export function statusText(status: JobSnapshot['status']) {
  const locale = readLocale()
  return locale === 'en-US'
    ? ({ Pending: 'Pending', Running: 'Running', Stopped: 'Stopped', Completed: 'Completed', Failed: 'Failed' })[status]
    : ({ Pending: '等待', Running: '运行中', Stopped: '已停止', Completed: '已完成', Failed: '失败' })[status]
}

export function eventStatusText(status: ProcessEvent['status']) {
  return readLocale() === 'en-US'
    ? ({ Pending: 'Pending', Running: 'Running', Completed: 'Complete', Failed: 'Failed' })[status]
    : ({ Pending: '等待', Running: '运行中', Completed: '完成', Failed: '失败' })[status]
}

export function sourceKindText(kind: ProjectListItem['sourceKind']) {
  return readLocale() === 'en-US'
    ? ({ Empty: 'Local project', Folder: 'Folder import', Pdf: 'PDF import' })[kind]
    : ({ Empty: '本地项目', Folder: '文件夹导入', Pdf: 'PDF 导入' })[kind]
}

export function stageDisplayName(locale: Locale, stageId: string, fallback: string) {
  const names: Record<string, [string, string]> = {
    'precheck-manifest': ['扫描', 'Scanning'],
    'precheck-analysis': ['预处理', 'Preprocessing'],
    translation: ['翻译', 'Translation'],
  }
  const name = names[stageId]
  return name ? name[locale === 'en-US' ? 1 : 0] : fallback
}

export function versionKindText(kind: PageVersion['kind']) {
  return readLocale() === 'en-US'
    ? ({ Ai: 'AI', User: 'User', Imported: 'Imported' })[kind]
    : ({ Ai: 'AI', User: '用户', Imported: '导入' })[kind]
}

export function formatDate(value: string) {
  return new Intl.DateTimeFormat(readLocale(), { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
}
