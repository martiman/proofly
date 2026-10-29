# Proofly Control API (draft 0.1)

Contract: [`openapi.yaml`](./openapi.yaml) (OpenAPI 3.1, 54 operations). Validate with
`npx --yes @redocly/cli lint docs/integrations/api/openapi.yaml`.

The contract is derived from the frontend mock (`apps/web/src/api/types.ts`, `service.ts`) and the
screen docs, and adds what the screens had to skip. Field names follow the frontend types unless listed
under "New in the real API vs mock".

## Conventions

- **Base path and versioning.** `/api/v1`. Additive changes (new optional fields, enum values, endpoints)
  are non-breaking; clients must ignore unknown fields and tolerate new enum values. Breaking changes
  go to `/api/v2`. `GET /me` returns `apiVersion`.
- **Money.** `{ "amount": "-50.00", "currency": "EUR" }`. Amount is a decimal string (regex
  `^-?\d+(\.\d{1,6})?$`), never a JSON number. Aggregates are returned per currency, never summed across
  currencies. Query filters on value (`valueMin`, `valueMax`) are decimal strings and require `currency`.
- **JSON maps.** Keys of JSON objects used as maps (`bySeverity`, `byStatus`, `byControl`,
  `permissionsByLegalEntity`, `fields`) are data and are never re-cased: enum keys keep their wire names
  (`CRITICAL`, `independent_ledger`), entity ids keep their spelling. Property names are camelCase.
- **Nullable fields.** A field the server cannot know is `null` (or omitted when optional); the server never
  substitutes a made-up value (`""`, `0`, midnight, the start time, `"system"`). The nullable fields are listed
  under "Nullable fields" below.
- **Dates.** `date` = `YYYY-MM-DD` (business/value dates); `date-time` = RFC 3339 with offset.
- **Run-version scoping.** All control data belongs to a run version `runId`. Run-scoped resources live
  under `/runs/{runId}/...`. Reprocessing creates a new run (`version+1`) and marks the old one
  `SUPERSEDED`; commands against a superseded run fail with 409 `run-superseded`.
  Exceptions, adjustments, match groups and deliveries are addressable by id outside the run path
  because their ids are globally unique and carry `runId` in the body.
- **Pagination.** Cursor-based: `?limit=25&cursor=...` (max 200). Response is
  `{ "items": [...], "page": { "nextCursor", "hasMore", "total?" } }`. `nextCursor` is always present (null on the
  last page). `total` is omitted, never null, unless `includeTotal=true` (declared on every cursor-paged list:
  listRuns, listDeliveryRecords, listExceptions, listTransactions, listMatchGroups, listAdjustments).
  Sorting is stable and defined per endpoint (`sort`). Aggregates (totals, per-currency sums, counts by
  severity/status) come from dedicated summary objects computed server-side over the full filtered set,
  never from the current page.
- **Errors.** RFC 9457 `application/problem+json` with `type`, `title`, `status`, `detail`, `requestId`,
  and `errors[]` (JSON pointer / parameter + message) for 422. Problem types used:
  `unauthenticated` (401); `forbidden`, `out-of-scope`, `separation-of-duties` (403); `not-found` (404);
  `invalid-state`, `record-already-used`, `run-superseded`, `stale-signoff`, `stale-proposal`, `idempotency-in-progress`, `duplicate` (409);
  `precondition-failed` (412); `validation` (422); `precondition-required` (428); `internal` (500).
  A problem `type` is `https://proofly.example/problems/<slug>`.
  Statuses: 401 unauthenticated, 403 not permitted / out of scope / separation of duties,
  404 not found or outside scope (do not leak existence), 409 state conflict, 412 stale `If-Match`,
  422 validation (e.g. missing reason), 428 `If-Match` missing.
- **Optimistic concurrency.** Single-resource GETs return `ETag`. Every mutation of an existing resource
  requires `If-Match`. Mismatch returns 412 and the client re-reads. List items for exceptions carry an
  `etag` field so bulk commands do not need a read per item.
- **Idempotency.** Every command (POST that changes state) requires `Idempotency-Key` (UUID). Replays
  within 24 h return the stored response (same status, `Idempotent-Replayed: true`); the same key with a
  different body returns 422; a request still in flight returns 409 `idempotency-in-progress`.
- **Request correlation.** Clients send `X-Request-Id` (UUID); the server echoes it, logs it, and stores it
  on audit events (`AuditEvent.requestId`). Responses always include it. (Declared as a reusable
  parameter/header component, not repeated on every operation.)
- **Auth and permissions.** OIDC bearer token. Roles and legal-entity scope are resolved server-side;
  `GET /me` returns `roles`, `legalEntityIds`, `permissions` (and optionally per-entity). Each operation
  carries `x-required-permission` (and optionally `x-required-permission-any: [..]`: the caller needs any one of them and the handler enforces the exact one, used by `decideConfigChange`); the server checks permission **and** legal-entity scope on every call.
  Object-level `allowedActions` / `canDecide` flags are computed per caller so the UI never has to
  re-implement separation-of-duties rules (it may still hide actions based on `permissions`).
- **Material decisions and audit.** Every command that is a material decision requires `reason`
  (3-4000 chars, 422 if absent) and writes an audit event named in `x-audit-event`. Approvals record
  object, proposal version, proposer, approver, decision, rationale, evidence refs, policy version
  (`Approval`). Changing a proposal invalidates the approval. Proposer cannot decide their own proposal
  (403 `separation-of-duties`). Original control results are never overwritten by resolutions.
- **Audit actor.** `actor` is `{type: USER|SYSTEM|SERVICE_ACCOUNT, id, name}` on audit events and timelines.
- **Evidence access.** Source records and downloads need `source.view` / `evidence.export`; downloads
  are themselves audited (`docs/security/data-handling.md`).

## Permission vocabulary

`x-required-permission` values (see `components.schemas.Permission`). Suggested role mapping, from
`docs/security/roles-and-approvals.md`:

