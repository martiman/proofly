/**
 * Fixtures derived at load time from the synthetic package (copied verbatim to ./data from
 * examples/synthetic-data/2026-10-27). Source values are never corrected in place.
 */
import transfersCsv from './data/transfer_instructions.csv?raw'
import ddCsv from './data/direct_debit_events.csv?raw'
import ledgerCsv from './data/internal_ledger_entries.csv?raw'
import balancesCsv from './data/client_account_balances.csv?raw'
import bankCsv from './data/banking_transactions.csv?raw'
import sgCsv from './data/safeguarding_balances.csv?raw'
import cardTxCsv from './data/card_transactions.csv?raw'
import cardSetCsv from './data/card_settlements.csv?raw'
import manifestCsv from './data/delivery_manifest.csv?raw'
import findingsCsv from './data/expected_findings.csv?raw'
import { parseCsv, type Row } from './csv'
import { addMoney, equalMoney, isCurrency, money, negate, sumMoney, subMoney } from './money'
import type {
  Account, AuditEvent, ControlArea, Currency, EligibleBalanceLine, EvidenceEdge, EvidenceNode,
  ExceptionDetail, ExceptionStatus, LegalEntity, LedgerEntry, MatchGroup, MatchStatus, Money,
  ObligationLine, Run, SafeguardingAdjustment, SafeguardingCurrencyPosition, Severity,
  SourceDelivery, SourceRecord, TimelineEvent, Transaction, UserAccess, RuleConfig, SourceConfig,
} from './types'

export const LE_ID = 'LE-CZ-001'
export const BUSINESS_DATE = '2026-10-27'
export const RUN_ID = 'RUN-LE-CZ-001-20261027-v1'
export const PREVIOUS_RUN_ID = 'RUN-LE-CZ-001-20261026-v1'
export const RULESET = 'oMVP-ruleset-0.1'

export const legalEntity: LegalEntity = { id: LE_ID, name: 'Proofly Demo Payments s.r.o.', currencies: ['EUR', 'CZK', 'USD'] }

const t = {
  transfers: parseCsv(transfersCsv), dd: parseCsv(ddCsv), ledger: parseCsv(ledgerCsv),
  balances: parseCsv(balancesCsv), bank: parseCsv(bankCsv), sg: parseCsv(sgCsv),
  cardTx: parseCsv(cardTxCsv), cardSet: parseCsv(cardSetCsv), manifest: parseCsv(manifestCsv),
  findings: parseCsv(findingsCsv),
}

const tables: Record<string, { rows: Row[]; key: string }> = {
  transfer_instructions: { rows: t.transfers, key: 'source_row_id' },
  direct_debit_events: { rows: t.dd, key: 'source_row_id' },
  internal_ledger_entries: { rows: t.ledger, key: 'ledger_entry_id' },
  client_account_balances: { rows: t.balances, key: 'account_id' },
  banking_transactions: { rows: t.bank, key: 'external_transaction_id' },
  safeguarding_balances: { rows: t.sg, key: 'source_row_id' },
  card_transactions: { rows: t.cardTx, key: 'source_row_id' },
  card_settlements: { rows: t.cardSet, key: 'source_row_id' },
}
export const sourceNames = Object.keys(tables)

const cur = (s: string | undefined): Currency | null => (s && isCurrency(s) ? s : null)
const mny = (r: Row, f = 'amount', c = 'currency'): Money | null => {
  const c2 = cur(r[c])
  return c2 && r[f] !== undefined && r[f] !== '' ? money(r[f], c2) : null
}

// ---------- Findings -> exceptions ----------
const REJECT_RE = /^(Reject|Exclude|Quarantine)/
const stem = (f: string) => f.replace(/\.csv$/, '')
export interface Finding {
  id: string; sourceFile: string; sourceName: string; sourceRowId: string; control: ControlArea
  reasonCode: string; severity: Severity; description: string; expectedEffect: string
}
export const findings: Finding[] = t.findings.map((r) => ({
  id: r.finding_id!, sourceFile: r.source_file!, sourceName: stem(r.source_file!), sourceRowId: r.source_row_id!,
  control: r.control as ControlArea, reasonCode: r.reason_code!, severity: r.severity as Severity,
  description: r.description!, expectedEffect: r.expected_effect!,
}))

