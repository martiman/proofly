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
