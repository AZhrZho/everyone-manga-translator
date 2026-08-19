import { app, BrowserWindow, dialog, ipcMain, Menu } from 'electron'
import { spawn, spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { dynamicApiListenUrl, parseApiReadyAddress } from './api-launch.mjs'
import { appendDesktopLog, formatError } from './logging.mjs'

const currentDirectory = path.dirname(fileURLToPath(import.meta.url))
const apiProject = path.resolve(currentDirectory, '..', 'MangaTranslator.Api', 'MangaTranslator.Api.csproj')
const maximumLocalModelBytes = 512 * 1024 * 1024
const localModelFiles = Object.freeze({ craft: 'craft.onnx' })
const desktopLogFileName = 'desktop.log'
const apiStartupTimeoutMilliseconds = 20000
let apiProcess
let apiUrl
let mainWindow
let quitting = false
let closeDialogInFlight = false
let closeRequestSequence = 0
let dialogRequestSequence = 0
let uiLocale = 'zh-CN'
let initialTheme = 'light'

const ui = (zh, en) => uiLocale === 'en-US' ? en : zh

function desktopLogPath() {
  const configuredLogsRoot = process.env.EMT_LOGS_ROOT
  if (configuredLogsRoot) return path.join(path.resolve(configuredLogsRoot), desktopLogFileName)
  try {
    return path.join(app.getPath('logs'), desktopLogFileName)
  } catch {
    const appData = process.env.APPDATA ?? currentDirectory
    return path.join(appData, 'EMT Studio', 'logs', desktopLogFileName)
  }
}

function writeDesktopLog(level, message, details) {
  appendDesktopLog(desktopLogPath(), level, message, details)
}

process.on('uncaughtException', error => {
  writeDesktopLog('ERROR', 'Uncaught Electron exception.', formatError(error))
})
process.on('unhandledRejection', reason => {
  writeDesktopLog('ERROR', 'Unhandled Electron promise rejection.', formatError(reason))
})

function desktopPath(environmentName, electronPathName) {
  const configured = process.env[environmentName]
  return configured ? path.resolve(configured) : app.getPath(electronPathName)
}

async function refreshDesktopPreferences() {
  try {
    const response = await fetch(`${apiUrl}/api/settings`)
    if (!response.ok) return
    const settings = await response.json()
    uiLocale = settings.locale === 'en-US' ? 'en-US' : 'zh-CN'
    initialTheme = settings.theme === 'light' ? 'light' : 'dark'
  } catch {
    // Keep the platform-language fallback when settings are unavailable.
  }
}

function startApi() {
  const command = app.isPackaged
    ? path.join(process.resourcesPath, 'api', 'MangaTranslator.Api.exe')
    : process.env.EMT_DOTNET_PATH
      ?? (process.platform === 'win32' ? 'C:\\Program Files\\dotnet\\dotnet.exe' : 'dotnet')
  const args = app.isPackaged
    ? []
    : ['run', '--project', apiProject, '--no-launch-profile', '--no-restore']
  const requestedApiUrl = process.env.EMT_API_URL ?? dynamicApiListenUrl
  const startedProcess = spawn(command, args, {
    windowsHide: true,
    cwd: app.isPackaged ? path.dirname(command) : currentDirectory,
    env: {
      ...process.env,
      ASPNETCORE_URLS: requestedApiUrl,
      EMT_HOST_KIND: 'desktop',
      EMT_USER_DATA_ROOT: desktopPath('EMT_USER_DATA_ROOT', 'userData'),
      EMT_CACHE_ROOT: desktopPath('EMT_CACHE_ROOT', 'cache'),
      EMT_LOGS_ROOT: desktopPath('EMT_LOGS_ROOT', 'logs'),
      EMT_TEMP_ROOT: desktopPath('EMT_TEMP_ROOT', 'temp'),
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  apiProcess = startedProcess
  writeDesktopLog('INFO', 'Starting local API.', {
    command,
    args,
    packaged: app.isPackaged,
    requestedApiUrl,
    processId: startedProcess.pid,
    logsRoot: desktopPath('EMT_LOGS_ROOT', 'logs'),
  })
  return new Promise((resolve, reject) => {
    let ready = false
    let standardOutput = ''
    const timeout = setTimeout(() => {
      reject(new Error(ui('本地服务未能报告监听地址。', 'The local service did not report its listening address.')))
    }, apiStartupTimeoutMilliseconds)
    const rejectStartup = error => {
      if (ready) return
      clearTimeout(timeout)
      reject(error)
    }

    startedProcess.on('error', error => {
      writeDesktopLog('ERROR', 'Failed to start local API.', formatError(error))
      console.error(`[api] Failed to start ${command}:`, error)
      rejectStartup(error)
    })
    startedProcess.stdout.on('data', data => {
      const chunk = data.toString()
      const text = chunk.trimEnd()
      standardOutput = `${standardOutput}${chunk}`.slice(-8192)
      writeDesktopLog('INFO', 'API stdout.', { text })
      console.info(`[api] ${data}`)
      if (ready) return
      const boundAddress = parseApiReadyAddress(standardOutput)
      if (!boundAddress) return
      ready = true
      clearTimeout(timeout)
      writeDesktopLog('INFO', 'Local API is ready.', { apiUrl: boundAddress, processId: startedProcess.pid })
      resolve(boundAddress)
    })
    startedProcess.stderr.on('data', data => {
      const text = data.toString().trimEnd()
      writeDesktopLog('ERROR', 'API stderr.', { text })
      console.error(`[api] ${data}`)
    })
    startedProcess.on('exit', (code, signal) => {
      clearTimeout(timeout)
      writeDesktopLog(code === 0 || quitting ? 'INFO' : 'ERROR', 'Local API exited.', { code, signal })
      if (!ready) {
        rejectStartup(new Error(ui(
          `本地服务在启动完成前退出，代码 ${code ?? 'unknown'}。`,
          `The local service exited before startup completed with code ${code ?? 'unknown'}.`,
        )))
        return
      }
      if (!quitting && code !== 0) {
        const alertOptions = {
          kind: 'alert',
          title: ui('EMT 服务已停止', 'EMT service stopped'),
          message: ui(
            `本地服务异常退出，代码 ${code ?? 'unknown'}。\n日志：${desktopLogPath()}`,
            `The local service exited unexpectedly with code ${code ?? 'unknown'}.\nLog: ${desktopLogPath()}`,
          ),
          confirmLabel: ui('知道了', 'OK'),
          cancelLabel: ui('关闭', 'Close'),
        }
        void requestRendererDialog(alertOptions).then(result => {
          if (result === null) void dialog.showErrorBox(alertOptions.title, alertOptions.message)
        })
      }
    })
  })
}

function installBundledLocalModels() {
  const bundledModelsDirectory = app.isPackaged
    ? path.join(process.resourcesPath, 'models')
    : path.resolve(currentDirectory, '..', '..', 'resources', 'models')
  const installedModelsDirectory = path.join(desktopPath('EMT_USER_DATA_ROOT', 'userData'), 'models')
  for (const modelFileName of ['craft.onnx', 'comictextdetector.pt.onnx']) {
    const source = path.join(bundledModelsDirectory, modelFileName)
    const destination = path.join(installedModelsDirectory, modelFileName)
    if (!fs.existsSync(source) || fs.existsSync(destination)) continue
    fs.mkdirSync(installedModelsDirectory, { recursive: true })
    const temporaryDestination = `${destination}.${process.pid}.tmp`
    try {
      fs.copyFileSync(source, temporaryDestination)
      try {
        fs.renameSync(temporaryDestination, destination)
        console.info(`[desktop] Installed local model: ${destination}`)
      } catch (error) {
        if (!fs.existsSync(destination)) throw error
      }
    } finally {
      fs.rmSync(temporaryDestination, { force: true })
    }
  }
}

async function readInstalledLocalModel(modelId) {
  const modelFileName = localModelFiles[modelId]
  if (!modelFileName) throw new Error('Unsupported local model.')
  const modelPath = path.join(desktopPath('EMT_USER_DATA_ROOT', 'userData'), 'models', modelFileName)
  let stat
  try {
    stat = await fs.promises.stat(modelPath)
  } catch (error) {
    if (error?.code === 'ENOENT') {
      throw new Error(ui(
        `未找到浏览器 CRAFT 模型：${modelFileName}。请重新安装应用或将模型安装到应用模型目录。`,
        `The Browser CRAFT model (${modelFileName}) is not installed. Reinstall the app or install the model in the application model directory.`,
      ))
    }
    throw error
  }
  if (!stat.isFile()) throw new Error(ui('本地模型路径不是文件。', 'The local model path is not a file.'))
  if (stat.size > maximumLocalModelBytes) throw new Error(ui('本地模型超过 512 MiB 限制。', 'The local model exceeds the 512 MiB limit.'))
  const bytes = await fs.promises.readFile(modelPath)
  return {
    name: modelFileName,
    bytes: bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
  }
}

function stopApi() {
  if (!apiProcess || apiProcess.exitCode !== null || !apiProcess.pid) return
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(apiProcess.pid), '/t', '/f'], {
      windowsHide: true,
      stdio: 'ignore',
    })
  } else {
    apiProcess.kill('SIGTERM')
  }
  apiProcess = undefined
}

async function waitForApi(boundApiUrl) {
  for (let attempt = 0; attempt < 80; attempt += 1) {
    try {
      const response = await fetch(`${boundApiUrl}/health`)
      if (response.ok) return
    } catch {
      // Service is still starting.
    }
    await new Promise(resolve => setTimeout(resolve, 250))
  }
  throw new Error(ui('本地服务启动超时。', 'The local service took too long to start.'))
}

async function createWindow() {
  writeDesktopLog('INFO', 'Creating desktop window.', {
    packaged: app.isPackaged,
    platform: process.platform,
    electron: process.versions.electron,
    node: process.versions.node,
  })
  uiLocale = app.getLocale().toLowerCase().startsWith('zh') ? 'zh-CN' : 'en-US'
  installBundledLocalModels()
  apiUrl = await startApi()
  await waitForApi(apiUrl)
  await refreshDesktopPreferences()
  Menu.setApplicationMenu(null)
  const clientUrl = process.env.EMT_CLIENT_URL ?? (app.isPackaged ? apiUrl : 'http://127.0.0.1:5173')
  mainWindow = new BrowserWindow({
    title: 'EMT Studio',
    width: 1440,
    height: 900,
    minWidth: 1100,
    minHeight: 700,
    backgroundColor: initialTheme === 'light' ? '#f3f1f5' : '#211f25',
    frame: false,
    titleBarStyle: 'hidden',
    autoHideMenuBar: true,
    icon: path.join(currentDirectory, 'emt-logo.png'),
    show: false,
    webPreferences: {
      preload: path.join(currentDirectory, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      additionalArguments: [`--emt-api-url=${apiUrl}`],
    },
  })
  mainWindow.once('ready-to-show', () => mainWindow?.show())
  mainWindow.webContents.on('did-fail-load', (_event, errorCode, errorDescription, validatedURL) => {
    writeDesktopLog('ERROR', 'Renderer failed to load.', { errorCode, errorDescription, validatedURL })
  })
  mainWindow.webContents.on('render-process-gone', (_event, details) => {
    writeDesktopLog('ERROR', 'Renderer process exited.', details)
  })
  mainWindow.on('maximize', () => mainWindow?.webContents.send('emt:window-state', { maximized: true }))
  mainWindow.on('unmaximize', () => mainWindow?.webContents.send('emt:window-state', { maximized: false }))
  mainWindow.on('close', event => {
    if (quitting) return
    event.preventDefault()
    if (closeDialogInFlight) return
    closeDialogInFlight = true
    void confirmClose().finally(() => { closeDialogInFlight = false })
  })
  await mainWindow.loadURL(clientUrl)
  const rendererHostKind = await mainWindow.webContents.executeJavaScript(
    'window.emtHost?.kind ?? null',
  )
  if (rendererHostKind !== 'desktop') {
    throw new Error('Electron preload bridge was not exposed to the renderer.')
  }
  console.info(`[desktop] Renderer host bridge: ${rendererHostKind}`)
  writeDesktopLog('INFO', 'Renderer host bridge ready.', { rendererHostKind, clientUrl })
  if (process.env.EMT_DESKTOP_SMOKE_TEST === '1') {
    quitting = true
    mainWindow.destroy()
    app.quit()
  }
}

async function confirmClose() {
  await refreshDesktopPreferences()
  const editsSaved = await requestRendererFlush()
  if (!editsSaved && mainWindow) {
    const result = await requestConfirmation({
      title: ui('自动保存未完成', 'Autosave did not finish'),
      message: ui('部分用户编辑未能在关闭前保存。', 'Some edits could not be saved before closing.'),
      detail: ui('仍然退出可能丢失最近几秒的编辑。建议取消并检查保存错误。', 'Exiting now may lose the most recent edits. Cancel and review the save error if possible.'),
      confirmLabel: ui('仍然退出', 'Exit anyway'),
      cancelLabel: ui('取消', 'Cancel'),
      destructive: true,
    })
    if (result !== true) return
  }

  let activeJobs = []
  try {
    const response = await fetch(`${apiUrl}/api/jobs/active`)
    if (response.ok) {
      const jobs = await response.json()
      activeJobs = jobs.filter(job => ['Pending', 'Running', 'StopRequested'].includes(job.status))
    }
  } catch {
    // If the local service is unavailable there is no task process to keep alive.
  }

  if (activeJobs.length > 0 && mainWindow) {
    const result = await requestConfirmation({
      title: ui('任务仍在运行', 'Tasks are still running'),
      message: ui(`当前有 ${activeJobs.length} 个任务仍在运行。`, `${activeJobs.length} ${activeJobs.length === 1 ? 'task is' : 'tasks are'} still running.`),
      detail: ui('退出会请求停止任务，并保留已经完成的批次和中间结果。', 'Exiting will request cancellation and keep completed batches and intermediate results.'),
      confirmLabel: ui('停止任务并退出', 'Stop tasks and exit'),
      cancelLabel: ui('取消', 'Cancel'),
      destructive: true,
    })
    if (result !== true) return
    await Promise.allSettled(activeJobs.map(job => fetch(`${apiUrl}/api/jobs/${encodeURIComponent(job.jobId)}/stop`, { method: 'POST' })))
  }

  quitting = true
  mainWindow?.close()
}

function requestRendererDialog(options) {
  if (!mainWindow || mainWindow.isDestroyed()) return Promise.resolve(null)
  const requestId = `dialog-${Date.now()}-${dialogRequestSequence += 1}`
  return new Promise(resolve => {
    const timeout = setTimeout(() => finish(null), 15000)
    const listener = (_event, responseId, confirmed) => {
      if (responseId === requestId) finish(Boolean(confirmed))
    }
    const finish = result => {
      clearTimeout(timeout)
      ipcMain.removeListener('emt:dialog-response', listener)
      resolve(result)
    }
    ipcMain.on('emt:dialog-response', listener)
    mainWindow.webContents.send('emt:dialog-request', requestId, options)
  })
}

async function requestConfirmation(options) {
  const result = await requestRendererDialog(options)
  if (result !== null) return result
  if (!mainWindow || mainWindow.isDestroyed()) return false
  const fallback = await dialog.showMessageBox(mainWindow, {
    type: 'warning',
    title: options.title,
    message: options.message,
    detail: options.detail,
    buttons: [options.confirmLabel, options.cancelLabel],
    defaultId: 1,
    cancelId: 1,
    noLink: true,
  })
  return fallback.response === 0
}

function requestRendererFlush() {
  if (!mainWindow || mainWindow.isDestroyed()) return Promise.resolve(true)
  const requestId = `close-${Date.now()}-${closeRequestSequence += 1}`
  return new Promise(resolve => {
    const timeout = setTimeout(() => finish(false), 5000)
    const listener = (_event, responseId, succeeded) => {
      if (responseId === requestId) finish(Boolean(succeeded))
    }
    const finish = succeeded => {
      clearTimeout(timeout)
      ipcMain.removeListener('emt:close-ready', listener)
      resolve(succeeded)
    }
    ipcMain.on('emt:close-ready', listener)
    mainWindow.webContents.send('emt:prepare-close', requestId)
  })
}

async function selectAndRegister(kind, title) {
  const options = { title, properties: ['openDirectory'] }
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, options)
    : await dialog.showOpenDialog(options)
  if (result.canceled || !result.filePaths[0]) return null
  return registerLocalPath(result.filePaths[0], kind)
}

async function registerLocalPath(selectedPath, kind) {
  const response = await fetch(`${apiUrl}/api/host/local-path-grants`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: selectedPath, kind }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? ui(`无法登记所选位置 (${response.status})`, `Could not register the selected location (${response.status})`))
  }
  const grant = await response.json()
  return { ...grant, displayPath: selectedPath }
}

