[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts/$Runtime"
$dist = Join-Path $root "dist/$Runtime/MailDog"

$publishArgs = @(
    '-c', $Configuration
    '-r', $Runtime
    '-p:PublishAot=true'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:StripSymbols=true'
    '--nologo'
)

Write-Host "Publishing MailDog.Engine ($Runtime, $Configuration)..."
dotnet publish (Join-Path $root 'src/MailDog.Engine/MailDog.Engine.csproj') @publishArgs -o (Join-Path $artifacts 'engine')
if ($LASTEXITCODE -ne 0) { throw "Engine publish failed with exit code $LASTEXITCODE." }

Write-Host "Publishing MailDog.UI ($Runtime, $Configuration)..."
dotnet publish (Join-Path $root 'src/MailDog.UI/MailDog.UI.csproj') @publishArgs -o (Join-Path $artifacts 'ui')
if ($LASTEXITCODE -ne 0) { throw "UI publish failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $artifacts 'engine/MailDog.Engine.exe') $dist -Force
Copy-Item (Join-Path $artifacts 'ui/MailDog.UI.exe') $dist -Force

Write-Host "`nPublished files:"
Get-ChildItem $dist | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
