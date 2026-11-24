# CLI 集成测试：拿一份预置的小数据库起一个真实的 Engine，再让 CLI 走命名管道读它。
# 只用只读命令与 send --dry-run，不会发信、不改用户数据（数据库是临时副本）。
$ErrorActionPreference = 'Stop'
# GitHub 的 pwsh 默认把非零退出码当异常，这里要读退出码。
$PSNativeCommandUseErrorActionPreference = $false

$engine = 'src/MailDog.Engine/bin/Release/net11.0-windows10.0.19041.0/MailDog.Engine.exe'
$cli = 'src/MailDog.Cli/bin/Release/net11.0/MailDog.Cli.exe'

foreach ($path in @($engine, $cli, 'src/build/testdata/cli-seed.db')) {
    if (-not (Test-Path $path)) { throw "缺少 $path，先跑一次 Release 构建。" }
}

$database = Join-Path $env:RUNNER_TEMP 'maildog-cli-seed.db'
Copy-Item 'src/build/testdata/cli-seed.db' $database -Force
Write-Host "seed db: $database"

$engineProcess = Start-Process $engine -ArgumentList @('--db', "`"$database`"", '--console') -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        & $cli accounts --json --no-start *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Engine 30 秒内没有就绪。' }

    $accounts = & $cli accounts --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "accounts 退出 $LASTEXITCODE" }
    $account = $accounts | ConvertFrom-Json
    if ($account.email -ne 'seed@example.com') { throw "accounts 没返回种子账号：$accounts" }

    $list = & $cli list --json --limit 5 --no-start
    if ($LASTEXITCODE -ne 0) { throw "list 退出 $LASTEXITCODE" }
    $rows = @($list | ConvertFrom-Json)
    if ($rows.Count -lt 3) { throw "list 只返回了 $($rows.Count) 封" }

    $search = & $cli search haystack --json --all --no-start
    if ($LASTEXITCODE -ne 0) { throw "search 退出 $LASTEXITCODE" }
    if (($search | ConvertFrom-Json).subject -ne 'Seed mail two') { throw "search 没命中正文里的 haystack：$search" }

    $show = & $cli show 2 --body --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "show 退出 $LASTEXITCODE" }
    if (($show | ConvertFrom-Json).text -notmatch 'haystack') { throw "show 没读到种子正文：$show" }

    $send = & $cli send --account seed-account-0001 --to someone@example.com --subject 'Seed dry run' --text 'hello from seed' --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "send --dry-run 退出 $LASTEXITCODE" }
    if (-not ($send | ConvertFrom-Json).dryRun) { throw "send 没有进入 dry-run：$send" }

    Write-Host 'CLI seed integration passed'
}
finally {
    if ($engineProcess -and -not $engineProcess.HasExited) {
        Stop-Process -Id $engineProcess.Id -Force
    }

    Remove-Item $database -Force -ErrorAction SilentlyContinue
    Remove-Item "$($database)-wal" -Force -ErrorAction SilentlyContinue
    Remove-Item "$($database)-shm" -Force -ErrorAction SilentlyContinue
}
