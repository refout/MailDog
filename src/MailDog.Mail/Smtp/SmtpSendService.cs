using System.Net.Sockets;
using MailDog.Contracts.Messages;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Mime;
using MailDog.Mail.Storage;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace MailDog.Mail.Smtp;

/// <summary>Outbox 入队、立即尝试发送与失败重试。</summary>
public sealed class SmtpSendService
{
    private const int MaxAttempts = 8;
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60)
    ];

    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly IAccessTokenProvider _tokens;
    private readonly ImapSyncService? _imap;
    private readonly string _attachmentRoot;

    public SmtpSendService(
        SqliteStore store,
        AccountService accounts,
        IAccessTokenProvider tokens,
        string dataDirectory,
        ImapSyncService? imap = null)
    {
        _store = store;
        _accounts = accounts;
        _tokens = tokens;
        _imap = imap;
        _attachmentRoot = Path.Combine(dataDirectory, "outbox");
        Directory.CreateDirectory(_attachmentRoot);

        var recovered = _store.RecoverInterruptedOutbox();
        if (recovered > 0)
        {
            Diagnostic?.Invoke($"已恢复 {recovered} 封上次未完成的发件箱邮件。");
        }
    }

    public event Action<string>? Diagnostic;

    public async Task<SendMailResponse> EnqueueAndSendAsync(
        SendMailRequest request,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(request.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");

        _ = MailComposer.ParseAddresses(request.To, "收件人");
        _ = MailComposer.ParseAddresses(request.Cc, "抄送");
        _ = MailComposer.ParseAddresses(request.Bcc, "密送");
        var copies = CopyAttachments(request.AttachmentPaths);
        var messageId = MimeUtils.GenerateMessageId();
        var now = DateTimeOffset.UtcNow;

        long outboxId;
        try
        {
            var record = new OutboxRecord(
                Id: 0,
                AccountId: account.Id,
                ToAddresses: request.To,
                CcAddresses: request.Cc,
                BccAddresses: request.Bcc,
                Subject: request.Subject,
                BodyText: request.BodyText,
                InReplyTo: request.InReplyTo,
                MessageId: messageId,
                Status: OutboxStatuses.Pending,
                Attempts: 0,
                NextAttemptUtc: now,
                LastError: null,
                CreatedUtc: now,
                SentUtc: null,
                Attachments: copies,
                IsMarkdown: request.IsMarkdown,
                BodyHtml: request.BodyHtml);

            outboxId = _store.InsertOutbox(record, copies);
        }
        catch
        {
            DeleteAttachmentDirectory(copies);
            throw;
        }

        return await TrySendAsync(outboxId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SendMailResponse> TrySendAsync(long outboxId, CancellationToken cancellationToken)
    {
        var record = _store.GetOutbox(outboxId);
        if (record is null)
        {
            return Failure(outboxId, "发件箱记录不存在。");
        }

        if (record.Status == OutboxStatuses.Sent)
        {
            return new SendMailResponse
            {
                OutboxId = outboxId,
                Sent = true,
                Status = OutboxStatuses.Sent
            };
        }

        if (record.Status == OutboxStatuses.Failed)
        {
            return Failure(outboxId, record.LastError ?? "发送失败。");
        }

        var account = _accounts.GetRecord(record.AccountId);
        if (account is null)
        {
            _store.MarkOutboxFailed(outboxId, "账户已被删除。");
            return Failure(outboxId, "账户已被删除。");
        }

        var attempts = _store.BeginOutboxAttempt(
            outboxId,
            DateTimeOffset.UtcNow.Add(NextAttempt(record.Attempts + 1)));

        try
        {
            var attachments = record.Attachments.Select(MailComposer.ToAttachment).ToArray();
            using var message = MailComposer.Build(
                account,
                record.ToAddresses,
                record.CcAddresses,
                record.BccAddresses,
                record.Subject,
                record.BodyText,
                record.InReplyTo,
                record.MessageId,
                attachments,
                record.IsMarkdown,
                bodyHtml: record.BodyHtml);

            await SendViaSmtpAsync(account, message, cancellationToken).ConfigureAwait(false);

            try
            {
                if (_imap is not null)
                {
                    await _imap.AppendSentCopyAsync(record.AccountId, message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                // 邮件已经发出；保存已发送副本失败不能把发送结果改成失败，否则会重复投递。
                Diagnostic?.Invoke($"已发送，但保存到“已发送”失败：{Describe(exception)}");
            }

            _store.CompleteOutbox(outboxId, DateTimeOffset.UtcNow);
            DeleteAttachmentDirectory(record.Attachments);
            return new SendMailResponse
            {
                OutboxId = outboxId,
                Sent = true,
                Status = OutboxStatuses.Sent
            };
        }
        catch (OperationCanceledException)
        {
            // 请求超时或关闭：保留在发件箱，下次启动/轮询继续。
            _store.FailOutbox(outboxId, DateTimeOffset.UtcNow, "发送已取消，稍后重试。");
            return new SendMailResponse
            {
                OutboxId = outboxId,
                Queued = true,
                Status = OutboxStatuses.Pending,
                Error = "发送已取消，已加入发件箱重试。"
            };
        }
        catch (Exception exception)
        {
            var error = Describe(exception);
            Diagnostic?.Invoke($"发送邮件失败（第 {attempts} 次）：{error}");

            if (attempts >= MaxAttempts)
            {
                _store.MarkOutboxFailed(outboxId, error);
                return Failure(outboxId, error);
            }

            _store.FailOutbox(outboxId, DateTimeOffset.UtcNow.Add(NextAttempt(attempts)), error);
            return new SendMailResponse
            {
                OutboxId = outboxId,
                Queued = true,
                Status = OutboxStatuses.Pending,
                Error = error
            };
        }
    }

    public async Task<bool> DeleteOutboxAsync(long outboxId, CancellationToken cancellationToken)
    {
        var record = _store.GetOutbox(outboxId);
        if (record is null)
        {
            return false;
        }

        if (record.Status == OutboxStatuses.Sending)
        {
            throw new InvalidOperationException("这封邮件正在发送，请稍后再删除。");
        }

        if (!_store.DeleteOutbox(outboxId))
        {
            return false;
        }

        DeleteAttachmentDirectory(record.Attachments);
        return true;
    }

    /// <summary>添加自定义服务器时先验证 SMTP 认证，避免账户可用但发不出信。</summary>
    public async Task TestPasswordConnectionAsync(
        ProviderConfig config,
        string email,
        string secret,
        CancellationToken cancellationToken)
    {
        var socketOptions = config.SmtpStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.SslOnConnect;

        using var client = new SmtpClient { Timeout = 30000 };
        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, socketOptions, cancellationToken)
            .ConfigureAwait(false);
        await client.AuthenticateAsync(email, secret, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendViaSmtpAsync(
        AccountRecord account,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        var config = _accounts.GetServerConfig(account);
        var socketOptions = config.SmtpStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.SslOnConnect;

        using var client = new SmtpClient { Timeout = 30000 };
        await client.ConnectAsync(
            config.SmtpHost,
            config.SmtpPort,
            socketOptions,
            cancellationToken).ConfigureAwait(false);

        if (account.AuthKind == "oauth2")
        {
            var token = await _tokens.GetAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(account.Email, token),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var password = _accounts.GetPassword(account)
                ?? throw new AuthenticationException("未找到账户授权码，请重新添加账户。");

            await client.AuthenticateAsync(
                new SaslMechanismPlain(account.Email, password),
                cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private List<OutboxAttachmentRecord> CopyAttachments(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var directory = Path.Combine(_attachmentRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var result = new List<OutboxAttachmentRecord>();
        long total = 0;

        try
        {
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"附件不存在：{path}", path);
                }

                var info = new FileInfo(path);
                total += info.Length;
                if (total > MaxAttachmentBytes)
                {
                    throw new InvalidOperationException("附件总大小不能超过 25 MiB。");
                }

                var fileName = Path.GetFileName(path);
                var storedPath = Path.Combine(directory, fileName);
                File.Copy(path, storedPath, overwrite: true);

                result.Add(new OutboxAttachmentRecord(
                    Id: 0,
                    FileName: fileName,
                    StoredPath: storedPath,
                    MimeType: MimeTypes.GetMimeType(fileName),
                    Size: info.Length));
            }

            return result;
        }
        catch
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响主错误。
            }

            throw;
        }
    }

    private static TimeSpan NextAttempt(int attempts)
    {
        var index = Math.Clamp(attempts - 1, 0, RetryDelays.Length - 1);
        return RetryDelays[index];
    }

    private static void DeleteAttachmentDirectory(IReadOnlyList<OutboxAttachmentRecord> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(attachments[0].StoredPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 附件目录清理失败不应影响发送结果。
        }
    }

    private static string Describe(Exception exception)
    {
        if (exception is SmtpCommandException command)
        {
            return $"SMTP 拒绝：{command.Message}";
        }

        if (exception is AuthenticationException or SmtpProtocolException or IOException or SocketException)
        {
            return exception.Message;
        }

        return exception.Message;
    }

    private static SendMailResponse Failure(long outboxId, string error)
        => new()
        {
            OutboxId = outboxId,
            Sent = false,
            Queued = false,
            Status = OutboxStatuses.Failed,
            Error = error
        };
}
