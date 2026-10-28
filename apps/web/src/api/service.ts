/**
 * Mock async service shaped like a future authenticated REST API.
 * Each function maps to one endpoint (see comment). Swap the implementation, keep the signatures.
 */
import * as fx from './fixtures'
import type {
  AccountLedgerView, AuditEvent, AuditFilter, ControlAreaSummary, ControlRoomData, ControlState, EvidencePack,
  ExceptionDetail, ExceptionFilter, ExceptionSummary, LegalEntity, ListParams, MatchingWorkbench, Page,
  ResolutionProposal, Run, RunSignOff, SafeguardingAdjustment, SafeguardingPosition, SettingsData,
  SourceDelivery, SourceRecord, TimelineEvent, Transaction, TransactionFilter, ChecklistItem, ControlArea, Account, Severity,
} from './types'
import { money, subMoney } from './money'

const LATENCY_MS = 60
const delay = <T>(v: T): Promise<T> => new Promise((r) => setTimeout(() => r(structuredClone(v)), LATENCY_MS))
const fail = (status: number, msg: string): never => { throw Object.assign(new Error(msg), { status }) }

function paginate<T>(items: T[], p: ListParams): Page<T> {
  const page = p.page ?? 1
  const pageSize = p.pageSize ?? 25
  return { items: items.slice((page - 1) * pageSize, page * pageSize), total: items.length, page, pageSize }
}
const matches = (s: string, q?: string) => !q || s.toLowerCase().includes(q.toLowerCase())

// ---- mutable in-memory state ----
const state = {
  runs: structuredClone(fx.runs),
  exceptions: new Map(fx.exceptionBases.map((b) => [b.id, { ...b, timeline: fx.initialTimeline(b) }])),
  adjustments: structuredClone(fx.safeguardingAdjustments),
  audit: structuredClone(fx.auditEvents) as AuditEvent[],
  pack: null as EvidencePack | null,
}
const ACTOR = 'martin.matousek'
let seq = 1000

function audit(type: AuditEvent['type'], subjectKind: AuditEvent['subjectKind'], subjectId: string, summary: string) {
  const at = new Date().toISOString()
  state.audit.push({ id: `AUD-${++seq}`, at, actor: ACTOR, type, runId: fx.RUN_ID, subjectKind, subjectId, summary, ruleVersion: null, hash: (seq * 2654435761 >>> 0).toString(16).padStart(8, '0').repeat(4) })
}
const toSummary = (e: ReturnType<typeof state.exceptions.get> & object): ExceptionSummary => ({
  id: e.id, title: e.title, control: e.control, reasonCode: e.reasonCode, severity: e.severity, status: e.status, value: e.value,
  legalEntityId: e.legalEntityId, businessDate: e.businessDate, owner: e.owner, dueAt: e.dueAt, blocking: e.blocking,
  sourceFile: e.sourceFile, sourceRowId: e.sourceRowId,
})
const SEV: Record<Severity, number> = { CRITICAL: 0, HIGH: 1, MEDIUM: 2, LOW: 3 }
const isClosed = (s: string) => ['RESOLVED', 'ACCEPTED_DIFFERENCE', 'FALSE_POSITIVE'].includes(s)
const allExceptions = () => [...state.exceptions.values()]

// ---- Context: GET /legal-entities, /runs ----
export const listLegalEntities = (): Promise<LegalEntity[]> => delay([fx.legalEntity])
export const listRuns = (legalEntityId?: string): Promise<Run[]> => delay(state.runs.filter((r) => !legalEntityId || r.legalEntityId === legalEntityId))
export async function getRun(runId: string): Promise<Run> {
  return delay(state.runs.find((r) => r.id === runId) ?? fail(404, `Run ${runId} not found`))
}

