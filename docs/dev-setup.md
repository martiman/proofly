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
