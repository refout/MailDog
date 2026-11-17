[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 覆盖程序集版本（打包流水线传入标签版本号时使用）。
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$srcRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $srcRoot
$artifacts = Join-Path $repoRoot "artifacts/$Runtime"
$dist = Join-Path $repoRoot "dist/$Runtime/MailDog"

$publishArgs = @(
    '-c', $Configuration
    '-r', $Runtime
    '-p:PublishAot=true'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:StripSymbols=true'
    '--nologo'
)

if ($Version) {
    $publishArgs += "-p:Version=$Version"
}

Push-Location $srcRoot
try {
    Write-Host "Publishing MailDog.Engine ($Runtime, $Configuration)..."
    dotnet publish (Join-Path $srcRoot 'MailDog.Engine/MailDog.Engine.csproj') @publishArgs -o (Join-Path $artifacts 'engine')
    if ($LASTEXITCODE -ne 0) { throw "Engine publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing MailDog.UI ($Runtime, $Configuration)..."
    dotnet publish (Join-Path $srcRoot 'MailDog.UI/MailDog.UI.csproj') @publishArgs -o (Join-Path $artifacts 'ui')
    if ($LASTEXITCODE -ne 0) { throw "UI publish failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $artifacts 'engine/MailDog.Engine.exe') $dist -Force
Copy-Item (Join-Path $artifacts 'ui/MailDog.UI.exe') $dist -Force

$package = Join-Path (Split-Path -Parent $dist) "MailDog-$Runtime.zip"
Remove-Item $package -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $package -Force

Write-Host "`nPublished files:"
Get-ChildItem $dist | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize

Write-Host "Package: $package"
Get-Item $package | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
