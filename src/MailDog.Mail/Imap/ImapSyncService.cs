using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Messages;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Mime;
using MailDog.Mail.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace MailDog.Mail.Imap;

public sealed partial class ImapSyncService : IAsyncDisposable
{
    private const int MaxBodyBytes = 10 * 1024 * 1024;

    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly IAccessTokenProvider _tokens;
    private readonly RemoteCertificateValidationCallback? _certificateValidator;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionsGate = new(1, 1);

    public ImapSyncService(
        SqliteStore store,
        AccountService accounts,
        IAccessTokenProvider tokens,
        RemoteCertificateValidationCallback? certificateValidator = null)
    {
        _store = store;
        _accounts = accounts;
        _tokens = tokens;
        _certificateValidator = certificateValidator;
    }

    public event Action<NewMailEvent>? NewMail;

    public event Action<SyncStateEvent>? SyncStateChanged;

    public event Action<AuthStateEvent>? AuthStateChanged;

    public event Action<string>? Diagnostic;

    public async Task SyncAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");

        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            RaiseSync(accountId, null, SyncStateKind.Syncing, 0, "正在连接邮件服务器…");
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folders = await LoadFoldersAsync(session.Client!, account.Id, cancellationToken).ConfigureAwait(false);
            _store.UpsertFolders(account.Id, folders);

            var stored = _store.GetFolders(account.Id);
            var targets = stored
                .Where(folder => folder.Role is FolderRole.Inbox
                    or FolderRole.Sent
                    or FolderRole.Drafts
                    or FolderRole.Junk
                    or FolderRole.Trash
                    or FolderRole.Archive)
                .ToArray();

            for (var index = 0; index < targets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = targets[index];
                RaiseSync(accountId, folder.Id, SyncStateKind.Syncing, (double)index / targets.Length, $"正在同步 {folder.DisplayName}…");
                await SyncFolderAsync(
                    session.Client!,
                    account,
                    folder,
                    folder.Role == FolderRole.Inbox ? 200 : 50,
                    cancellationToken).ConfigureAwait(false);
            }