// ---- Control Room: GET /runs/{runId}/control-room ----
function controlAreas(): ControlAreaSummary[] {
  const defs: { area: ControlArea; label: string; count: number; unit: Parameters<typeof money>[1] }[] = [
    { area: 'data_completeness', label: 'Data completeness', count: fx.deliveries.reduce((n, d) => n + d.actualRecordCount, 0), unit: 'EUR' },
    { area: 'independent_ledger', label: 'Independent ledger verification', count: fx.ledgerEntries.length, unit: 'EUR' },
    { area: 'matching', label: 'Matching and reconciliation', count: fx.matchGroups.length, unit: 'EUR' },
    { area: 'transaction_lifecycle', label: 'Transaction lifecycle', count: fx.transactions.filter((x) => ['BANK_TRANSFER', 'DIRECT_DEBIT', 'CARD'].includes(x.kind)).length, unit: 'EUR' },
    { area: 'safeguarding', label: 'Safeguarding coverage', count: fx.eligibleBalances.length, unit: 'EUR' },
  ]
  return defs.map((d) => {
    const ex = allExceptions().filter((e) => e.control === d.area && !isClosed(e.status))
    const worst = ex.map((e) => SEV[e.severity]).sort()[0]
    const st: ControlState = ex.length === 0 ? 'PASSED' : worst === 0 ? 'BLOCKED' : worst === 1 ? 'FAILED' : 'REVIEW_REQUIRED'
    const value = d.area === 'safeguarding' ? fx.safeguardingPositions.map((p) => p.eligibleBalance) : d.area === 'independent_ledger' ? [money('21124.51', 'EUR'), money('131800', 'CZK')] : []
    return { area: d.area, label: d.label, state: st, recordCount: d.count, value, openExceptions: ex.length, previousRecordCount: d.count - 2, changeFromPrevious: 2 }
  })
}
export async function getControlRoom(runId: string = fx.RUN_ID): Promise<ControlRoomData> {
  const run = state.runs.find((r) => r.id === runId) ?? fail(404, 'Run not found')
  const attention = allExceptions().filter((e) => !isClosed(e.status) && (e.severity === 'CRITICAL' || e.severity === 'HIGH')).sort((a, b) => SEV[a.severity] - SEV[b.severity]).slice(0, 8).map(toSummary)
  return delay({
    run,
    summary: [
      { text: 'EUR safeguarding shortfall created as a blocking exception (F-019).', link: { kind: 'exception', id: 'F-019' } },
      { text: 'Banking delivery declares 9 records but contains 10 (F-014).', link: { kind: 'exception', id: 'F-014' } },
      { text: '2 amount mismatches between instructions and bank evidence (F-011, F-012).', link: { kind: 'control', id: 'matching' } },
      { text: 'Direct debit DD-002 was returned with no reversing ledger entry (F-013).', link: { kind: 'exception', id: 'F-013' } },
    ],
    evidenceReadiness: fx.deliveries, controlAreas: controlAreas(), attentionQueue: attention, safeguarding: fx.safeguardingPositions,
  })
}

// ---- Sources: GET /sources ----
export const listSourceDeliveries = (runId: string = fx.RUN_ID): Promise<SourceDelivery[]> => { void runId; return delay(fx.deliveries) }
export async function getSourceDelivery(deliveryId: string): Promise<SourceDelivery> {
  return delay(fx.deliveries.find((d) => d.deliveryId === deliveryId) ?? fail(404, 'Delivery not found'))
}
export const listSourceRecords = (sourceName: string, p: ListParams & { rejectedOnly?: boolean } = {}): Promise<Page<SourceRecord>> =>
  delay(paginate(fx.sourceRecords(sourceName).filter((r) => (!p.rejectedOnly || !r.accepted) && matches(r.sourceRowId + Object.values(r.fields).join(' '), p.search)), p))

