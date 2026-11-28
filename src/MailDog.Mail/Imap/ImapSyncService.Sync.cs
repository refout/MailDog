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
            .ConnectAsync(account, _accounts, _tokens, _certificateValidator, cancellationToken)
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
