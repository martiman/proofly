import { describe, expect, it } from 'vitest'
import { switcherCurrencies } from './logic'

describe('account ledger currency switcher', () => {
  it('uses availableCurrencies from the ledger, the entity currencies only without a ledger', () => {
    expect(switcherCurrencies(['EUR', 'GBP'], ['USD'])).toEqual(['EUR', 'GBP'])
    expect(switcherCurrencies(undefined, ['USD'])).toEqual(['USD'])
  })
})
