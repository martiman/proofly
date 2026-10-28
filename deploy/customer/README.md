# Customer installation (Linux systemd and Windows Service)

Status: authored and partly verified. Single-file publish was exercised on the build host only; the unit files, the Windows script and the procedures below have NOT been run on a target machine.

## Build
`scripts/publish.sh [rid ...]` or `scripts/publish.ps1` produce `artifacts/publish/<rid>/{api,worker}` (self-contained, single file, ReadyToRun). Default RIDs: `linux-x64`, `win-x64`. No .NET runtime is needed on the target.

## Layout (Linux)
- `/opt/proofly/releases/<version>/{api,worker}` and symlink `/opt/proofly/current` -> active release
- `/etc/proofly/proofly.env` (from `proofly.env.example`, mode 0640 root:proofly)
- `/var/lib/proofly/evidence` evidence directory, owned by `proofly`
- user `proofly` (no login shell)

## Install (Linux)
1. Copy a published release to `/opt/proofly/releases/<version>` and `ln -sfn` it to `/opt/proofly/current`.
2. Create the config: `proofly.env` and, optionally, `appsettings.Production.json` (see `appsettings.production.example.json`) next to the binaries.
3. `sudo -u proofly env $(cat /etc/proofly/proofly.env | xargs) /opt/proofly/current/worker/Proofly.Worker migrate`
4. Install `proofly-api.service` and `proofly-worker.service` to `/etc/systemd/system/`, then `systemctl daemon-reload && systemctl enable --now proofly-api proofly-worker`.
5. Put a TLS-terminating reverse proxy in front of the API (it listens on loopback only by default).

## Install (Windows)
Run `install-windows-service.ps1` elevated, set `ConnectionStrings__Proofly` in the service environment (or a ACL-restricted `appsettings.Production.json`), run `Proofly.Worker.exe migrate`, then `sc.exe start ProoflyApi` and `ProoflyWorker`. Services use `UseWindowsService`/`UseSystemd`, so they log to stdout JSON (journald on Linux) and stop gracefully.

## Configuration contract
Environment variables override JSON (`__` replaces `:`). The process refuses to start (exit with a list of all problems) when:
- `ConnectionStrings:Proofly` is missing;
- `Auth:Mode=Local` in Production (Local is evaluation only);
- `Evidence:Directory` is missing in Production, or configured but absent on disk.

| Key | Meaning | Default |
|---|---|---|
| `ConnectionStrings:Proofly` | Npgsql connection string (put the password in the env file) | none, required |
| `Evidence:Directory` | writable evidence store | none, required in Production |
| `Auth:Mode`, `Auth:Oidc:*` | `Oidc` for production | `Oidc` |
| `Cors:AllowedOrigins` | web origins | dev origins |
| `Database:MigrateOnStartup` | apply migrations on start (leave false; use `migrate`) | false |
| `Observability:Metrics:Enabled` | expose Prometheus metrics | false |
| `Observability:Metrics:AllowedAddresses` | API: peers allowed to read `/metrics` | loopback |
| `Observability:Metrics:Port` | API: only serve `/metrics` on this local port (add a second URL to `ASPNETCORE_URLS`) | any |
| `Observability:Metrics:ListenerPrefix` | worker: HttpListener prefix | `http://127.0.0.1:9465/` |
| `ASPNETCORE_URLS` | API bind address | `http://127.0.0.1:8080` |

## Health and observability
- `GET /healthz` liveness: process responds; no dependencies.
- `GET /readyz` readiness: 200 only when PostgreSQL is reachable, all embedded migrations are applied and the evidence directory is writable; otherwise 503 with the failing check named (no secrets in the body).
- `/metrics` (API) and the worker listener expose ASP.NET Core HTTP metrics and the `Proofly.Jobs` counters (`proofly.jobs.succeeded|failed|dead`) in Prometheus format. Disabled by default.
- Logs: one compact JSON object per line on stdout with `RequestId` (API) and `RunId` (worker, via `RunLogScope.Begin`) when in scope. Connection strings and tokens are never logged by these components.

## Upgrade
1. Back up (below). 2. Publish/copy the new release to `/opt/proofly/releases/<new>`. 3. `systemctl stop proofly-api proofly-worker`. 4. Run `Proofly.Worker migrate` from the new release. 5. `ln -sfn` current, start both services. 6. Check `/readyz` returns 200.

## Rollback
Migrations are forward-only. If the new release fails before or during `migrate`: stop services, repoint `current` to the previous release, start. If migrations were applied and the old release is incompatible: stop services, restore the pre-upgrade database dump and evidence snapshot, repoint `current`, start. Decide before upgrading which applies by reading the release notes.

## Backup and restore
- PostgreSQL: `pg_dump -Fc -h db -U proofly proofly > proofly-<date>.dump`; restore into an empty database with `pg_restore --clean --if-exists -d proofly proofly-<date>.dump`.
- Evidence directory: snapshot or `rsync -a --delete /var/lib/proofly/evidence/ <backup>/evidence-<date>/` taken at the same time as the dump (stop the worker first for a consistent pair). Evidence files are content-addressed and immutable; restore the copy back to the same path with ownership `proofly`.
- After restore: run `migrate` (no-op if current), start services, check `/readyz`, and verify audit chain integrity via the governance endpoints.
- Test a restore regularly on a separate host.
