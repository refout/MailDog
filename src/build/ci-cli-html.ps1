# CLI HTML-only 集成测试：种子库里第 2 封的 text_plain 为 NULL，只保留 HTML。
# 验证 show 原样返回 HTML/纯文本：--html 输出原始 HTML、--json 同时带 text/html。
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$engine = 'src/MailDog.Engine/bin/Release/net11.0-windows10.0.19041.0/MailDog.Engine.exe'
$cli = 'src/MailDog.Cli/bin/Release/net11.0/MailDog.Cli.exe'

foreach ($path in @($engine, $cli, 'src/build/testdata/cli-html-seed.db')) {
    if (-not (Test-Path $path)) { throw "缺少 $path，先跑一次 Release 构建。" }
}

$database = Join-Path $env:RUNNER_TEMP 'maildog-cli-html-seed.db'
Copy-Item 'src/build/testdata/cli-html-seed.db' $database -Force
Write-Host "html-only seed db: $database"

$engineProcess = Start-Process $engine -ArgumentList @('--db', "`"$database`"", '--console') -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        & $cli accounts --json --no-start *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Engine 30 秒内没有就绪。' }

    $show = & $cli show 2 --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "show --json 退出 $LASTEXITCODE" }
    $body = $show | ConvertFrom-Json
    if ($null -ne $body.text) { throw "HTML-only 邮件的 text 应为 null，实际 $($body.text)：$show" }
    if ($body.html -notmatch '<b>HTML</b>') { throw "JSON 没有原始 HTML：$show" }

    $html = & $cli show 2 --html --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "show --html --json 退出 $LASTEXITCODE" }
    $htmlBody = $html | ConvertFrom-Json
    if ($htmlBody.html -notmatch '<b>HTML</b>') { throw "JSON 没有原始 HTML：$html" }

    $human = & $cli show 2 --no-start
    if ($LASTEXITCODE -ne 0) { throw "show 退出 $LASTEXITCODE" }
    $humanText = $human -join "`n"
    if ($humanText -notmatch '以下是原始 HTML 正文') { throw "人类可读输出没有说明原始 HTML：$humanText" }
    if ($humanText -notmatch '<b>HTML</b>') { throw "人类可读输出没有原始 HTML：$humanText" }

    $raw = & $cli show 2 --html --no-start
    if ($LASTEXITCODE -ne 0) { throw "show --html 退出 $LASTEXITCODE" }
    $rawText = $raw -join "`n"
    if ($rawText -notmatch '<b>HTML</b>') { throw "原始 HTML 没有输出：$rawText" }

    Write-Host 'CLI HTML-only integration passed'
}
finally {
    if ($engineProcess -and -not $engineProcess.HasExited) {
        Stop-Process -Id $engineProcess.Id -Force
    }

    Remove-Item $database -Force -ErrorAction SilentlyContinue
    Remove-Item "$($database)-wal" -Force -ErrorAction SilentlyContinue
    Remove-Item "$($database)-shm" -Force -ErrorAction SilentlyContinue
}
