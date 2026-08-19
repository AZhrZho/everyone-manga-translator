export type ImageEditTool = 'inpaint'
export type ImageEditPoint = { x: number; y: number }
export type ImageEditOperation = {
  operation_id: string
  tool: ImageEditTool
  size_px: number
  points: ImageEditPoint[]
  spacing?: number
}
export type ImageBrushSettings = { spacing: number }
export type ImageEditDocument = {
  schema_version?: string
  operations: ImageEditOperation[]
}
export type ImageEditHistory = { undo: string[]; redo: string[] }

export function parseImageEdit(operationsJson: string | null): ImageEditDocument {
  if (operationsJson) {
    try {
      const parsed = JSON.parse(operationsJson) as ImageEditDocument
      if (Array.isArray(parsed.operations)) return { schema_version: '2.0.0', operations: parsed.operations.filter(operation => operation.tool === 'inpaint') }
    } catch {
      // Fall through to a safe empty document when legacy or partial data is unreadable.
    }
  }
  return createDefaultImageEdit()
}

export function createDefaultImageEdit(): ImageEditDocument {
  return { schema_version: '2.0.0', operations: [] }
}

export function serializeImageEdit(document: ImageEditDocument) { return JSON.stringify(document) }