function findingValue(f: Finding): Money | null {
  switch (f.id) {
    case 'F-019': return null // set from the safeguarding position below
    case 'F-009': return money('1999.98', 'EUR') // recorded 2999.99 vs independent 1000.01
    case 'F-010': return money('250.00', 'EUR')
    case 'F-011': return money('5.00', 'EUR')
    case 'F-012': return money('10.00', 'EUR')
    case 'F-013': return money('85.00', 'EUR')
    case 'F-014': return money('0.00', 'EUR')
    case 'F-020': return money('6.00', 'EUR')
    case 'F-024': return money('5.00', 'EUR')
    case 'F-015': return money('-50.00', 'EUR')
  }
  const tb = tables[f.sourceName]
  const row = tb?.rows.find((r) => r[tb.key] === f.sourceRowId)
  if (!row) return null
  return mny(row, 'amount') ?? mny(row, 'balance_amount') ?? mny(row, 'gross_amount')
}

const STATUS_CYCLE: ExceptionStatus[] = ['OPEN', 'OPEN', 'IN_INVESTIGATION', 'WAITING_FOR_DATA', 'OPEN', 'PENDING_APPROVAL', 'IN_INVESTIGATION']
const OWNERS = ['Jana Novakova', 'Petr Svoboda', 'Eva Dvorakova', null]
const exceptionTitle = (f: Finding) => `${f.reasonCode.replace(/_/g, ' ').toLowerCase()} - ${f.sourceName.replace(/_/g, ' ')} ${f.sourceRowId}`

export interface ExceptionState {
  base: Omit<ExceptionDetail, 'timeline' | 'evidenceNodes' | 'evidenceEdges' | 'relatedTransactionIds' | 'relatedAccountIds'>
}

// ---------- Transactions ----------
interface TxnInternal extends Transaction { rowIds: string[] }
const lc = (...parts: (string | undefined)[]) => parts.filter((p): p is string => !!p)
const txns: TxnInternal[] = []

const dupSeen = new Set<string>()
for (const r of t.transfers) {
  const dup = dupSeen.has(r.instruction_id!)
  dupSeen.add(r.instruction_id!)
  const m = mny(r)
  txns.push({
    id: dup ? `${r.instruction_id}#2` : r.instruction_id!, kind: 'BANK_TRANSFER', source: 'transfer_instructions',
    sourceRowId: r.source_row_id!, rowIds: [r.source_row_id!], reference: r.external_reference!,
    accountId: r.client_account_id || null, timestamp: r.instruction_timestamp!,
    amount: m ?? money('0', 'EUR'), direction: 'DEBIT', sourceStatus: r.source_status!,
    lifecycle: lc(r.source_status), matchStatus: 'NOT_APPLICABLE', matchGroupId: null, exceptionIds: [],
    accepted: !!r.client_account_id && !dup,
  })
}
const ddById = new Map<string, Row[]>()
for (const r of t.dd) ddById.set(r.instruction_id!, [...(ddById.get(r.instruction_id!) ?? []), r])
for (const [id, rows] of ddById) {
  const last = rows[rows.length - 1]!
  const first = rows[0]!
  txns.push({
    id, kind: 'DIRECT_DEBIT', source: 'direct_debit_events', sourceRowId: first.source_row_id!,
    rowIds: rows.map((r) => r.source_row_id!), reference: id, accountId: first.client_account_id || null,
    timestamp: first.event_timestamp!, amount: mny(first) ?? money(first.amount!, 'EUR'), direction: 'CREDIT',
    sourceStatus: last.source_status!, lifecycle: rows.map((r) => r.event_type!),
    matchStatus: 'NOT_APPLICABLE', matchGroupId: null, exceptionIds: [],
    accepted: rows.every((r) => !!r.mandate_reference && isCurrency(r.currency!)),
  })
}
for (const r of t.bank) {
  txns.push({
    id: r.external_transaction_id!, kind: 'BANK_EVIDENCE', source: 'banking_transactions',
    sourceRowId: r.external_transaction_id!, rowIds: [r.external_transaction_id!], reference: r.bank_reference!,
    accountId: r.external_account_id!, timestamp: r.booking_timestamp!, amount: mny(r)!,
    direction: r.debit_credit_indicator as 'DEBIT' | 'CREDIT', sourceStatus: r.transaction_status!,
    lifecycle: [r.transaction_status!], matchStatus: 'NOT_APPLICABLE', matchGroupId: null, exceptionIds: [], accepted: true,
  })
}
for (const r of t.ledger) {
  txns.push({
    id: r.ledger_entry_id!, kind: 'LEDGER_ENTRY', source: 'internal_ledger_entries', sourceRowId: r.ledger_entry_id!,
    rowIds: [r.ledger_entry_id!], reference: r.transaction_reference!, accountId: r.account_id!,
    timestamp: r.booking_timestamp!, amount: mny(r)!, direction: r.debit_credit_indicator as 'DEBIT' | 'CREDIT',
    sourceStatus: r.source_status!, lifecycle: [r.entry_type!, r.source_status!],
    matchStatus: 'NOT_APPLICABLE', matchGroupId: null, exceptionIds: [], accepted: true,
  })
}
const cardById = new Map<string, Row[]>()
for (const r of t.cardTx) cardById.set(r.card_transaction_id!, [...(cardById.get(r.card_transaction_id!) ?? []), r])
for (const [id, rows] of cardById) {
  const pick = rows.find((r) => r.event_type === 'PRESENTMENT') ?? rows[0]!
  const auth = rows.find((r) => r.event_type === 'AUTHORIZATION')
  let ms: MatchStatus = 'NOT_APPLICABLE'
  if (pick.event_type === 'PRESENTMENT') ms = !auth ? 'UNMATCHED' : auth.amount === pick.amount ? 'MATCHED' : 'VALUE_MISMATCH'
  txns.push({
    id, kind: 'CARD', source: 'card_transactions', sourceRowId: pick.source_row_id!,
    rowIds: rows.map((r) => r.source_row_id!), reference: pick.merchant_reference!, accountId: pick.client_account_id!,
    timestamp: pick.event_timestamp!, amount: mny(pick) ?? money(pick.amount!, 'EUR'),
    direction: pick.debit_credit_indicator as 'DEBIT' | 'CREDIT', sourceStatus: pick.source_status!,
    lifecycle: rows.map((r) => r.event_type!), matchStatus: ms, matchGroupId: ms === 'NOT_APPLICABLE' ? null : `MG-${id}`,
    exceptionIds: [], accepted: rows.every((r) => isCurrency(r.currency!)),
  })
}

