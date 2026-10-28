export type Row = Record<string, string>

export function parseCsv(text: string): Row[] {
  const lines = text.split(/\r?\n/).filter((l) => l.trim() !== '')
  const split = (l: string): string[] => {
    const out: string[] = []
    let cur = ''
    let q = false
    for (const ch of l) {
      if (ch === '"') q = !q
      else if (ch === ',' && !q) {
        out.push(cur)
        cur = ''
      } else cur += ch
    }
    out.push(cur)
    return out
  }
  const header = split(lines[0] ?? '')
  return lines.slice(1).map((l) => {
    const c = split(l)
    return Object.fromEntries(header.map((h, i) => [h, c[i] ?? '']))
  })
}
