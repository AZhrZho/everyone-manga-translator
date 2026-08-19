import type { Locale } from '../../../i18n'
import type { ImageEditTool } from '../../editor/model/imageEdit'

export function imageEditToolLabel(tool: ImageEditTool, locale: Locale) {
  if (tool === 'inpaint') return locale === 'en-US' ? 'Content-aware erase' : '智能清除'
  if (tool === 'mask-add') return locale === 'en-US' ? 'Add mask' : '添加 Mask'
  if (tool === 'mask-remove') return locale === 'en-US' ? 'Erase mask' : '擦除 Mask'
  return locale === 'en-US'
    ? ({ clean: 'Clean brush', restore: 'Restore original', clone: 'Clone texture', repair: 'Repair brush' } as const)[tool]
    : ({ clean: '清字笔刷', restore: '恢复原图', clone: '纹理复制', repair: '修复画笔' } as const)[tool]
}