async function selectPdfAndRegister() {
  const options = {
    title: ui('选择漫画 PDF', 'Select manga PDF'),
    properties: ['openFile'],
    filters: [{ name: ui('PDF 文档', 'PDF documents'), extensions: ['pdf'] }],
  }
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, options)
    : await dialog.showOpenDialog(options)
  if (result.canceled || !result.filePaths[0]) return null
  return registerLocalPath(result.filePaths[0], 'ImportPdf')
}

async function selectFontsAndRegister() {
  const options = {
    title: ui('导入字体', 'Import fonts'),
    properties: ['openFile', 'multiSelections'],
    filters: [{ name: ui('字体文件', 'Font files'), extensions: ['ttf', 'otf', 'ttc', 'otc'] }],
  }
  const result = mainWindow
    ? await dialog.showOpenDialog(mainWindow, options)
    : await dialog.showOpenDialog(options)
  if (result.canceled || result.filePaths.length === 0) return []
  return Promise.all(result.filePaths.map(filePath => registerLocalPath(filePath, 'ImportFont')))
}

async function selectExportFileAndRegister(kind, title, suggestedName, extension) {
  const result = mainWindow
    ? await dialog.showSaveDialog(mainWindow, {
        title,
        defaultPath: `${suggestedName}.${extension}`,
        filters: [{ name: extension.toUpperCase(), extensions: [extension] }],
      })
    : await dialog.showSaveDialog({
        title,
        defaultPath: `${suggestedName}.${extension}`,
        filters: [{ name: extension.toUpperCase(), extensions: [extension] }],
      })
  if (result.canceled || !result.filePath) return null
  const selectedPath = result.filePath.toLowerCase().endsWith(`.${extension}`)
    ? result.filePath
    : `${result.filePath}.${extension}`
  return registerLocalPath(selectedPath, kind)
}

