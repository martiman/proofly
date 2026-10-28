# Backlog draft (proposal, nothing created on GitHub)

Repo martiman/proofly. Existing: #1 "Api spec" (the OpenAPI contract; referenced as `#1`, not duplicated).
Prerequisite needing user approval: labels `area/{web,api,worker,controls,ingestion,security,docs}`, `type/{feature,bug,spike,decision}` and milestones M1-M5 do not exist yet (repo has only default labels, no milestones; the one milestone-like API result was empty).
Order = proposed creation/priority order. "Blocked by" uses proposed numbers (P##); replace with real issue numbers on creation. All backend tasks also depend on #1 (contract change first, via PR).
Common acceptance for every task: contract (`openapi.yaml`) unchanged or changed first; `dotnet build`/`dotnet test` (backend) or `npm run build` (web) pass; material decisions write audit events.

## Table

| # | Title | Labels | Milestone | Blocked by |
|---|---|---|---|---|
| P01 | Decide: approver roles and role-to-permission mapping | type/decision, area/security | M1 Foundation | #1 |
| P02 | Decide: approval policy (thresholds, who approves what, versioning) | type/decision, area/security | M1 Foundation | P01 |
| P03 | Decide: money precision and rounding per currency | type/decision, area/controls | M1 Foundation | #1 |
| P04 | Decide: run qualification rules (qualified pass, blocking controls) | type/decision, area/controls | M3 Controls on synthetic data | #1 |
| P05 | Decide: reprocess trigger (manual, automatic on late evidence, both) | type/decision, area/controls | M3 Controls on synthetic data | #1 |
| P06 | Decide: saved views (personal vs shared, ownership, limits) | type/decision, area/api | M2 Read-only API | #1 |
| P07 | Decide: bulk assign limit and partial-failure semantics | type/decision, area/api | M2 Read-only API | #1 |
| P08 | Decide: manual match approval (when a match is material) | type/decision, area/controls | M4 Approvals and sign-off | P01, P02 |
| P09 | Decide: configuration activation (four-eyes, effective dating) | type/decision, area/security | M4 Approvals and sign-off | P01, P02 |
| P10 | .NET solution skeleton (api, worker, domain, controls, connectors, contracts) | type/feature, area/api | M1 Foundation | none |
| P11 | CI and Docker Compose | type/feature, area/docs | M1 Foundation | P10 |
| P13 | PostgreSQL baseline schema and `proofly migrate` | type/feature, area/api | M1 Foundation | P10, P03 |
| P14 | Contract-first API host: validation, ProblemDetails, ETag/If-Match, Idempotency-Key, X-Request-Id | type/feature, area/api | M1 Foundation | P10 |
| P15 | AuthN/AuthZ: OIDC, `GET /me`, permission and legal-entity scope checks | type/feature, area/security | M1 Foundation | P14, P01 |
| P16 | Append-only audit store with hash chain, same-transaction writes | type/feature, area/security | M1 Foundation | P13 |
| P17 | Durable background job queue in PostgreSQL | type/feature, area/worker | M1 Foundation | P13 |
| P18 | Load synthetic data into the database as read model fixture | type/feature, area/api | M2 Read-only API | P13 |
| P19 | Legal entities, runs, control room read endpoints | type/feature, area/api | M2 Read-only API | P15, P18 |
| P20 | Deliveries, delivery detail, records, record preview read endpoints | type/feature, area/api | M2 Read-only API | P19 |
| P21 | Exceptions list/detail/summary with filters, sorting, cursor paging | type/feature, area/api | M2 Read-only API | P19 |
| P22 | Transactions, lineage, accounts and ledger read endpoints | type/feature, area/api | M2 Read-only API | P19 |
| P23 | Matching workbench and match groups read endpoints | type/feature, area/api | M2 Read-only API | P22 |
| P24 | Safeguarding position and adjustments list endpoints | type/feature, area/api | M2 Read-only API | P19 |
| P25 | Audit events, settings, approvals inbox, evidence pack read endpoints | type/feature, area/api | M2 Read-only API | P16, P19 |
| P26 | Exception working commands: assign, bulk assign, notes, request evidence, saved views | type/feature, area/api | M2 Read-only API | P21, P06, P07 |
| P27 | API contract tests against openapi.yaml | type/feature, area/api | M2 Read-only API | P19 |
| P28 | Fix delivery manifest EUR total mismatch (22669.49 vs 22874.49) | type/bug, area/docs | M3 Controls on synthetic data | none |
| P29 | Ingestion framework: evidence store, SHA-256, delivery registry, reject-not-repair | type/feature, area/ingestion | M3 Controls on synthetic data | P13, P17 |
| P30 | CSV adapter(s) for the synthetic input contracts | type/feature, area/ingestion | M3 Controls on synthetic data | P29 |
| P31 | XLSX and legacy XLS adapter | type/feature, area/ingestion | M3 Controls on synthetic data | P29 |
| P32 | XML adapter with XSD validation | type/feature, area/ingestion | M3 Controls on synthetic data | P29 |
| P33 | Completeness controls | type/feature, area/controls | M3 Controls on synthetic data | P30, P28, P04 |
| P34 | Independent ledger recalculation control | type/feature, area/controls | M3 Controls on synthetic data | P30, P03 |
| P35 | Matching engine (exact, composite, grouped, ambiguity) | type/feature, area/controls | M3 Controls on synthetic data | P30 |
| P36 | Lifecycle verification (transfer, direct debit, card) | type/feature, area/controls | M3 Controls on synthetic data | P35 |
| P37 | Safeguarding coverage control | type/feature, area/controls | M3 Controls on synthetic data | P30, P03 |
| P38 | Exception generation and `expected_findings.csv` reproduction test | type/feature, area/controls | M3 Controls on synthetic data | P33, P34, P35, P36, P37 |
| P39 | Run orchestration, determinism, reprocess as new version, compare runs | type/feature, area/worker | M3 Controls on synthetic data | P38, P05 |
| P40 | Source operations: retry ingestion, supersede delivery, processing history | type/feature, area/ingestion | M3 Controls on synthetic data | P29 |
| P41 | Resolution proposals and approvals (separation of duties) | type/feature, area/api | M4 Approvals and sign-off | P26, P02, P16 |
| P42 | Safeguarding adjustment propose/decide and expiry | type/feature, area/api | M4 Approvals and sign-off | P37, P41 |
| P43 | Manual match proposal and approval | type/feature, area/controls | M4 Approvals and sign-off | P35, P41, P08 |
| P44 | Run sign-off: submit, return, approve, attestation hash, stale detection | type/feature, area/api | M4 Approvals and sign-off | P39, P41, P04 |
| P45 | Config change propose/decide (four-eyes activation) | type/feature, area/security | M4 Approvals and sign-off | P25, P09 |
| P46 | Evidence pack generation (async) and audited download | type/feature, area/api | M4 Approvals and sign-off | P39, P16 |
| P47 | Governance acceptance tests (SoD, entity scope, immutability after reprocess) | type/feature, area/security | M4 Approvals and sign-off | P41, P44 |
| P48 | Spike: 100,000-transaction run and reprocess on one machine | type/spike, area/worker | M5 Pilot | P39, P49 |
| P49 | Decide: agree TBD performance and deadline targets | type/decision, area/docs | M3 Controls on synthetic data | #1 |
| P50 | Packaging and observability (self-contained build, systemd/Windows Service, health, logs, OTel) | type/feature, area/api | M5 Pilot | P10 |
| P52 | Backup/restore and interrupted-job recovery tests | type/feature, area/worker | M5 Pilot | P39, P50 |
| P53 | Security acceptance: secrets, evidence access audit, session expiry, library licences | type/feature, area/security | M5 Pilot | P46, P15 |
| P54 | Pilot acceptance run and usability walkthrough | type/feature, area/docs | M5 Pilot | P47, P49, P52 |
| P55 | Web: generate TypeScript types from openapi.yaml, drop hand-written types | type/feature, area/web | M2 Read-only API | #1 |
| P56 | Web: API client (auth, problem+json, ETag, Idempotency-Key, cursor paging) replacing mock transport | type/feature, area/web | M2 Read-only API | P55, P14 |
| P57 | Web: switch read screens from mock service to real API | type/feature, area/web | M2 Read-only API | P56, P19, P20, P21, P22, P23, P24, P25 |
| P58 | Web: Exceptions Queue uses server summary, remove browser-side money sums | type/bug, area/web | M2 Read-only API | P21, P55 |
| P59 | Web: switch commands to real API, permission-driven actions (`allowedActions`) | type/feature, area/web | M4 Approvals and sign-off | P57, P41, P44 |
| P61 | Spike: design-fidelity audit of all 11 screens against pen frames (incl. Evidence & Audit layout) | type/spike, area/web | M2 Read-only API | none |
| P62 | Web: close design-fidelity gaps found by P61 (checklist per screen) | type/feature, area/web | M3 Controls on synthetic data | P61 |
| P63 | Decide/fix: EUR/GBP in design frames vs data currencies | type/decision, area/web | M1 Foundation | none |

60 items after review (P12, P51, P60 merged into P11, P50, P61; numbers kept, gaps intentional).

## Bodies

Format for each: Goal. Acceptance criteria (AC). Decisions list options (A, B, C) and a recommendation (Rec).

### Decisions

**P01 Approver roles.** Goal: fix who may approve what; unblocks `x-required-permission` mapping. Options: A) the README mapping (CONTROL_OWNER/APPROVER holds all approve permissions); B) one approver role per object type; C) permission grants per user, roles only as bundles. Rec: A for pilot, with permissions kept granular so C is possible later. AC: mapping table agreed, recorded in `docs/security/roles-and-approvals.md`, referenced by an ADR or spec PR; `Permission`/`Role` enums in the contract updated if needed.

