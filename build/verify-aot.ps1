[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path $root "artifacts/aot-$Runtime.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null

Write-Host "Running AOT publish and capturing output to $log ..."
& (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime -Configuration $Configuration 2>&1 |
    Tee-Object -FilePath $log

if ($LASTEXITCODE -ne 0) {
    throw "AOT publish failed with exit code $LASTEXITCODE."
}

$ilIssues = Select-String -Path $log -Pattern '\b(warning|error) IL\d{4}\b'
if ($ilIssues) {
    Write-Host "`nAOT/trimming diagnostics found:" -ForegroundColor Red
    $ilIssues | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw "AOT verification failed: $($ilIssues.Count) IL warning(s)/error(s)."
}

Write-Host "AOT verification passed: no IL warnings or errors." -ForegroundColor Green
