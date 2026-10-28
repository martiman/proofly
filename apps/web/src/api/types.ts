/** Typed API contract for Proofly web. Money is always a decimal string + currency. */

export type Currency = 'EUR' | 'CZK' | 'USD'
export type IsoDate = string // YYYY-MM-DD
export type IsoDateTime = string // ISO 8601 with offset

export interface Money {
  amount: string // decimal string, e.g. "-50.00"
  currency: Currency
}

export type Severity = 'CRITICAL' | 'HIGH' | 'MEDIUM' | 'LOW'
export type ControlArea =
  | 'data_completeness'
  | 'matching'
  | 'independent_ledger'
  | 'transaction_lifecycle'
  | 'safeguarding'
export type ControlState = 'PASSED' | 'REVIEW_REQUIRED' | 'FAILED' | 'BLOCKED' | 'QUALIFIED'

export interface Page<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}
export interface ListParams {
  page?: number
  pageSize?: number
  search?: string
}

// ---- Context / runs ----
export interface LegalEntity {
  id: string
  name: string
  currencies: Currency[]
}
export type RunState =
  | 'AWAITING_EVIDENCE'
  | 'PROCESSING'
  | 'REVIEW_REQUIRED'
  | 'READY_FOR_SIGN_OFF'
  | 'SIGNED_OFF'
  | 'SUPERSEDED'
  | 'FAILED'
export type SignOffState =
  | 'PREPARATION_INCOMPLETE'
  | 'READY_FOR_REVIEW'
  | 'RETURNED'
  | 'APPROVAL_PENDING'
  | 'SIGNED_OFF'
  | 'INVALIDATED'
  | 'SUPERSEDED'

export interface Run {
  id: string
  legalEntityId: string
  businessDate: IsoDate
  version: number
  rulesetVersion: string
  state: RunState
  signOffState: SignOffState
  startedAt: IsoDateTime
  lastEvidenceArrivalAt: IsoDateTime
}

export interface ControlAreaSummary {
  area: ControlArea
  label: string
  state: ControlState
  recordCount: number
  value: Money[]
  openExceptions: number
  previousRecordCount: number
  changeFromPrevious: number
}

export interface DailySummaryItem {
  text: string
  link?: { kind: 'exception' | 'control' | 'source'; id: string }
}

export interface ControlRoomData {
  run: Run
  summary: DailySummaryItem[]
  evidenceReadiness: SourceDelivery[]
  controlAreas: ControlAreaSummary[]
  attentionQueue: ExceptionSummary[]
  safeguarding: SafeguardingCurrencyPosition[]
}

// ---- Sources ----
export type DeliveryState = 'RECEIVED' | 'LATE' | 'MISSING' | 'REJECTED' | 'SUPERSEDED' | 'QUALIFIED'
export interface SourceDelivery {
  deliveryId: string
  sourceName: string
  fileName: string
  businessDate: IsoDate
  schemaVersion: string
  declaredRecordCount: number
  actualRecordCount: number
  declaredTotals: Money[]
  expectedBy: IsoDateTime
  receivedAt: IsoDateTime | null
  correctionOfDeliveryId: string | null
  state: DeliveryState
  acceptedRecords: number
  rejectedRecords: number
  qualifications: string[]
  findingIds: string[]
}
export interface SourceRecordRef {
  sourceName: string
  sourceRowId: string
}
export interface SourceRecord extends SourceRecordRef {
  deliveryId: string
  rowNumber: number
  accepted: boolean
  fields: Record<string, string>
  findingIds: string[]
}

// ---- Exceptions ----
export type ExceptionStatus =
  | 'OPEN'
  | 'IN_INVESTIGATION'
  | 'WAITING_FOR_DATA'
  | 'PENDING_APPROVAL'
  | 'RESOLVED'
  | 'ACCEPTED_DIFFERENCE'
  | 'FALSE_POSITIVE'
