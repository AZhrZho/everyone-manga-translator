export type Locale = 'zh-CN' | 'en-US'

export const localeLabels: Record<Locale, string> = {
  'zh-CN': '简体中文',
  'en-US': 'English',
}

// UI copy is intentionally kept in one small, dependency-free module so the
// client can later move to ICU/messageformat without changing component APIs.
// The fallback is the source text, which keeps newly added workflow copy safe.
const en: Record<string, string> = {
  '连接中…': 'Connecting…',
  '设置': 'Settings',
  '预处理审阅': 'Preprocessing review',
  '翻译工作区': 'Translation workspace',
  '运行过程': 'Run progress',
  '页面类型': 'Page types',
  '角色与术语': 'Characters & terms',
  '项目状态': 'Project status',
  '字体管理': 'Font management',
  '外部图片编辑器': 'External image editors',
  'Provider 配置': 'Provider configuration',
  '阶段模型': 'Stage models',
  '界面主题': 'Interface theme',
  '界面语言': 'Interface language',
  '保存设置': 'Save settings',
  '取消': 'Cancel',
  '关闭': 'Close',
  '添加': 'Add',
  '删除': 'Delete',
  '导入字体': 'Import fonts',
  '筛选字体': 'Filter fonts',
  '任务中心': 'Task center',
  '暂无运行中的任务': 'No running tasks',
  '正在处理': 'Processing',
  '导出': 'Export',
  '导出中…': 'Exporting…',
  '重置视图': 'Reset view',
  '预览': 'Preview',
  '底图编辑': 'Background edit',
  '文本布局': 'Text layout',
  '原图': 'Original',
  '底图': 'Clean background',
  '原图叠加': 'Original + layout',
  '结果图': 'Rendered result',
  '上一页': 'Previous page',
  '下一页': 'Next page',
  '撤销': 'Undo',
  '重做': 'Redo',
  '显示原图': 'Show original',
  '复制格式': 'Copy format',
  '应用格式': 'Apply format',
  '新建文本框': 'Add text box',
  '驳回': 'Reject',
  '立即重新生成': 'Regenerate now',
  '重新运行已驳回页面': 'Rerun rejected pages',
  '适合': 'Fit',
  '按住原图': 'Hold for original',
  '清字笔刷': 'Clean brush',
  '恢复原图': 'Restore original',
  '纹理复制': 'Clone texture',
  '修复画笔': 'Repair brush',
  '工具': 'Tool',
  '尺寸': 'Size',
  '清空本页操作': 'Clear page edits',
  '当前页版本': 'Current page versions',
  '双轨独立': 'Independent tracks',
  '嵌字版本': 'Lettering versions',
  '底图版本': 'Background versions',
  '页面批注': 'Page annotation',
  '已保存': 'Saved',
  '保存中…': 'Saving…',
  '保存失败': 'Save failed',
  '所有修改已保存': 'All changes saved',
  '有未保存修改': 'Unsaved changes',
  '底图叠加渲染（结果图）': 'Background + lettering (result)',
  '原图叠加渲染': 'Original + lettering',
  '页面': 'Page',
  '自动发现': 'Auto-discovered',
  '自定义': 'Custom',
  '系统': 'System',
  '可用': 'Available',
  '未找到': 'Not found',
  '已配置': 'Configured',
  '未配置 Key': 'API key not configured',
  '默认字体：未指定': 'Default font: not set',
  '全部状态': 'All statuses',
  '优先校对': 'Priority review',
  '已接受': 'Accepted',
  '已驳回': 'Rejected',
  '未审阅': 'Unreviewed',
}

export function t(locale: Locale, source: string): string {
  return locale === 'en-US' ? (en[source] ?? source) : source
}

export function readLocale(): Locale {
  try {
    return window.localStorage.getItem('emt.locale') === 'en-US' ? 'en-US' : 'zh-CN'
  } catch {
    return 'zh-CN'
  }
}

export function writeLocale(locale: Locale): void {
  try { window.localStorage.setItem('emt.locale', locale) } catch { /* storage can be unavailable in embedded webviews */ }
}
