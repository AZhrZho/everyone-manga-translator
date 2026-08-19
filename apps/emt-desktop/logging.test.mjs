import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { appendDesktopLog, formatError } from './logging.mjs'

test('writes feedback-ready desktop log entries', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'emt-desktop-log-'))
  try {
    const logPath = path.join(root, 'logs', 'desktop.log')
    appendDesktopLog(logPath, 'ERROR', 'EMT failed to start.', formatError(new Error('sample failure')))

    const content = fs.readFileSync(logPath, 'utf8')
    assert.match(content, /\[ERROR\] EMT failed to start\./)
    assert.match(content, /"message":"sample failure"/)
    assert.match(content, /"stack":"Error: sample failure/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('keeps one rotated log when the size limit is reached', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'emt-desktop-log-'))
  try {
    const logPath = path.join(root, 'desktop.log')
    appendDesktopLog(logPath, 'INFO', 'first entry', { value: 'x'.repeat(80) }, 100)
    appendDesktopLog(logPath, 'INFO', 'second entry', undefined, 100)

    assert.equal(fs.existsSync(`${logPath}.1`), true)
    assert.match(fs.readFileSync(`${logPath}.1`, 'utf8'), /first entry/)
    assert.match(fs.readFileSync(logPath, 'utf8'), /second entry/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})
