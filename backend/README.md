# Proofly backend (.NET 10, PostgreSQL)

Contract: `docs/integrations/api/openapi.yaml`. Stack: ADR 0004. Build/test: `cd backend && dotnet build -warnaserror && dotnet test`.

## Layout (replaces the `apps/*`, `packages/*` plan in ADR 0004; backend lives under `backend/`)

| Project | Role | References |
|---|---|---|
| `src/Proofly.Domain` | Entities, `Money` (decimal + currency), ids, enums (Role, Permission, Severity, ControlArea, RunState, ...) | none |
| `src/Proofly.Contracts` | API DTOs mirroring openapi schemas (hand-written subset; generation later) | none |
| `src/Proofly.Controls` | Pure, deterministic control functions. No web or DB dependency | Domain |
| `src/Proofly.Connectors` | File adapters (reject with reason, never repair) | Domain |
| `src/Proofly.Data` | Npgsql, `MigrationRunner`, repositories | Domain |
| `src/Proofly.Api` | ASP.NET Core host, `/healthz` | Contracts, Domain, Data, Controls |
| `src/Proofly.Worker` | Worker host; `Proofly.Worker migrate` verb runs migrations (no separate CLI project) | Data, Controls, Connectors |
| `tests/Proofly.<X>.Tests` | xUnit, one per project | the project under test |
| `tools/Proofly.Benchmarks` | Performance spike harness (issue #48): deterministic generator, ingest/run/reprocess/API timings against `proofly_bench`. A console tool, not run by `dotnet test`. Results: `docs/testing/performance-spike.md` | Api, Data, Connectors, Controls, Domain |

## Module convention (avoids merge conflicts)

- Each feature area has its own folder and an `IServiceCollection AddXxx(...)` extension (plus `MapXxx` for endpoints in Api).
- `Proofly.Api/Modules.cs` and `Proofly.Worker/Modules.cs` list modules, one line each, alphabetical. `Program.cs` only calls `AddModules`/`MapModules`; do not edit it for features.
- Wire enum names: use `WireNames` (UPPER_SNAKE, lower snake for ControlArea, dotted for Permission).

## Rules

- Money is `decimal`/`NUMERIC`, never floating point. `Money` rejects mixed currencies.
- Warnings are errors; nullable on; package versions only in `Directory.Packages.props`.
- Connection string `ConnectionStrings:Proofly`; dev DB `proofly_dev`, tests use `proofly_test` (localhost:55432, trust auth).

## Background jobs (issue #17)

Decision: small in-house outbox on PostgreSQL (`jobs` table, migration `0017_jobs.sql`) instead of Hangfire (ADR 0004 option). Claiming uses `SELECT ... FOR UPDATE SKIP LOCKED`; no extra dependency and no licence exposure for on-prem distribution.

- Code: `Proofly.Data/Jobs` (queue, store, processor, handler registry), host loop in `Proofly.Worker/Jobs`.
- Enqueue: `IJobQueue.EnqueueAsync(new JobRequest(type, payload, DedupeKey: ...), transaction)`. Pass the business `NpgsqlTransaction` so both commit or roll back together; the unique `dedupe_key` makes it idempotent (`EnqueueResult.Created` is false on a repeat).
- Handlers: `services.AddJobHandler<THandler, TPayload>("job.type")` (after `AddJobs`); `THandler : IJobHandler<TPayload>`, resolved per job in a new DI scope. Handlers must be idempotent (at-least-once). Throw `PermanentJobException` to dead-letter immediately.
- States: QUEUED, RUNNING (leased), FAILED (retry waits until `run_after`), SUCCEEDED, DEAD. Failure backoff is `BackoffBase * 2^(attempt-1)` capped at `BackoffMax`; DEAD after `max_attempts`.
- Lease: heartbeat at lease/3; a RUNNING job with an expired lease is re-queued (DEAD if attempts exhausted) by every worker poll. Completion is fenced by `locked_by`, so a stale worker cannot overwrite the result. Shutdown releases the in-flight job without consuming an attempt.
- Metrics: `IJobQueue.CountsByStateAsync()` and `System.Diagnostics.Metrics` meter `Proofly.Jobs` (`proofly.jobs.succeeded|failed|dead`).

## Read API conventions (contract reconciliation, migration `0020`)

- One shared page type: `Proofly.Contracts.Common.PageInfoDto` / `PageDto<T>` (omits a null `total`, always writes `nextCursor`). `Paging.Create` builds it; never add per-area copies.
- `WireJson` writes dictionary keys as-is (they are data); use `Dictionary<string, T>`, not `JsonObject`.
- Ids: `Proofly.Domain.Ids.TransactionIds` owns the `TRF-`/`DD-`/`CRD-`/`BNK-`/`LED-` + pk scheme; record ids are `<sourceName>:<pk>` / `rejected:<pk>` (see the API README, Identifiers). Do not invent another form.
- Fields the server cannot know are `null` (nullable in the contract), never a made-up value.
- Ingestion stores the 1-based file row of every accepted record in `row_number` (`RecordTables.InsertAsync`).
- The controls orchestration (later issue) must write `account_control_results`, `exception_rule_traces`, `exception_links`, `match_group_exceptions` and the match group `tolerance`/`rationale`/`cardinality`; the read side already prefers them.

## Exception resolutions and the sign-off gate (issue #41)
- `proposeResolution` / `decideResolution` / `reopenException` live in `Proofly.Data/Exceptions/Commands/ResolutionCommandRepository.cs` (one transaction each, audit event inside it).
- A proposal creates `resolution_proposals` (previous working status kept in `previous_status`, migration `0041`) and an `approvals` row (`EXCEPTION_RESOLUTION`, required role `APPROVER`, `proposal_version` = n-th proposal of the exception). The exception becomes `PENDING_APPROVAL`. Proposing again while pending invalidates the earlier proposal and approval.
- The approver must differ from the proposer for every exception, blocking or not (ADR 0005): `403 separation-of-duties`, plus the DB CHECK `approver <> proposer`. A stale `proposalVersion` is `409 stale-proposal` (same for adjustments, manual matches and config changes); a proposal that is no longer PENDING is `409 invalid-state`.
- Approve sets the exception to the proposed kind; reject restores the previous working status. Reopen (terminal -> `OPEN`, non-superseded run only) clears the current resolution fields; proposals, approvals and audit history remain. The finding itself (control, reason code, severity, value, blocking, rule trace) is never changed.
- Sign-off rule (one shared query, `Proofly.Data.Exceptions.UnresolvedBlockingExceptions`, used by run processing, `IResolutionCommandRepository.HasUnresolvedBlockingExceptionsAsync(runId)` and the sign-off gate): true when any `blocking` exception of the run is not `RESOLVED`, `ACCEPTED_DIFFERENCE` or `FALSE_POSITIVE` (open, in investigation, waiting for data and pending approval all count as unresolved). Non-blocking exceptions never block.

## Security acceptance (issue #52)

`tests/Proofly.Security.Tests` holds the security acceptance tests (secrets, evidence audit, sessions, dev/prod isolation, authorization matrix, legal-entity isolation, injection, CORS, headers). Matrices are generated from the OpenAPI contract and the role bundles; results, defects and residual risks are in `docs/security/security-acceptance.md`, licences in `deploy/customer/THIRD-PARTY.md`. The project runs its classes sequentially (shared PostgreSQL connection limit).

## Second contract pass (hardening)

- Contract: see the "Changelog (second contract pass)" in `docs/integrations/api/README.md` (H1-H10). New audit types `ADJUSTMENT_EXPIRED`, `RUN_PROCESSED`, `SIGN_OFF_INVALIDATED`, `EVIDENCE_ACCESS_DENIED` (also in `AuditVocabulary`).
- Per-kind permission: `x-required-permission-any` (platform lets any listed permission through, the handler enforces the kind: `settings.approve` for CONFIG, `access.manage` for ACCESS changes; proposer != approver always).
- Migration `0047`: `safeguarding_positions.difference_amount` nullable (QUALIFIED currencies), `safeguarding_positions.stale` (set when an adjustment is approved or expires after processing; cleared by reprocessing, which writes a new run), `delivery_rejections.superseded_by_retry`.
- A first-attempt `run.process` failure leaves a FAILED run marker (`RunProcessor.MarkFailedAsync`, own transaction).
- Binary responses (evidence-pack zip) bypass response buffering in `ResponseValidationMiddleware` and stream from the evidence store.
- `GET /lineage/{subjectKind}/{subjectId}` (`Proofly.Data/Transactions/LineageRepository`): TRANSACTION (`TRF-4`), RECORD (`transfer_instructions:4`), EXCEPTION and CONTROL_RESULT (exception id or `EXC-<id>`); `runId` optional narrows the subject; entity scope and `evidence.view` of the subject's legal entity.

## Contract batch 3 (web screen migrations)

- Contract: see "Changelog (contract batch 3)" (K1-K10) in `docs/integrations/api/README.md`; migration `0051` (`evidence_packs.manifest`, `audit_chain`).
- Summary and list share `ExceptionQueryParser` and `ExceptionReadRepository.BuildWhere`; a test checks `summary.total == list total` for random filter combinations. Never add a filter to one without the other.
- `Environment:Name` / `Environment:Kind` (`DEV|TEST|PILOT|PRODUCTION`) are required in Production (`StartupSelfCheck`) and surface in `getMe` as `environment`.
- Permissions: `listAssignableUsers` needs `exception.assign` (checked per legal entity), not `settings.view`. **`audit.view` is intentionally not part of the ANALYST bundle** (ADR 0005 separation: the analyst who prepares and resolves exceptions does not review the audit trail; VIEWER, APPROVER and SECURITY_ADMIN do). The web Evidence & Audit screen must hide the audit panel for analysts instead of requesting it.

## Backend fixes from web QA and end-to-end runs (migration `0052`)

- Contract: "Changelog (backend fixes from web QA and end-to-end runs)" (L1-L8) in `docs/integrations/api/README.md`.
- **Adjustments belong to (legal entity, business date).** `RunInputLoader` and the read models select `safeguarding_adjustments` through the runs of the same entity and date with `run_version <=` the run's version (the fragment `a.run_id IN (SELECT x.id FROM runs x JOIN runs t ON t.id=@r AND ... x.run_version<=t.run_version)`); `run_id` is only the version it was created in. A reprocess therefore includes approved, unexpired adjustments; a superseded version is never recomputed (its position keeps the snapshot in `safeguarding_positions.detail`, which the read models prefer over the live sum). Approving a pending proposal of a superseded run and expiring an adjustment flag the current version's position stale (`AdjustmentCommands.CurrentRunAsync`). Test: `AdjustmentCarryOverTests`.
- Exception text: `Proofly.Controls.Common.FindingText` builds the headline (title, <= 80) and the detail (description) from the finding; `exceptions.description` is nullable and readers use `COALESCE(description, title)` for rows written outside run processing. The result hash does not include either text.
- Money scale: `Proofly.Contracts.Common.DecimalString.Normalize` is the only place that formats a wire amount; never call `ToString` on a decimal for a response.
- Dev seed: `BaselineRuleset.InstallAsync` (called by `--seed-dev` before the run) inserts the baseline `config_rules`, validated by `ConfigPayloadValidator`, and refreshes the `v1` ruleset snapshot like migration `0045` built it.
