# Run in an elevated PowerShell. Installs Proofly API and worker as Windows Services.
param(
  [string]$Root = 'C:\Proofly',          # contains current\api\Proofly.Api.exe and current\worker\Proofly.Worker.exe
  [string]$ServiceAccount = 'NT SERVICE\ProoflyApi'  # virtual account; grant it modify rights on the evidence directory
)
$ErrorActionPreference = 'Stop'
$services = @(
  @{ Name = 'ProoflyApi';    Display = 'Proofly API';    Exe = "$Root\current\api\Proofly.Api.exe" },
  @{ Name = 'ProoflyWorker'; Display = 'Proofly Worker'; Exe = "$Root\current\worker\Proofly.Worker.exe" }
)
foreach ($s in $services) {
  sc.exe create $s.Name binPath= "`"$($s.Exe)`"" DisplayName= "$($s.Display)" start= delayed-auto obj= "NT SERVICE\$($s.Name)" | Out-Null
  sc.exe description $s.Name "$($s.Display) (customer-hosted Proofly data plane)" | Out-Null
  sc.exe failure $s.Name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
  # Machine-level environment for the service only (REG_MULTI_SZ). Edit values before running; do not commit them.
  $env = @('DOTNET_ENVIRONMENT=Production', 'ASPNETCORE_ENVIRONMENT=Production',
           "Evidence__Directory=$Root\evidence", 'Auth__Mode=Oidc')
  # ConnectionStrings__Proofly (contains the password) must be added the same way, or use a secured appsettings.Production.json next to the exe.
  Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($s.Name)" -Name Environment -Type MultiString -Value $env
}
New-Item -ItemType Directory -Force "$Root\evidence" | Out-Null
icacls "$Root\evidence" /grant "NT SERVICE\ProoflyApi:(OI)(CI)M" "NT SERVICE\ProoflyWorker:(OI)(CI)M" | Out-Null
Write-Host 'Installed. Run migrations first:  & "$Root\current\worker\Proofly.Worker.exe" migrate   then: sc.exe start ProoflyApi; sc.exe start ProoflyWorker'
