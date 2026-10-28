import type { Currency, Money } from './types'

const VALID: Currency[] = ['EUR', 'CZK', 'USD']
export const isCurrency = (c: string): c is Currency => (VALID as string[]).includes(c)

/** Parse a decimal string into integer minor units (2 dp) without floats. */
export function toMinor(amount: string): bigint {
  const s = amount.trim()
  const neg = s.startsWith('-')
  const [i = '0', f = ''] = s.replace(/^[-+]/, '').split('.')
  const v = BigInt(i || '0') * 100n + BigInt((f + '00').slice(0, 2))
  return neg ? -v : v
}
export function fromMinor(v: bigint): string {
  const neg = v < 0n
  const a = neg ? -v : v
  return `${neg ? '-' : ''}${a / 100n}.${(a % 100n).toString().padStart(2, '0')}`
}
export const money = (amount: string, currency: Currency): Money => ({
  amount: fromMinor(toMinor(amount)),
  currency,
})
export function addMoney(a: Money, b: Money): Money {
  if (a.currency !== b.currency) throw new Error('Currency mismatch')
  return { amount: fromMinor(toMinor(a.amount) + toMinor(b.amount)), currency: a.currency }
}
export function subMoney(a: Money, b: Money): Money {
  return addMoney(a, { amount: fromMinor(-toMinor(b.amount)), currency: b.currency })
}
export const sumMoney = (items: Money[], currency: Currency): Money =>
  items.reduce((acc, m) => addMoney(acc, m), money('0', currency))
export const negate = (m: Money): Money => ({ amount: fromMinor(-toMinor(m.amount)), currency: m.currency })
export const isNegative = (m: Money): boolean => toMinor(m.amount) < 0n
export const isZero = (m: Money): boolean => toMinor(m.amount) === 0n
export const equalMoney = (a: Money, b: Money): boolean =>
  a.currency === b.currency && toMinor(a.amount) === toMinor(b.amount)
