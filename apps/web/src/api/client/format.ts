/**
 * DISPLAY ONLY. Money is a decimal string plus currency end to end; the browser never does financial arithmetic
 * (CLAUDE.md). There are deliberately no add/sum/compare helpers here.
 */
const DECIMAL = /^-?\d+(\.\d{1,6})?$/

/** Format a contract decimal string with its currency. The string goes to Intl as is (exact decimal, no float conversion). */
export function formatMoney(amount: string, currency: string, locale?: string | string[]): string {
  if (!DECIMAL.test(amount)) throw new RangeError(`Invalid decimal amount: ${JSON.stringify(amount)}`)
  if (!/^[A-Z]{3}$/.test(currency)) throw new RangeError(`Invalid currency: ${JSON.stringify(currency)}`)
  const base = new Intl.NumberFormat(locale, { style: 'currency', currency })
  const own = base.resolvedOptions().minimumFractionDigits ?? 2
  // Only insignificant trailing zeros are dropped (down to the currency's minor units); no precision is ever rounded away.
  const given = amount.includes('.') ? amount.split('.')[1]!.replace(/0+$/, '').length : 0
  const digits = Math.max(own, given)
  // Intl.NumberFormat v3 accepts a decimal string and formats it exactly; the TS lib types only number, hence the cast.
  return new Intl.NumberFormat(locale, { style: 'currency', currency, minimumFractionDigits: digits, maximumFractionDigits: digits })
    .format(amount as unknown as number)
}
