# 崩溃上报

MailDog UI 的未处理异常仍会先写本地 `crash.log`。配置端点后，额外 POST 一份崩溃摘要；失败会落到 `crash-pending/`，下次启动重试。

## 配置

优先级从高到低：

1. 环境变量 `MAILDOG_CRASH_ENDPOINT`
2. 数据目录下 `crash-endpoint.txt`（`%LOCALAPPDATA%\MailDog\crash-endpoint.txt`）

只接受 `https://`；本机调试可用 `http://localhost`、`http://127.0.0.1` 或 `http://[::1]`。

示例：

```powershell
$env:MAILDOG_CRASH_ENDPOINT = 'https://crash.example.com/v1/maildog'
```

或写入文件：

```powershell
Set-Content "$env:LOCALAPPDATA\MailDog\crash-endpoint.txt" 'https://crash.example.com/v1/maildog' -Encoding ascii
```

## 上报内容

```json
{
  "app": "MailDog",
  "version": "0.3.5",
  "time": "2026-11-26T02:00:00.0000000+00:00",
  "kind": "未处理异常",
  "exception": "System.NullReferenceException",
  "stack": "..."
}
```

**不上报异常消息**，避免把邮件地址、主题或正文带出去；只上报异常类型和堆栈。

## 失败与重试

- HTTP 非 2xx、超时、网络异常：写入 `%LOCALAPPDATA%\MailDog\crash-pending\*.json`。
- 每次 UI 启动读取端点并重试待上传文件；成功一个删一个。
- 没有配置端点：只写本地 `crash.log`，不联网。

## 验收

`CrashReportUploaderTests` 覆盖：失败落盘、下次重试成功、成功不落盘、端点协议校验（https / 本机 http / 拒绝远程 http / 非法地址）。
