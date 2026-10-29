/**
 * View models for the Lite screens. Mock of future "lite" read endpoints: all grouping, money maths and
 * wording of derived values happens here (the API layer), never in the Vue components.
 */
import * as fx from './fixtures'
import {
  assignException, getException, getRun, getRunSignOff, getSafeguardingPosition, listExceptions, listSourceDeliveries,
  proposeResolution, requestEvidence,
} from './service'
import { addMoney, equalMoney, formatDecimal, formatMoney, isNegative, negate, sumMoney, toMinor } from './money'
import type { ExceptionDetail, ExceptionSummary, Money, ResolutionKind, SourceDelivery } from './types'

export const CURRENT_USER = 'Charlotte Hughes'
/** "Priya Shah" -> "P. Shah" */
export const shortName = (n: string): string => n.replace(/^(\S)\S*\s+/, '$1. ')
export const CURRENT_USER_ROLE = 'Control owner'
/** Mock "now" used to judge overdue items; the real API supplies server time. */
const NOW = Date.parse('2026-10-28T09:00:00+00:00')

export type Tone = 'pass' | 'warn' | 'fail' | 'muted'

const hhmm = (iso: string) => new Date(iso).toISOString().slice(11, 16)
const dayMonth = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
const sameDay = (iso: string) => new Date(iso).toISOString().slice(0, 10) === new Date(NOW).toISOString().slice(0, 10)

export interface LiteDue { short: string; long: string; tone: Tone }
function due(iso: string, blocking: boolean, owner: string | null): LiteDue {
  if (Date.parse(iso) < NOW) return { short: `Overdue ${hhmm(iso)}`, long: `Overdue since ${hhmm(iso)}`, tone: 'fail' }
  const text = sameDay(iso) ? `Due ${hhmm(iso)}` : dayMonth(iso)
  return { short: text, long: text, tone: blocking && !owner ? 'warn' : 'muted' }
}

// ---- plain-language wording (mock copy, keyed by finding id) ----
const COPY: Record<string, { title: string; ref?: string }> = {
  'F-001': { title: 'A transfer has no client account' },
  'F-002': { title: 'A transfer instruction appears twice', ref: 'TRF-004' },
  'F-003': { title: 'A direct debit has no mandate reference' },
  'F-004': { title: 'A direct debit has no mandate reference' },
  'F-005': { title: 'A direct debit has an invalid currency' },
  'F-006': { title: 'A return points to an unknown direct debit', ref: 'DD-999' },
  'F-007': { title: 'A transfer is posted the wrong way round', ref: 'TRF-004' },
  'F-008': { title: 'A rejected transfer was still debited', ref: 'TRF-005' },
  'F-009': { title: 'A client account does not match the ledger', ref: 'CA-004' },
  'F-010': { title: 'A client account does not match the ledger', ref: 'CA-005' },
  'F-011': { title: 'Bank paid 205.00 GBP, instruction said 210.00 GBP', ref: 'DD-003' },
  'F-012': { title: 'Bank paid 610.00 GBP, instruction said 600.00 GBP', ref: 'TRF-008' },
  'F-013': { title: 'A returned direct debit was never reversed in the ledger', ref: 'DD-002' },
  'F-014': { title: 'The bank file is incomplete', ref: 'Sources' },
  'F-015': { title: 'A safeguarding balance is negative', ref: 'SG-GBP-004' },
  'F-016': { title: 'A safeguarding balance is out of date', ref: 'SG-EUR-003' },
  'F-017': { title: 'A safeguarding balance has no evidence', ref: 'SG-GBP-005' },
  'F-018': { title: 'A safeguarding balance appears twice', ref: 'SG-GBP-001' },
  'F-019': { title: 'Client money is short', ref: 'Safeguarding' },
  'F-020': { title: 'A card settlement total does not add up' },
  'F-021': { title: 'A card settlement has no destination account' },
  'F-022': { title: 'A card settlement has an invalid currency' },
  'F-023': { title: 'A card settlement appears twice', ref: 'CARD-009' },
  'F-024': { title: 'Card payment presented for 65.00 GBP, authorised for 60.00 GBP', ref: 'CTX-002' },
  'F-025': { title: 'A card payment has no authorisation', ref: 'CTX-004' },
  'F-026': { title: 'A card refund points to an unknown payment', ref: 'CTX-999' },
  'F-027': { title: 'A card payment has an invalid currency' },
  'F-028': { title: 'A card event appears twice', ref: 'CEV-009' },
  'F-029': { title: 'A card payment points to an unknown settlement', ref: 'CARD-999' },
}
/** Findings shown as one row. The first id leads. */
const GROUPS: string[][] = [['F-009', 'F-010']]
const GROUP_TITLE: Record<string, { title: string; ref: string }> = {
  'F-009': { title: '2 client accounts do not match the ledger', ref: 'Ledger' },
}
const isClosed = (s: string) => ['RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE'].includes(s)