**P02 Approval policy.** Goal: define when approval is required and by whom (`ApprovalPolicy.requiredRole`, `version`). Options: A) fixed rules by type (all material resolutions, adjustments, sign-off); B) value thresholds per currency and severity; C) A now, thresholds configurable via P45 later. Rec: C. AC: policy table with versioning, who records `policy version` on `Approval`, documented; proposer never approves own proposal.

**P03 Money precision per currency.** Goal: contract allows up to 6 dp but mock used 2; fix storage and display. Options: A) NUMERIC(38,6) everywhere, display by ISO 4217 minor units; B) store per-currency scale; C) fixed 2 dp. Rec: A, round only at display with ISO minor units, never in calculation; reject inputs with more than the contract allows. AC: documented in an ADR, schema and amount regex consistent, currencies with 0/3 dp covered by a test case.

**P04 Run qualification.** Goal: define when a run can be signed off and with which qualifications (`RunSignOff.qualifications`). Options: A) any unresolved blocking exception forbids sign-off; B) sign-off allowed with named qualifications and approver reason; C) A for MVP, B for non-blocking items only. Rec: C (matches acceptance plan "cannot sign off with unresolved blocking control"). AC: rules per control and severity documented; `blocking` semantics defined; used by P33 and P44.

**P05 Reprocess trigger.** Goal: who/what creates a new run version. Options: A) manual only (`requestRunReprocess`, reason required); B) automatic on superseding delivery; C) automatic proposal, manual confirmation. Rec: A for pilot, C after (signed runs must stay immutable). AC: decision recorded, signed run never mutated, behaviour for superseded run (409 `run-superseded`) specified.

