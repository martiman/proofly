/** Currency switcher options: the ledger's `availableCurrencies`; before a ledger loaded (422 asks for a currency) the legal entity's currencies. */
export const switcherCurrencies = (available: readonly string[] | undefined, fallback: readonly string[]): readonly string[] => available ?? fallback
