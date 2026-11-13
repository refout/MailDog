[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

$uiPackages = dotnet list (Join-Path $root 'src/MailDog.UI/MailDog.UI.csproj') package --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Failed to list UI package references.' }

$mailLeaks = $uiPackages | Select-String -Pattern '^\s*>?\s*(MailKit|MimeKit)\s'
if ($mailLeaks) {
    $mailLeaks | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw 'MailDog.UI must not reference MailKit/MimeKit.'
}

$enginePackages = dotnet list (Join-Path $root 'src/MailDog.Engine/MailDog.Engine.csproj') package --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Failed to list Engine package references.' }

$uiLeaks = $enginePackages | Select-String -Pattern '^\s*>?\s*Avalonia'
if ($uiLeaks) {
    $uiLeaks | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw 'MailDog.Engine must not reference Avalonia.'
}

Write-Host 'Dependency direction verification passed.' -ForegroundColor Green