| Role | Permissions |
| --- | --- |
| ANALYST | auth.self, run.view, source.view, source.retry, exception.view/assign/investigate/propose/reopen, transaction.view, matching.view/propose, ledger.view, safeguarding.view, safeguarding.adjustment.propose, signoff.view, signoff.prepare, evidence.view, approval.view |
| APPROVER | analyst set + run.reprocess, source.supersede, exception.approve, safeguarding.adjustment.approve, signoff.decide, evidence.export, audit.view, settings.view |
| CONFIG_ADMIN | auth.self, run.view, settings.view/draft/approve, approval.view (cannot approve own change) |
| SECURITY_ADMIN | auth.self, run.view, access.manage, settings.view, approval.view, audit.view |
| VIEWER | read-only: auth.self, run/source/exception/transaction/matching/ledger/safeguarding/signoff/evidence/approval `.view`, audit.view, evidence.export |

`settings.approve` is granted "as configured" and never lets a user approve their own change.

## Identifiers

One scheme everywhere (transactions, matching, ledger, lineage, evidence, source records). Ids are opaque to
clients; the structure is documented so the same record is addressed the same way from every screen.

| Id | Form | Example | Notes |
| --- | --- | --- | --- |
| `transactionId` | `<prefix>-<n>`, `n` = bigint primary key of the canonical record | `TRF-4` | Prefix by record table: `TRF` transfer instruction, `DD` direct debit event, `CRD` card event, `BNK` bank transaction, `LED` ledger entry. Accepted by `getTransaction`, `getTransactionLineage`, `getMatchingWorkbench?transactionId=`, `MatchCandidate.transactionId`, `Transaction.id`, `LedgerEntry.id` (`LED-n`), `proposeManualMatch`. |
| `recordId` | `<sourceName>:<n>` for an accepted record, `rejected:<n>` for a rejected row | `transfer_instructions:4`, `rejected:12` | `sourceName` is the manifest source name (`transfer_instructions`, `direct_debit_events`, `card_transactions`, `banking_transactions`, `internal_ledger_entries`, `client_account_balances`, `safeguarding_balances`, `card_settlements`); `n` is the same primary key as in the transaction id, so `TRF-4` has `recordId` `transfer_instructions:4`. For `rejected:<n>`, `n` is the rejection row id. Accepted by `getSourceRecord`; emitted in `SourceRecord.recordId`, `Transaction.recordId`, `LedgerEntry.recordId`, `EligibleBalanceLine.recordId`, `EvidenceNode.recordId`. |
| `deliveryId` | the manifest `delivery_id` | `DEL-TRF-01` | |
| `exceptionId`, `runId`, `matchGroupId`, `adjustmentId` | server issued, globally unique | `F-019` | |
| lineage node ids | the transaction id, `DLV-<deliveryId>`, `EXC-<exceptionId>` | | |
| `getLineage` subject | `TRANSACTION`: transaction id; `RECORD`: `recordId` of an accepted record; `EXCEPTION` / `CONTROL_RESULT`: exception id (or `EXC-<id>`) | `/lineage/TRANSACTION/TRF-4` | Unknown, malformed, or outside the optional `runId` or the caller's scope: 404. An unknown `subjectKind`: 422. |

`Transaction.sourceRowId` is the source file's own row key (`source_row_id`, `ledger_entry_id`,
`external_transaction_id`); it is not an API id. `Transaction.deliveryId` plus `SourceRecord.rowNumber` locate
the row in the file.

## Nullable fields

The contract states these as nullable; the server returns `null` instead of a fallback.

| Field | Null when |
| --- | --- |
| `Run.lastEvidenceArrivalAt` | no delivery has arrived for the run |
| `Run.requiredApprover` | the approval policy has not named the approver (set when the run is submitted for sign-off) |
| `SourceDelivery.declaredRecordCount` | the manifest declares no count |
| `SourceDelivery.expectedBy` | no expected-by time is configured for the source |
| `SourceRecord.rowNumber` | the record was ingested before row tracking (migration `0020`); rows ingested since always carry it |
| `SourceRecord.extractionTimestamp`, `sourceTimeZone` | not recorded (always null today) |
| `ExceptionSummary.sourceFile`, `sourceRowId` | the exception is not tied to a file row |
| `EvidenceNode.sourceRowId` | a control-result node not tied to one row |
| `RuleTrace.tolerance` | the control recorded none (and the trace is derived) |
| `Lineage.deliveries[].rowNumber` | row tracking unavailable for the record |
| `MatchingWorkbench.focusTransactionId` | the run has no match group to focus, or the focused exception has no transaction |
| `MatchGroup.ruleVersion` | manual match without a rule |
| `MatchGroup.tolerance`, `rationale` | the control recorded none |
| `MatchCandidate.timestamp`, `reference` | the canonical record is no longer present |
| `Account.openingBalance`, `recordedClosingBalance`, `independentClosingBalance`, `difference` | the control could not compute them (e.g. opening balance unsupported or recorded balance missing) |
| `Actor.id` | SYSTEM actors |
| `Actor.name` | no display name recorded |
| `EvidencePack.id` | status `NOT_GENERATED` (no pack exists yet) |

Optional members that are omitted when absent: `ObligationLine.movementType`, `MatchCandidate.score` (null),
`ControlRoom.summary[].link`, `ProcessingStep.actor`/`message`, `CheckResult.detail`.

## Rules

- **Transaction direction.** `Transaction.direction` is the booking direction on the client account. Card, bank
  and ledger records carry their own `DEBIT`/`CREDIT` indicator. A transfer instruction (`BANK_TRANSFER`) is
  always `DEBIT` of the client account and a direct debit event (`DIRECT_DEBIT`) is always `CREDIT`, whatever its
  event type. `TransactionPage.aggregates` (`debitTotal`, `creditTotal`) use this direction.