// Matching: instruction (+ ledger) <-> bank evidence
const matchGroups: MatchGroup[] = []
const cand = (x: Transaction) => ({ transactionId: x.id, label: `${x.kind} ${x.id}`, amount: x.amount, timestamp: x.timestamp, reference: x.reference })
const bankByRef = new Map(txns.filter((x) => x.kind === 'BANK_EVIDENCE').map((x) => [x.reference, x]))
const ledgerByRef = new Map(txns.filter((x) => x.kind === 'LEDGER_ENTRY').map((x) => [x.reference, x]))
for (const x of txns.filter((y) => (y.kind === 'BANK_TRANSFER' && !y.id.includes('#')) || y.kind === 'DIRECT_DEBIT')) {
  const bank = x.kind === 'BANK_TRANSFER' ? bankByRef.get(x.reference) : bankByRef.get(x.id)
  const led = ledgerByRef.get(x.id)
  const gid = `MG-${x.id}`
  let status: MatchStatus
  if (['REJECTED', 'CANCELLED'].includes(x.sourceStatus)) status = 'NOT_APPLICABLE'
  else if (!bank) status = 'UNMATCHED'
  else status = equalMoney(x.amount, bank.amount) ? 'MATCHED' : 'VALUE_MISMATCH'
  const group: MatchGroup = {
    id: gid, status, left: [cand(x), ...(led ? [cand(led)] : [])], right: bank ? [cand(bank)] : [],
    difference: bank && x.amount.currency === bank.amount.currency ? subMoney(bank.amount, x.amount) : null,
    ruleVersion: 'MATCH-1.0', tolerance: 'Exact amount and currency; reference key; same value date',
    rationale: status === 'MATCHED' ? 'Reference, amount, currency and value date agree.'
      : status === 'VALUE_MISMATCH' ? 'Reference agrees but amount differs; records left unmatched.'
      : status === 'UNMATCHED' ? 'No external bank evidence found for this reference.' : 'Instruction rejected or cancelled; matching not required.',
    exceptionIds: [],
  }
  matchGroups.push(group)
  for (const m of [x, bank, led]) if (m) { m.matchGroupId = gid; if (m !== led) m.matchStatus = status }
}
for (const [id, rows] of cardById) {
  const x = txns.find((y) => y.id === id)!
  if (!x.matchGroupId) continue
  const auth = rows.find((r) => r.event_type === 'AUTHORIZATION')
  const pres = rows.find((r) => r.event_type === 'PRESENTMENT')!
  matchGroups.push({
    id: x.matchGroupId, status: x.matchStatus,
    left: auth ? [{ transactionId: id, label: `Authorisation ${auth.card_event_id}`, amount: mny(auth)!, timestamp: auth.event_timestamp!, reference: auth.merchant_reference! }] : [],
    right: [{ transactionId: id, label: `Presentment ${pres.card_event_id}`, amount: mny(pres) ?? money(pres.amount!, 'EUR'), timestamp: pres.event_timestamp!, reference: pres.merchant_reference! }],
    difference: auth && mny(auth) && mny(pres) ? subMoney(mny(pres)!, mny(auth)!) : null,
    ruleVersion: 'CARD-MATCH-1.0', tolerance: 'Presentment must equal authorised amount',
    rationale: x.matchStatus === 'MATCHED' ? 'Presentment equals authorisation.' : x.matchStatus === 'VALUE_MISMATCH' ? 'Presentment amount differs from authorisation.' : 'Presentment has no required authorisation.',
    exceptionIds: [],
  })
}