**P06 Saved views.** Goal: scope of `ExceptionView`. Options: A) personal only; B) personal and shared by legal entity; C) shared needs a permission. Rec: B with creator-only deletion plus admin override, max 50 per user. AC: ownership, visibility, limits documented; contract updated if needed.

**P07 Bulk assign limit.** Goal: set cap and failure semantics for `bulkAssignExceptions`. Options: A) 200 items (matches the API spec), 207 per-item results; B) 100; C) all-or-nothing. Rec: A (200, as in the spec). AC: limit in the contract (`maxItems`), 422 above it, per-item error codes listed.

**P08 Manual match approval.** Goal: when is a manual match material (needs approval, `PENDING_APPROVAL`). Options: A) always; B) above value threshold; C) only when it clears a blocking exception. Rec: C, thresholds later via P02. AC: rule documented, matches P43, status transitions defined.

**P09 Configuration activation.** Goal: how a config change goes live. Options: A) immediate after approval; B) effective from next run; C) effective-dated by approver. Rec: B (keeps runs reproducible from ruleset version). AC: activation semantics and ruleset versioning written; approver cannot be proposer.

### M1 Foundation

**P10 .NET solution skeleton.** Goal: `apps/api`, `apps/worker`, `packages/{domain,controls,connectors,contracts}` per ADR 0004; controls project has no web dependency. AC: solution builds on current .NET LTS; empty tests run; layout documented.

**P11 CI and Docker Compose.** Goal: automated gates and an evaluation stack. AC: pipeline runs `dotnet build/test`, `npx @redocly/cli lint docs/integrations/api/openapi.yaml` and `cd apps/web && npm run build`, failing step fails the PR; `deploy/local/` Docker Compose starts PostgreSQL, api and worker with migrations applied; README steps verified from scratch; no secrets committed.

**P13 PostgreSQL schema and migrations.** Goal: versioned migrations, `proofly migrate` and at start-up. AC: NUMERIC money columns only, audit tables append-only, runs/deliveries/records/exceptions baseline tables; migrations idempotent and run in CI against a real PostgreSQL.

