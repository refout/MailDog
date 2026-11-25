[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 复用已有的 publish 结果，只做打包（dist/<Runtime>/MailDog 必须已存在）。
    [switch]$SkipPublish,

    # 额外生成 Inno Setup 安装包（仅 win-x64）。
    [switch]$Installer,

    # ISCC.exe 路径；未指定时依次查找 PATH 与默认安装目录。
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'

$srcRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $srcRoot
$distRoot = Join-Path $repoRoot 'dist'
$appDir = Join-Path $distRoot "$Runtime/MailDog"
$isWindowsTarget = $Runtime -like 'win-*'

if (-not $Version) {
    $propsPath = Join-Path $srcRoot 'Directory.Build.props'
    $props = Get-Content $propsPath -Raw
    $match = [regex]::Match($props, '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        throw "无法从 $propsPath 读取 <Version>，请通过 -Version 指定。"
    }

    $Version = $match.Groups[1].Value.Trim()
}

Write-Host "MailDog $Version ($Runtime, $Configuration)" -ForegroundColor Cyan

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime -Configuration $Configuration -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败，退出码 $LASTEXITCODE。"
    }
}

$engineExe = Join-Path $appDir "MailDog.Engine$($isWindowsTarget ? '.exe' : '')"
$uiExe = Join-Path $appDir "MailDog.UI$($isWindowsTarget ? '.exe' : '')"
$cliExe = Join-Path $appDir "MailDog.Cli$($isWindowsTarget ? '.exe' : '')"
$cliShim = Join-Path $appDir ($isWindowsTarget ? 'maildog.cmd' : 'maildog')
foreach ($file in @($engineExe, $uiExe, $cliExe, $cliShim)) {
    if (-not (Test-Path $file)) {
        throw "缺少发布文件：$file（先运行 publish.ps1 或去掉 -SkipPublish）。"
    }
}

if (-not $isWindowsTarget) {
    foreach ($library in @('libSkiaSharp.so', 'libHarfBuzzSharp.so')) {
        $path = Join-Path $appDir $library

        if (-not (Test-Path $path)) {
            throw "缺少 Linux 原生库：$path（先运行 publish.ps1 或去掉 -SkipPublish）。"
        }
    }
}

# 压缩包：Windows 用 zip，Linux 用 tar.gz（保留可执行权限）。
$archiveExtension = $isWindowsTarget ? 'zip' : 'tar.gz'
$packagePath = Join-Path $distRoot "MailDog-$Version-$Runtime.$archiveExtension"
Remove-Item $packagePath -Force -ErrorAction SilentlyContinue

if ($isWindowsTarget) {
    Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $packagePath -Force
}
else {
    & tar -czf $packagePath -C $appDir .
    if ($LASTEXITCODE -ne 0) {
        throw "tar 打包失败，退出码 $LASTEXITCODE。"
    }
}

Write-Host "压缩包：$packagePath" -ForegroundColor Green

# 安装包
if ($Installer) {
    if ($Runtime -ne 'win-x64') {
        throw '安装包目前只支持 win-x64。'
    }

    if (-not $InnoCompiler) {
        $candidates = @(
            (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path $_) }

        $InnoCompiler = $candidates | Select-Object -First 1
    }

    if (-not $InnoCompiler) {
        throw '找不到 ISCC.exe，请先安装 Inno Setup 6（choco install innosetup）或用 -InnoCompiler 指定路径。'
    }

    $issPath = Join-Path $PSScriptRoot 'installer/MailDog.iss'
    & $InnoCompiler "/DMyAppVersion=$Version" "/DMySourceDir=$appDir" "/DMyOutputDir=$distRoot" "/DMyRuntime=$Runtime" $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup 编译失败，退出码 $LASTEXITCODE。"
    }

    $setupPath = Join-Path $distRoot "MailDog-$Version-$Runtime-setup.exe"
    if (-not (Test-Path $setupPath)) {
        throw "安装包未生成：$setupPath"
    }

    Write-Host "安装包：$setupPath" -ForegroundColor Green
}

Write-Host "`n产物：" -ForegroundColor Cyan
Get-ChildItem $distRoot -File -Filter "MailDog-$Version-*" |
    Select-Object Name, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize
