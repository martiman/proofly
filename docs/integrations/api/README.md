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
- **Dates.** `date` = `YYYY-MM-DD` (business/value dates); `date-time` = RFC 3339 with offset.
- **Run-version scoping.** All control data belongs to a run version `runId`. Run-scoped resources live
  under `/runs/{runId}/...`. Reprocessing creates a new run (`version+1`) and marks the old one
  `SUPERSEDED`; commands against a superseded run fail with 409 `run-superseded`.
  Exceptions, adjustments, match groups and deliveries are addressable by id outside the run path
  because their ids are globally unique and carry `runId` in the body.
- **Pagination.** Cursor-based: `?limit=25&cursor=...` (max 200). Response is
  `{ "items": [...], "page": { "nextCursor", "hasMore", "total?" } }`. `total` only with `includeTotal=true`.
  Sorting is stable and defined per endpoint (`sort`). Aggregates (totals, per-currency sums, counts by
  severity/status) come from dedicated summary objects computed server-side over the full filtered set,
  never from the current page.
- **Errors.** RFC 9457 `application/problem+json` with `type`, `title`, `status`, `detail`, `requestId`,
  and `errors[]` (JSON pointer / parameter + message) for 422. Problem types used:
  `forbidden`, `out-of-scope`, `separation-of-duties` (403); `invalid-state`, `run-superseded`,
  `stale-signoff`, `idempotency-in-progress`, `duplicate` (409); `validation` (422).
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
  carries `x-required-permission`; the server checks permission **and** legal-entity scope on every call.
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
| CONTROL_ANALYST | run.view, source.view, source.retry, exception.view/assign/investigate/propose/reopen, transaction.view, matching.view/propose, ledger.view, safeguarding.view, safeguarding.adjustment.propose, signoff.view, signoff.prepare, evidence.view, approval.view |
| CONTROL_OWNER / APPROVER | analyst set + run.reprocess, source.supersede, exception.approve, safeguarding.adjustment.approve, signoff.decide, evidence.export, settings.view |
| CONFIG_ADMIN (ADMIN) | settings.view/draft (cannot approve own change) |
| SECURITY_ADMIN | access.manage, settings.view |
| AUDIT_REVIEWER | read-only: *.view, audit.view, evidence.view, evidence.export |

`settings.approve` is granted "as configured" and never lets a user approve their own change.

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
| `SourceDelivery` adds `supersededByDeliveryId`; new `SourceDeliveryDetail` adds `fingerprint`, `sequenceNumber`, `extractionTimestamp`, `sourceTimeZone`, `checks[]` (fingerprint/duplicate/sequence/schema/count/totals), `actualTotals`, `allowedActions`. | Sources screen detail; matches `docs/integrations/input-contracts.md`. |
| `SourceRecord` adds `recordId`, `rejectionReasons`, `contentFingerprint`; `EvidenceNode`, `Transaction`, `LedgerEntry`, `EligibleBalanceLine` add `recordId` (and `deliveryId` on Transaction). | Link any displayed row to the evidence record preview. |
| `Transaction.matchStatus` adds `PENDING_APPROVAL`; `MatchGroup` adds `manual`, `approval`. | Manual matches awaiting approval. |
| `TransactionPage.aggregates` and `AccountPage.aggregates` (per currency). | Server-side totals. |
| `SafeguardingAdjustment.owner`: string becomes `{id,name}`; adds `runId`, `expiresOn`, `approval`. `AdjustmentInput` requires at least one `evidenceRefs`. **Breaking (owner).** | Identity, expiry status `EXPIRED`, approval record. |
| `AuditEvent.actor`: string becomes `{type,id,name}`; adds `reason`, `requestId`, `previousHash`; `runId` nullable; `subjectKind` and `type` enums extended (ADJUSTMENT, SIGN_OFF, CONFIG, MATCH_GROUP; resolution decided, config, access, retry, supersede, download, etc.). Filters add `actorId`, `actorType`, `runId`, `from`, `to`. **Breaking (actor).** | Audit actor type, hash chain, correlation. |
| `TimelineEvent.actor`: string becomes `Actor`. **Breaking.** | Same as audit. |
| `EvidencePack.status` adds `GENERATING`, `FAILED`; adds `sha256`, `sizeBytes`; generate returns 202. | Async generation, integrity. |
| `Role` adds `CONFIG_ADMIN`, `SECURITY_ADMIN`. | Roles doc distinguishes them from ADMIN. |
| `Settings` adds `pendingChanges`; `RuleConfig`/`SourceConfig`/`UserAccess` add `pendingChangeId`; `ApprovalPolicy` adds `requiredRole`, `version`. | Pending configuration changes visible in the settings screen. |
| `Page<T>` for `listRuns`; `listLegalEntities` stays an array. | Runs grow unbounded. |
| `ControlRoom`, `ControlAreaSummary`, `Account`, `Money`, `SafeguardingPosition` and the remaining types unchanged (Money amount regex allows up to 6 dp; mock used 2). | Compatibility. |

## Lint

`npx --yes @redocly/cli lint docs/integrations/api/openapi.yaml` passes with no errors. The two remaining
warnings are `no-unused-components` for the `RequestId` header/parameter: `X-Request-Id` is a global
convention, so it is defined as a component but not referenced from each of the 54 operations.
