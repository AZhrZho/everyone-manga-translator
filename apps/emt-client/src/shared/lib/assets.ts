import { apiBaseUrl } from '../../api'

export function assetUrl(path: string) {
  return path.startsWith('http://') || path.startsWith('https://') ? path : `${apiBaseUrl}${path}`
}