// ---- Exceptions: GET /exceptions, /exceptions/{id} ----
export async function listExceptions(f: ExceptionFilter = {}): Promise<Page<ExceptionSummary>> {
  const items = allExceptions().filter((e) =>
    (!f.severity || e.severity === f.severity) && (!f.control || e.control === f.control) && (!f.status || e.status === f.status) &&
    (!f.owner || e.owner === f.owner) && (!f.currency || e.value?.currency === f.currency) && matches(`${e.id} ${e.title} ${e.sourceRowId}`, f.search),
  ).sort((a, b) => SEV[a.severity] - SEV[b.severity] || a.id.localeCompare(b.id)).map(toSummary)
  return delay(paginate(items, f))
}
export async function getException(id: string): Promise<ExceptionDetail> {
  const e = state.exceptions.get(id) ?? fail(404, `Exception ${id} not found`)
  const f = fx.findings.find((x) => x.id === id)!
  const ev = fx.buildEvidence(f)
  return delay({ ...e, evidenceNodes: ev.nodes, evidenceEdges: ev.edges, relatedTransactionIds: ev.txnIds, relatedAccountIds: ev.accountIds })
}
function mutate(id: string, ev: Omit<TimelineEvent, 'id' | 'at' | 'actor'>, patch: Partial<ExceptionDetail> = {}, aud?: AuditEvent['type']) {
  const e = state.exceptions.get(id) ?? fail(404, `Exception ${id} not found`)
  Object.assign(e, patch)
  e.timeline.push({ id: `${id}-T${e.timeline.length + 1}`, at: new Date().toISOString(), actor: ACTOR, ...ev })
  if (aud) audit(aud, 'EXCEPTION', id, ev.text)
}
export async function assignException(id: string, owner: string): Promise<ExceptionDetail> {
  mutate(id, { type: 'ASSIGNMENT', text: `Assigned to ${owner}.` }, { owner, status: state.exceptions.get(id)?.status === 'OPEN' ? 'IN_INVESTIGATION' : state.exceptions.get(id)?.status }, 'EXCEPTION_ASSIGNED')
  return getException(id)
}
export async function addExceptionNote(id: string, text: string): Promise<ExceptionDetail> {
  mutate(id, { type: 'NOTE', text }, {}, 'NOTE_ADDED')
  return getException(id)
}
export async function requestEvidence(id: string, text: string): Promise<ExceptionDetail> {
  mutate(id, { type: 'EVIDENCE_REQUEST', text }, { status: 'WAITING_FOR_DATA', states: ['WAITING_FOR_DATA'] })
  return getException(id)
}
export async function proposeResolution(id: string, p: ResolutionProposal): Promise<ExceptionDetail> {
  if (!p.reason.trim()) fail(422, 'Reason is mandatory')
  mutate(id, { type: 'RESOLUTION_PROPOSED', text: `${p.kind} proposed: ${p.reason}` }, { status: 'PENDING_APPROVAL', states: ['PENDING_APPROVAL'] }, 'RESOLUTION_PROPOSED')
  return getException(id)
}
export async function decideResolution(id: string, approve: boolean, kind: ResolutionProposal['kind'] = 'RESOLVED'): Promise<ExceptionDetail> {
  mutate(id, { type: 'APPROVAL', text: approve ? `Resolution approved (${kind}). Original control result retained.` : 'Resolution rejected; exception reopened.' },
    { status: approve ? kind : 'IN_INVESTIGATION', states: [] })
  return getException(id)
}
export async function reopenException(id: string, reason: string): Promise<ExceptionDetail> {
  mutate(id, { type: 'REOPENED', text: reason }, { status: 'IN_INVESTIGATION' })
  return getException(id)
}

