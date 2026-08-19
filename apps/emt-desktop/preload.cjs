const { contextBridge, ipcRenderer } = require('electron')

let beforeCloseHandler = async () => true
let windowStateHandler = () => undefined
let dialogHandler = async () => false

ipcRenderer.on('emt:prepare-close', async (_event, requestId) => {
  let succeeded = false
  try {
    succeeded = await beforeCloseHandler() !== false
  } catch {
    succeeded = false
  }
  ipcRenderer.send('emt:close-ready', requestId, succeeded)
})

ipcRenderer.on('emt:window-state', (_event, state) => {
  windowStateHandler(Boolean(state?.maximized))
})

ipcRenderer.on('emt:dialog-request', async (_event, requestId, request) => {
  let result = false
  try {
    result = Boolean(await dialogHandler(request))
  } catch {
    result = false
  }
  ipcRenderer.send('emt:dialog-response', requestId, result)
})

contextBridge.exposeInMainWorld('emtHost', Object.freeze({
  kind: 'desktop',
  platform: process.platform,
  versions: process.versions,
  selectImportFolder: () => ipcRenderer.invoke('emt:select-import-folder'),
  selectImportPdf: () => ipcRenderer.invoke('emt:select-import-pdf'),
  selectProjectParent: () => ipcRenderer.invoke('emt:select-project-parent'),
  selectExistingProject: () => ipcRenderer.invoke('emt:select-existing-project'),
  selectExportFolder: () => ipcRenderer.invoke('emt:select-export-folder'),
  selectExportZip: suggestedName => ipcRenderer.invoke('emt:select-export-zip', suggestedName),
  selectExportPdf: suggestedName => ipcRenderer.invoke('emt:select-export-pdf', suggestedName),
  selectFontFiles: () => ipcRenderer.invoke('emt:select-font-files'),
  loadLocalModel: modelId => ipcRenderer.invoke('emt:load-local-model', modelId),
  minimize: () => ipcRenderer.invoke('emt:window-minimize'),
  maximize: () => ipcRenderer.invoke('emt:window-maximize'),
  close: () => ipcRenderer.invoke('emt:window-close'),
  isMaximized: () => ipcRenderer.invoke('emt:window-is-maximized'),
  setLocale: locale => ipcRenderer.invoke('emt:set-locale', locale),
  setDialogHandler: handler => { dialogHandler = typeof handler === 'function' ? handler : async () => false },
  setWindowStateHandler: handler => {
    windowStateHandler = typeof handler === 'function' ? handler : () => undefined
    void ipcRenderer.invoke('emt:window-is-maximized').then(maximized => windowStateHandler(Boolean(maximized)))
  },
  setBeforeCloseHandler: handler => { beforeCloseHandler = handler },
}))
