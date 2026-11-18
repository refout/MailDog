using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Storage;

namespace MailDog.Engine.Ipc;

public sealed class RequestRouter
{
    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly ImapSyncService _sync;
    private readonly IOutlookAuthService _auth;

    public RequestRouter(
        SqliteStore store,
        AccountService accounts,
        ImapSyncService sync,
        IOutlookAuthService auth)
    {
        _store = store;
        _accounts = accounts;
        _sync = sync;
        _auth = auth;
    }

    public event Action<string>? Diagnostic;

    /// <summary>由 EngineHost 注入：取出并清除待跳转的通知目标。</summary>
    public Func<PendingNavigationResponse>? TakePendingNavigation { get; set; }

    public HelloAck CreateHelloAck()
        => new()
        {
            ProtocolVersion = IpcProtocol.Version,
            EngineVersion = typeof(RequestRouter).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            Accounts = _accounts.GetAccounts().ToArray()
        };

    public async Task<IIpcMessage?> HandleAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var message = IpcCodecRegistry.Read(envelope);

        switch (message)
        {
            case HelloRequest:
                return CreateHelloAck();

            case GetAccountsRequest:
                return new AccountsResponse { Items = _accounts.GetAccounts().ToArray() };

            case AddPasswordAccountRequest request:
                return await AddPasswordAccountAsync(request, cancellationToken).ConfigureAwait(false);

            case StartOAuthRequest request:
                if (request.Provider != MailProvider.Outlook)
                {
                    throw new ArgumentException("该服务商不支持 OAuth 登录。");
                }

                await _auth.StartAsync(request.LoginHint, cancellationToken).ConfigureAwait(false);
                return new OkResponse();

            case CancelOAuthRequest:
                _auth.Cancel();
                return new OkResponse();

            case RemoveAccountRequest request:
                _accounts.RemoveAccount(request.AccountId);
                return new OkResponse { Affected = 1 };

            case GetFoldersRequest request:
                return GetFolders(request);

            case GetMessagesRequest request:
                return GetMessages(request);

            case GetMessageBodyRequest request:
                return new MessageBodyResponse
                {
                    Body = await _sync.GetBodyAsync(request.MessageId, cancellationToken).ConfigureAwait(false)
                };

            case DownloadAttachmentRequest request:
            {
                var (fileName, size) = await _sync.SaveAttachmentAsync(
                    request.MessageId,
                    request.Index,
                    request.DestinationPath,
                    cancellationToken).ConfigureAwait(false);

                return new AttachmentSavedResponse
                {
                    Path = request.DestinationPath,
                    FileName = fileName,
                    Size = size
                };
            }

            case MarkReadRequest request:
                await _sync.SetSeenAsync(request.MessageId, request.IsRead, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case MarkReadBatchRequest request:
                return new OkResponse
                {
                    Affected = await _sync.SetSeenManyAsync(request.MessageIds, request.IsRead, cancellationToken)
                        .ConfigureAwait(false)
                };

            case SetFlagRequest request:
                await _sync.SetFlaggedAsync(request.MessageId, request.Value, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case SetFlagBatchRequest request:
                return new OkResponse
                {
                    Affected = await _sync.SetFlaggedManyAsync(request.MessageIds, request.Value, cancellationToken)
                        .ConfigureAwait(false)
                };

            case SyncAccountRequest request:
                if (string.IsNullOrEmpty(request.AccountId))
                {
                    foreach (var account in _accounts.GetAccounts())
                    {
                        QueueSync(account.Id);
                    }
                }
                else
                {
                    QueueSync(request.AccountId);
                }

                return new OkResponse { Affected = 1 };

            case GetSettingsRequest:
                return new SettingsResponse { Settings = _store.GetSettings() };

            case TakePendingNavigationRequest:
                return TakePendingNavigation?.Invoke() ?? new PendingNavigationResponse();

            case UpdateSettingsRequest request:
                _store.SaveSettings(request.Settings);
                return new OkResponse { Affected = 1 };

            default:
                throw new NotSupportedException($"不支持的 IPC 消息：{envelope.Type}");
        }
    }

    private async Task<IIpcMessage> AddPasswordAccountAsync(
        AddPasswordAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Provider != MailProvider.QQ)
        {
            throw new NotSupportedException("密码登录仅支持 QQ 邮箱；Outlook 请使用浏览器登录。");
        }

        await _sync.TestPasswordConnectionAsync(
            request.Provider,
            request.Email,
            request.Secret,
            cancellationToken).ConfigureAwait(false);

        var account = _accounts.AddPasswordAccount(
            request.Provider,
            request.Email,
            request.Secret,
            request.DisplayName);

        QueueSync(account.Id);
        return new AccountResponse { Account = account };
    }

    private IIpcMessage GetFolders(GetFoldersRequest request)
    {
        var folders = _store.GetFolders(request.AccountId);
        if (folders.Count == 0)
        {
            QueueSync(request.AccountId);
        }

        return new FoldersResponse
        {
            AccountId = request.AccountId,
            Items = folders.Select(ToDto).ToArray()
        };
    }

    private IIpcMessage GetMessages(GetMessagesRequest request)
    {
        var page = request.Role is { } role
            ? _store.QueryMessagesByRole(role, request.BeforeDate, request.BeforeId, request.Limit)
            : _store.QueryMessages(request.FolderId, request.BeforeDate, request.BeforeId, request.Limit);

        if (page.Items.Length == 0 && request.Role is null)
        {
            var folder = _store.GetFolder(request.FolderId);
            if (folder is not null)
            {
                QueueSync(folder.AccountId);
            }
        }

        return new MessagesPageResponse
        {
            Items = page.Items,
            HasMore = page.HasMore,
            NextDate = page.NextDate,
            NextId = page.NextId
        };
    }

    private void QueueSync(string accountId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _sync.SyncAccountAsync(accountId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke($"同步账户 {accountId} 失败：{exception.Message}");
            }
        });
    }

    private static FolderDto ToDto(FolderRecord folder)
        => new()
        {
            Id = folder.Id,
            AccountId = folder.AccountId,
            Name = folder.DisplayName,
            FullName = folder.FullName,
            Role = folder.Role,
            UnreadCount = folder.UnreadCount,
            TotalCount = folder.TotalCount
        };
}
