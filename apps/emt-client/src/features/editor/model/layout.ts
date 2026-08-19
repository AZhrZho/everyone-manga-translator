import type { ProjectPage } from '../../../contracts'

export type LayoutBox = { x0: number; y0: number; x1: number; y1: number }
export type LayoutStroke = { width_px: number; color: string; round_corners: boolean }
export type LayoutLineAdjustment = { line_index: number; offset_x_px: number; offset_y_px: number; font_size_px?: number | null; color?: string | null }
export type LayoutCharacterAdjustment = { character_index: number; offset_x_px: number; offset_y_px: number; font_size_px?: number | null; color?: string | null }
export type LayoutRubyAnnotation = { base_character_index: number; base_character_length: number; text: string; placement: 'auto' | 'before' | 'after'; offset_x_px: number; offset_y_px: number; font_size_px?: number | null; color?: string | null }
export type LayoutPlacementGroup = { line_indices: number[]; source_region_ids: string[]; text_box: LayoutBox }
export type LayoutAdvanced = {
  line_adjustments: LayoutLineAdjustment[]
  character_adjustments: LayoutCharacterAdjustment[]
  ruby_annotations: LayoutRubyAnnotation[]
  placement_groups: LayoutPlacementGroup[]
}
export type LayoutSpec = {
  text_box: LayoutBox
  orientation: 'horizontal' | 'vertical'
  reading_direction: 'left_to_right' | 'right_to_left'
  alignment: 'start' | 'center' | 'end' | 'justify'
  font_id: string
  font_size_px: number
  italic: boolean
  color: string
  letter_spacing_px: number
  font_weight: number
  rotation?: { center_x_px: number; center_y_px: number; angle_deg: number } | null
  stroke?: LayoutStroke | null
  advanced?: LayoutAdvanced | null
  [key: string]: unknown
}
export type LayoutSegment = {
  segment_id: string
  translated_text: string
  source_text?: string
  status?: string
  layout: LayoutSpec
  [key: string]: unknown
}
export type LayoutDocument = {
  schema_version?: string
  segments: LayoutSegment[]
  user_deleted_segment_ids?: string[]
  [key: string]: unknown
}
export type LayoutManualOverride = 'translated_text' | 'text_box' | 'rotation' | 'basic_format' | 'flow' | 'advanced_layout'
export type BasicTextFormat = Pick<LayoutSpec, 'font_id' | 'font_size_px' | 'font_weight' | 'italic' | 'color' | 'letter_spacing_px'> & { stroke: LayoutStroke }
export type LayoutHistory = { undo: string[]; redo: string[] }
export type TextClip = { url: string; width: number; height: number; fingerprint: string; renderKey: string }

export function parseLayout(layoutJson: string | null, page: ProjectPage): LayoutDocument {
  if (layoutJson) {
    try {
      const parsed = JSON.parse(layoutJson) as Partial<LayoutDocument>
      if (Array.isArray(parsed.segments)) return parsed as LayoutDocument
      if (typeof (parsed as { text?: unknown }).text === 'string') {
        const text = String((parsed as { text: string }).text)
        return text.trim() ? { schema_version: '1.0.0', segments: [createSegment(page, 1, text)] } : createDefaultLayout()
      }
    } catch {
      // Fall through to a safe empty document when legacy or partial data is unreadable.
    }
  }
  return createDefaultLayout()
}

export function createDefaultLayout(): LayoutDocument {
  return { schema_version: '1.0.0', segments: [] }
}

export function createSegment(page: ProjectPage, index: number, text = '', center?: { x: number; y: number }): LayoutSegment {
  const width = Math.min(Math.max(160, Math.round(page.width * 0.42)), Math.max(40, page.width - 48))
  const height = Math.min(Math.max(100, Math.round(page.height * 0.16)), Math.max(40, page.height - 48))
  const centerX = Math.max(24 + width / 2, Math.min(page.width - 24 - width / 2, center?.x ?? page.width / 2))
  const centerY = Math.max(24 + height / 2, Math.min(page.height - 24 - height / 2, center?.y ?? page.height / 2))
  const x0 = Math.round(centerX - width / 2)
  const y0 = Math.round(centerY - height / 2)
  return { segment_id: `user-segment-${Date.now()}-${index}`, translated_text: text, source_text: '', layout: { text_box: { x0, y0, x1: x0 + width, y1: y0 + height }, orientation: 'horizontal', reading_direction: 'left_to_right', alignment: 'center', font_id: 'default', font_size_px: Math.max(18, Math.round(page.width / 24)), italic: false, color: '#ffffff', letter_spacing_px: 0, font_weight: 400, rotation: null, stroke: { width_px: 3, color: '#000000', round_corners: true }, advanced: { line_adjustments: [], character_adjustments: [], ruby_annotations: [], placement_groups: [] } } }
}

export function serializeLayout(layout: LayoutDocument) { return JSON.stringify(layout) }

export function readBasicTextFormat(layout: LayoutSpec): BasicTextFormat {
  return {
    font_id: layout.font_id,
    font_size_px: layout.font_size_px,
    font_weight: layout.font_weight,
    italic: layout.italic,
    color: layout.color,
    letter_spacing_px: layout.letter_spacing_px,
    stroke: { ...(layout.stroke ?? { width_px: 0, color: '#000000', round_corners: true }) },
  }
}

export function segmentRenderKey(segment: LayoutSegment) {
  const box = segment.layout.text_box
  const advanced = segment.layout.advanced
  return JSON.stringify({
    translated_text: segment.translated_text,
    status: segment.status,
    layout: {
      ...segment.layout,
      text_box: { x0: 0, y0: 0, x1: box.x1 - box.x0, y1: box.y1 - box.y0 },
      rotation: null,
      advanced: advanced
        ? {
            ...advanced,
            placement_groups: advanced.placement_groups.map(group => ({
              ...group,
              text_box: {
                x0: group.text_box.x0 - box.x0,
                y0: group.text_box.y0 - box.y0,
                x1: group.text_box.x1 - box.x0,
                y1: group.text_box.y1 - box.y0,
              },
            })),
          }
        : advanced,
    },
  })
}

export function clampBox(box: LayoutBox, page: ProjectPage): LayoutBox {
  const width = Math.max(32, box.x1 - box.x0)
  const height = Math.max(28, box.y1 - box.y0)
  const x0 = Math.max(0, Math.min(page.width - width, box.x0))
  const y0 = Math.max(0, Math.min(page.height - height, box.y0))
  return { x0: Math.round(x0), y0: Math.round(y0), x1: Math.round(Math.min(page.width, x0 + width)), y1: Math.round(Math.min(page.height, y0 + height)) }
}

export function normalizeAdvancedLayout(value: LayoutAdvanced | null | undefined): LayoutAdvanced {
  return {
    line_adjustments: Array.isArray(value?.line_adjustments) ? value.line_adjustments : [],
    character_adjustments: Array.isArray(value?.character_adjustments) ? value.character_adjustments : [],
    ruby_annotations: Array.isArray(value?.ruby_annotations) ? value.ruby_annotations : [],
    placement_groups: Array.isArray(value?.placement_groups) ? value.placement_groups : [],
  }
}

export function nullableNumber(value: string) { return value.trim() === '' ? null : Number(value) }
export function integerList(value: string) { return value.split(',').map(item => Number(item.trim())).filter(item => Number.isInteger(item) && item >= 0) }
export function stringList(value: string) { return value.split(',').map(item => item.trim()).filter(Boolean) }
