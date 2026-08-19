import { apiBaseUrl } from '../../api'
import type { FontInfo } from '../../contracts'
import type { Locale } from '../../i18n'

export function ManagedFontSample({ font, locale }: { font: FontInfo; locale: Locale }) {
  const previewUrl = `${apiBaseUrl}/api/settings/fonts/${encodeURIComponent(font.fontId)}/preview?locale=${encodeURIComponent(locale)}`
  return <span className="managed-font-option"><b>{font.displayName}</b><img src={previewUrl} alt={locale === 'en-US' ? `${font.displayName} preview` : `${font.displayName} 字体样例`} loading="lazy"/></span>
}
