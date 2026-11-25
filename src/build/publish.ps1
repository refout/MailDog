[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
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

$isWindowsTarget = $Runtime -like 'win-*'
$exeSuffix = if ($isWindowsTarget) { '.exe' } else { '' }
$engineFramework = if ($isWindowsTarget) { 'net11.0-windows10.0.19041.0' } else { 'net11.0' }

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
    dotnet publish (Join-Path $srcRoot 'MailDog.Engine/MailDog.Engine.csproj') @publishArgs -f $engineFramework -o (Join-Path $artifacts 'engine')
    if ($LASTEXITCODE -ne 0) { throw "Engine publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing MailDog.UI ($Runtime, $Configuration)..."
    dotnet publish (Join-Path $srcRoot 'MailDog.UI/MailDog.UI.csproj') @publishArgs -o (Join-Path $artifacts 'ui')
    if ($LASTEXITCODE -ne 0) { throw "UI publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing MailDog.Cli ($Runtime, $Configuration)..."
    dotnet publish (Join-Path $srcRoot 'MailDog.Cli/MailDog.Cli.csproj') @publishArgs -o (Join-Path $artifacts 'cli')
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $artifacts "engine/MailDog.Engine$exeSuffix") $dist -Force
Copy-Item (Join-Path $artifacts "ui/MailDog.UI$exeSuffix") $dist -Force
Copy-Item (Join-Path $artifacts "cli/MailDog.Cli$exeSuffix") $dist -Force

if (-not $isWindowsTarget) {
    # Linux NativeAOT 的单文件不会把 Skia/HarfBuzz 原生库塞进去，必须和 UI 放同一目录。
    foreach ($library in @('libSkiaSharp.so', 'libHarfBuzzSharp.so')) {
        Copy-Item (Join-Path $artifacts "ui/$library") $dist -Force
    }
}

if ($isWindowsTarget) {
    # maildog.cmd 只是个转发壳：命令行里敲 maildog 实际跑同目录的 MailDog.Cli.exe。
    $shim = Join-Path $dist 'maildog.cmd'
    Set-Content -Path $shim -Encoding ascii -NoNewline -Value @"
@echo off
"%~dp0MailDog.Cli.exe" %*
"@
}
else {
    # Linux 下给一个同名启动脚本，命令行里敲 maildog 即可。
    $shim = Join-Path $dist 'maildog'
    Set-Content -Path $shim -Encoding ascii -NoNewline -Value @"
#!/usr/bin/env sh
exec "`$(dirname "`$0")/MailDog.Cli" "`$@"
"@
    & chmod +x $shim
}

$archiveExtension = if ($isWindowsTarget) { 'zip' } else { 'tar.gz' }
$package = Join-Path (Split-Path -Parent $dist) "MailDog-$Runtime.$archiveExtension"
Remove-Item $package -Force -ErrorAction SilentlyContinue

if ($isWindowsTarget) {
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $package -Force
}
else {
    & tar -czf $package -C $dist .
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }
}

Write-Host "`nPublished files:"
Get-ChildItem $dist | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize

Write-Host "Package: $package"
Get-Item $package | Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
