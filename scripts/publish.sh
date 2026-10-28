#!/usr/bin/env bash
# Self-contained single-file publish of Api and Worker.
# Usage: scripts/publish.sh [rid ...]   (default: linux-x64 win-x64)   Output: artifacts/publish/<rid>/{api,worker}
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rids=("$@"); [ ${#rids[@]} -eq 0 ] && rids=(linux-x64 win-x64)
for rid in "${rids[@]}"; do
  for p in Api Worker; do
    out="$root/artifacts/publish/$rid/$(echo "$p" | tr A-Z a-z)"
    dotnet publish "$root/backend/src/Proofly.$p/Proofly.$p.csproj" -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=None -o "$out"
  done
done
