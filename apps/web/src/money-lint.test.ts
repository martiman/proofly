import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * Lint-style guard (CLAUDE.md): money is a decimal string; no float conversion or arithmetic in the browser.
 * ALLOW lists `file:line-content-substring` pairs that are NOT money (document the reason next to each entry).
 */
const ALLOW: { file: string; contains: string; reason: string }[] = [
  { file: 'views/MatchingWorkbench/index.vue', contains: 'autoMatchRate', reason: 'server-supplied ratio shown as a percentage, not money' },
  { file: 'views/SafeguardingPosition/index.vue', contains: 'r.amount?.amount', reason: 'string concatenation building a table row key, not arithmetic' },
]
const ROOT = join(__dirname)
const PATTERNS = [/\bNumber\(/, /\bparseFloat\(/, /\bparseInt\(/, /\+\s*\w*\.?amount\b/, /\bamount\s*\+/, /\.amount\s*\+/, /\bwith\.amount\b.*[-*/]/]

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n)
    if (statSync(p).isDirectory()) return n === 'generated' || n === '__tests__' ? [] : files(p)
    return /\.(vue|ts)$/.test(n) && !/\.test\.ts$/.test(n) && !n.endsWith('.d.ts') ? [p] : []
  })
}

describe('money handling lint', () => {
  it('has no float conversion or addition of money in src', () => {
    const hits: string[] = []
    for (const f of files(ROOT)) {
      const rel = relative(ROOT, f)
      readFileSync(f, 'utf8').split('\n').forEach((line, i) => {
        if (!PATTERNS.some((re) => re.test(line))) return
        if (ALLOW.some((a) => a.file === rel && line.includes(a.contains))) return
        hits.push(`${rel}:${i + 1}: ${line.trim()}`)
      })
    }
    expect(hits).toEqual([])
  })
})
