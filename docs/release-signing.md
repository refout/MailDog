# 代码签名

MailDog 的 Windows 产物由 `src/build/sign.ps1` 签名。没有证书时脚本直接跳过，不影响本地构建。

## 签名范围

- `MailDog.Engine.exe`
- `MailDog.UI.exe`
- `MailDog.Cli.exe`（打包进 `cli/MailDog-cli.zip`）
- `MailDog-<版本>-win-x64-setup.exe` 由 Inno Setup 在已签名的 exe 基础上生成

`package.ps1` 在发布结果生成后、压缩与打安装包之前调用签名脚本，因此 zip 和 setup.exe 内的 exe 都是签名后的版本。

## 证书来源

生产环境使用 CA 签发的代码签名证书（PFX）。CI 通过仓库 Secrets 注入：

| 名称 | 类型 | 用途 |
| --- | --- | --- |
| `MAILDOG_SIGN_PFX_BASE64` | Secret | PFX 的 Base64 编码 |
| `MAILDOG_SIGN_PFX_PASSWORD` | Secret | PFX 密码 |
| `MAILDOG_SIGN_TIMESTAMP_URL` | Variable | RFC 3161 时间戳地址，默认 `https://timestamp.digicert.com` |

只配置变量、不配置 PFX 时，签名步骤会跳过，产物仍可发布但为未签名。

生成 Base64：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('MailDog.pfx')) | Set-Clipboard
```

## 本地验证

用自签证书验证签名链路，不需要 CA：

```powershell
$pwd = ConvertTo-SecureString 'TestPassword' -AsPlainText -Force
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MailDog Test' `
  -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable
Export-PfxCertificate -Cert $cert -FilePath "$env:TEMP\maildog-test.pfx" -Password $pwd

pwsh -NoProfile -File src\build\sign.ps1 `
  -Path dist\win-x64\MailDog `
  -PfxPath "$env:TEMP\maildog-test.pfx" `
  -PfxPassword TestPassword `
  -TimestampUrl '' `
  -Require

Get-AuthenticodeSignature dist\win-x64\MailDog\MailDog.Engine.exe
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
```

自签证书的签名状态会显示 `UnknownError`（根证书不受信任），但 `SignerCertificate` 应存在。换用受信任 CA 证书后状态应为 `Valid`。

## 失败处理

- `-Require`：有 PFX 但签名失败时终止；CI 使用该模式。
- 没有 PFX：输出“跳过签名”并以 0 退出，方便本地和 PR 构建。
- 时间戳服务器不可达时会签名失败；CI 可临时把 `MAILDOG_SIGN_TIMESTAMP_URL` 设为空，但正式发布不建议去掉时间戳。