// ---- Transactions: GET /transactions ----
export async function listTransactions(f: TransactionFilter = {}): Promise<Page<Transaction>> {
  const items = fx.transactions.filter((x) =>
    (!f.kind || x.kind === f.kind) && (!f.matchStatus || x.matchStatus === f.matchStatus) && (!f.accountId || x.accountId === f.accountId) &&
    (!f.currency || x.amount.currency === f.currency) && (f.hasException === undefined || (x.exceptionIds.length > 0) === f.hasException) &&
    matches(`${x.id} ${x.reference} ${x.accountId ?? ''}`, f.search))
  return delay(paginate(items, f))
}
export async function getTransaction(id: string): Promise<Transaction> {
  return delay(fx.transactions.find((x) => x.id === id) ?? fail(404, `Transaction ${id} not found`))
}
/** GET /matching/workbench?transactionId= or ?exceptionId= */
export async function getMatchingWorkbench(ref: { transactionId?: string; exceptionId?: string }): Promise<MatchingWorkbench> {
  let focus = ref.transactionId
  if (!focus && ref.exceptionId) focus = fx.buildEvidence(fx.findings.find((f) => f.id === ref.exceptionId) ?? fail(404, 'Exception not found')).txnIds[0]
  const x = fx.transactions.find((y) => y.id === focus) ?? fail(404, 'Transaction not found')
  const groups = fx.matchGroups.filter((g) => g.id === x.matchGroupId)
  const unmatched = fx.matchGroups.filter((g) => g.status === 'UNMATCHED').flatMap((g) => g.left)
  return delay({ focusTransactionId: x.id, groups, unmatched })
}
export const listMatchGroups = (status?: MatchGroupStatusFilter) => delay(fx.matchGroups.filter((g) => !status || g.status === status))
type MatchGroupStatusFilter = import('./types').MatchStatus

// ---- Accounts: GET /accounts, /accounts/{id}/ledger ----
export const listAccounts = (): Promise<Account[]> => delay(fx.accounts)
export async function getAccountLedger(accountId: string): Promise<AccountLedgerView> {
  const account = fx.accounts.find((a) => a.id === accountId) ?? fail(404, `Account ${accountId} not found`)
  const rf = fx.rollForwards.get(accountId)!
  return delay({
    account, entries: fx.ledgerEntries.filter((e) => e.accountId === accountId),
    rollForward: [{ label: 'Opening balance', amount: rf.opening }, ...rf.lines, { label: 'Independent closing balance', amount: rf.independent }, { label: 'Recorded closing balance', amount: account.recordedClosingBalance }],
    relatedTransactions: fx.transactions.filter((x) => x.accountId === accountId),
    exceptions: allExceptions().filter((e) => account.exceptionIds.includes(e.id) || fx.ledgerEntries.some((l) => l.accountId === accountId && l.exceptionIds.includes(e.id))).map(toSummary),
  })
}

// ---- Safeguarding: GET /runs/{id}/safeguarding ----
export async function getSafeguardingPosition(runId: string = fx.RUN_ID): Promise<SafeguardingPosition> {
  const run = state.runs.find((r) => r.id === runId) ?? fail(404, 'Run not found')
  return delay({
    runId, businessDate: run.businessDate, legalEntityId: run.legalEntityId, evidenceReadiness: 'QUALIFIED', approvalState: run.signOffState,
    positions: fx.safeguardingPositions, obligations: fx.obligations, eligibleBalances: fx.eligibleBalances, adjustments: state.adjustments,
    changeAnalysis: fx.safeguardingPositions.filter((p) => p.previousResult).map((p) => ({
      label: 'Net movement in coverage vs previous business date', currency: p.currency,
      change: subMoney(p.result, p.previousResult!),
    })),
  })
}
export async function proposeAdjustment(a: Pick<SafeguardingAdjustment, 'currency' | 'amount' | 'reason' | 'evidenceRefs'>): Promise<SafeguardingAdjustment> {
  const adj: SafeguardingAdjustment = { id: `ADJ-${String(state.adjustments.length + 1).padStart(3, '0')}`, ...a, status: 'PROPOSED', owner: ACTOR,
    history: [{ id: 'h1', at: new Date().toISOString(), actor: ACTOR, type: 'RESOLUTION_PROPOSED', text: 'Adjustment proposed.' }] }
  state.adjustments.push(adj)
  return delay(adj)
}
export async function decideAdjustment(id: string, approve: boolean): Promise<SafeguardingAdjustment> {
  const a = state.adjustments.find((x) => x.id === id) ?? fail(404, 'Adjustment not found')
  a.status = approve ? 'APPROVED' : 'REJECTED'
  a.history.push({ id: `h${a.history.length + 1}`, at: new Date().toISOString(), actor: ACTOR, type: 'APPROVAL', text: approve ? 'Approved.' : 'Rejected.' })
  return delay(a)
}

