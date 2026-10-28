#!/usr/bin/env bash
# Local dev backend for the web app: PostgreSQL -> migrate proofly_web -> seed demo data -> API (Auth:Mode=Local).
#   scripts/dev-backend.sh [start]   start (idempotent: reuses a running API, keeps seeded data)
#   scripts/dev-backend.sh start --recreate   drop proofly_web and reseed
#   scripts/dev-backend.sh stop | status
# Everything it writes lives in .dev/ (gitignored): api.log, api.pid, api.port, signing-key, tokens.txt, evidence/.
# Never use in production: Auth:Mode=Local issues tokens for fixed users.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEV="$ROOT/.dev"
PG_HOST="${PROOFLY_PG_HOST:-localhost}"
PG_PORT="${PROOFLY_PG_PORT:-55432}"
PG_USER="${PROOFLY_PG_USER:-proofly}"
DB="${PROOFLY_DB:-proofly_web}"
SCALE="${PROOFLY_SEED_SCALE:-0.02}"
API_PORT_START="${PROOFLY_API_PORT:-5080}"
WEB_ORIGINS="${PROOFLY_WEB_ORIGINS:-http://localhost:5199,http://127.0.0.1:5199,http://localhost:5173,http://127.0.0.1:5173}"
CONN="Host=$PG_HOST;Port=$PG_PORT;Username=$PG_USER;Database=$DB"

cmd="${1:-start}"; shift || true
RECREATE=""; [[ "${1:-}" == "--recreate" ]] && RECREATE="--recreate"

port_free() { ! lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
api_running() { [[ -f "$DEV/api.pid" ]] && kill -0 "$(cat "$DEV/api.pid")" 2>/dev/null; }

stop_api() {
  if api_running; then kill "$(cat "$DEV/api.pid")" && echo "stopped API (pid $(cat "$DEV/api.pid"))"; fi
  rm -f "$DEV/api.pid" "$DEV/api.port"
}

case "$cmd" in
  stop) stop_api; exit 0 ;;
  status)
    if api_running; then echo "API running on http://127.0.0.1:$(cat "$DEV/api.port") (pid $(cat "$DEV/api.pid")); tokens: $DEV/tokens.txt"; else echo "API not running"; fi
    exit 0 ;;
  start) ;;
  *) echo "usage: $0 [start [--recreate]|stop|status]" >&2; exit 2 ;;
esac

mkdir -p "$DEV/evidence"; chmod 700 "$DEV"

# (a) PostgreSQL. Default: the existing local server (localhost:55432, user proofly, trust auth).
# PROOFLY_PG_DOCKER=1 starts a throwaway container with the same settings if nothing listens there.
pg_up() { pg_isready -q -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" 2>/dev/null || psql -qtAc 'select 1' "host=$PG_HOST port=$PG_PORT user=$PG_USER dbname=postgres" >/dev/null 2>&1; }
if ! pg_up; then
  if [[ "${PROOFLY_PG_DOCKER:-}" == "1" ]] && command -v docker >/dev/null; then
    echo "starting postgres:16-alpine as proofly-dev-pg on port $PG_PORT"
    docker start proofly-dev-pg >/dev/null 2>&1 || docker run -d --name proofly-dev-pg -p "$PG_PORT":5432 \
      -e POSTGRES_USER="$PG_USER" -e POSTGRES_HOST_AUTH_METHOD=trust postgres:16-alpine >/dev/null
    for _ in $(seq 1 30); do pg_up && break; sleep 1; done
  fi
  pg_up || { echo "PostgreSQL is not reachable at $PG_HOST:$PG_PORT (user $PG_USER). Start it or set PROOFLY_PG_DOCKER=1." >&2; exit 1; }
fi

# (b) database + migrations (the seed tool re-applies pending ones too)
if [[ -n "$RECREATE" ]]; then stop_api; fi
if [[ -z "$(psql -qtAc "select 1 from pg_database where datname='$DB'" "host=$PG_HOST port=$PG_PORT user=$PG_USER dbname=postgres")" ]]; then
  psql -q -c "create database $DB" "host=$PG_HOST port=$PG_PORT user=$PG_USER dbname=postgres"
fi
echo "migrating $DB"
ConnectionStrings__Proofly="$CONN" Evidence__Directory="$DEV/evidence" dotnet run --project "$ROOT/backend/src/Proofly.Worker" -- migrate

# (c) demo data through the real registry/ingestor and run.process (skipped when the database already holds runs)
dotnet run -c Release --project "$ROOT/backend/tools/Proofly.Benchmarks" -- \
  --seed-dev --scale "$SCALE" --database "$DB" --evidence-dir "$DEV/evidence" $RECREATE

# (d) API
if api_running; then
  echo "API already running on http://127.0.0.1:$(cat "$DEV/api.port")"; exit 0
fi
PORT=""
for p in $(seq "$API_PORT_START" $((API_PORT_START + 19))); do port_free "$p" && { PORT="$p"; break; }; done
[[ -n "$PORT" ]] || { echo "no free port in $API_PORT_START..$((API_PORT_START + 19))" >&2; exit 1; }
[[ -f "$DEV/signing-key" ]] || { umask 077; openssl rand -hex 32 > "$DEV/signing-key"; }

dotnet build -c Debug "$ROOT/backend/src/Proofly.Api" -nologo -v q >/dev/null
: > "$DEV/api.log"
i=0; CORS=()
IFS=',' read -r -a ORIGINS <<< "$WEB_ORIGINS"
for o in "${ORIGINS[@]}"; do CORS+=("Cors__AllowedOrigins__$i=$o"); i=$((i + 1)); done
env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  ConnectionStrings__Proofly="$CONN;Maximum Pool Size=20" Evidence__Directory="$DEV/evidence" \
  Auth__Mode=Local Auth__Local__LogTokens=true Auth__Local__SigningKey="$(cat "$DEV/signing-key")" "${CORS[@]}" \
  nohup dotnet "$ROOT/backend/src/Proofly.Api/bin/Debug/net10.0/Proofly.Api.dll" >> "$DEV/api.log" 2>&1 &
echo $! > "$DEV/api.pid"; echo "$PORT" > "$DEV/api.port"

for _ in $(seq 1 60); do
  curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  api_running || { echo "API exited; see $DEV/api.log" >&2; tail -20 "$DEV/api.log" >&2; exit 1; }
  sleep 1
done
curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null || { echo "API did not become healthy; see $DEV/api.log" >&2; exit 1; }

# tokens per role, from the API log (Auth:Local:LogTokens). Treat the file like a password.
( umask 077; grep -h 'Local token for' "$DEV/api.log" | sed -nE 's/.*Local token for \\"([^\\]+)\\" \(\\"([^\\]+)\\"\): \\"([^\\"]+)\\".*/\2 \1 \3/p' | sort -u > "$DEV/tokens.txt" )
echo
echo "API:    http://127.0.0.1:$PORT/api/v1   (log $DEV/api.log, pid $(cat "$DEV/api.pid"))"
echo "Tokens: $DEV/tokens.txt  (role, user id, token; valid 8 h; same signing key across restarts)"
echo "Web:    cd apps/web && PROOFLY_API_PORT=$PORT WEB_PORT=5199 VITE_DEV_TOKEN=\$(awk '\$1==\"ANALYST\"{print \$3}' ../../.dev/tokens.txt) npm run dev"