- **Account balances.** `Account.difference` = independent closing balance minus recorded closing balance
  (positive when the independent figure is higher). The balances come from the independent-ledger control's
  persisted result (`account_control_results`); while a run has no persisted result the server recomputes the
  independent balance from POSTED ledger entries (documented fallback, identical difference sign). An account may
  hold several currencies: `getAccountLedger` takes an optional `currency`; without it the account must have exactly
  one currency in the run, otherwise 422 on `currency`.
- **Control record counts.** `ControlAreaSummary.recordCount` per area: `data_completeness` = source rows received
  by the run's deliveries (accepted plus rejected), `matching` = match groups, `independent_ledger` = ledger
  entries, `transaction_lifecycle` = transfer, direct debit and card events, `safeguarding` = currency positions.
  `previousRecordCount` is the same count for the previous business date's run and `changeFromPrevious` the
  difference (0 when there is no previous run). An area with neither records nor exceptions is omitted from
  `ControlRoom.controlAreas` rather than reported as PASSED.
- **Control names.** `exceptions.control` and every `ControlArea` value use the lower snake names
  `data_completeness`, `matching`, `independent_ledger`, `transaction_lifecycle`, `safeguarding` (enforced by a
  CHECK constraint).
- **Approvals.** `Approval.objectId` is the resolution *proposal* id for `EXCEPTION_RESOLUTION` and the id of the
  object itself for every other object type (adjustment, match group, run, config change).
- **Match groups.** `cardinality` is `<left>:<right>` with counts `0`, `1` or `N` (more than one). `exceptionIds`
  are the exceptions explicitly linked to the group (`match_group_exceptions`) plus those derived from its
  candidates' records. `stats.autoMatchRate` is automatically matched groups / all groups as a decimal string.
- **Exception links.** `Transaction.exceptionIds` and the account/ledger exception lists combine the exceptions
  matched by delivery and source row with explicit record links (`exception_links`).
- **Evidence pack status.** Stored `GENERATING`/`READY`/`FAILED` map to `GENERATING`/`GENERATED`/`FAILED`;
  `NOT_GENERATED` is reported only while no pack row exists (a queued or dead generation job still reports
  `GENERATING`/`FAILED` in that window). `sizeBytes` and `contents` are stored with the pack.

## Stored payload shapes

JSON columns the read side interprets. The control orchestration writes them; fixtures must follow them.

**`safeguarding_positions.detail`** (object; every key optional, missing = none):

```json
{
  "approvedAdjustments": "25.500000",
  "qualifications": ["stale balance"],
  "obligationLines": [{"accountId": "CA-1", "amount": "100.00", "movementType": "CLIENT_FUNDS"}],
  "balanceLines": [{"sourceRowId": "row-1", "included": true, "reason": "ELIGIBLE", "findingIds": ["F-1"]}]
}
```

`approvedAdjustments` is a decimal string (a snapshot; when absent the live sum of APPROVED, unexpired
adjustments of the legal entity and business date, created in this or an earlier run version, is used). `obligationLines[].movementType` is optional. A `safeguarding_balances` row without a
`balanceLines` entry is reported with `included: false` and reason `NOT_EVALUATED` (the control has not evaluated it).
Amount, account, timestamp and evidence of a balance line come from `safeguarding_balances`, not from the detail.

**`safeguarding_adjustments`**: `evidence_refs` (jsonb array, at least one entry, checked by the database) and
`expires_on` (date; an APPROVED adjustment counts while `expires_on` is null or not before the business date).

**Adjustments belong to (legal entity, business date), not to one run version.** `safeguarding_adjustments.run_id` is the
provenance (the version it was created in). Run processing loads every adjustment of the run's legal entity and business date that was
created in this or an earlier version, so an approved, unexpired adjustment stays valid after a reprocess and the new version's
position `approvedAdjustments` includes it; the expiry rule and the `stale` flag are unchanged. A superseded or signed-off version is never
recomputed: it keeps the snapshot it was calculated with (its position `approvedAdjustments`, its result hash and its evidence pack), and a
version only lists adjustments created in itself or earlier versions. A pending proposal of a superseded version can still be decided; the
decision flags the position of the current version `stale` (and is refused with `run-locked` when that version is signed off). An approved
adjustment that expires flags the current version's position the same way.

**`config_rules`** (`id`, `kind`, `name`, `config_version`, `enabled`, `payload`; disabled rows are not listed):

| `kind` | id/name/version map to | `payload` |
| --- | --- | --- |
| `rule` | `RuleConfig.ruleId` = id, `version` = config_version, `description` falls back to `name` | `{"control": "<ControlArea>", "description": "...", "tolerance": "0.01 GBP", "severity": "HIGH", "effectiveFrom": "2026-01-01"}` |
| `source` | `SourceConfig.sourceName` = name, `schemaVersion` falls back to config_version | `{"schemaVersion": "1.2", "expectedByLocalTime": "08:00", "mandatory": true}` |
| `approval_policy` | `ApprovalPolicy.name` = name, `version` = config_version | `{"description": "Second person", "requiredRole": "APPROVER"}` |
| `setting` (id `freshness_policy`) | `Settings.freshnessPolicy` | `{"text": "Evidence must arrive by 09:00 local"}` |

**`config_changes`**: `status` is the contract `ConfigChangeStatus` (`DRAFT`, `PENDING_APPROVAL`, `APPROVED`,
`ACTIVE`, `REJECTED`, `RETIRED`). `change_kind` is `CONFIG` (targets a `config_rules` row via `config_id`) or
`ACCESS` (targets a user via `target_user_id`, no `config_id`). `Settings.pendingChanges` counts PENDING_APPROVAL
changes of either kind; `UserAccess.pendingChangeId` is the pending access change of that user.