// Link findings to transactions / groups
const idsIn = (s: string) => s.match(/\b(?:TRF|DD|CTX|CARD|BTX|LED|CEV)-\d+\b/g) ?? []
for (const f of findings) {
  for (const x of txns) if (x.rowIds.includes(f.sourceRowId)) x.exceptionIds.push(f.id)
}
for (const g of matchGroups) {
  for (const side of [...g.left, ...g.right]) {
    const x = txns.find((y) => y.id === side.transactionId)
    for (const e of x?.exceptionIds ?? []) if (!g.exceptionIds.includes(e)) g.exceptionIds.push(e)
  }
}
export const transactions: Transaction[] = txns.map(({ rowIds: _r, ...rest }) => rest)
export { matchGroups }

// ---------- Accounts & ledger ----------
export const ledgerEntries: LedgerEntry[] = t.ledger.map((r) => ({
  id: r.ledger_entry_id!, accountId: r.account_id!, timestamp: r.booking_timestamp!, valueDate: r.value_date!,
  direction: r.debit_credit_indicator as 'DEBIT' | 'CREDIT', amount: mny(r)!, reference: r.transaction_reference!,
  entryType: r.entry_type!, sourceStatus: r.source_status!,
  exceptionIds: findings.filter((f) => f.sourceRowId === r.ledger_entry_id).map((f) => f.id),
}))

export interface RollForward { opening: Money; lines: { label: string; amount: Money }[]; independent: Money }
export const rollForwards = new Map<string, RollForward>()
export const accounts: Account[] = t.balances.map((r) => {
  const c = cur(r.currency)!
  const opening = money(r.opening_balance_amount!, c)
  let running = opening
  const lines: { label: string; amount: Money }[] = []
  for (const e of ledgerEntries.filter((l) => l.accountId === r.account_id)) {
    if (e.amount.currency !== c) { lines.push({ label: `${e.id} ${e.reference} excluded (currency ${e.amount.currency} on ${c} account)`, amount: money('0', c) }); continue }
    if (e.sourceStatus !== 'POSTED' || findings.some((f) => f.sourceRowId === e.id && f.reasonCode === 'INVALID_STATE')) {
      lines.push({ label: `${e.id} ${e.reference} excluded (rejected instruction)`, amount: money('0', c) }); continue
    }
    // Independent view: outgoing transfers are debits regardless of source sign.
    const dir = e.entryType === 'TRANSFER' ? 'DEBIT' : e.direction
    const signed = dir === 'DEBIT' ? negate(e.amount) : e.amount
    lines.push({ label: `${e.id} ${e.reference} ${dir}`, amount: signed })
    running = addMoney(running, signed)
  }
  rollForwards.set(r.account_id!, { opening, lines, independent: running })
  const recorded = money(r.balance_amount!, c)
  const diff = subMoney(recorded, running)
  return {
    id: r.account_id!, currency: c, legalEntityId: LE_ID, openingBalance: opening, recordedClosingBalance: recorded,
    independentClosingBalance: running, difference: diff, state: equalMoney(recorded, running) ? 'PASSED' : 'FAILED',
    exceptionIds: findings.filter((f) => f.sourceName === 'client_account_balances' && f.sourceRowId === r.account_id).map((f) => f.id),
  } satisfies Account
})

