import { describe, expect, it } from 'vitest'
import { environmentView } from './environment'

describe('environment label', () => {
  it('uses name and kind from the server and flags PRODUCTION', () => {
    expect(environmentView({ name: 'Acme prod', kind: 'PRODUCTION' })).toEqual({ name: 'Acme prod', kind: 'PRODUCTION', production: true })
    expect(environmentView({ name: 'local', kind: 'DEV' }).production).toBe(false)
    expect(environmentView(undefined)).toEqual({ name: '—', kind: null, production: false })
  })
})
