[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$srcRoot = Split-Path -Parent $PSScriptRoot

Push-Location $srcRoot
try {
    $uiPackages = dotnet list (Join-Path $srcRoot 'MailDog.UI/MailDog.UI.csproj') package --include-transitive
    if ($LASTEXITCODE -ne 0) { throw 'Failed to list UI package references.' }

    $mailLeaks = $uiPackages | Select-String -Pattern '^\s*>?\s*(MailKit|MimeKit)\s'
    if ($mailLeaks) {
        $mailLeaks | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
        throw 'MailDog.UI must not reference MailKit/MimeKit.'
    }

    $enginePackages = dotnet list (Join-Path $srcRoot 'MailDog.Engine/MailDog.Engine.csproj') package --include-transitive
    if ($LASTEXITCODE -ne 0) { throw 'Failed to list Engine package references.' }

    $uiLeaks = $enginePackages | Select-String -Pattern '^\s*>?\s*Avalonia'
    if ($uiLeaks) {
        $uiLeaks | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
        throw 'MailDog.Engine must not reference Avalonia.'
    }
}
finally {
    Pop-Location
}

Write-Host 'Dependency direction verification passed.' -ForegroundColor Green