**Configuration changes (#45)**: `proposeConfigChange` stores `config_changes` (`target_kind`, `target_id`, `effective_from`, `before`/`after` = the
mutable keys of the payload table above, `base_version` = `config_rules.version` at proposal) plus an `approvals` row (`CONFIG_CHANGE` ->
`CONFIG_ADMIN`, `ACCESS_CHANGE` -> `SECURITY_ADMIN`). `after` is validated per kind and unknown keys are rejected: `RULE` needs
`control, description, tolerance ("<non-negative decimal> <CCY>"), severity, effectiveFrom`; `SOURCE` needs `schemaVersion, expectedByLocalTime (HH:mm),
mandatory`; `APPROVAL_POLICY` needs `description` (+ optional `requiredRole`); `USER_ACCESS` needs `roles` and `legalEntityIds` (applied as their cross
product to `user_roles`). Read-only echoes of the GET shape (`ruleId`, `version`, `pendingChangeId`, ...) are tolerated and not stored. Approval
(different user, `settings.approve`; `access.manage` for access changes) sets the change `ACTIVE`, writes the target, bumps `config_rules.config_version`
to the new ruleset version and inserts a `ruleset_versions` row (`v<n>`, `activated_at`, `effective_from = max(requested, today)`, snapshot of all
enabled `config_rules`); the previous ACTIVE change of the target becomes `RETIRED`. Orchestration must set `runs.ruleset_version` at run start from
`IRulesetResolver.ResolveAsync(now, tx)` (latest ruleset with `activated_at <= now` and `effective_from <= UTC date`); a change therefore applies to
the next run only and never to a run that already started.

`runs.ruleset_version` is `<config ruleset version>+<control rule versions>` (for example `v2+proofly-controls/1[...]`); the result hash does not include
the configuration version, so a re-run under a new ruleset has the same hash unless a parameter changed the outcome. Control parameters are read from the
resolved snapshot by rule id (`kind = rule`, others ignored, unset = code default): `MATCH-AMOUNT-TOLERANCE` (tolerance per currency, severity of value
mismatches), `MATCH-UNMATCHED` / `MATCH-AMBIGUOUS` (severity), `LEDGER-BALANCE-TOLERANCE` (tolerance amount), `LIFECYCLE-DEFAULT` / `LIFECYCLE-TIMING`
(severity), `SAFEGUARDING-STALE` (severity). Windows, blocking flags and stale/eligibility thresholds have no payload key yet and stay code defaults.
A snapshot value that does not parse fails the run visibly.

**`exception_rule_traces`** (one row per exception, written when the control raises it): `rule_id`,
`rule_version`, `tolerance`, `expected_effect`, `description` and `calculation` =
`{"inputs": [{"name": "...", "value": "..."}], "text": "...", "result": "..."}`. `getException` reads it; without a
row it derives the trace from the exception and ruleset (tolerance null).

**`account_control_results`** (one row per run, account and currency): the independent-ledger control's
`IndependentBalance` (opening, credits, debits, adjustments, independent, recorded, difference, opening_supported,
contributions, exclusions, rule_version). `contributions`/`exclusions` are stored for audit and not part of the API.

**`legal_entities.currencies`**: currencies the entity holds client funds in; always contains the base currency.

## Frontend service function to operationId

| Frontend function | operationId | Method and path |
| --- | --- | --- |
| listLegalEntities | listLegalEntities | GET /legal-entities |
| listRuns | listRuns | GET /runs |
| getRun | getRun | GET /runs/{runId} |
| getControlRoom | getControlRoom | GET /runs/{runId}/control-room |
| listSourceDeliveries | listSourceDeliveries | GET /runs/{runId}/deliveries |
| getSourceDelivery | getSourceDelivery | GET /deliveries/{deliveryId} |
| listSourceRecords | listDeliveryRecords | GET /deliveries/{deliveryId}/records |
| listExceptions | listExceptions | GET /runs/{runId}/exceptions |
| getException | getException | GET /exceptions/{exceptionId} |
| assignException | assignException | POST /exceptions/{exceptionId}/assign |
| addExceptionNote | addExceptionNote | POST /exceptions/{exceptionId}/notes |
| requestEvidence | requestExceptionEvidence | POST /exceptions/{exceptionId}/request-evidence |
| proposeResolution | proposeResolution | POST /exceptions/{exceptionId}/resolution-proposals |
| decideResolution | decideResolution | POST /exceptions/{exceptionId}/resolution-proposals/{proposalId}/decision |
| reopenException | reopenException | POST /exceptions/{exceptionId}/reopen |
| listTransactions | listTransactions | GET /runs/{runId}/transactions |
| getTransaction | getTransaction | GET /runs/{runId}/transactions/{transactionId} |
| getMatchingWorkbench | getMatchingWorkbench | GET /runs/{runId}/matching/workbench |
| listMatchGroups | listMatchGroups | GET /runs/{runId}/match-groups |
| listAccounts | listAccounts | GET /runs/{runId}/accounts |
| getAccountLedger | getAccountLedger | GET /runs/{runId}/accounts/{accountId}/ledger |
| getSafeguardingPosition | getSafeguardingPosition | GET /runs/{runId}/safeguarding |
| proposeAdjustment | proposeAdjustment | POST /runs/{runId}/safeguarding/adjustments |
| decideAdjustment | decideAdjustment | POST /safeguarding/adjustments/{adjustmentId}/decision |
| getRunSignOff | getRunSignOff | GET /runs/{runId}/sign-off |
| returnRun | returnRun | POST /runs/{runId}/sign-off/return |
| signOffRun | signOffRun | POST /runs/{runId}/sign-off/approve |
| listAuditEvents | listAuditEvents | GET /audit-events |
| getEvidencePack | getEvidencePack | GET /runs/{runId}/evidence-pack |
| generateEvidencePack | generateEvidencePack | POST /runs/{runId}/evidence-pack |
| getSettings | getSettings | GET /settings |

## New in the real API vs mock

Endpoints with no mock equivalent (the mock lacked them, so the screens skipped the feature):

