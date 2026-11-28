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

public sealed partial class ImapSyncService
{
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
}
