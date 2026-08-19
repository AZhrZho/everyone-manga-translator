export function formatTaskDuration(milliseconds: number, locale: 'zh-CN' | 'en-US') {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000))
  if (seconds < 60) return locale === 'en-US' ? `${seconds}s` : `${seconds} 秒`
  const minutes = Math.floor(seconds / 60)
  const remainingSeconds = seconds % 60
  if (minutes < 60) return locale === 'en-US'
    ? `${minutes}m ${remainingSeconds}s`
    : `${minutes} 分 ${remainingSeconds} 秒`
  const hours = Math.floor(minutes / 60)
  const remainingMinutes = minutes % 60
  return locale === 'en-US'
    ? `${hours}h ${remainingMinutes}m`
    : `${hours} 小时 ${remainingMinutes} 分`
}
