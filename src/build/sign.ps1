[CmdletBinding()]
param(
    # 一个或多个待签名的 exe；也可以传目录，自动签 MailDog.Engine.exe / MailDog.UI.exe / MailDog.Cli.exe。
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    # PFX 文件；不传时读取 MAILDOG_SIGN_PFX_BASE64（Base64 编码的 PFX）。
    [string]$PfxPath,

    # PFX 密码；不传时读取 MAILDOG_SIGN_PFX_PASSWORD。
    [string]$PfxPassword,

    # RFC 3161 时间戳地址；留空则不打时间戳（仅建议本地测试使用）。
    [string]$TimestampUrl = 'https://timestamp.digicert.com',

    # 有证书时签名失败是否终止；CI 使用 $true。
    [switch]$Require
)

$ErrorActionPreference = 'Stop'

if ($env:MAILDOG_SIGN_TIMESTAMP_URL) {
    $TimestampUrl = $env:MAILDOG_SIGN_TIMESTAMP_URL
}

function Resolve-Files {
    param([string[]]$inputs)
    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($inputPath in $inputs) {
        if (-not (Test-Path $inputPath)) {
            throw "待签名路径不存在：$inputPath"
        }

        if (Test-Path $inputPath -PathType Container) {
            foreach ($name in @('MailDog.Engine.exe', 'MailDog.UI.exe', 'MailDog.Cli.exe')) {
                $candidate = Join-Path $inputPath $name
                if (Test-Path $candidate) {
                    $files.Add((Resolve-Path $candidate).Path)
                }
            }
        }
        else {
            $files.Add((Resolve-Path $inputPath).Path)
        }
    }

    return $files | Select-Object -Unique
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $candidate = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Get-Certificate {
    param([string]$pfx, [string]$password)

    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        $pfx,
        $secure,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
}

$files = Resolve-Files -inputs $Path
if (-not $files) {
    throw '没有找到可签名的 exe。'
}

$temporaryPfx = $false
$certificate = $null

try {
    if (-not $PfxPath -and $env:MAILDOG_SIGN_PFX_BASE64) {
        $bytes = [Convert]::FromBase64String($env:MAILDOG_SIGN_PFX_BASE64)
        $PfxPath = Join-Path ([IO.Path]::GetTempPath()) "maildog-sign-$([guid]::NewGuid().ToString('N')).pfx"
        [IO.File]::WriteAllBytes($PfxPath, $bytes)
        $temporaryPfx = $true
    }

    if (-not $PfxPath) {
        if ($Require) {
            throw '未提供签名证书：请设置 MAILDOG_SIGN_PFX_BASE64 或传 -PfxPath。'
        }

        Write-Host '未配置签名证书，跳过签名。' -ForegroundColor Yellow
        return
    }

    if (-not (Test-Path $PfxPath)) {
        throw "PFX 不存在：$PfxPath"
    }

    if (-not $PfxPassword) {
        $PfxPassword = $env:MAILDOG_SIGN_PFX_PASSWORD
    }

    if (-not $PfxPassword) {
        throw 'PFX 密码为空：请设置 MAILDOG_SIGN_PFX_PASSWORD 或传 -PfxPassword。'
    }

    $signTool = Find-SignTool

    foreach ($file in $files) {
        Write-Host "签名：$file" -ForegroundColor Cyan

        if ($signTool) {
            $arguments = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $PfxPassword)
            if ($TimestampUrl) {
                $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
            }
            $arguments += $file

            & $signTool @arguments
            if ($LASTEXITCODE -ne 0) {
                throw "signtool 签名失败（退出码 $LASTEXITCODE）：$file"
            }
        }
        else {
            if (-not $certificate) {
                $certificate = Get-Certificate -pfx $PfxPath -password $PfxPassword
            }

            $parameters = @{
                FilePath = $file
                Certificate = $certificate
                HashAlgorithm = 'SHA256'
            }
            if ($TimestampUrl) {
                $parameters.TimestampServer = $TimestampUrl
            }

            Set-AuthenticodeSignature @parameters | Out-Null
        }

        $signature = Get-AuthenticodeSignature $file
        if (-not $signature.SignerCertificate) {
            throw "签名后未读到签名者证书：$file"
        }

        Write-Host "  签名者：$($signature.SignerCertificate.Subject)" -ForegroundColor Green
    }
}
finally {
    if ($temporaryPfx) {
        Remove-Item $PfxPath -Force -ErrorAction SilentlyContinue
    }
}
