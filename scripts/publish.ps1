# Self-contained single-file publish of Api and Worker.
# Usage: scripts\publish.ps1 [-Rids linux-x64,win-x64]   Output: artifacts\publish\<rid>\{api,worker}
param([string[]]$Rids = @('linux-x64', 'win-x64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($rid in $Rids) {
  foreach ($p in 'Api', 'Worker') {
    $out = Join-Path $root "artifacts\publish\$rid\$($p.ToLower())"
    dotnet publish "$root\backend\src\Proofly.$p\Proofly.$p.csproj" -c Release -r $rid --self-contained `
      -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:DebugType=None -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish $p $rid failed" }
  }
}
