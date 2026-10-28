# Local evaluation stack (Docker Compose)

Starts PostgreSQL 16, a one-shot `migrate` job (`Proofly.Worker migrate`), the API and the worker. Evaluation only, not a production deployment (see `docs/architecture/deployment-model.md`).

Status: written without a running Docker daemon; images were not built or run when authored. Verify the steps below.

## Steps
1. `cd deploy/local`
2. `cp .env.example .env` and set `POSTGRES_PASSWORD` (`.env` is not committed).
3. `docker compose up --build -d`
4. `docker compose ps` — `migrate` exits 0, `api` becomes healthy.
5. `curl http://127.0.0.1:8080/healthz`
6. Stop: `docker compose down` (add `-v` to delete the database and evidence volumes).

## Notes
- Migrations run in the `migrate` service before api and worker start.
- Evidence directory is the named volume `evidence`, mounted at `/var/proofly/evidence` (`Evidence__Directory`; the backend does not read it yet).
- The API listens on 127.0.0.1 only. The worker has no HTTP endpoint, so it has no healthcheck.
- Images: `backend/Dockerfile` has targets `api` and `worker` (non-root user `app`).
