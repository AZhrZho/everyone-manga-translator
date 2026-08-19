export const dynamicApiListenUrl = 'http://127.0.0.1:0'
export const apiReadyPrefix = 'EMT_API_READY='

export function parseApiReadyAddress(output) {
  const marker = output
    .split(/\r?\n/u)
    .map(line => line.trim())
    .find(line => line.startsWith(apiReadyPrefix))
  if (!marker) return null

  try {
    const address = new URL(marker.slice(apiReadyPrefix.length))
    const isLoopback = address.hostname === '127.0.0.1'
      || address.hostname === 'localhost'
      || address.hostname === '[::1]'
    const port = Number(address.port)
    if (address.protocol !== 'http:' || !isLoopback || !Number.isInteger(port) || port < 1 || port > 65535) {
      return null
    }
    return address.origin
  } catch {
    return null
  }
}
