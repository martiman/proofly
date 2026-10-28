# Recovery and resilience tests (issue #51)

Automated evidence for the "Reliability and recovery" section of `omvp-acceptance-plan.md`. Project: `backend/tests/Proofly.Recovery.Tests`. Run: `cd backend && dotnet test tests/Proofly.Recovery.Tests`.

## How a failure is simulated

| Failure | Simulation |
|---|---|
| Worker killed mid-delivery | The adapter throws or cancels the processing token after N rows; or the audit writer fails on its Nth append while the ingestion transaction already holds the canonical rows. |
| Worker dies holding a job | The job is claimed with a 100 ms lease and never completed (optionally after its effect was committed); the lease expires and another worker polls. |
| Process restart | `RecoveryDb.Restart()` closes every pooled connection and opens a new data source; a new `Rig` rebuilds the whole object graph. |
| Run interrupted | The audit writer fails or cancels at the Nth append inside the single run transaction. |
| Disaster | `pg_dump -Fc`, evidence directory copy, original database and directory deleted, `pg_restore --clean --if-exists` into a new database. |
| Evidence corruption | One byte flipped, file truncated, file deleted in the evidence directory. |

Databases: schemas inside `proofly_test` (localhost:55432), or `proofly_rec_<guid>` databases created and dropped by the tests (backup/restore only). `pg_dump`/`pg_restore` are searched in `PG_BIN`, the Homebrew `postgresql@14` path and `PATH`; without them the two dump tests are reported as skipped with the reason.

## Scenarios and acceptance criteria

| Acceptance criterion | Test class | Result asserted |
|---|---|---|
| Interrupted ingestion restarts without duplicates | `InterruptedIngestionTests` | After a kill at row 1, 5 or 12, a cancel at row 7, or an audit failure while writing any of the three delivery audit events, nothing is persisted (no records, rejections, totals, audit events); the restart produces the same records, rejections, totals, delivery counts and audit event counts as an undisturbed ingestion; the audit chain verifies. Repeat ingestion and repeat registration change nothing. |
| Failed control jobs retried idempotently (job dies, lease expires) | `JobRecoveryTests` | For `delivery.ingest`, `run.process`, `evidence_pack.generate` and `safeguarding.adjustment.expire`, with the effect committed before death or not: the job runs a second time on another worker, the effect exists once (rows, audit events, files, result hash), the stale worker cannot complete the job, no third run happens. Also: a worker that died after committing the last ingestion but before enqueueing `run.process` still produces exactly one run job; an expiry crash between two adjustments is completed without double expiry; a job whose worker dies on its last attempt is DEAD with a visible `last_error`. |
| Run interrupted mid-run leaves no partial run; retry gives the identical result | `RunInterruptionTests` | Crash at audit append 1, 2, 5, 9 or cancellation: no run, exception, trace, link, group, position, result or audit row, deliveries not linked; the retry has the reference `result_hash` and identical row counts and exception set. Job retry and dead-letter then manual retry behave the same. A processed run is untouched when its job is redelivered. |
| Restart keeps queue, approvals, saved views, idempotency keys | `RestartTests` | Job rows (state, attempts, dedupe key, delayed jobs) identical after restart and processed once; a job RUNNING at kill time is taken over after the lease; PENDING approval keeps its fields and separation of duties still applies; saved view keeps filters, sort, owner privacy; idempotency keys replay the stored response, reject a different body, report in-flight, are scoped per caller. |
| DB and evidence-store restore keeps references and run history | `BackupRestoreTests` | Per-table row counts and an md5 over every row (as JSON) of every table are identical after restore; `IAuditVerifier` is valid with the same number of events; delivery and evidence-pack files resolve by SHA-256 from the restored directory copy; `runs.result_hash` identical; `MigrationRunner.RunAsync()` applies 0 scripts; afterwards the restored system accepts a decision, a new job (sequence continues), a new delivery and a new audit event with the chain still valid. Restoring in place over a changed database returns it to the backup state. |
| Evidence corruption detected and reported | `EvidenceCorruptionTests` | `EvidenceStore.VerifyAsync` is false for a flipped byte, a truncated file and a missing file (and true for intact objects); ingestion of a delivery with an altered file is REJECTED with `EVIDENCE_MISSING` in `delivery_rejections` and in the audit event, no records stored; a corrupted evidence pack fails the download integrity check and `EVIDENCE_PACK_DOWNLOADED` with `INTEGRITY_FAILURE` is audited. |

## Findings

No defect was found in `src`; every scenario passed against the existing implementation. Observations (not changed, out of scope of this issue):

1. (Fixed in the second contract pass) A `run.process` that fails on its first attempt rolled back the run row together with everything else, so there was no run to mark FAILED. `RunProcessor.MarkFailedAsync` now creates a FAILED run marker (version 1, no result, no exceptions) in its own transaction with `RUN_STARTED` and a failure audit event; reads report state `FAILED` and a later successful attempt processes that same run. Covered by `RunInterruptionTests.Job_that_exhausts_its_attempts_...`.
2. `EvidenceStore.WriteAsync` does not replace an existing object whose content no longer matches its name (it only checks existence). Re-delivering identical bytes therefore does not heal a corrupted object; the corruption is found when the object is next verified (ingestion, pack download), and the remedy is restoring the evidence directory from backup.
3. There is no operator command that verifies every evidence reference in the database against the store; after a restore the checks are done per object when it is used. A "verify all" job would make the restore drill in `deploy/customer/README.md` a single step.