export interface LiteItem {
  key: string
  ids: string[]
  title: string
  ref: string
  amount: Money | null
  owner: string | null
  ownerLabel: string | null
  due: LiteDue
  blocking: boolean
  why: string
}

async function openExceptions(): Promise<ExceptionSummary[]> {
  const page = await listExceptions({ pageSize: 500 })
  return page.items.filter((e) => !isClosed(e.status))
}

function groupItems(list: ExceptionSummary[]): { lead: ExceptionSummary; members: ExceptionSummary[] }[] {
  const out: { lead: ExceptionSummary; members: ExceptionSummary[] }[] = []
  const seen = new Set<string>()
  for (const e of list) {
    if (seen.has(e.id)) continue
    const g = GROUPS.find((ids) => ids.includes(e.id))
    const members = g ? list.filter((x) => g.includes(x.id)) : [e]
    members.forEach((m) => seen.add(m.id))
    out.push({ lead: members[0]!, members })
  }
  return out
}

function toItem(lead: ExceptionSummary, members: ExceptionSummary[]): LiteItem {
  const ids = members.map((m) => m.id)
  const grouped = members.length > 1
  const copy = COPY[lead.id]
  const amounts = members.map((m) => m.value).filter((v): v is Money => !!v)
  const cur = amounts[0]?.currency
  const amount = lead.id === 'F-019' ? lead.value : amounts.length && cur ? sumMoney(amounts, cur) : null
  const g = GROUP_TITLE[lead.id]
  return {
    key: ids.join('-'), ids,
    title: grouped && g ? g.title : copy?.title ?? lead.title,
    ref: [...ids, grouped && g ? g.ref : copy?.ref ?? lead.sourceRowId].join(' · '),
    amount: lead.id === 'F-014' ? null : amount,
    owner: lead.owner, ownerLabel: lead.owner ? shortName(lead.owner) : null, due: due(lead.dueAt, lead.blocking, lead.owner), blocking: lead.blocking, why: '',
  }
}