// ---------- Safeguarding ----------
export const eligibleBalances: EligibleBalanceLine[] = t.sg.map((r) => {
  const fs = findings.filter((f) => f.sourceName === 'safeguarding_balances' && f.sourceRowId === r.source_row_id)
  const bal = mny(r, 'balance_amount')!
  const stale = !r.balance_timestamp!.startsWith(BUSINESS_DATE)
  const included = fs.length === 0
  return {
    sourceRowId: r.source_row_id!, externalAccountId: r.external_account_id!, balanceTimestamp: r.balance_timestamp!,
    balance: bal, evidenceReference: r.evidence_reference || null, included,
    reason: included ? 'Eligible: current, positive, evidenced, unique snapshot' : `Excluded: ${fs.map((f) => f.description).join('; ')}${stale ? ' (stale)' : ''}`,
    findingIds: fs.map((f) => f.id),
  }
})
export const obligations: ObligationLine[] = accounts.map((a) => ({
  accountId: a.id, currency: a.currency, amount: a.independentClosingBalance, movementType: 'Independent closing balance',
}))
const previousResults: Record<Currency, string> = { EUR: '-1800.00', CZK: '205000.00', USD: '5000.00' }
export const safeguardingPositions: SafeguardingCurrencyPosition[] = (['EUR', 'CZK', 'USD'] as Currency[]).map((c) => {
  const obligation = sumMoney(obligations.filter((o) => o.currency === c).map((o) => o.amount), c)
  const eligible = sumMoney(eligibleBalances.filter((e) => e.included && e.balance.currency === c).map((e) => e.balance), c)
  const adj = money('0', c)
  const result = subMoney(addMoney(eligible, adj), obligation)
  const neg = result.amount.startsWith('-')
  const noObl = obligation.amount === '0.00'
  return {
    currency: c, obligation, eligibleBalance: eligible, approvedAdjustments: adj, result,
    state: noObl ? 'NO_OBLIGATION' : neg ? 'SHORTFALL' : 'SURPLUS',
    qualifications: eligibleBalances.filter((e) => !e.included && e.balance.currency === c).map((e) => `${e.externalAccountId} excluded (${e.findingIds.join(', ')})`),
    previousResult: money(previousResults[c], c), exceptionIds: c === 'EUR' ? ['F-019'] : [],
  } satisfies SafeguardingCurrencyPosition
})
export const safeguardingAdjustments: SafeguardingAdjustment[] = [
  {
    id: 'ADJ-001', currency: 'EUR', amount: money('2500', 'EUR'), status: 'PROPOSED', owner: 'Eva Dvorakova',
    reason: 'Include SG-EUR-005 balance once bank statement evidence is supplied', evidenceRefs: [],
    history: [{ id: 'ADJ-001-1', at: '2026-10-28T08:20:00+01:00', actor: 'Eva Dvorakova', type: 'RESOLUTION_PROPOSED', text: 'Adjustment proposed; evidence outstanding.' }],
  },
  {
    id: 'ADJ-000', currency: 'CZK', amount: money('10000', 'CZK'), status: 'EXPIRED', owner: 'Petr Svoboda',
    reason: 'Temporary timing adjustment for 2026-10-23', evidenceRefs: ['STATEMENT-CZK-001'],
    history: [{ id: 'ADJ-000-1', at: '2026-10-23T09:00:00+01:00', actor: 'Petr Svoboda', type: 'APPROVAL', text: 'Approved with expiry 2026-10-26.' }],
  },
]

