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

describe('useCommand submit lock', () => {
  it('three rapid calls produce one request', async () => {
    let n = 0
    const c = useCommand(async (_: { v: number }) => { n++; await new Promise((r) => setTimeout(r, 10)); return n })
    const rs = await Promise.all([c.run({ v: 1 }), c.run({ v: 1 }), c.run({ v: 1 })])
    expect(n).toBe(1); expect(rs).toEqual([1, 1, 1]); expect(c.pending.value).toBe(false)
    await c.run({ v: 1 }); expect(n).toBe(2) // lock released afterwards
  })
})
