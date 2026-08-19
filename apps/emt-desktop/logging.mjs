import fs from 'node:fs'
import path from 'node:path'

export const defaultMaximumLogBytes = 8 * 1024 * 1024

export function appendDesktopLog(logPath, level, message, details, maximumBytes = defaultMaximumLogBytes) {
  try {
    fs.mkdirSync(path.dirname(logPath), { recursive: true })
    const suffix = details === undefined ? '' : ` ${JSON.stringify(details)}`
    const entry = `${new Date().toISOString()} [${level}] ${message}${suffix}\n`
    if (fs.existsSync(logPath) && fs.statSync(logPath).size + Buffer.byteLength(entry, 'utf8') > maximumBytes) {
      try {
        const rotatedPath = `${logPath}.1`
        fs.rmSync(rotatedPath, { force: true })
        fs.renameSync(logPath, rotatedPath)
      } catch {
        // Keep appending to the current file if rotation is unavailable.
      }
    }
    fs.appendFileSync(logPath, entry, 'utf8')
  } catch {
    // Logging must never prevent the application from starting or shutting down.
  }
}

export function formatError(error) {
  if (error instanceof Error) return { name: error.name, message: error.message, stack: error.stack }
  return { message: String(error) }
}
