# CLI HTML-only 集成测试：种子库里第 2 封的 text_plain 为 NULL，只保留 HTML。
# 验证 show 默认从 HTML 转文本、--html 输出原始 HTML、--json 的 textSource/html 字段。
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
    if ($body.textSource -ne 'html') { throw "textSource 应为 html，实际 $($body.textSource)：$show" }
    if ($body.text -notmatch 'HTML only') { throw "默认正文没有从 HTML 转出来：$show" }
    if ($body.PSObject.Properties.Name -contains 'html') { throw "没加 --html 时 JSON 不应带 html 字段：$show" }

    $html = & $cli show 2 --html --json --no-start
    if ($LASTEXITCODE -ne 0) { throw "show --html --json 退出 $LASTEXITCODE" }
    $htmlBody = $html | ConvertFrom-Json
    if ($htmlBody.html -notmatch '<b>HTML</b>') { throw "JSON 没有原始 HTML：$html" }

    $human = & $cli show 2 --no-start
    if ($LASTEXITCODE -ne 0) { throw "show 退出 $LASTEXITCODE" }
    $humanText = $human -join "`n"
    if ($humanText -notmatch '从 HTML 正文转换') { throw "人类可读输出没有说明 HTML 回退：$humanText" }
    if ($humanText -notmatch 'HTML only') { throw "人类可读输出没有正文：$humanText" }

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