// ---- Sign-off: GET/POST /runs/{id}/sign-off ----
export async function getRunSignOff(runId: string = fx.RUN_ID): Promise<RunSignOff> {
  const run = state.runs.find((r) => r.id === runId) ?? fail(404, 'Run not found')
  const checklist: ChecklistItem[] = controlAreas().map((c) => ({
    id: `CHK-${c.area}`, control: c.area, label: c.label, state: c.state, mandatory: true,
    note: c.openExceptions ? `${c.openExceptions} open exception(s)` : 'No open exceptions', exceptionIds: allExceptions().filter((e) => e.control === c.area && !isClosed(e.status)).map((e) => e.id),
  }))
  const blockers = checklist.filter((c) => c.mandatory && c.state !== 'PASSED').map((c) => `${c.label}: ${c.state}`)
  return delay({
    run, checklist, openMatters: allExceptions().filter((e) => !isClosed(e.status) && SEV[e.severity] <= 1).map(toSummary),
    changesSincePreparation: ['No changes since preparation (mock).'],
    attestationText: 'I confirm that I have reviewed the mandatory controls, open matters and qualifications for this run and approve sign-off on the basis of the evidence shown.',
    canSignOff: blockers.length === 0, blockers, evidencePackId: state.pack?.id ?? null,
  })
}
export async function returnRun(runId: string, reason: string): Promise<Run> {
  const run = state.runs.find((r) => r.id === runId) ?? fail(404, 'Run not found')
  run.signOffState = 'RETURNED'
  audit('SIGN_OFF_RETURNED', 'RUN', runId, reason)
  return delay(run)
}
export async function signOffRun(runId: string, attestation: boolean): Promise<Run> {
  const so = await getRunSignOff(runId)
  if (!attestation) fail(422, 'Attestation required')
  if (!so.canSignOff) fail(409, `Sign-off blocked: ${so.blockers.join('; ')}`)
  const run = state.runs.find((r) => r.id === runId)!
  run.signOffState = 'SIGNED_OFF'
  run.state = 'SIGNED_OFF'
  return delay(run)
}

// ---- Evidence & audit: GET /audit-events, /evidence-packs ----
export async function listAuditEvents(f: AuditFilter = {}): Promise<Page<AuditEvent>> {
  const items = state.audit.filter((e) => (!f.subjectKind || e.subjectKind === f.subjectKind) && (!f.subjectId || e.subjectId === f.subjectId) && (!f.type || e.type === f.type) && matches(`${e.summary} ${e.subjectId}`, f.search))
  return delay(paginate(items, { pageSize: 50, ...f }))
}
export const getEvidencePack = (runId: string = fx.RUN_ID): Promise<EvidencePack> =>
  delay(state.pack ?? { id: `PACK-${runId}`, runId, generatedAt: null, status: 'NOT_GENERATED', contents: [] })
export async function generateEvidencePack(runId: string = fx.RUN_ID): Promise<EvidencePack> {
  state.pack = { id: `PACK-${runId}`, runId, generatedAt: new Date().toISOString(), status: 'GENERATED', contents: ['Run header and ruleset', 'Source deliveries and manifests', 'Control results', 'Exceptions and timelines', 'Safeguarding calculation', 'Audit event log'] }
  audit('EVIDENCE_PACK_GENERATED', 'EVIDENCE_PACK', state.pack.id, 'Evidence pack generated')
  return delay(state.pack)
}

// ---- Settings: GET /settings ----
export const getSettings = (): Promise<SettingsData> => delay({
  legalEntity: fx.legalEntity, rules: fx.rules, sources: fx.sourceConfigs, users: fx.users,
  freshnessPolicy: 'External safeguarding balances must be dated the business date (no older than 0 days).',
  approvalPolicies: [
    { name: 'Critical / blocking', description: 'Safeguarding control owner approval, four-eyes.' },
    { name: 'High', description: 'Control owner approval for accepted differences.' },
    { name: 'Medium / Low', description: 'Analyst resolution with reviewer.' },
  ],
})