| operationId | Why |
| --- | --- |
| getMe, `Me` schema | Role, legal-entity scope and effective permissions drive what UI shows; mock hard-coded one actor. |
| requestRunReprocess, compareRuns | Re-run after late/corrected evidence creates a superseding run version; compare shows what changed between versions. |
| getDeliveryProcessingHistory | Sources screen needs processing history per delivery. |
| retryDeliveryIngestion, markDeliverySuperseded | Source operations that screens could not perform. Supersession is a link; the original is never overwritten. |
| getSourceRecord | Evidence record preview from exception/transaction/lineage views. |
| getExceptionsSummary | Server-side totals: counts and per-currency sums over the full filtered set, not the visible page. |
| bulkAssignExceptions | Bulk assign in the queue; per-item ETag and per-item result (207 on partial success). |
| listExceptionViews, createExceptionView, deleteExceptionView | Saved views (filters and sort), personal or shared. |
| getTransactionLineage, getLineage | Lineage from normalised event to delivery, record location, fingerprint, mapping and schema version. |
| proposeManualMatch | Material manual matches need a proposal and approval (roles doc). |
| listAdjustments | Adjustments listed independently of the full position. |
| submitRunForSignOff | Preparer hands the run to the required approver; fixes preparer and approver for separation of duties. |
| downloadEvidencePack | Audited export. |
| listConfigChanges, getConfigChange, proposeConfigChange, decideConfigChange | Change history, pending changes and four-eyes activation of material config and access changes. |
| listApprovals | Inbox of approvals awaiting the caller across object types. |

Changes to existing shapes (all additive unless stated):

| Change | Why |
| --- | --- |
| Pagination: `page`/`pageSize`/`total` replaced by cursor `limit`/`cursor` and `page{nextCursor,hasMore,total?}`. **Breaking for the mock client.** | Stable under concurrent inserts, avoids expensive counts; `total` is opt-in. |
| Collection routes for run-scoped data nested under `/runs/{runId}` (transactions, accounts, exceptions, safeguarding, sign-off). Mock had `/exceptions`, `/transactions`. | Run-version scoping. Exception detail and commands stay at `/exceptions/{id}`. |
| `listSourceRecords(sourceName, ...)` becomes `listDeliveryRecords(deliveryId)` with `accepted` filter instead of `rejectedOnly`. | A record belongs to a delivery; `sourceName` is ambiguous across deliveries/versions. |
| `ExceptionFilter`: `severity/control/status` accept lists; `owner` becomes `ownerId` (+ `unassigned`); added `blocking`, `legalEntityId`, `valueMin/Max`, `businessDateFrom/To`, `ageDaysMin/Max`, `dueBefore`, `savedViewId`, `sort`. | Filters by date, age, value range, entity and saved views required by the queue. |
| `ExceptionSummary.owner`: string becomes `{id,name}` or null; added `runId`, `createdAt`, `ageDays`, `etag`. **Breaking.** | Stable owner identity, run scoping, age filter, bulk commands. |
| `ExceptionDetail` adds `pendingApproval`, `approvals[]`, `allowedActions[]`. | Approval workflow with server-evaluated separation of duties. |
| Commands: all require `If-Match` + `Idempotency-Key`; resolution/adjustment/sign-off/reopen/return/reprocess/retry/supersede/config decisions require `reason`. `decideResolution(id, approve, kind)` becomes `{decision: APPROVE\|REJECT, reason, proposalVersion}` against a proposal id; the kind comes from the proposal. | Audit, concurrency, replay safety, and approval records (proposal version). |
| `proposeResolution` `reason` must be 3+ chars; `evidenceRefs` required. | Mock only checked non-empty reason. |
| `Run` adds `supersedesRunId`, `supersededByRunId`, `preparer`, `requiredApprover`, `signedOffBy`, `signedOffAt`. | Sign-off screen needs preparer and required approver; version chain. |
| `RunSignOff` adds `qualifications`, `attestationTextHash`, `approval`, `history`; `canSignOff` evaluated for the caller. | Approver signs the exact text shown; stale sign-off detection (409). |
| `signOffRun(runId, attestation)` becomes `{attestation: true, attestationTextHash, reason}`; `returnRun` unchanged apart from headers. | Material decision needs reason; text integrity. |
| `SourceDelivery` adds `supersededByDeliveryId`; new `SourceDeliveryDetail` adds `fingerprint`, `sequenceNumber`, `extractionTimestamp`, `sourceTimeZone`, `checks[]` (fingerprint/duplicate/sequence/schema/count/totals), `actualTotals`, `allowedActions`. `declaredTotals` / `actualTotals` are lists of `Money`, one entry per currency (any ISO 4217 code; no fixed EUR/CZK); a currency measured but not declared appears only in `actualTotals`. | Sources screen detail; matches `docs/integrations/input-contracts.md`. |
| `SourceRecord` adds `recordId`, `rejectionReasons`, `contentFingerprint`; `EvidenceNode`, `Transaction`, `LedgerEntry`, `EligibleBalanceLine` add `recordId` (and `deliveryId` on Transaction). | Link any displayed row to the evidence record preview. |
| `Transaction.matchStatus` adds `PENDING_APPROVAL`; `MatchGroup` adds `manual`, `approval`. | Manual matches awaiting approval. |
| `TransactionPage.aggregates` and `AccountPage.aggregates` (per currency). | Server-side totals. |
| `SafeguardingAdjustment.owner`: string becomes `{id,name}`; adds `runId`, `expiresOn`, `approval`. `AdjustmentInput` requires at least one `evidenceRefs`. **Breaking (owner).** | Identity, expiry status `EXPIRED`, approval record. |
| `AuditEvent.actor`: string becomes `{type,id,name}`; adds `reason`, `requestId`, `previousHash`; `runId` nullable; `subjectKind` and `type` enums extended (ADJUSTMENT, SIGN_OFF, CONFIG, MATCH_GROUP; resolution decided, config, access, retry, supersede, download, etc.). Filters add `actorId`, `actorType`, `runId`, `from`, `to`. **Breaking (actor).** | Audit actor type, hash chain, correlation. |
| `TimelineEvent.actor`: string becomes `Actor`. **Breaking.** | Same as audit. |
| `EvidencePack.status` adds `GENERATING`, `FAILED`; adds `sha256`, `sizeBytes`; generate returns 202. | Async generation, integrity. |
| `Role` is `ANALYST`, `APPROVER`, `CONFIG_ADMIN`, `SECURITY_ADMIN`, `VIEWER`; mock `ADMIN` dropped (ADR 0005). | Fixed role bundles. |
| `Settings` adds `pendingChanges`; `RuleConfig`/`SourceConfig`/`UserAccess` add `pendingChangeId`; `ApprovalPolicy` adds `requiredRole`, `version`. | Pending configuration changes visible in the settings screen. |
| `Page<T>` for `listRuns`; `listLegalEntities` stays an array. | Runs grow unbounded. |
| `ControlRoom`, `ControlAreaSummary`, `Account`, `Money`, `SafeguardingPosition` and the remaining types unchanged (Money amount regex allows up to 6 dp; mock used 2). | Compatibility. |

