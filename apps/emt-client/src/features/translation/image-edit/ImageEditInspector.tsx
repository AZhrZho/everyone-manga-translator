import { Paintbrush } from 'lucide-react'
import { readLocale } from '../../../i18n'

export function ImageEditInspector({ operationCount }: { operationCount: number }) {
  const locale = readLocale()
  return <div className="image-edit-inspector">
    <div className="image-edit-tool-summary"><Paintbrush size={18}/><span><b>{locale === 'en-US' ? 'Smart cleanup' : '智能清除'}</b><small>{locale === 'en-US' ? 'Paint over residual text or artifacts. Rendering starts after the pointer is released.' : '涂抹残留文字或清字瑕疵；松开指针后才提交并渲染。'}</small></span></div>
    <div className="image-edit-operation-count"><span>{locale === 'en-US' ? 'Repair strokes on this page' : '本页修复笔划'}</span><b>{operationCount}</b></div>
    <div className="image-edit-reset-note">{locale === 'en-US' ? 'AI regeneration, lettering version switches, and text-box changes rebuild the clean background and reset these repair strokes.' : 'AI 重新生成、切换嵌字版本或修改文本框后，系统会重建底图并重置这些修复笔划。'}</div>
    <small className="image-brush-shortcuts">{locale === 'en-US' ? '[ / ] size · Space-drag canvas' : '[ / ] 调整尺寸 · 空格拖动画布'}</small>
  </div>
}