// ---------- Deliveries ----------
const rejectedRows = new Map<string, Set<string>>()
for (const f of findings) {
  if (f.sourceRowId === 'MULTIPLE' || f.sourceName === 'delivery_manifest') continue
  if (REJECT_RE.test(f.expectedEffect)) {
    const s = rejectedRows.get(f.sourceName) ?? new Set<string>()
    s.add(f.sourceRowId)
    rejectedRows.set(f.sourceName, s)
  }
}
export const deliveries: SourceDelivery[] = t.manifest.map((r) => {
  const name = r.source_name!
  const rej = rejectedRows.get(name)?.size ?? 0
  const totals: Money[] = []
  if (r.declared_total_eur !== '0') totals.push(money(r.declared_total_eur!, 'EUR'))
  if (r.declared_total_czk !== '0') totals.push(money(r.declared_total_czk!, 'CZK'))
  const actual = tables[name]!.rows.length
  const fIds = findings.filter((f) => f.sourceName === name || f.sourceName === 'delivery_manifest' && f.sourceRowId === name).map((f) => f.id)
  const quals: string[] = []
  if (actual !== Number(r.declared_record_count)) quals.push(`Manifest declares ${r.declared_record_count} records but file contains ${actual}`)
  if (rej > 0) quals.push(`${rej} record(s) rejected or quarantined; population qualified`)
  return {
    deliveryId: r.delivery_id!, sourceName: name, fileName: r.file_name!, businessDate: r.business_date!, schemaVersion: r.schema_version!,
    declaredRecordCount: Number(r.declared_record_count), actualRecordCount: actual, declaredTotals: totals,
    expectedBy: r.expected_by!, receivedAt: r.received_at || null, correctionOfDeliveryId: r.correction_of_delivery_id || null,
    state: quals.length ? 'QUALIFIED' : 'RECEIVED', acceptedRecords: actual - rej, rejectedRecords: rej, qualifications: quals, findingIds: fIds,
  } satisfies SourceDelivery
})
export function sourceRecords(name: string): SourceRecord[] {
  const tb = tables[name]
  const d = deliveries.find((x) => x.sourceName === name)
  if (!tb || !d) return []
  return tb.rows.map((r, i) => ({
    sourceName: name, sourceRowId: r[tb.key]!, deliveryId: d.deliveryId, rowNumber: i + 1,
    accepted: !rejectedRows.get(name)?.has(r[tb.key]!), fields: r,
    findingIds: findings.filter((f) => f.sourceName === name && f.sourceRowId === r[tb.key]).map((f) => f.id),
  }))
}

// ---------- Exceptions ----------
export const exceptionBases: ExceptionState['base'][] = findings.map((f, i) => {
  const blocking = f.severity === 'CRITICAL'
  const eurShort = safeguardingPositions.find((p) => p.currency === 'EUR')!.result
  const value = f.id === 'F-019' ? eurShort : findingValue(f)
  return {
    id: f.id, title: exceptionTitle(f), control: f.control, reasonCode: f.reasonCode, severity: f.severity,
    status: f.id === 'F-019' ? 'IN_INVESTIGATION' : STATUS_CYCLE[i % STATUS_CYCLE.length]!,
    value, legalEntityId: LE_ID, businessDate: BUSINESS_DATE, owner: f.id === 'F-019' ? 'Jana Novakova' : OWNERS[i % OWNERS.length]!,
    dueAt: blocking ? '2026-10-28T12:00:00+01:00' : f.severity === 'HIGH' ? '2026-10-29T17:00:00+01:00' : '2026-10-30T17:00:00+01:00',
    blocking, sourceFile: f.sourceFile, sourceRowId: f.sourceRowId, description: f.description, expectedEffect: f.expectedEffect,
    summary: `${f.description}. Expected effect: ${f.expectedEffect}. Raised by ${f.control.replace(/_/g, ' ')} control rule ${f.control}-${f.reasonCode}.`,
    trace: {
      control: f.control, ruleId: `${f.control.toUpperCase()}.${f.reasonCode}`, ruleVersion: `${RULESET}/1`,
      inputs: [{ name: 'source_file', value: f.sourceFile }, { name: 'source_row_id', value: f.sourceRowId }, { name: 'business_date', value: BUSINESS_DATE }],
      tolerance: f.control === 'matching' ? 'Exact amount' : 'No tolerance', calculation: f.description,
      result: `${f.severity} - ${f.reasonCode}`,
    },
    states: f.id === 'F-019' ? ['PENDING_EXTERNAL_ACTION'] : i % 7 === 3 ? ['WAITING_FOR_DATA'] : i % 7 === 5 ? ['PENDING_APPROVAL'] : [],
    approvalPolicy: blocking ? 'Safeguarding control owner approval required (four-eyes)' : f.severity === 'HIGH' ? 'Control owner approval for accepted difference' : 'Analyst resolution with reviewer',
  }
})