            _store.UpdateAccountStatus(account.Id, AccountStatus.Ok, DateTimeOffset.UtcNow);
            RaiseSync(accountId, null, SyncStateKind.Idle, 1, null);
        }
        catch (AuthenticationException exception)
        {
            var status = IsMailboxProtocolBlocked(exception)
                ? AccountStatus.ProtocolBlocked
                : AccountStatus.AuthRequired;

            _store.UpdateAccountStatus(accountId, status);
            AuthStateChanged?.Invoke(new AuthStateEvent
            {
                AccountId = accountId,
                State = AuthStateKind.Required,
                Message = exception.Message
            });
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ImapProtocolException or ImapCommandException)
        {
            _store.UpdateAccountStatus(accountId, AccountStatus.Offline);
            RaiseSync(accountId, null, SyncStateKind.Error, null, exception.Message);
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>把完整 MIME 源邮件导出为 .eml 文件，保留原始头、正文和附件。</summary>
    public async Task<long> ExportMessageAsync(
        long messageId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");

        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            try
            {
                var mime = await folder.GetMessageAsync(
                    ToUniqueId(message.Uid),
                    cancellationToken).ConfigureAwait(false);

                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var stream = File.Create(destinationPath);
                await mime.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return stream.Length;
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>读取附件字节用于内嵌预览；超过上限时拒绝，避免把大文件搬进 IPC 和内存。</summary>
    public async Task<AttachmentBytesDto> ReadAttachmentAsync(
        long messageId,
        int attachmentIndex,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");

        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            try
            {
                var mime = await folder.GetMessageAsync(
                    ToUniqueId(message.Uid),
                    cancellationToken).ConfigureAwait(false);

                var part = MimeConverter.FindAttachment(mime, attachmentIndex)
                    ?? throw new InvalidOperationException("附件不存在，请刷新邮件后重试。");

                if (part.Content is null)
                {
                    throw new InvalidOperationException("附件内容为空。");
                }

                await using var buffer = new MemoryStream();
                await part.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (buffer.Length > Math.Max(1, maxBytes))
                {
                    throw new InvalidOperationException("附件超过内嵌预览上限，请使用“打开”或“下载”。");
                }

                return new AttachmentBytesDto
                {
                    FileName = part.FileName ?? "attachment",
                    MimeType = part.ContentType.MimeType,
                    Content = buffer.ToArray()
                };
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<MailBodyDto> GetBodyAsync(long messageId, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");

        var cached = _store.GetBody(messageId);
        if (cached is not null && cached.Html?.Contains("cid:", StringComparison.OrdinalIgnoreCase) != true)
        {
            return MimeConverter.ToDto(
                message,
                cached.Html,
                cached.PlainText,
                cached.Truncated,
                cached.Attachments,
                fromCache: true);
        }

        if (message.Size > MaxBodyBytes)
        {
            _store.UpdateBodyState(messageId, BodyStates.TooLarge);
            return new MailBodyDto
            {
                MessageId = messageId,
                Subject = message.Subject,
                PlainText = "邮件内容超过 10 MiB，已跳过正文下载。",
                Truncated = true,
                FromName = message.FromName,
                FromAddress = message.FromAddress,
                ToAddresses = MimeConverter.SplitAddresses(message.ToAddresses),
                CcAddresses = MimeConverter.SplitAddresses(message.CcAddresses),
                Date = message.Date,
                RfcMessageId = message.MessageId
            };
        }

        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            try
            {
                var mime = await folder.GetMessageAsync(
                    ToUniqueId(message.Uid),
                    cancellationToken).ConfigureAwait(false);
                var body = MimeConverter.ExtractBody(mime);
                var html = MimeConverter.InlineCidImages(mime, body.Html);
                body = (html, body.PlainText, body.Truncated, body.Attachments);
                _store.SaveBody(messageId, body.Html, body.PlainText, body.Truncated, body.Attachments);

                return MimeConverter.ToDto(
                    message,
                    body.Html,
                    body.PlainText,
                    body.Truncated,
                    body.Attachments);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _store.UpdateBodyState(messageId, BodyStates.Failed);
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<UnsubscribeInfoDto> GetUnsubscribeInfoAsync(long messageId, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");
        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);
            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            try
            {
                var mime = await folder.GetMessageAsync(ToUniqueId(message.Uid), cancellationToken).ConfigureAwait(false);
                return ParseUnsubscribe(
                    mime.Headers["List-Unsubscribe"],
                    mime.Headers["List-Unsubscribe-Post"]);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>
    /// Outlook.com 有可能在 OAuth 完全成功的情况下拒绝单个邮箱的 IMAP 会话：
    /// “User is authenticated but not connected.” 或 SMTP 返回
    /// “SmtpClientAuthentication is disabled for the Mailbox.”。
    /// 这类问题不是令牌过期，重新登录也不会恢复，需要邮箱侧开通协议。
    /// </summary>
    private static bool IsMailboxProtocolBlocked(AuthenticationException exception)
        => exception.Message.Contains("authenticated but not connected", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("IMAP access is disabled", StringComparison.OrdinalIgnoreCase);

    public static UnsubscribeInfoDto ParseUnsubscribe(string? listUnsubscribe, string? listUnsubscribePost)
    {
        var url = default(string);
        var mailTo = default(string);

        foreach (var raw in (listUnsubscribe ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().Trim('<', '>').Trim();
            if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                mailTo ??= value;
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                url ??= value;
            }
        }

        var oneClick = (listUnsubscribePost ?? string.Empty).Contains("one-click", StringComparison.OrdinalIgnoreCase)
            || (listUnsubscribePost ?? string.Empty).Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase);

        return new UnsubscribeInfoDto
        {
            Available = url is not null || mailTo is not null,
            Url = url,
            MailTo = mailTo,
            OneClickPost = oneClick && url is not null,
            Error = url is null && mailTo is null ? "这封邮件没有提供 List-Unsubscribe 退订头。" : null
        };
    }
}
