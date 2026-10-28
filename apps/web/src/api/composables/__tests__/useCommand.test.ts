import { describe, expect, it } from 'vitest'
import { useCommand } from '../useCommand'

describe('useCommand idempotency key', () => {
  it('reuses the key on retry of the same input, renews after success or a changed input', async () => {
    const keys: (string | undefined)[] = []
    let fail = true
    const c = useCommand(async (_: { v: number }, o) => { keys.push(o.idempotencyKey); if (fail) throw new Error('net'); return 1 })
    await expect(c.run({ v: 1 })).rejects.toThrow()
    await expect(c.run({ v: 1 })).rejects.toThrow()
    await expect(c.run({ v: 2 })).rejects.toThrow() // different body: new key
    fail = false
    await c.run({ v: 2 }); await c.run({ v: 2 }) // success, then a new action
    expect(keys[0]).toBe(keys[1]); expect(keys[2]).not.toBe(keys[1]); expect(keys[3]).toBe(keys[2]); expect(keys[4]).not.toBe(keys[3])
  })
})