ipcMain.handle('emt:select-import-folder', () => selectAndRegister('ImportFolder', ui('选择漫画源文件夹', 'Select manga source folder')))
ipcMain.handle('emt:select-import-pdf', selectPdfAndRegister)
ipcMain.handle('emt:select-project-parent', () => selectAndRegister('ProjectParent', ui('选择项目保存位置', 'Select project location')))
ipcMain.handle('emt:select-existing-project', () => selectAndRegister('ExistingProject', ui('打开 EMT 项目文件夹', 'Open EMT project folder')))
ipcMain.handle('emt:select-export-folder', () => selectAndRegister('ExportFolder', ui('选择图片导出文件夹', 'Select image export folder')))
ipcMain.handle('emt:select-export-zip', (_event, suggestedName) => selectExportFileAndRegister('ExportZip', ui('导出 ZIP', 'Export ZIP'), suggestedName, 'zip'))
ipcMain.handle('emt:select-export-pdf', (_event, suggestedName) => selectExportFileAndRegister('ExportPdf', ui('导出 PDF', 'Export PDF'), suggestedName, 'pdf'))
ipcMain.handle('emt:select-font-files', selectFontsAndRegister)
ipcMain.handle('emt:load-local-model', (_event, modelId) => readInstalledLocalModel(modelId))
ipcMain.handle('emt:window-minimize', () => { mainWindow?.minimize() })
ipcMain.handle('emt:window-maximize', () => {
  if (!mainWindow) return
  if (mainWindow.isMaximized()) mainWindow.unmaximize()
  else mainWindow.maximize()
})
ipcMain.handle('emt:window-close', () => { mainWindow?.close() })
ipcMain.handle('emt:window-is-maximized', () => mainWindow?.isMaximized() ?? false)
ipcMain.handle('emt:set-locale', (_event, locale) => { uiLocale = locale === 'en-US' ? 'en-US' : 'zh-CN' })

app.whenReady().then(createWindow).catch(error => {
  writeDesktopLog('ERROR', 'EMT failed to start.', formatError(error))
  const errorMessage = error instanceof Error ? error.message : String(error)
  dialog.showErrorBox(
    ui('EMT 启动失败', 'EMT failed to start'),
    `${errorMessage}\n\n${ui('日志', 'Log')}: ${desktopLogPath()}`,
  )
  app.quit()
})

app.on('child-process-gone', (_event, details) => {
  writeDesktopLog('ERROR', 'Electron child process exited.', details)
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit()
})

app.on('before-quit', event => {
  if (!quitting && mainWindow && !mainWindow.isDestroyed()) {
    event.preventDefault()
    if (!closeDialogInFlight) {
      closeDialogInFlight = true
      void confirmClose().finally(() => { closeDialogInFlight = false })
    }
    return
  }

  quitting = true
  writeDesktopLog('INFO', 'Electron application is quitting.')
  stopApi()
})
