param(
    [string]$OldVersion = '0.3.3',
    [string]$NewEnginePath = '',
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cli = Join-Path $repoRoot 'src\MailDog.Cli\bin\Release\net11.0\MailDog.Cli.exe'

if (-not $NewEnginePath) {
    $NewEnginePath = Get-ChildItem (Join-Path $repoRoot 'src\MailDog.Engine\bin\Release') -Recurse -Filter 'MailDog.Engine.exe' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not (Test-Path $NewEnginePath)) {
    throw "找不到当前构建的 MailDog.Engine.exe，请先 dotnet build：$NewEnginePath"
}

if (-not (Test-Path $cli)) {
    throw "找不到 CLI，请先 dotnet build：$cli"
}

if (Get-Process MailDog.Engine -ErrorAction SilentlyContinue) {
    throw '检测到 MailDog.Engine 正在运行。先执行 maildog quit 或关闭托盘，避免命名管道冲突。'
}

if (-not $WorkRoot) {
    $WorkRoot = Join-Path $env:TEMP "maildog-rollback-$([Guid]::NewGuid().ToString('N'))"
}

New-Item -ItemType Directory -Force $WorkRoot | Out-Null
$data = Join-Path $WorkRoot 'data'
New-Item -ItemType Directory -Force $data | Out-Null
$database = Join-Path $data 'rollback.db'
$zip = Join-Path $WorkRoot "MailDog-$OldVersion-win-x64.zip"
$oldApp = Join-Path $WorkRoot "old-$OldVersion"

if (-not (Test-Path $zip)) {
    $url = "https://github.com/refout/MailDog/releases/download/v$OldVersion/MailDog-$OldVersion-win-x64.zip"
    Invoke-WebRequest $url -OutFile $zip
}

if (-not (Test-Path (Join-Path $oldApp 'MailDog.Engine.exe'))) {
    Expand-Archive $zip $oldApp
}

function Start-TestEngine([string]$exe) {
    $process = Start-Process $exe -ArgumentList @('--db', "`"$database`"") -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) {
        throw "Engine 启动后立即退出：$exe（退出码 $($process.ExitCode)）"
    }

    return $process
}

function Stop-TestEngine([System.Diagnostics.Process]$process) {
    & $cli quit | Out-Null
    if (-not $process.WaitForExit(10000)) {
        $process.Kill($true)
        throw "Engine 未在 10 秒内正常退出：$($process.Id)"
    }

    if ($process.ExitCode -ne 0) {
        throw "Engine 退出码异常：$($process.ExitCode)"
    }
}

try {
    Write-Host "1/4 旧版 $OldVersion 创建数据库" -ForegroundColor Cyan
    $oldEngine = Join-Path $oldApp 'MailDog.Engine.exe'
    $process = Start-TestEngine $oldEngine
    Stop-TestEngine $process
    if (-not (Test-Path $database)) {
        throw "旧版未创建数据库：$database"
    }

    Write-Host '2/4 当前版本升级数据库并生成快照' -ForegroundColor Cyan
    $process = Start-TestEngine $NewEnginePath
    Stop-TestEngine $process
    $backup = Get-ChildItem $data -Filter '*.pre-v*.bak' | Select-Object -First 1
    if (-not $backup) {
        throw '当前版本升级后没有生成 pre-v*.bak 快照。'
    }

    Write-Host "    快照：$($backup.Name)" -ForegroundColor DarkGray

    Write-Host '3/4 旧版直接打开新版 schema' -ForegroundColor Cyan
    $process = Start-TestEngine $oldEngine
    Stop-TestEngine $process

    Write-Host '4/4 用快照回滚，旧版再次启动' -ForegroundColor Cyan
    Copy-Item $backup.FullName $database -Force
    Remove-Item "$database-wal", "$database-shm" -Force -ErrorAction SilentlyContinue
    $process = Start-TestEngine $oldEngine
    Stop-TestEngine $process

    Write-Host '升级 / 回滚验收通过。' -ForegroundColor Green
}
finally {
    Get-Process MailDog.Engine -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "测试目录：$WorkRoot" -ForegroundColor DarkGray
}
