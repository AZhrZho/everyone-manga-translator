export interface EmtDesktopHost {
  kind: 'desktop'
  platform: string
  versions: Record<string, string>
  selectImportFolder: () => Promise<HostPathGrant | null>
  selectImportPdf: () => Promise<HostPathGrant | null>
  selectProjectParent: () => Promise<HostPathGrant | null>
  selectExistingProject: () => Promise<HostPathGrant | null>
  selectExportFolder: () => Promise<HostPathGrant | null>
  selectExportZip: (suggestedName: string) => Promise<HostPathGrant | null>
  selectExportPdf: (suggestedName: string) => Promise<HostPathGrant | null>
  selectFontFiles: () => Promise<HostPathGrant[]>
  loadLocalModel: (modelId: 'craft') => Promise<HostLocalModel>
  minimize: () => Promise<void>
  maximize: () => Promise<void>
  close: () => Promise<void>
  isMaximized: () => Promise<boolean>
  setLocale: (locale: 'zh-CN' | 'en-US') => Promise<void>
  setDialogHandler: (handler: (request: DesktopDialogRequest) => Promise<boolean>) => void
  setWindowStateHandler: (handler: (maximized: boolean) => void) => void
  setBeforeCloseHandler: (handler: () => Promise<boolean>) => void
}

export interface HostLocalModel {
  name: string
  bytes: ArrayBuffer | Uint8Array
}

export interface DesktopDialogRequest {
  kind?: 'confirm' | 'alert'
  title: string
  message: string
  detail?: string
  confirmLabel: string
  cancelLabel: string
  destructive?: boolean
}

export interface HostPathGrant {
  token: string
  kind: 'ProjectParent' | 'ExistingProject' | 'ImportFolder' | 'ImportPdf' | 'ImportFont' | 'ExportFolder' | 'ExportZip' | 'ExportPdf'
  displayName: string
  displayPath?: string
  expiresAt: string
}

declare global {
  interface Window {
    emtHost?: EmtDesktopHost
  }
}

export const emtHost = window.emtHost