## Changelog (reconciliation of the read API, migration `0020`)

Changes against the previous contract text (the 0.1 draft as implemented by the first read endpoints). All are
additive or relax a type to nullable except where marked **breaking for a typed client**.

| # | Change |
| --- | --- |
| G1 | `PageInfo.total` is omitted (never null) unless `includeTotal=true`; documented. No schema change. |
| G2 | JSON map keys are never re-cased (enum keys keep their wire names); documented. No schema change. |
| G3 | Problem slugs `unauthenticated`, `not-found`, `precondition-failed`, `precondition-required`, `internal` documented in README and in the shared response descriptions. |
| G4 | `includeTotal` declared on `listRuns`, `listDeliveryRecords`, `listMatchGroups` (and the new paging of `listAdjustments`). |
| G5 | Identifier scheme documented (transaction ids `TRF-`/`DD-`/`CRD-`/`BNK-`/`LED-` + n; record ids `<sourceName>:<n>`, `rejected:<n>`). `Transaction.recordId`, `LedgerEntry.recordId` and `EligibleBalanceLine.recordId` now use it (was `{deliveryId}/{sourceRowId}` and a bare number); matching `transactionId` now uses the transaction scheme (was `{table}:{id}`). **Breaking for clients that parsed the old forms.** |
| G6 | Nullable: `Run.lastEvidenceArrivalAt`, `SourceDelivery.expectedBy`, `declaredRecordCount`, `ExceptionSummary.sourceFile`/`sourceRowId`, `SourceRecord.rowNumber`, `EvidenceNode.sourceRowId`, `Actor.id`/`name`, `MatchingWorkbench.focusTransactionId`, `MatchGroup.ruleVersion`/`tolerance`/`rationale`, `MatchCandidate.timestamp`/`reference`, `RuleTrace.tolerance`, `EvidencePack.id`, `Account.openingBalance`/`recordedClosingBalance`/`independentClosingBalance`/`difference`. New optional nullable `SourceRecord.extractionTimestamp`/`sourceTimeZone`. A workbench for an empty run or an ungrouped transaction is 200 with `groups: []` (was 404). |
| G7 | `SourceRecord.rowNumber` and lineage `deliveries[].rowNumber` come from the new `row_number` column filled at ingestion. |
| G8 | `MatchGroup.cardinality` added. `MatchCandidate` gains `kind`, `side`, `score`, `selected`. `MatchingWorkbench.stats` added (`autoMatchRate`, `openGroups`, `unmatchedCount`, `unmatchedByCurrency`). |
| G9 | `EvidencePack.failureReason` added; `sizeBytes`/`contents`/status stored with the pack row. |
| G10 | `listAdjustments` is cursor paged: response `AdjustmentPage {items, page}` (was `{items}`), parameters `limit`, `cursor`, `includeTotal`. `ObligationLine.movementType` optional. `safeguarding_positions.detail` and adjustment storage documented. |
| G11 | `getAccountLedger` takes optional `currency` (422 when ambiguous). `Account.difference` is independent minus recorded (the previous implementation returned recorded minus independent: **sign flip**). Balances read from `account_control_results` with a documented recompute fallback. |
| G12 | `RuleTrace` read from `exception_rule_traces`; derived trace has `tolerance: null` (was the string `not recorded`). |
| G13 | `ExceptionsSummary.byReason` and `bySeverityCurrency` added. |
| G14 | `Transaction.direction` rule documented and applied (`BANK_TRANSFER` = DEBIT, `DIRECT_DEBIT` = CREDIT; was null) and used by aggregates. Explicit `exception_links` feed `exceptionIds`. |
| G15 | `config_rules.payload` shapes documented; `config_changes.status` aligned with `ConfigChangeStatus`; access changes supported (`change_kind`); `UserAccess.pendingChangeId` is now populated. |
| G16 | `LegalEntity.currencies` comes from `legal_entities.currencies` (always includes the base currency). |
| G17 | Control names documented and enforced; `approvals.object_id` semantics documented. |
| G18 | `ControlAreaSummary.recordCount` and omission of empty areas documented. |

## Changelog (second contract pass)

All additive or relaxations except where marked **breaking for a typed client**.