export interface ExceptionSummary {
  id: string // e.g. F-019
  title: string
  control: ControlArea
  reasonCode: string
  severity: Severity
  status: ExceptionStatus
  value: Money | null
  legalEntityId: string
  businessDate: IsoDate
  owner: string | null
  dueAt: IsoDateTime
  blocking: boolean
  sourceFile: string
  sourceRowId: string
}
export interface EvidenceNode {
  id: string
  kind: 'INSTRUCTION' | 'LEDGER_ENTRY' | 'BANK_TRANSACTION' | 'LIFECYCLE_EVENT' | 'BALANCE' | 'CONTROL_RESULT' | 'DELIVERY'
  label: string
  source: string
  sourceRowId: string
  amount: Money | null
}
export interface EvidenceEdge {
  from: string
  to: string
  relation: string
}
export interface RuleTrace {
  control: ControlArea
  ruleId: string
  ruleVersion: string
  inputs: { name: string; value: string }[]
  tolerance: string
  calculation: string
  result: string
}
export interface TimelineEvent {
  id: string
  at: IsoDateTime
  actor: string
  type: 'ASSIGNMENT' | 'NOTE' | 'EVIDENCE_REQUEST' | 'STATUS_CHANGE' | 'RESOLUTION_PROPOSED' | 'APPROVAL' | 'REOPENED' | 'SYSTEM'
  text: string
}
export interface ExceptionDetail extends ExceptionSummary {
  description: string
  expectedEffect: string
  summary: string
  evidenceNodes: EvidenceNode[]
  evidenceEdges: EvidenceEdge[]
  trace: RuleTrace
  timeline: TimelineEvent[]
  states: ('WAITING_FOR_DATA' | 'CONFLICTING_EVIDENCE' | 'PENDING_EXTERNAL_ACTION' | 'PENDING_APPROVAL' | 'SUPERSEDED_RUN')[]
  relatedTransactionIds: string[]
  relatedAccountIds: string[]
  approvalPolicy: string
}
export interface ExceptionFilter extends ListParams {
  severity?: Severity
  control?: ControlArea
  status?: ExceptionStatus
  owner?: string
  currency?: Currency
}
export type ResolutionKind = 'RESOLVED' | 'ACCEPTED_DIFFERENCE' | 'FALSE_POSITIVE'
export interface ResolutionProposal {
  kind: ResolutionKind
  reason: string
  evidenceRefs: string[]
}

// ---- Transactions / matching ----
export type TransactionKind = 'BANK_TRANSFER' | 'DIRECT_DEBIT' | 'CARD' | 'BANK_EVIDENCE' | 'LEDGER_ENTRY'
export type MatchStatus = 'MATCHED' | 'UNMATCHED' | 'VALUE_MISMATCH' | 'NOT_APPLICABLE'
export interface Transaction {
  id: string // normalised id, e.g. TRF-004, DD-002, CTX-002, BTX-008, LED-004
  kind: TransactionKind
  source: string
  sourceRowId: string
  reference: string
  accountId: string | null
  timestamp: IsoDateTime
  amount: Money
  direction: 'DEBIT' | 'CREDIT' | null
  sourceStatus: string
  lifecycle: string[]
  matchStatus: MatchStatus
  matchGroupId: string | null
  exceptionIds: string[]
  accepted: boolean
}
export interface TransactionFilter extends ListParams {
  kind?: TransactionKind
  matchStatus?: MatchStatus
  accountId?: string
  currency?: Currency
  hasException?: boolean
}
export interface MatchCandidate {
  transactionId: string
  label: string
  amount: Money
  timestamp: IsoDateTime
  reference: string
}
export interface MatchGroup {
  id: string
  status: MatchStatus
  left: MatchCandidate[]
  right: MatchCandidate[]
  difference: Money | null
  ruleVersion: string
  tolerance: string
  rationale: string
  exceptionIds: string[]
}
export interface MatchingWorkbench {
  focusTransactionId: string
  groups: MatchGroup[]
  unmatched: MatchCandidate[]
}

// ---- Accounts / ledger ----
export interface Account {
  id: string
  currency: Currency
  legalEntityId: string
  openingBalance: Money
  recordedClosingBalance: Money
  independentClosingBalance: Money
  difference: Money
  state: ControlState
  exceptionIds: string[]
}
export interface LedgerEntry {
  id: string
  accountId: string
  timestamp: IsoDateTime
  valueDate: IsoDate
  direction: 'DEBIT' | 'CREDIT'
  amount: Money
  reference: string
  entryType: string
  sourceStatus: string
  exceptionIds: string[]
}
export interface AccountLedgerView {
  account: Account
  entries: LedgerEntry[]
  rollForward: { label: string; amount: Money }[]
  relatedTransactions: Transaction[]
  exceptions: ExceptionSummary[]
}