**P14 Contract-first API host.** Goal: implement cross-cutting conventions. AC: request/response validation against openapi.yaml; RFC 9457 problems with `requestId`; ETag/If-Match (412/428); Idempotency-Key replay, mismatch 422, in-flight 409; `X-Request-Id` echoed and stored; cursor paging helper.

**P15 AuthN/AuthZ.** Goal: OIDC bearer auth, local users for evaluation only. AC: `GET /me` returns roles, legalEntityIds, permissions; every operation enforces `x-required-permission` and entity scope; out-of-scope returns 404/403 per contract; test per role (acceptance: unauthorised user cannot see another legal entity).

**P16 Audit store.** AC: append-only table, `previousHash` chain, written in the same transaction as the change, `actor` typed, `reason` and `requestId` stored; tamper-detection test.

**P17 Job queue.** AC: durable PostgreSQL-backed queue; retry is idempotent; restart does not lose queued work; library licence checked for on-prem distribution.

### M2 Read-only API

**P18 Synthetic fixture loader.** AC: `examples/synthetic-data/` loadable into read tables without running controls; documented command; no real customer data.

**P19-P25 read endpoints.** Goal: implement the listed operationIds (see mapping table in `docs/integrations/api/README.md`): P19 listLegalEntities, listRuns, getRun, getControlRoom; P20 deliveries, getSourceDelivery, listDeliveryRecords, getSourceRecord, processing history read; P21 listExceptions, getException, getExceptionsSummary; P22 listTransactions, getTransaction, getTransactionLineage/getLineage, listAccounts, getAccountLedger; P23 getMatchingWorkbench, listMatchGroups; P24 getSafeguardingPosition, listAdjustments; P25 listAuditEvents, getSettings, listApprovals, getEvidencePack. AC for all: responses validate against the contract; per-currency aggregates computed server-side over the full filtered set; stable sort and cursor paging; permission and entity scope enforced; numbers equal the synthetic fixture.

**P26 Exception working commands.** Goal: assignException, bulkAssignExceptions, addExceptionNote, requestExceptionEvidence, saved views CRUD. AC: If-Match and Idempotency-Key required; per-item ETag and 207 results; each writes an audit event; limits from P06/P07.

**P27 Contract tests.** AC: every operation has at least one test checking status codes and schema; contract drift fails CI.

### M3 Controls on synthetic data

**P28 Manifest EUR control total mismatch.** Goal: `delivery_manifest.csv` (line 5) declares an EUR total of 22669.49 for `client_account_balances.csv`, but the file sums to 22874.49 (difference 205.00; CZK 131800 matches). The web copy `apps/web/src/api/data/delivery_manifest.csv` is identical, and `expected_findings.csv` has no finding for this. Options: A) the manifest is wrong: correct it to 22874.49 in both copies (or generate the web copy); B) the mismatch is intended: add a `CONTROL_VALUE_MISMATCH` finding (data_completeness, HIGH) to `expected_findings.csv`, document it in the dataset README, and make P33/P38 expect it. Rec: B if the dataset is meant to exercise control-total checks, else A; decide, then apply. AC: decision recorded, fixtures and expected findings consistent, completeness control test (P33/P38) agrees, web copy identical to the source.

**P29 Ingestion framework.** AC: SHA-256 fingerprint, duplicate delivery detection, evidence stored on configured directory, delivery registry and checks (fingerprint/duplicate/sequence/schema/count/totals); invalid records rejected visibly and counted in the completeness denominator, never repaired.

**P30-P32 adapters.** AC each: parse all synthetic inputs of that format per `docs/integrations/input-contracts.md`; types and dates strictly validated; money parsed as decimal; malformed rows produce rejection reasons; XML validated against XSD (P32); legacy `.xls` covered by a fixture (P31); library licences checked.

**P33 Completeness.** AC: scenarios from the acceptance plan pass (missing mandatory source, duplicate delivery, count/value mismatch, superseded delivery, malformed records visible); incomplete population cannot receive an unqualified pass.

**P34 Ledger.** AC: calculated closing balance = opening + movements per account and currency; detects missing/duplicate movement, wrong sign, unsupported opening, absent closing; differences explainable to records.

**P35 Matching.** AC: exact, composite, one-to-one, controlled one-to-many, no incompatible reuse, ambiguous candidates remain unmatched with explanation.

**P36 Lifecycle.** AC: transfer, direct-debit (return with/without reversal), card (authorisation, presentment, settlement batch, refund/chargeback, processor settlement to bank) flows; pending inside window, breach after window.

**P37 Safeguarding.** AC: surplus, shortfall, missing and stale external balance, unapproved adjustment excluded, currencies isolated, no cross-currency netting; shortfall creates a high-severity exception.