| # | Change |
| --- | --- |
| H1 | `deleteExceptionView` declares 428; `SavedView.etag` added (the `If-Match` value for the delete). |
| H2 | `assignException` / `bulkAssignExceptions` accept optional `reason` (min 3); it is the audit reason, else the note. |
| H3 | `createExceptionView` documents 409 (`duplicate` for a repeated name, `invalid-state` for the per-user limit). **Breaking: both were 422 before.** |
| H4 | `ResolutionProposalInput.evidenceRefs` has `minItems: 1`. |
| H5 | Problem slug `stale-proposal` (409) for a stale `proposalVersion` in decideResolution, decideAdjustment, decideConfigChange, decideManualMatch (was `invalid-state`). **Breaking for clients that matched the slug.** |
| H6 | Audit event types `ADJUSTMENT_EXPIRED`, `RUN_PROCESSED`, `SIGN_OFF_INVALIDATED`, `EVIDENCE_ACCESS_DENIED`. |
| H7 | `RunComparison.changedExceptionIds`; `ConfigChange.rulesetVersion` (nullable). |
| H8 | New extension `x-required-permission-any: [..]` (the caller needs any one; `x-required-permission` stays as the primary). `decideConfigChange` lists `settings.approve` and `access.manage`; the handler enforces `settings.approve` for CONFIG and `access.manage` for ACCESS changes. |
| H9 | `SafeguardingCurrencyPosition.result` is nullable (null for a QUALIFIED currency); `stale` boolean on `SafeguardingCurrencyPosition` and `SafeguardingPosition`. **Breaking for a typed client reading `result.amount`.** |
| H10 | `getLineage` implemented (no contract change). `listDeliveryRecords` for a delivery whose source is not a manifest source is 404 (the records are not addressable); `RunState` `FAILED` is reported for a run whose first processing attempt failed. |

## Changelog (bounded response bodies, issue found by the 100k spike)

The 100,000-transaction spike measured a 77 MB sign-off response, 8.9 MB safeguarding position and 3 MB control room (see
[performance-spike.md](../../testing/performance-spike.md)). Every list in those three responses is now bounded; the full lists are paged
operations. Additive unless marked **breaking for a typed client**.

| # | Change |
| --- | --- |
| I1 | `getRunSignOff`: `openMatters` is capped at `openMattersLimit` (query, default 50, max 200) in the order blocking first, severity, absolute value descending, id. New `openMattersTotal` and `openMattersByCategory` (`OpenMatterCategory {control, total, blocking}`). **Breaking for a client that assumed `openMatters` is the full list.** |
| I2 | New `listRunOpenMatters` (`GET /runs/{runId}/sign-off/open-matters`, `signoff.view`): the same order, cursor paged, filters `blocking` and `severity`, `includeTotal`. |
| I3 | `ChecklistItem.exceptionIds` lists at most `openMattersLimit` ids (first in open-matter order); new `ChecklistItem.exceptionCount` is the full number. **Breaking for a client that treated `exceptionIds` as complete.** |
| I4 | `getSafeguardingPosition`: `obligations` and `eligibleBalances` hold the top `linesLimit` (query, default 50, max 200) lines per currency by absolute amount; new `SafeguardingCurrencyPosition.obligationLinesTotal` / `balanceLinesTotal`. Positions, adjustments and change analysis are unchanged. **Breaking for a client that summed or listed the lines as complete.** Order of the lines changed (largest absolute amount first; was currency, account). |
| I5 | New `listSafeguardingLines` (`GET /runs/{runId}/safeguarding/{currency}/lines?kind=obligation|balance`, `safeguarding.view`): cursor paged, `search` by account, `includeTotal`; response `SafeguardingLinePage` with `obligationLines` or `balanceLines` (the other is empty). 404 when the run has no position in that currency. |
| I6 | `getControlRoom`: each delivery in `evidenceReadiness` lists at most 20 `findingIds` and each safeguarding currency at most 20 `exceptionIds`; new `SourceDelivery.findingCount` and `SafeguardingCurrencyPosition.exceptionCount` carry the true numbers (present in the control room only). The attention queue stays at 10. **Breaking for a client that treated `findingIds` / `exceptionIds` as complete in the control room.** |

Migration `0050` adds the partial index for the open-matter order and an index for the balance-line order.

## Changelog (web client findings)

| # | Change |
| --- | --- |
| J1 | `SafeguardingAdjustment.etag` (required) and `ConfigChange.etag` (required): the strong ETag of the row version (for example `"v3"`), same format as the `ETag` header, so `decideAdjustment` and the config decide operations can send `If-Match` after a page reload. Present in list items, command responses and the embedded `adjustments` of the safeguarding position. **Breaking for a client that builds these objects by hand.** |
| J2 | `PageInfo.nextCursor` is now `required` (`string` or explicit `null` on the last page). Was already always written; generated types are `string | null`. |

## Changelog (contract batch 3, web screen migrations)

All changes are additive for a client that ignores unknown fields; new response fields are declared `required` (always written, possibly `null`) so generated types are exact. Migration `0051`.

