import assert from 'node:assert/strict'
import test from 'node:test'
import { dynamicApiListenUrl, parseApiReadyAddress } from './api-launch.mjs'

test('requests an operating-system-assigned loopback port', () => {
  assert.equal(dynamicApiListenUrl, 'http://127.0.0.1:0')
})

test('parses the explicit API readiness marker from buffered output', () => {
  const firstChunk = 'info: API starting\r\nEMT_API_'
  const secondChunk = 'READY=http://127.0.0.1:54321\r\n'
  assert.equal(parseApiReadyAddress(firstChunk), null)
  assert.equal(parseApiReadyAddress(firstChunk + secondChunk), 'http://127.0.0.1:54321')
})

test('rejects non-loopback, non-http, and unresolved readiness addresses', () => {
  assert.equal(parseApiReadyAddress('EMT_API_READY=http://0.0.0.0:5000\n'), null)
  assert.equal(parseApiReadyAddress('EMT_API_READY=https://127.0.0.1:5000\n'), null)
  assert.equal(parseApiReadyAddress('EMT_API_READY=http://127.0.0.1:0\n'), null)
})