export function buildEvidence(f: Finding): { nodes: EvidenceNode[]; edges: EvidenceEdge[]; txnIds: string[]; accountIds: string[] } {
  const nodes: EvidenceNode[] = []
  const edges: EvidenceEdge[] = []
  const txnIds = new Set<string>()
  const accountIds = new Set<string>()
  const ctl = `CTL-${f.id}`
  nodes.push({ id: ctl, kind: 'CONTROL_RESULT', label: `${f.control} / ${f.reasonCode}`, source: 'control', sourceRowId: f.id, amount: null })
  const delivery = deliveries.find((d) => d.sourceName === f.sourceName)
  if (delivery) {
    nodes.push({ id: delivery.deliveryId, kind: 'DELIVERY', label: delivery.fileName, source: 'delivery_manifest', sourceRowId: delivery.sourceName, amount: null })
    edges.push({ from: delivery.deliveryId, to: ctl, relation: 'contains' })
  }
  const refs = new Set<string>(idsIn(f.description))
  if (f.sourceRowId !== 'MULTIPLE') refs.add(f.sourceRowId)
  for (const ref of refs) {
    for (const x of txns) {
      if (x.id === ref || x.rowIds.includes(ref) || x.reference === ref) {
        txnIds.add(x.id)
        if (x.accountId && x.accountId.startsWith('CA-')) accountIds.add(x.accountId)
        const k = x.kind === 'BANK_TRANSFER' || x.kind === 'DIRECT_DEBIT' || x.kind === 'CARD' ? (x.lifecycle.length > 1 ? 'LIFECYCLE_EVENT' : 'INSTRUCTION') : x.kind === 'LEDGER_ENTRY' ? 'LEDGER_ENTRY' : 'BANK_TRANSACTION'
        nodes.push({ id: `N-${x.id}`, kind: k, label: `${x.kind} ${x.id}`, source: x.source, sourceRowId: x.sourceRowId, amount: x.accepted ? x.amount : null })
        edges.push({ from: `N-${x.id}`, to: ctl, relation: 'evidence for' })
      }
    }
  }
  if (f.sourceName === 'client_account_balances') accountIds.add(f.sourceRowId)
  if (f.sourceName === 'safeguarding_balances') {
    for (const e of eligibleBalances.filter((b) => f.sourceRowId === 'MULTIPLE' || b.sourceRowId === f.sourceRowId)) {
      nodes.push({ id: `N-${e.sourceRowId}`, kind: 'BALANCE', label: `${e.externalAccountId} ${e.included ? 'included' : 'excluded'}`, source: 'safeguarding_balances', sourceRowId: e.sourceRowId, amount: e.balance })
      edges.push({ from: `N-${e.sourceRowId}`, to: ctl, relation: e.included ? 'counted in' : 'excluded from' })
    }
  }
  for (const a of accountIds) {
    const acc = accounts.find((x) => x.id === a)
    if (acc && !nodes.some((n) => n.id === `N-${a}`)) {
      nodes.push({ id: `N-${a}`, kind: 'BALANCE', label: `Account ${a}`, source: 'client_account_balances', sourceRowId: a, amount: acc.recordedClosingBalance })
      edges.push({ from: `N-${a}`, to: ctl, relation: 'balance impact' })
    }
  }
  return { nodes, edges, txnIds: [...txnIds], accountIds: [...accountIds] }
}

export const initialTimeline = (b: ExceptionState['base']): TimelineEvent[] => {
  const ev: TimelineEvent[] = [{ id: `${b.id}-T1`, at: '2026-10-28T07:20:00+01:00', actor: 'Proofly control engine', type: 'SYSTEM', text: `Exception created by ${b.trace.ruleId} (${b.trace.ruleVersion}).` }]
  if (b.owner) ev.push({ id: `${b.id}-T2`, at: '2026-10-28T08:05:00+01:00', actor: 'Proofly control engine', type: 'ASSIGNMENT', text: `Assigned to ${b.owner}.` })
  if (b.status === 'IN_INVESTIGATION') ev.push({ id: `${b.id}-T3`, at: '2026-10-28T08:40:00+01:00', actor: b.owner ?? 'analyst', type: 'NOTE', text: 'Reviewing source record and requesting supporting bank evidence.' })
  return ev
}