| # | Change |
| --- | --- |
| K1 | `getExceptionsSummary` accepts exactly the `listExceptions` filters (all except `sort` and paging: `blocking`, `legalEntityId`, `valueMin`/`valueMax` with the currency rule, `ageDaysMin`/`ageDaysMax`, `businessDateFrom`/`businessDateTo`, `dueBefore`, `search`, `ownerId`, `unassigned`, `severity`/`status`/`control` lists, `currency`, `savedViewId`) and documents 422. It uses the same parser and SQL predicate as the list, so `summary.total` equals the `listExceptions` `page.total` (`includeTotal`) for the same query. |
| K2 | New `listAssignableUsers` (`GET /legal-entities/{legalEntityId}/assignable-users`, permission `exception.assign` for that entity): `[{id, name, roles}]`, active users holding `exception.investigate` there (directly or through an all-entities role), ordered by name. 404 when the entity does not exist or is outside the caller's scope, 403 without `exception.assign`. Not `settings.view`. Note: `assignException` itself still accepts any active user with a role in the entity. |
| K3 | `Approval.kind` (required, nullable): for `EXCEPTION_RESOLUTION` approvals the proposed `ResolutionKind`, `null` for every other object type. `ExceptionDetail.pendingApproval` / `approvals[]` therefore carry the pending proposal's kind (`kind`), reason (`rationale`) and `evidenceRefs`; no other `ExceptionDetail` field was missing. |
| K4 | `EvidencePack.manifest` (`schemaVersion`, `resultHash`, `files[{path, sha256, sizeBytes}]` without `manifest.json` itself, `generatedBy` (UserRef or null), `generatedAt`) and `EvidencePack.auditChain` (`valid`, `eventsChecked`, `firstBreak` = `{chainId, chainSeq, eventId, problem}` or null) are persisted when the pack becomes READY (`evidence_packs.manifest`, `audit_chain`, migration `0051`) and returned as is. `null` while GENERATING / NOT_GENERATED / FAILED and for packs generated before `0051`. |
| K5 | New `listUnmatchedRecords` (`GET /runs/{runId}/matching/unmatched`, `matching.view`): cursor paged candidates of UNMATCHED groups (order: group, candidate), filters `pair` (a `TransactionKind`: there is no other pair concept in the data), `currency`, `search` (reference, source row key, source name, or an exact transaction id), `includeTotal` (the filtered count; unfiltered it equals `stats.unmatchedCount`). `getMatchingWorkbench` gains `unmatchedLimit` (default 50, max 200, 422 outside); **the inline `unmatched` list was 200 and is now 50 by default**; the true population is `stats.unmatchedCount`. |
| K6 | `ControlRoom.blockingCount` (unresolved blocking exceptions, the sign-off gate's definition) and `openExceptionsTotal` (all unresolved exceptions), computed in SQL over the whole run (the attention queue still shows 10). |
| K7 | `RunSignOff.blockerDetails[{code, message, exceptionId, control}]` (same length and order as `blockers`, which stays a `string[]`). Codes: `RUN_STATE`, `NO_RESULT_HASH`, `COMPLETENESS_MISSING`, `COMPLETENESS_NOT_PASSED`, `BLOCKING_EXCEPTION` (`exceptionId` and `control` set). |
| K8 | `AccountLedgerView.availableCurrencies`: every currency the account holds in the run (the shown one included), sorted. |
| K9 | `Me.environment {name, kind: DEV|TEST|PILOT|PRODUCTION}` from configuration `Environment:Name` / `Environment:Kind`. Outside Production the defaults are the host environment name and `DEV`; Production refuses to start without both (startup self-check), and a present `Kind` must be one of the four values in every environment. |
| K10 | Not a contract change: `audit.view` (Evidence & Audit) is deliberately **not** in the ANALYST bundle; it is held by VIEWER, APPROVER and SECURITY_ADMIN. Analysts work exceptions and sign-off preparation; the audit trail is reviewed by independent roles. See the backend README. |

## Changelog (backend fixes from web QA and end-to-end runs)

Migration `0052`. Items L1-L8 change behaviour as described; none renames a field. Clients that compared money strings must compare decimals (L7).

| # | Change |
| --- | --- |
| L1 | Safeguarding adjustments are tied to (legal entity, business date); `SafeguardingAdjustment.runId` is provenance only. Approved, unexpired adjustments carry into reprocessed run versions (see "Stored payload shapes"); `listAdjustments` of a run returns those created in it and in earlier versions of the same entity and date; `decideAdjustment` of a superseded run's pending proposal applies to the current version. The old signed-off version keeps its snapshot. |
| L2 | `getMatchingWorkbench` always lists the pending manual proposals (`PENDING_APPROVAL`, at most 50) in `groups`; the pending group that contains the focused transaction or exception is the focus, and without a focus the first pending group is. `listMatchGroups` default order is PENDING_APPROVAL, then other non-MATCHED groups (status, id), then MATCHED, so "needs attention" comes first; the cursor follows that order. |
| L3 | Dev seed (`Proofly.Benchmarks --seed-dev`) installs a baseline ruleset (`BaselineRuleset`): per-currency matching tolerances (`MATCH-AMOUNT-TOLERANCE`, `MATCH-AMOUNT-TOLERANCE-EUR`, `-JPY`), ledger tolerance, severities, one source configuration per data source (expected-by time, mandatory), the four-eyes approval policy and the freshness policy, and refreshes the `v1` ruleset snapshot. Control parameters accept several `MATCH-AMOUNT-TOLERANCE-<CCY>` rules (one tolerance carries one currency). |
| L4 | `ExceptionSummary.title` is a short headline (at most 80 characters, e.g. "EUR balance difference on CA-004") and the new required `ExceptionSummary.description` is the longer text; both are written when the exception is created (`FindingText` in the controls project) and stored in `exceptions.title` / `exceptions.description`. `ExceptionDetail.description` is the stored description; `ExceptionDetail.summary` is the one-line explanation (the control's message) and never equals `description`. `description` also appears in the control room attention queue, the account ledger exceptions and the sign-off open matters (all typed ExceptionSummary). Existing rows were backfilled by migration `0052` (description = rule-trace message, title = reason label). |
| L5 | `getAccountLedger` with a `currency` the account does not hold is `422` on `currency` (the detail lists the currencies it holds); 404 only for an unknown account. |
| L6 | `listTransactions` order is (timestamp, numeric row id, id): DD-47 before DD-111; the cursor keeps the same key (old cursors stay valid). |
| L7 | `DecimalString` serialisation rule: exact value, at least 2 fraction digits, trailing zeros beyond the second removed, never rounded (`0` -> `0.00`, `1081977.250000` -> `1081977.25`, `20.123456` unchanged). Applied in one place (`DecimalString.Normalize`, used by `MoneyDto` and the JSON decimal converter). **Behaviour change for a client comparing amount strings.** |
| L8 | `ExceptionSummary` objects embedded elsewhere carry `description` too (see L4). |

## Lint

`npx --yes @redocly/cli lint docs/integrations/api/openapi.yaml` passes with no errors. The two remaining
warnings are `no-unused-components` for the `RequestId` header/parameter: `X-Request-Id` is a global
convention, so it is defined as a component but not referenced from each of the 59 operations.
