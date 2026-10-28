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
- The approver must differ from the proposer for every exception, blocking or not (ADR 0005): `403 separation-of-duties`, plus the DB CHECK `approver <> proposer`. A stale `proposalVersion` or a proposal that is no longer PENDING is `409 invalid-state`.
- Approve sets the exception to the proposed kind; reject restores the previous working status. Reopen (terminal -> `OPEN`, non-superseded run only) clears the current resolution fields; proposals, approvals and audit history remain. The finding itself (control, reason code, severity, value, blocking, rule trace) is never changed.
- Sign-off rule: `IResolutionCommandRepository.HasUnresolvedBlockingExceptionsAsync(runId)` is true when any `blocking` exception of the run is not `RESOLVED`, `ACCEPTED_DIFFERENCE` or `FALSE_POSITIVE` (open, in investigation, waiting for data and pending approval all count as unresolved). Non-blocking exceptions never block.

## Security acceptance (issue #52)

`tests/Proofly.Security.Tests` holds the security acceptance tests (secrets, evidence audit, sessions, dev/prod isolation, authorization matrix, legal-entity isolation, injection, CORS, headers). Matrices are generated from the OpenAPI contract and the role bundles; results, defects and residual risks are in `docs/security/security-acceptance.md`, licences in `deploy/customer/THIRD-PARTY.md`. The project runs its classes sequentially (shared PostgreSQL connection limit).