// ---------- Run, users, settings, audit ----------
export const runs: Run[] = [
  { id: RUN_ID, legalEntityId: LE_ID, businessDate: BUSINESS_DATE, version: 1, rulesetVersion: RULESET, state: 'REVIEW_REQUIRED', signOffState: 'PREPARATION_INCOMPLETE', startedAt: '2026-10-28T07:20:00+01:00', lastEvidenceArrivalAt: '2026-10-28T06:58:00+01:00' },
  { id: PREVIOUS_RUN_ID, legalEntityId: LE_ID, businessDate: '2026-10-26', version: 1, rulesetVersion: RULESET, state: 'SIGNED_OFF', signOffState: 'SIGNED_OFF', startedAt: '2026-10-27T07:20:00+01:00', lastEvidenceArrivalAt: '2026-10-27T06:55:00+01:00' },
]
export const users: UserAccess[] = [
  { id: 'U-1', name: 'Jana Novakova', email: 'jana.novakova@example.test', roles: ['CONTROL_ANALYST', 'CONTROL_OWNER'], legalEntityIds: [LE_ID] },
  { id: 'U-2', name: 'Petr Svoboda', email: 'petr.svoboda@example.test', roles: ['CONTROL_ANALYST'], legalEntityIds: [LE_ID] },
  { id: 'U-3', name: 'Eva Dvorakova', email: 'eva.dvorakova@example.test', roles: ['CONTROL_ANALYST'], legalEntityIds: [LE_ID] },
  { id: 'U-4', name: 'Tomas Kral', email: 'tomas.kral@example.test', roles: ['APPROVER'], legalEntityIds: [LE_ID] },
  { id: 'U-5', name: 'Alena Horakova', email: 'alena.horakova@example.test', roles: ['AUDIT_REVIEWER'], legalEntityIds: [LE_ID] },
]
export const rules: RuleConfig[] = (['data_completeness', 'matching', 'independent_ledger', 'transaction_lifecycle', 'safeguarding'] as ControlArea[]).map((c) => ({
  ruleId: `${c.toUpperCase()}.CORE`, control: c, version: `${RULESET}/1`, effectiveFrom: '2026-10-01',
  description: `Core ${c.replace(/_/g, ' ')} control`, tolerance: c === 'matching' ? 'Exact amount' : 'None', severity: c === 'safeguarding' ? 'CRITICAL' : 'HIGH',
}))
export const sourceConfigs: SourceConfig[] = deliveries.map((d) => ({ sourceName: d.sourceName, schemaVersion: d.schemaVersion, expectedByLocalTime: d.expectedBy.slice(11, 16), mandatory: true }))

const hash = (s: string) => {
  let h = 2166136261
  for (const ch of s) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619) }
  return (h >>> 0).toString(16).padStart(8, '0').repeat(4)
}
export const auditEvents: AuditEvent[] = (() => {
  const ev: Omit<AuditEvent, 'hash'>[] = []
  for (const d of deliveries) ev.push({ id: '', at: d.receivedAt!, actor: 'ingestion', type: 'DELIVERY_RECEIVED', runId: RUN_ID, subjectKind: 'DELIVERY', subjectId: d.deliveryId, summary: `${d.fileName} received (${d.actualRecordCount} records)`, ruleVersion: null })
  ev.push({ id: '', at: '2026-10-28T07:20:00+01:00', actor: 'Proofly control engine', type: 'RUN_STARTED', runId: RUN_ID, subjectKind: 'RUN', subjectId: RUN_ID, summary: `Run v1 started with ${RULESET}`, ruleVersion: RULESET })
  for (const [n, rs] of rejectedRows) for (const r of rs) ev.push({ id: '', at: '2026-10-28T07:21:00+01:00', actor: 'ingestion', type: 'RECORD_REJECTED', runId: RUN_ID, subjectKind: 'RECORD', subjectId: `${n}:${r}`, summary: `Record ${r} in ${n} rejected or quarantined`, ruleVersion: RULESET })
  for (const b of exceptionBases) ev.push({ id: '', at: '2026-10-28T07:25:00+01:00', actor: 'Proofly control engine', type: 'EXCEPTION_CREATED', runId: RUN_ID, subjectKind: 'EXCEPTION', subjectId: b.id, summary: b.title, ruleVersion: b.trace.ruleVersion })
  ev.sort((a, b) => a.at.localeCompare(b.at))
  return ev.map((e, i) => ({ ...e, id: `AUD-${String(i + 1).padStart(4, '0')}`, hash: hash(`${i}|${e.at}|${e.subjectId}|${e.summary}`) }))
})()
