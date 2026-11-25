using System.Net.Sockets;
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

public sealed class ImapSyncService : IAsyncDisposable
{
    private const int MaxBodyBytes = 10 * 1024 * 1024;

    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly IAccessTokenProvider _tokens;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionsGate = new(1, 1);

    public ImapSyncService(SqliteStore store, AccountService accounts, IAccessTokenProvider tokens)
    {
        _store = store;
        _accounts = accounts;
        _tokens = tokens;
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
            _store.UpdateAccountStatus(accountId, AccountStatus.AuthRequired);
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

    public async Task<(string FileName, long Size)> SaveAttachmentAsync(
        long messageId,
        int attachmentIndex,
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

                var part = MimeConverter.FindAttachment(mime, attachmentIndex)
                    ?? throw new InvalidOperationException("附件不存在，请刷新邮件后重试。");

                if (part.Content is null)
                {
                    throw new InvalidOperationException("附件内容为空。");
                }

                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var stream = File.Create(destinationPath);
                await part.Content.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                return (part.FileName ?? "attachment", stream.Length);
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

    /// <summary>把 .eml 或 mbox 批量追加到指定 IMAP 文件夹；失败单封跳过，不中断整批。</summary>
    public async Task<MailboxImportResultDto> ImportMailboxAsync(
        string accountId,
        long folderId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(folderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");
        if (!string.Equals(folderRecord.AccountId, accountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("目标文件夹不属于该账户。");
        }

        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);
            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                foreach (var path in paths)
                {
                    try
                    {
                        await using var stream = File.OpenRead(path);
                        var extension = Path.GetExtension(path);
                        if (string.Equals(extension, ".mbox", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(extension, ".mbx", StringComparison.OrdinalIgnoreCase))
                        {
                            var parser = new MimeParser(stream, MimeFormat.Mbox);
                            while (!parser.IsEndOfStream)
                            {
                                var message = parser.ParseMessage(cancellationToken);
                                if (await TryAppendAsync(folder, message, errors, cancellationToken).ConfigureAwait(false))
                                {
                                    imported++;
                                }
                                else
                                {
                                    skipped++;
                               }
                            }
                        }
                        else
                        {
                            var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                            if (await TryAppendAsync(folder, message, errors, cancellationToken).ConfigureAwait(false))
                            {
                                imported++;
                            }
                            else
                            {
                                skipped++;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        skipped++;
                        if (errors.Count < 8)
                        {
                            errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
                        }
                    }
                }
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

        return new MailboxImportResultDto
        {
            Imported = imported,
            Skipped = skipped,
            Errors = errors.ToArray(),
            Message = $"已导入 {imported} 封" + (skipped > 0 ? $"，跳过 {skipped} 封" : string.Empty) + "。"
        };
    }

    private static async Task<bool> TryAppendAsync(
        IMailFolder folder,
        MimeMessage message,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            await folder.AppendAsync(message, MessageFlags.None, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            if (errors.Count < 8)
            {
                errors.Add($"{message.Subject}: {exception.Message}");
            }

            return false;
        }
    }

    /// <summary>把账户（或单个文件夹）全部邮件导出为 mbox；逐封从 IMAP 拉原始 MIME。</summary>
    public async Task<MailboxExportResultDto> ExportMailboxAsync(
        string accountId,
        long? folderId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var messages = _store.GetMessagesForExport(accountId, folderId);
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var exported = 0;

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            await using var writer = new StreamWriter(
                File.Create(destinationPath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (var group in messages.GroupBy(message => message.FolderId))
            {
                var folderRecord = _store.GetFolder(group.Key);
                if (folderRecord is null)
                {
                    continue;
                }

                var folder = session.Client!.GetFolder(folderRecord.FullName);
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                try
                {
                    foreach (var message in group)
                    {
                        try
                        {
                            var mime = await folder.GetMessageAsync(
                                ToUniqueId(message.Uid),
                                cancellationToken).ConfigureAwait(false);
                            var sender = mime.Sender ?? mime.From.Mailboxes.FirstOrDefault();
                            var senderAddress = sender?.Address ?? "maildog@localhost";
                            await writer.WriteAsync(MboxFormat.CreateSeparator(senderAddress, mime.Date))
                                .ConfigureAwait(false);

                            await using var buffer = new MemoryStream();
                            await mime.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
                            await writer.WriteAsync(MboxFormat.NormalizeMessage(Encoding.UTF8.GetString(buffer.ToArray()))
                                + "\n\n").ConfigureAwait(false);
                            exported++;
                        }
                        catch
                        {
                            // 单封失败不影响其余邮件；结果消息里显示导出数量。
                        }
                    }
                }
                finally
                {
                    await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            session.Gate.Release();
        }

        var size = new FileInfo(destinationPath).Length;
        return new MailboxExportResultDto
        {
            Path = destinationPath,
            Size = size,
            Messages = exported,
            Message = $"已导出 {exported} 封邮件到 mbox。"
        };
    }

    public async Task SetSeenAsync(long messageId, bool isRead, CancellationToken cancellationToken)
    {
        _store.UpdateSeen(messageId, isRead);
        await StoreFlagAsync(messageId, MessageFlags.Seen, isRead, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetFlaggedAsync(long messageId, bool value, CancellationToken cancellationToken)
    {
        _store.UpdateFlagged(messageId, value);
        await StoreFlagAsync(messageId, MessageFlags.Flagged, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> SetSeenManyAsync(
        IReadOnlyList<long> messageIds,
        bool isRead,
        CancellationToken cancellationToken)
        => SetFlagManyAsync(messageIds, MessageFlags.Seen, isRead, cancellationToken);

    public Task<int> SetFlaggedManyAsync(
        IReadOnlyList<long> messageIds,
        bool value,
        CancellationToken cancellationToken)
        => SetFlagManyAsync(messageIds, MessageFlags.Flagged, value, cancellationToken);

    private async Task<int> SetFlagManyAsync(
        IReadOnlyList<long> messageIds,
        MessageFlags flag,
        bool value,
        CancellationToken cancellationToken)
    {
        var messages = messageIds
            .Distinct()
            .Select(id => _store.GetMessage(id))
            .Where(message => message is not null)
            .Select(message => message!)
            .ToArray();

        if (messages.Length == 0)
        {
            return 0;
        }

        _store.UpdateFlagMany(messages.Select(message => message.Id).ToArray(), ToLocalFlag(flag), value);

        foreach (var group in messages.GroupBy(message => (message.AccountId, message.FolderId)))
        {
            var account = _accounts.GetRecord(group.Key.AccountId);
            var folderRecord = _store.GetFolder(group.Key.FolderId);

            if (account is null || folderRecord is null)
            {
                continue;
            }

            var uids = group
                .Where(message => message.Uid is > 0 and <= uint.MaxValue)
                .Select(message => new UniqueId((uint)message.Uid))
                .Distinct()
                .ToArray();

            if (uids.Length == 0)
            {
                continue;
            }

            var session = await GetSessionAsync(account.Id, cancellationToken).ConfigureAwait(false);
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

                var folder = session.Client!.GetFolder(folderRecord.FullName);
                await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

                try
                {
                    await folder.StoreAsync(
                        uids,
                        new StoreFlagsRequest(value ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 本地状态已更新；远端失败进入重试队列，避免下次同步被服务器旧状态覆盖。
                Diagnostic?.Invoke($"批量更新邮件标记失败：{exception.Message}");
                foreach (var message in group)
                {
                    _store.QueuePendingFlag(
                        message.Id,
                        FlagName(flag),
                        value,
                        DateTimeOffset.UtcNow.AddMinutes(1));
                }
            }
            finally
            {
                session.Gate.Release();
            }
        }

        return messages.Length;
    }

    public async Task TestPasswordConnectionAsync(
        ProviderConfig config,
        string email,
        string secret,
        CancellationToken cancellationToken)
    {
        using var client = new ImapClient { Timeout = 30000 };
        var socketOptions = config.ImapStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.SslOnConnect;

        await client.ConnectAsync(config.ImapHost, config.ImapPort, socketOptions, cancellationToken)
            .ConfigureAwait(false);
        await client.AuthenticateAsync(email, secret, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static UniqueId ToUniqueId(long uid)
        => uid is > 0 and <= uint.MaxValue
            ? new UniqueId((uint)uid)
            : throw new InvalidOperationException("邮件同步数据不完整，请点击“立即同步”后重试。");

    private static MailFlags ToLocalFlag(MessageFlags flag)
        => flag.HasFlag(MessageFlags.Flagged) ? MailFlags.Flagged : MailFlags.Seen;

    public async Task<DraftContentDto> GetDraftAsync(long messageId, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("邮件所在文件夹不存在。");
        if (folderRecord.Role != FolderRole.Drafts)
        {
            throw new InvalidOperationException("只能继续编辑草稿箱中的邮件。");
        }

        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
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

                var format = mime.Headers["X-MailDog-Format"]?.Trim();
                var isMarkdown = string.Equals(
                        mime.Headers["X-MailDog-Markdown"],
                        "1",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase);
                var isRichHtml = !isMarkdown
                    && string.Equals(format, "html", StringComparison.OrdinalIgnoreCase);

                return new DraftContentDto
                {
                    MessageId = messageId,
                    FromAddress = mime.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                    FromName = mime.From.Mailboxes.FirstOrDefault()?.Name ?? string.Empty,
                    To = JoinAddresses(mime.To),
                    Cc = JoinAddresses(mime.Cc),
                    Bcc = JoinAddresses(mime.Bcc),
                    Subject = mime.Subject ?? string.Empty,
                    BodyText = isMarkdown || isRichHtml
                        ? mime.TextBody ?? string.Empty
                        : mime.TextBody ?? mime.HtmlBody ?? string.Empty,
                    BodyHtml = isRichHtml ? mime.HtmlBody ?? string.Empty : null,
                    IsMarkdown = isMarkdown,
                    AttachmentNames = mime.Attachments
                        .OfType<MimePart>()
                        .Select(part => part.FileName ?? part.ContentDisposition?.FileName ?? "attachment")
                        .ToArray()
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

    private static string JoinAddresses(InternetAddressList addresses)
        => string.Join("; ", addresses.Mailboxes.Select(mailbox => mailbox.Address));

    public async Task<DraftSavedResponse> SaveDraftAsync(
        SaveDraftRequest request,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(request.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var identity = MailIdentityResolver.Resolve(
            account,
            _store.GetSettings(),
            request.FromAddress,
            request.FromName);

        MailComposer.ParseAddresses(request.To, "收件人");

        var attachments = BuildDraftAttachments(request.AttachmentPaths);
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        DraftSavedResponse response;
        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var drafts = _store.GetFolders(account.Id).FirstOrDefault(folder => folder.Role == FolderRole.Drafts);
            if (drafts is null)
            {
                var folders = await LoadFoldersAsync(session.Client!, account.Id, cancellationToken).ConfigureAwait(false);
                _store.UpsertFolders(account.Id, folders);
                drafts = _store.GetFolders(account.Id).FirstOrDefault(folder => folder.Role == FolderRole.Drafts);
            }

            if (drafts is null)
            {
                throw new InvalidOperationException("未找到草稿箱，请确认邮箱已启用 IMAP 并同步后再试。");
            }

            using var message = MailComposer.Build(
                account,
                request.To,
                request.Cc,
                request.Bcc,
                request.Subject,
                request.BodyText,
                null,
                MimeUtils.GenerateMessageId(),
                attachments,
                request.IsMarkdown,
                requireRecipients: false,
                bodyHtml: request.BodyHtml,
                fromAddress: identity.Address,
                fromName: identity.DisplayName);

            var folder = session.Client!.GetFolder(drafts.FullName);
            await folder.AppendAsync(
                message,
                MessageFlags.Draft | MessageFlags.Seen,
                cancellationToken).ConfigureAwait(false);

            response = new DraftSavedResponse
            {
                FolderId = drafts.Id,
                FolderName = drafts.DisplayName
            };
        }
        finally
        {
            session.Gate.Release();
        }

        if (request.ReplaceDraftMessageId is { } replaceId && replaceId > 0)
        {
            // 新草稿已经保存成功，再删除旧草稿；删除失败不回滚已保存的新草稿。
            try
            {
                await DeleteMessageAsync(replaceId, permanent: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return response;
    }

    /// <summary>发送成功后把同一封 MIME 消息追加到服务器的“已发送”文件夹。</summary>
    public async Task AppendSentCopyAsync(
        string accountId,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        var settings = _store.GetSettings();
        if (!settings.SaveSentCopy)
        {
            return;
        }

        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var sent = _store.GetFolders(account.Id).FirstOrDefault(folder => folder.Role == FolderRole.Sent);
            if (sent is null)
            {
                var folders = await LoadFoldersAsync(session.Client!, account.Id, cancellationToken).ConfigureAwait(false);
                _store.UpsertFolders(account.Id, folders);
                sent = _store.GetFolders(account.Id).FirstOrDefault(folder => folder.Role == FolderRole.Sent);
            }

            if (sent is null)
            {
                throw new InvalidOperationException("未找到“已发送”文件夹。");
            }

            var folder = session.Client!.GetFolder(sent.FullName);
            await folder.AppendAsync(message, MessageFlags.Seen, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task MoveMessageAsync(long messageId, long targetFolderId, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");
        var source = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("邮件所在文件夹不存在。");
        var target = _store.GetFolder(targetFolderId)
            ?? throw new KeyNotFoundException("目标文件夹不存在。");

        if (!string.Equals(source.AccountId, target.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不能把邮件移动到其他账户的文件夹。");
        }

        if (source.Id == target.Id)
        {
            return;
        }

        var account = _accounts.GetRecord(source.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var uid = ToUniqueId(message.Uid);
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var sourceFolder = session.Client!.GetFolder(source.FullName);
            await sourceFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                var destination = session.Client.GetFolder(target.FullName);
                await sourceFolder.MoveToAsync(
                    new[] { uid },
                    new MoveRequest(destination),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await sourceFolder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }

            _store.MoveMessageToFolder(messageId, targetFolderId);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task DeleteMessageAsync(long messageId, bool permanent, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");
        var source = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("邮件所在文件夹不存在。");

        if (!permanent && source.Role != FolderRole.Trash)
        {
            var trash = _store.GetFolders(source.AccountId)
                .FirstOrDefault(folder => folder.Role == FolderRole.Trash);
            if (trash is null)
            {
                throw new InvalidOperationException("该账户没有已删除文件夹，无法移动邮件。");
            }

            await MoveMessageAsync(messageId, trash.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var account = _accounts.GetRecord(source.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var uid = ToUniqueId(message.Uid);
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(source.FullName);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                await folder.StoreAsync(
                    uid,
                    new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true },
                    cancellationToken).ConfigureAwait(false);
                await folder.ExpungeAsync(
                    new[] { uid },
                    new ExpungeRequest(),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }

            _store.DeleteMessage(messageId);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<string> CreateFolderAsync(string accountId, string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("文件夹名称不能为空。", nameof(name));
        }

        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var root = session.Client!.GetFolder(session.Client.PersonalNamespaces[0]);
            var created = await root.CreateAsync(name, true, cancellationToken).ConfigureAwait(false);
            var folders = await LoadFoldersAsync(session.Client, accountId, cancellationToken).ConfigureAwait(false);
            _store.UpsertFolders(accountId, folders);

            return created?.FullName ?? name;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("文件夹名称不能为空。", nameof(name));
        }

        var folder = _store.GetFolder(folderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");
        if (folder.Role != FolderRole.Other)
        {
            throw new InvalidOperationException("系统文件夹不能重命名。");
        }

        var account = _accounts.GetRecord(folder.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var mailFolder = session.Client!.GetFolder(folder.FullName);
            var parent = mailFolder.ParentFolder
                ?? throw new InvalidOperationException("无法确定文件夹的父目录。");
            await mailFolder.RenameAsync(parent, name, cancellationToken).ConfigureAwait(false);

            var delimiter = mailFolder.DirectorySeparator;
            var fullName = string.IsNullOrEmpty(parent.FullName)
                ? name
                : $"{parent.FullName}{delimiter}{name}";
            _store.RenameFolder(folderId, fullName, name);

            var folders = await LoadFoldersAsync(session.Client, account.Id, cancellationToken).ConfigureAwait(false);
            _store.UpsertFolders(account.Id, folders);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task DeleteFolderAsync(long folderId, CancellationToken cancellationToken)
    {
        var folder = _store.GetFolder(folderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");
        if (folder.Role != FolderRole.Other)
        {
            throw new InvalidOperationException("系统文件夹不能删除。");
        }

        var account = _accounts.GetRecord(folder.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var mailFolder = session.Client!.GetFolder(folder.FullName);
            await mailFolder.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _store.DeleteFolder(folderId);

            var folders = await LoadFoldersAsync(session.Client, account.Id, cancellationToken).ConfigureAwait(false);
            _store.UpsertFolders(account.Id, folders);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static MailAttachment[] BuildDraftAttachments(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        const long maxTotal = 25 * 1024 * 1024;
        var result = new List<MailAttachment>(paths.Count);
        long total = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"附件不存在：{path}", path);
            }

            var info = new FileInfo(path);
            total += info.Length;
            if (total > maxTotal)
            {
                throw new InvalidOperationException("附件总大小不能超过 25 MiB。");
            }

            result.Add(MailComposer.ToAttachment(path));
        }

        return result.ToArray();
    }

    private async Task StoreFlagAsync(long messageId, MessageFlags flag, bool value, CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId);
        if (message is null)
        {
            return;
        }

        var account = _accounts.GetRecord(message.AccountId);
        var folderRecord = _store.GetFolder(message.FolderId);
        if (account is null || folderRecord is null)
        {
            return;
        }

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                await folder.StoreAsync(
                    ToUniqueId(message.Uid),
                    new StoreFlagsRequest(value ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 本地状态已更新；远端失败进入重试队列。
            Diagnostic?.Invoke($"更新邮件标记失败：{exception.Message}");
            _store.QueuePendingFlag(
                messageId,
                FlagName(flag),
                value,
                DateTimeOffset.UtcNow.AddMinutes(1));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<bool> TryApplyPendingFlagAsync(
        PendingFlagRecord pending,
        CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(pending.MessageId);
        if (message is null)
        {
            _store.CompletePendingFlag(pending.Id);
            return true;
        }

        var account = _accounts.GetRecord(message.AccountId);
        var folderRecord = _store.GetFolder(message.FolderId);
        if (account is null || folderRecord is null || message.Uid is <= 0 or > uint.MaxValue)
        {
            _store.CompletePendingFlag(pending.Id);
            return true;
        }

        var flag = pending.Flag == "Flagged" ? MessageFlags.Flagged : MessageFlags.Seen;
        var session = await GetSessionAsync(account.Id, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                await folder.StoreAsync(
                    ToUniqueId(message.Uid),
                    new StoreFlagsRequest(pending.Value ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }

            _store.CompletePendingFlag(pending.Id);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"重试邮件标记失败：{exception.Message}");
            _store.FailPendingFlag(
                pending.Id,
                DateTimeOffset.UtcNow.Add(PendingFlagBackoff(pending.Attempts + 1)),
                exception.Message);
            return false;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static string FlagName(MessageFlags flag)
        => flag.HasFlag(MessageFlags.Flagged) ? "Flagged" : "Seen";

    private static TimeSpan PendingFlagBackoff(int attempts)
    {
        var minutes = attempts switch
        {
            <= 1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 30
        };

        return TimeSpan.FromMinutes(minutes);
    }

    private async Task EnsureConnectedAsync(Session session, AccountRecord account, CancellationToken cancellationToken)
    {
        if (session.Client is { IsConnected: true, IsAuthenticated: true })
        {
            return;
        }

        if (session.Client is not null)
        {
            await session.Client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            session.Client.Dispose();
            session.Client = null;
        }

        session.Client = await ImapConnectionFactory
            .ConnectAsync(account, _accounts, _tokens, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FolderRecord>> LoadFoldersAsync(
        ImapClient client,
        string accountId,
        CancellationToken cancellationToken)
    {
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var result = new List<FolderRecord>();
        await CollectFoldersAsync(personal, accountId, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task CollectFoldersAsync(
        IMailFolder parent,
        string accountId,
        List<FolderRecord> result,
        CancellationToken cancellationToken)
    {
        IList<IMailFolder> children;
        try
        {
            children = await parent.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
            return;
        }

        foreach (var folder in children)
        {
            long uidValidity = 0;
            var unread = 0;
            var total = 0;

            try
            {
                await folder.StatusAsync(
                    StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity,
                    cancellationToken).ConfigureAwait(false);

                uidValidity = folder.UidValidity;
                unread = folder.Unread;
                total = folder.Count;
            }
            catch (ImapCommandException)
            {
                // 某些服务器拒绝 STATUS，保留 0，后续打开文件夹时再修正。
            }

            result.Add(new FolderRecord(
                Id: 0,
                AccountId: accountId,
                FullName: folder.FullName,
                DisplayName: folder.Name,
                Role: MapRole(folder.Attributes),
                UidValidity: uidValidity,
                LastSeenUid: 0,
                UnreadCount: unread,
                TotalCount: total));

            await CollectFoldersAsync(folder, accountId, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SyncFolderAsync(
        ImapClient client,
        AccountRecord account,
        FolderRecord folderRecord,
        int initialLimit,
        CancellationToken cancellationToken)
    {
        var folder = client.GetFolder(folderRecord.FullName);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        try
        {
            var uidValidity = folder.UidValidity;
            if (folderRecord.UidValidity != 0 && folderRecord.UidValidity != uidValidity)
            {
                _store.ResetFolderMessages(folderRecord.Id);
                folderRecord = folderRecord with { UidValidity = uidValidity, LastSeenUid = 0 };
            }

            var items = MessageSummaryItems.UniqueId
                        | MessageSummaryItems.Envelope
                        | MessageSummaryItems.Headers
                        | MessageSummaryItems.Flags
                        | MessageSummaryItems.InternalDate
                        | MessageSummaryItems.Size
                        | MessageSummaryItems.BodyStructure;

            IList<IMessageSummary> summaries;
            var hasIncrementalRange = folderRecord.LastSeenUid is > 0 and < uint.MaxValue;

            try
            {
                if (hasIncrementalRange
                    && folder.UidNext is { } uidNext
                    && folderRecord.LastSeenUid + 1 >= uidNext.Id)
                {
                    // 没有新邮件时不要对超出最大 UID 的区间执行 FETCH，Exchange 会返回 NO。
                    summaries = [];
                }
                else if (hasIncrementalRange)
                {
                    var range = new UniqueIdRange(
                        new UniqueId((uint)(folderRecord.LastSeenUid + 1)),
                        UniqueId.MaxValue);

                    summaries = await folder.FetchAsync(range, items, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // 用 UID 抓取最近一页。Exchange 对序号区间较严格，序号 FETCH 可能返回
                    // “The specified message set is invalid”，UID 集合更可靠。
                    var uids = await folder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
                    var take = Math.Min(initialLimit, uids.Count);
                    var subset = uids.Skip(uids.Count - take).ToArray();

                    summaries = take > 0
                        ? await folder.FetchAsync(subset, items, cancellationToken).ConfigureAwait(false)
                        : [];
                }
            }
            catch (ImapCommandException exception)
            {
                Diagnostic?.Invoke(
                    $"文件夹 {folderRecord.FullName} 抓取失败（count={folder.Count}, lastSeen={folderRecord.LastSeenUid}, uidNext={folder.UidNext?.Id}）：{exception.Message}");
                throw;
            }

            var records = summaries
                .Select(summary => MimeConverter.ToRecord(summary, account.Id, folderRecord.Id))
                .ToArray();

            var result = _store.UpsertMessages(account.Id, folderRecord.Id, records);
            var maxUid = records.Length > 0 ? records.Max(record => record.Uid) : folderRecord.LastSeenUid;
            _store.UpdateFolderSyncState(folderRecord.Id, uidValidity, maxUid);

            if (result.Count > 0 && result.Latest is not null)
            {
                NewMail?.Invoke(new NewMailEvent
                {
                    AccountId = account.Id,
                    FolderId = folderRecord.Id,
                    Count = result.Count,
                    Latest = result.Latest,
                    NewMessageIds = result.NewIds?.ToArray() ?? []
                });
            }
        }
        finally
        {
            await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<Session> GetSessionAsync(string accountId, CancellationToken cancellationToken)
    {
        await _sessionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(accountId, out var session))
            {
                session = new Session();
                _sessions[accountId] = session;
            }

            return session;
        }
        finally
        {
            _sessionsGate.Release();
        }
    }

    private static FolderRole MapRole(FolderAttributes attributes)
    {
        if ((attributes & FolderAttributes.Inbox) != 0)
        {
            return FolderRole.Inbox;
        }

        if ((attributes & FolderAttributes.Sent) != 0)
        {
            return FolderRole.Sent;
        }

        if ((attributes & FolderAttributes.Drafts) != 0)
        {
            return FolderRole.Drafts;
        }

        if ((attributes & FolderAttributes.Trash) != 0)
        {
            return FolderRole.Trash;
        }

        if ((attributes & FolderAttributes.Junk) != 0)
        {
            return FolderRole.Junk;
        }

        if ((attributes & FolderAttributes.Archive) != 0)
        {
            return FolderRole.Archive;
        }

        return FolderRole.Other;
    }

    private void RaiseSync(string accountId, long? folderId, SyncStateKind state, double? progress, string? message)
        => SyncStateChanged?.Invoke(new SyncStateEvent
        {
            AccountId = accountId,
            FolderId = folderId,
            State = state,
            Progress = progress,
            Message = message
        });

    public async ValueTask DisposeAsync()
    {
        await _sessionsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Values)
            {
                if (session.Client is { } client)
                {
                    try
                    {
                        if (client.IsConnected)
                        {
                            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // 退出时忽略断开异常。
                    }

                    client.Dispose();
                }

                session.Gate.Dispose();
            }

            _sessions.Clear();
        }
        finally
        {
            _sessionsGate.Release();
            _sessionsGate.Dispose();
        }
    }

    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ImapClient? Client { get; set; }
    }
}