export interface LiteContext { text: string }
async function context(): Promise<LiteContext> {
  const run = await getRun(fx.RUN_ID)
  const date = new Date(`${run.businessDate}T00:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })
  return { text: `${fx.legalEntity.id} · ${fx.legalEntity.name} · ${date}` }
}
export const getLiteContext = context

// ---- Today ----
export interface LiteCheck { label: string; tone: Tone; value: string }
export interface LiteToday {
  ready: boolean
  title: string
  summary: string
  problems: LiteItem[]
  checks: LiteCheck[]
}
const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

export async function getLiteToday(): Promise<LiteToday> {
  const [open, signOff, sg, deliveries] = await Promise.all([openExceptions(), getRunSignOff(), getSafeguardingPosition(), listSourceDeliveries()])
  const blocking = open.filter((e) => e.blocking)
  const problems = groupItems(blocking).map(({ lead, members }) => toItem(lead, members))
  const bankDelivery = deliveries.find((d) => d.sourceName === 'banking_transactions')
  for (const p of problems) {
    if (p.ids.includes('F-019')) {
      const gbp = sg.positions.find((x) => x.currency === 'GBP')
      if (gbp) p.why = `Bank balances cover ${formatMoney(gbp.eligibleBalance)} of the ${formatMoney(gbp.obligation)} owed.`
    } else if (p.ids.includes('F-014') && bankDelivery) {
      p.why = `It says ${bankDelivery.declaredRecordCount} records but contains ${bankDelivery.actualRecordCount}, so bank matching cannot be trusted yet.`
    } else if (p.ids.includes('F-009')) {
      const [first, ...rest] = pick(blocking, p.ids)
      p.why = `${first!.sourceRowId} is out by ${formatMoney(first!.value!)}${rest.map((m) => ` and ${m.sourceRowId} by ${formatMoney(m.value!)}`).join('')}.`
    }
  }
  const received = deliveries.filter((d) => d.receivedAt && d.state !== 'MISSING').length
  const matching = open.filter((e) => e.control === 'matching').length
  const lifecycle = open.filter((e) => e.control === 'transaction_lifecycle').length
  const waiting = open.filter((e) => e.status === 'PENDING_APPROVAL').length
  const ready = signOff.canSignOff && problems.length === 0
  return {
    ready,
    title: ready ? 'Ready to sign off' : 'Not ready to sign off',
    summary: ready ? 'Everything is on track.' : `${plural(problems.length, 'problem')} stand${problems.length === 1 ? 's' : ''} in the way. Everything else is on track.`,
    problems,
    checks: [
      { label: 'Files received', tone: received === deliveries.length ? 'pass' : 'warn', value: `${received} of ${deliveries.length}` },
      { label: 'Matching', tone: matching ? 'warn' : 'pass', value: matching ? `${matching} small ${matching === 1 ? 'difference' : 'differences'}` : 'all matched' },
      { label: 'Transactions', tone: lifecycle ? 'warn' : 'pass', value: lifecycle ? `${lifecycle} need a look` : 'all clear' },
      { label: 'Approvals', tone: waiting ? 'warn' : 'pass', value: waiting ? `${waiting} waiting` : 'none waiting' },
    ],
  }
}
const pick = (list: ExceptionSummary[], ids: string[]) => list.filter((e) => ids.includes(e.id))

// ---- Exceptions list ----
export interface LiteExceptions {
  open: number
  mine: number
  noOwner: number
  blockingCount: number
  blocking: LiteItem[]
  review: LiteItem[]
  user: string
}
export async function getLiteExceptions(): Promise<LiteExceptions> {
  const open = await openExceptions()
  const items = groupItems(open).map(({ lead, members: m }) => toItem(lead, m))
  const blocking = items.filter((i) => i.blocking)
  return {
    open: open.length, mine: open.filter((e) => e.owner === CURRENT_USER).length, noOwner: open.filter((e) => !e.owner).length,
    blockingCount: blocking.length, blocking, review: items.filter((i) => !i.blocking), user: CURRENT_USER,
  }
}

// ---- Exception detail ----
export interface LiteHappened {
  numbers?: { value: string; caption: string; tone: Tone | 'ink'; op?: string }[]
  text?: string
}
export interface LiteLine { account: string; reason: string; amount: string; tone: Tone | 'ink' }
export interface LiteExceptionView {
  id: string
  title: string
  tone: Tone
  statusText: string
  blocks: boolean
  owner: string | null
  happened: LiteHappened
  notCounted: LiteLine[]
  outlook: string | null
  note: string
  evidenceLabel: string
  calculation: { label: string; value: string }[]
  history: { at: string; actor: string; text: string }[]
  sources: { label: string; ref: string; amount: string | null }[]
}
const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1).toLowerCase()
const NOT_COUNTED_REASON: Record<string, string> = {
  'F-015': 'Negative balance, needs a policy decision', 'F-017': 'No evidence reference supplied',
  'F-018': 'Duplicate of an earlier snapshot', 'F-016': 'Balance is out of date',
}

export async function getLiteException(id: string): Promise<LiteExceptionView> {
  const ex: ExceptionDetail = await getException(id)
  const copy = COPY[id]
  let title = copy?.title ?? ex.title
  let happened: LiteHappened = { text: `${ex.description.charAt(0).toUpperCase()}${ex.description.slice(1)}. ${ex.expectedEffect}.` }
  let notCounted: LiteLine[] = []
  let outlook: string | null = null
  if (id === 'F-019') {
    const sg = await getSafeguardingPosition()
    const p = sg.positions.find((x) => x.currency === 'GBP')!
    title = `Client money is short by ${formatMoney(negate(p.result))}`
    happened = { numbers: [
      { value: formatDecimal(p.eligibleBalance.amount), caption: 'covered by bank balances', tone: 'ink' },
      { value: formatDecimal(p.obligation.amount), caption: 'owed to clients', tone: 'ink', op: '−' },
      { value: formatDecimal(p.result.amount), caption: `short, in ${p.currency}`, tone: 'fail', op: '=' },
    ] }
    notCounted = sg.eligibleBalances.filter((b) => !b.included && b.balance.currency === 'GBP').map((b) => {
      const fid = b.findingIds.find((f) => NOT_COUNTED_REASON[f]) ?? ''
      return {
        account: b.findingIds.includes('F-018') ? `${b.externalAccountId} (copy)` : b.externalAccountId,
        reason: NOT_COUNTED_REASON[fid] ?? b.reason, amount: formatMoney(b.balance), tone: isNegative(b.balance) ? 'fail' : 'ink',
      } satisfies LiteLine
    })
    const adj = sg.adjustments.find((a) => a.status === 'PROPOSED' && a.currency === 'GBP')
    const pending = adj && sg.eligibleBalances.find((b) => equalMoney(b.balance, adj.amount) && !b.included)
    if (adj && pending) {
      const cover = addMoney(p.eligibleBalance, adj.amount)
      const after = addMoney(cover, negate(p.obligation))
      outlook = `If the bank confirms ${pending.externalAccountId} (${formatMoney(adj.amount)}), coverage becomes ${formatMoney(cover)} and the position turns into a ${isNegative(after) ? 'shortfall' : 'surplus'} of ${formatMoney(isNegative(after) ? negate(after) : after)}.`
    }
  }
  const owner = ex.owner
  const d = due(ex.dueAt, ex.blocking, owner)
  const tone: Tone = ex.severity === 'CRITICAL' ? 'fail' : ex.severity === 'HIGH' ? 'warn' : 'muted'
  const statusText = [cap(ex.severity), ex.blocking ? 'blocks sign-off' : null, owner ? shortName(owner) : 'no owner', d.short.replace(/^Due/, 'due').replace(/^Overdue/, 'overdue')].filter(Boolean).join(' · ')
  return {
    id, title, tone, statusText, blocks: ex.blocking, owner, happened, notCounted, outlook,
    note: ex.severity === 'CRITICAL' ? "A critical item needs the control owner's approval. Whoever proposes cannot approve." : `${ex.approvalPolicy}.`,
    evidenceLabel: ex.control === 'safeguarding' || ex.sourceFile.startsWith('safeguarding') || ex.sourceFile.startsWith('banking') ? 'Request bank evidence' : 'Request evidence',
    calculation: [
      { label: 'Rule', value: `${ex.trace.ruleId} (${ex.trace.ruleVersion})` }, ...ex.trace.inputs.map((i) => ({ label: i.name, value: i.value })),
      { label: 'Tolerance', value: ex.trace.tolerance }, { label: 'Calculation', value: ex.trace.calculation }, { label: 'Result', value: ex.trace.result },
    ],
    history: ex.timeline.map((t) => ({ at: t.at, actor: t.actor, text: t.text })),
    sources: ex.evidenceNodes.map((n) => ({ label: n.label, ref: `${n.source} · ${n.sourceRowId}`, amount: n.amount ? formatMoney(n.amount) : null })),
  }
}
export const assignLiteException = (id: string, owner: string) => assignException(id, owner)
export const requestLiteEvidence = (id: string, text: string) => requestEvidence(id, text)
export const proposeLiteResolution = (id: string, kind: ResolutionKind, reason: string) => proposeResolution(id, { kind, reason, evidenceRefs: [] })
export const listAssignableUsers = (): string[] => fx.users.filter((u) => u.roles.includes('CONTROL_ANALYST') || u.roles.includes('CONTROL_OWNER')).map((u) => u.name)

// ---- Safeguarding ----
export interface LiteCurrency {
  currency: string
  tone: Tone
  statusText: string
  position: string
  detail: string
  short: boolean
  owed: string
  covered: string
  owedPct: number
  coveredPct: number
  notCounted: number
}
export interface LiteSafeguarding { summary: string; currencies: LiteCurrency[] }
/** Visual proportion only (0-100); never used as a financial value. */
const pct = (part: Money, whole: Money): number => {
  const w = toMinor(whole.amount)
  return w <= 0n ? 0 : Math.min(100, Number((toMinor(part.amount) * 10000n) / w) / 100)
}
export async function getLiteSafeguarding(): Promise<LiteSafeguarding> {
  const sg = await getSafeguardingPosition()
  const currencies = sg.positions.map((p): LiteCurrency => {
    const short = p.state === 'SHORTFALL'
    const base = p.obligation.amount === '0.00' ? p.eligibleBalance : p.obligation
    return {
      currency: p.currency, tone: short ? 'fail' : 'pass', short,
      statusText: short ? 'Short by' : p.state === 'SURPLUS' || p.state === 'NO_OBLIGATION' ? 'Covered, spare' : 'Covered',
      position: formatDecimal(p.result.amount, { sign: !short }),
      detail: `owed ${formatDecimal(p.obligation.amount)} · covered ${formatDecimal(p.eligibleBalance.amount)}`,
      owed: formatMoney(p.obligation), covered: formatMoney(p.eligibleBalance),
      owedPct: base === p.obligation ? 100 : 0, coveredPct: pct(p.eligibleBalance, base),
      notCounted: sg.eligibleBalances.filter((b) => !b.included && b.balance.currency === p.currency).length,
    }
  })
  const shorts = currencies.filter((c) => c.short).map((c) => c.currency)
  const ok = currencies.filter((c) => !c.short).map((c) => c.currency)
  const join = (l: string[]) => (l.length > 1 ? `${l.slice(0, -1).join(', ')} and ${l[l.length - 1]}` : l[0] ?? '')
  const summary = [shorts.length ? `${join(shorts)} ${shorts.length > 1 ? 'are' : 'is'} short.` : null, ok.length ? `${join(ok)} ${ok.length > 1 ? 'are' : 'is'} covered.` : null, 'Currencies are never netted against each other.'].filter(Boolean).join(' ')
  return { summary, currencies }
}

// ---- Sources ----
const SOURCE_COPY: Record<string, { name: string; from?: string; provider?: string }> = {
  transfer_instructions: { name: 'Transfer instructions', from: 'Payments Ops' },
  direct_debit_events: { name: 'Direct debits', from: 'Payments Ops' },
  internal_ledger_entries: { name: 'Ledger entries', from: 'Finance Systems' },
  client_account_balances: { name: 'Client balances', from: 'Finance Systems' },
  banking_transactions: { name: 'Bank transactions', from: 'Treasury Ops' },
  safeguarding_balances: { name: 'Safeguarding balances', from: 'Treasury Ops' },
  card_transactions: { name: 'Card transactions', provider: 'FIS' },
  card_settlements: { name: 'Card settlements', provider: 'FIS' },
}
export interface LiteFile { key: string; name: string; from?: string; provider?: string; arrived: string; attention: boolean; state: string; note: string | null }
export interface LiteSources { summary: string; files: LiteFile[]; attentionExceptionId: string | null }
export async function getLiteSources(): Promise<LiteSources> {
  const deliveries: SourceDelivery[] = await listSourceDeliveries()
  const files = deliveries.map((d): LiteFile => {
    const countMismatch = d.declaredRecordCount !== d.actualRecordCount
    const attention = countMismatch || d.state === 'LATE' || d.state === 'MISSING' || d.state === 'REJECTED'
    const c = SOURCE_COPY[d.sourceName] ?? { name: d.sourceName }
    const isSg = d.sourceName === 'safeguarding_balances'
    return {
      key: d.deliveryId, name: c.name, from: c.from, provider: c.provider, attention,
      arrived: d.receivedAt ? hhmm(d.receivedAt) : '—',
      state: attention ? 'Needs attention' : 'OK',
      note: countMismatch ? `says ${d.declaredRecordCount} records, has ${d.actualRecordCount}` : d.rejectedRecords ? (isSg ? `${d.rejectedRecords} balances not counted` : `${plural(d.rejectedRecords, 'record')} rejected`) : null,
    }
  })
  const arrived = deliveries.filter((d) => d.receivedAt).length
  const bad = files.filter((f) => f.attention).length
  return {
    summary: `${arrived} of ${deliveries.length} files arrived. ${bad ? `${bad} needs attention.` : 'Nothing needs attention.'}`,
    files, attentionExceptionId: deliveries.some((d) => d.findingIds.includes('F-014')) && files.some((f) => f.attention) ? 'F-014' : null,
  }
}
export const getLiteOpenCount = async (): Promise<number> => (await openExceptions()).length
