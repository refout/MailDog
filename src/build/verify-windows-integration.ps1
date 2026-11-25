param(
    [string]$EnginePath = '',
    [switch]$SkipJumpList
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $EnginePath) {
    $candidates = Get-ChildItem (Join-Path $repoRoot 'src\MailDog.Engine\bin\Release') -Recurse -Filter 'MailDog.Engine.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    $EnginePath = $candidates | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $EnginePath -or -not (Test-Path $EnginePath)) {
    throw '找不到 MailDog.Engine.exe，请先 dotnet build 或传 -EnginePath。'
}

$arguments = @('--register-integration')
if (-not $SkipJumpList) {
    $arguments += @('--test-jumplist', '900001')
}

& $EnginePath @arguments | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Engine 注册失败，退出码 $LASTEXITCODE。"
}

$checks = @(
    @{ Path = 'HKCU:\Software\Classes\MailDog.mailto\shell\open\command'; Name = '(default)' },
    @{ Path = 'HKCU:\Software\Classes\MailDog.Eml\shell\open\command'; Name = '(default)' },
    @{ Path = 'HKCU:\Software\Clients\Mail\MailDog\shell\open\command'; Name = '(default)' },
    @{ Path = 'HKCU:\Software\RegisteredApplications'; Name = 'MailDog' }
)

foreach ($check in $checks) {
    $value = (Get-ItemProperty -Path $check.Path -Name $check.Name -ErrorAction Stop).$($check.Name)
    if (-not $value) {
        throw "注册表缺少 $($check.Path) / $($check.Name)。"
    }
}

$sendTo = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\MailDog.lnk'
if (-not (Test-Path $sendTo)) {
    throw "SendTo 快捷方式未生成：$sendTo"
}

if (-not $SkipJumpList) {
    $jumpList = Join-Path $env:LOCALAPPDATA 'MailDog\JumpList'
    $links = Get-ChildItem $jumpList -Filter 'mail-*.lnk' -ErrorAction SilentlyContinue
    if (-not $links) {
        throw "JumpList 快捷方式未生成：$jumpList"
    }
}

Write-Host 'Windows 集成验收通过：mailto / .eml / 默认邮件程序 / SendTo / JumpList。' -ForegroundColor Green
