# Local development setup

How to run the web app against the real API with realistic demo data. Development and evaluation only: `Auth:Mode=Local` issues tokens for fixed users and is refused in Production.

## Prerequisites

- .NET 10 SDK, Node 22+, `psql`, `lsof`, `curl`, `openssl`.
- PostgreSQL reachable at `localhost:55432`, user `proofly`, trust authentication, as used by the backend tests and CI. If nothing listens there, either start your own server or let the script start a throwaway container: `PROOFLY_PG_DOCKER=1 scripts/dev-backend.sh` (postgres:16-alpine, container `proofly-dev-pg`). The Compose stack in `deploy/local` does not publish the database port.

## Backend

```bash
scripts/dev-backend.sh            # start (idempotent)
scripts/dev-backend.sh status
scripts/dev-backend.sh stop
scripts/dev-backend.sh start --recreate   # drop proofly_web and reseed
```

Steps: (a) check PostgreSQL; (b) create/migrate the `proofly_web` database (`Proofly.Worker migrate`); (c) seed demo data with `backend/tools/Proofly.Benchmarks --seed-dev --scale 0.02 --database proofly_web` (the benchmark generator, ingested through the real delivery registry and ingestor, then `run.process`; the database is kept and only dropped with `--recreate`; a database that already holds runs is left alone); (d) start the API on the first free port from 5080 (checked with `lsof`) with `Auth:Mode=Local`, CORS for `http://localhost:5199` and `:5173`, and Local tokens logged.

Seeded data: legal entity `LE-BENCH` ("Proofly Demo Ltd"), business date 2026-10-27, run `RUN-LE-BENCH-20261027-v1` in `REVIEW_REQUIRED`, about 84 exceptions with seeded defects in three currencies. Users are created by the API at startup, one per role with a fixed id: `local-analyst`, `local-approver`, `local-config-admin`, `local-security-admin`, `local-viewer` (all legal entities).

Everything generated is in `.dev/` (gitignored): `api.log`, `api.pid`, `api.port`, `signing-key` (stable across restarts so tokens stay valid), `tokens.txt` (`ROLE userId token`, mode 600, valid 8 hours), `evidence/`. Treat `tokens.txt` like a password.

Overrides: `PROOFLY_PG_HOST|PORT|USER`, `PROOFLY_DB`, `PROOFLY_SEED_SCALE`, `PROOFLY_API_PORT` (first port to try), `PROOFLY_WEB_ORIGINS` (comma separated CORS origins).

## Load a synthetic dataset

`examples/synthetic-data/<date>/` (or any directory with the same layout: `delivery_manifest.csv` plus the eight source CSV files) can be loaded into a database through the real pipeline, not by writing tables directly: every manifest row becomes a delivery (delivery id, declared record count and per-currency totals via `ManifestTotals`, `expected_by`, `received_at`, correction link), its file is stored as evidence, registered with `DeliveryRegistry` and ingested with `DeliveryIngestor` and the production CSV adapters (invalid rows are rejected with a reason, never repaired); then `run.process` runs for the business date. The legal entity is the one found in the data files (`LE-CZ-001` for the committed dataset); its row is created when absent.

```bash
cd backend
dotnet run --project tools/Proofly.Benchmarks -- --load-dataset ../examples/synthetic-data/2026-10-27 \
  --database proofly_dataset [--legal-entity LE-CZ-001] [--evidence-dir .dev/evidence-dataset] [--recreate] [--no-run] [--reject-count-mismatch]
```

- The database (default `proofly_dataset`, server `localhost:55432`) is created and migrated when absent and kept; `--recreate` drops it first. Running the command again is idempotent: the same delivery ids and fingerprints are found, nothing is ingested or processed twice.
- `--no-run` loads the deliveries and records only (read-model fixture without running the controls).
- By default a declared record-count mismatch QUALIFIES the delivery (records kept, the mismatch becomes an exception) because the dataset seeds one on the bank evidence and still expects the findings that need those records; `--reject-count-mismatch` applies the production default (REJECTED, no records stored).
- Code: `Proofly.Data/Fixtures/SyntheticDatasetLoader` (also used by the findings test). Point the API (`ConnectionStrings:Proofly`) at that database to browse the result.
- The findings test (`backend/tests/Proofly.Acceptance.Tests/Findings`) loads a dataset into a per-test schema and compares the exceptions with its `expected_findings.csv`: `PROOFLY_DATASET_DIR=<dir> dotnet test backend/tests/Proofly.Acceptance.Tests --filter FullyQualifiedName~Findings` (default: the committed dataset). `PROOFLY_SKIP_DATASET_FINDINGS=1` skips it explicitly. Deviations and their analysis: `docs/testing/dataset-findings-report.md`.

## Web

Port 5173 is often taken by other projects on a developer machine: use 5199 (see the `dev-server` skill) and confirm the page title is "Proofly".

```bash
cd apps/web
PROOFLY_API_PORT=$(cat ../../.dev/api.port) WEB_PORT=5199 \
  VITE_DEV_TOKEN=$(awk '$1=="ANALYST"{print $3}' ../../.dev/tokens.txt) npm run dev
```

Vite proxies `/api` to `http://127.0.0.1:$PROOFLY_API_PORT` (or `PROOFLY_API_URL`), so the browser stays same-origin. Instead of `VITE_DEV_TOKEN` you can paste a token in the browser console with `setDevToken('<token>')` (sessionStorage); pick another role's token to test permissions and separation of duties. Variables can also live in `apps/web/.env.local` (see `.env.example`, gitignored).

Contract smoke test against the real API:

```bash
PROOFLY_LIVE_BASE_URL=http://127.0.0.1:$(cat ../../.dev/api.port)/api/v1 \
PROOFLY_LIVE_TOKEN=$(awk '$1=="ANALYST"{print $3}' ../../.dev/tokens.txt) npm run test:live
```

## Generated API types

`npm run gen:api` (in `apps/web`) regenerates `src/api/generated/schema.d.ts` from `docs/integrations/api/openapi.yaml`. Commit the result together with the contract change; CI regenerates and fails on a diff.
