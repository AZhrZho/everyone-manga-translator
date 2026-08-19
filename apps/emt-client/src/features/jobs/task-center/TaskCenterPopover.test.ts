import { describe, expect, it } from 'vitest'
import { formatTaskDuration } from './formatTaskDuration'

describe('formatTaskDuration', () => {
  it('formats short, minute, and hour durations in Chinese', () => {
    expect(formatTaskDuration(42_000, 'zh-CN')).toBe('42 秒')
    expect(formatTaskDuration(125_000, 'zh-CN')).toBe('2 分 5 秒')
    expect(formatTaskDuration(3_720_000, 'zh-CN')).toBe('1 小时 2 分')
  })

  it('formats durations in English', () => {
    expect(formatTaskDuration(42_000, 'en-US')).toBe('42s')
    expect(formatTaskDuration(125_000, 'en-US')).toBe('2m 5s')
    expect(formatTaskDuration(3_720_000, 'en-US')).toBe('1h 2m')
  })
})
