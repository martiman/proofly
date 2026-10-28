import { describe, expect, it } from 'vitest'
import { formatMoney } from '../format'

describe('formatMoney', () => {
  it('trims insignificant trailing zeros to the minor units', () => {
    expect(formatMoney('-7571748.810000', 'GBP', 'en-GB')).toBe('-£7,571,748.81')
    expect(formatMoney('100.000000', 'JPY', 'en-US')).toBe('¥100')
    expect(formatMoney('5', 'GBP', 'en-GB')).toBe('£5.00')
  })
  it('keeps significant precision', () => {
    expect(formatMoney('0.123456', 'GBP', 'en-GB')).toBe('£0.123456')
    expect(formatMoney('1.1200', 'GBP', 'en-GB')).toBe('£1.12')
  })
  it('throws on invalid input', () => {
    expect(() => formatMoney('1e3', 'GBP')).toThrow(RangeError)
    expect(() => formatMoney('1.00', 'gbp')).toThrow(RangeError)
  })
})