// ---- Safeguarding ----
export type SafeguardingState = 'COVERED' | 'SHORTFALL' | 'SURPLUS' | 'QUALIFIED' | 'NO_OBLIGATION'
export interface SafeguardingCurrencyPosition {
  currency: Currency
  obligation: Money
  eligibleBalance: Money
  approvedAdjustments: Money
  result: Money // positive = surplus, negative = shortfall
  state: SafeguardingState
  qualifications: string[]
  previousResult: Money | null
  exceptionIds: string[]
}
export interface ObligationLine {
  accountId: string
  currency: Currency
  amount: Money
  movementType: string
}
export interface EligibleBalanceLine {
  sourceRowId: string
  externalAccountId: string
  balanceTimestamp: IsoDateTime
  balance: Money
  evidenceReference: string | null
  included: boolean
  reason: string
  findingIds: string[]
}
export type AdjustmentStatus = 'PROPOSED' | 'APPROVED' | 'REJECTED' | 'EXPIRED'
export interface SafeguardingAdjustment {
  id: string
  currency: Currency
  amount: Money
  status: AdjustmentStatus
  owner: string
  reason: string
  evidenceRefs: string[]
  history: TimelineEvent[]
}
export interface ChangeDriver {
  label: string
  currency: Currency
  change: Money
}
export interface SafeguardingPosition {
  runId: string
  businessDate: IsoDate
  legalEntityId: string
  evidenceReadiness: 'COMPLETE' | 'QUALIFIED' | 'INCOMPLETE'
  approvalState: SignOffState
  positions: SafeguardingCurrencyPosition[]
  obligations: ObligationLine[]
  eligibleBalances: EligibleBalanceLine[]
  adjustments: SafeguardingAdjustment[]
  changeAnalysis: ChangeDriver[]
}

// ---- Sign-off ----
export interface ChecklistItem {
  id: string
  control: ControlArea
  label: string
  state: ControlState
  mandatory: boolean
  note: string
  exceptionIds: string[]
}
export interface RunSignOff {
  run: Run
  checklist: ChecklistItem[]
  openMatters: ExceptionSummary[]
  changesSincePreparation: string[]
  attestationText: string
  canSignOff: boolean
  blockers: string[]
  evidencePackId: string | null
}

// ---- Evidence / audit ----
export type AuditEventType =
  | 'DELIVERY_RECEIVED'
  | 'RECORD_REJECTED'
  | 'RUN_STARTED'
  | 'CONTROL_EXECUTED'
  | 'EXCEPTION_CREATED'
  | 'EXCEPTION_ASSIGNED'
  | 'NOTE_ADDED'
  | 'RESOLUTION_PROPOSED'
  | 'SIGN_OFF_RETURNED'
  | 'EVIDENCE_PACK_GENERATED'
export interface AuditEvent {
  id: string
  at: IsoDateTime
  actor: string
  type: AuditEventType
  runId: string
  subjectKind: 'RUN' | 'DELIVERY' | 'RECORD' | 'EXCEPTION' | 'EVIDENCE_PACK'
  subjectId: string
  summary: string
  ruleVersion: string | null
  hash: string
}
export interface EvidencePack {
  id: string
  runId: string
  generatedAt: IsoDateTime | null
  status: 'NOT_GENERATED' | 'GENERATED'
  contents: string[]
}
export interface AuditFilter extends ListParams {
  subjectKind?: AuditEvent['subjectKind']
  subjectId?: string
  type?: AuditEventType
}

// ---- Settings ----
export interface RuleConfig {
  ruleId: string
  control: ControlArea
  version: string
  description: string
  tolerance: string
  severity: Severity
  effectiveFrom: IsoDate
}
export interface SourceConfig {
  sourceName: string
  schemaVersion: string
  expectedByLocalTime: string
  mandatory: boolean
}
export type Role = 'CONTROL_ANALYST' | 'CONTROL_OWNER' | 'APPROVER' | 'AUDIT_REVIEWER' | 'ADMIN'
export interface UserAccess {
  id: string
  name: string
  email: string
  roles: Role[]
  legalEntityIds: string[]
}
export interface SettingsData {
  legalEntity: LegalEntity
  rules: RuleConfig[]
  sources: SourceConfig[]
  users: UserAccess[]
  freshnessPolicy: string
  approvalPolicies: { name: string; description: string }[]
}