**P38 Findings reproduction.** AC: control output for 2026-10-27 reproduces `expected_findings.csv` exactly (xUnit, runs in CI); no unexpected findings.

**P39 Run orchestration.** AC: same evidence and ruleset yield identical results (byte-equal hash test); reprocess creates version+1 and marks old SUPERSEDED; `compareRuns` shows differences; duplicate ingestion creates no duplicate canonical records.

**P40 Source operations.** AC: retryDeliveryIngestion, markDeliverySuperseded (link, original kept), getDeliveryProcessingHistory; reason required, audited.

### M4 Approvals and sign-off

**P41 Resolutions.** AC: proposeResolution (reason 3+ chars, evidenceRefs), decideResolution with proposalVersion; proposer cannot decide (403 `separation-of-duties`); editing invalidates approval; original control result untouched; `allowedActions` computed.

**P42 Adjustments.** AC: propose/decide, evidence required, expiry to `EXPIRED`, unapproved excluded from position, recalculation visible.

**P43 Manual match.** AC: proposeManualMatch, `PENDING_APPROVAL`, approval per P08, match group marked manual.

**P44 Sign-off.** AC: submitRunForSignOff fixes preparer/approver; approve requires attestation text hash and reason; stale sign-off 409; blocked while blocking control unresolved; signed run immutable after reprocess.

**P45 Config changes.** AC: propose/decide/list, pending changes in settings, own change cannot be approved, activation per P09, audited.

**P46 Evidence pack.** AC: generate returns 202, status GENERATING/FAILED/READY with sha256 and size; pack lists source fingerprints and rule versions; download needs `evidence.export` and is audited.

**P47 Governance tests.** AC: scenarios in acceptance plan "Workflow and governance" automated.

### M5 Pilot

**P48 100k spike.** AC: run with 100,000 transactions and 100,000 accounts and a reprocess on one machine; timings per stage, memory, DB size recorded in a doc; no claim published beyond measured.

**P49 Performance targets.** AC: values for the TBD rows of the acceptance plan agreed and recorded.

**P50 Packaging and observability.** Goal: customer-installable build and operability. AC: self-contained single-file for linux-x64 and win-x64; systemd unit and Windows Service verified; health endpoints, structured logs without secrets, OTel metrics.

**P52 Recovery.** AC: interrupted ingestion restarts without duplicates; restart keeps queue and approvals; DB and evidence-store restore keeps references and run history.

**P53 Security acceptance.** AC: source credentials never exposed, evidence access audited, session expiry works, dev/prod stores isolated, library licences reviewed.

**P54 Pilot acceptance.** AC: exit criteria of the acceptance plan walked through; usability tasks 1-6 done without database access; limitations documented.

### Frontend

**P55 Generated types.** Goal: replace `apps/web/src/api/types.ts` with types generated from `openapi.yaml`. AC: generation script and CI check; hand-written types removed; breaking changes handled (owner/actor objects, cursor pagination); `npm run build` passes.

**P56 API client.** AC: typed client with bearer auth, X-Request-Id, problem+json mapping, If-Match/ETag, Idempotency-Key, cursor paging; 401/403/409/412 shown, not swallowed.

**P57 Read screens on real API.** AC: all 11 screens render from the API (mock behind a dev flag then removed); no calculations of financial values in the browser; verified against running backend with screenshots.

**P58 Exceptions Queue sums.** AC: totals and per-currency sums come from getExceptionsSummary; no addition of money in UI code; mixed currencies never summed.

**P59 Commands and permissions.** AC: all actions call the real commands with reason prompts; buttons follow `allowedActions`; errors for separation-of-duties and stale sign-off clear.

**P61 Fidelity audit.** AC: each of the 11 screens (Control Room, Exceptions Queue, Exception Detail, Transaction Explorer, Matching Workbench, Account Ledger, Safeguarding, Run Sign-off, Sources, Configuration & Access, Evidence & Audit) compared with its pen frame; gaps listed per screen with severity. Includes the Evidence & Audit two-column layout (search bar, filter chips, run-manifest strip, right-hand detail panel with verification checklist).

**P62 Close fidelity gaps.** AC: checklist from P61 closed, per-screen before/after screenshots; may be split into per-screen issues after P61.

**P63 EUR/GBP mismatch.** Goal: design frames and mock data use different currencies. Options: A) change design to data (EUR/GBP as synthetic); B) change fixture; C) design shows both. Rec: A (fixtures are acceptance data). AC: one source of truth stated, frames or data aligned, screenshots rechecked.
