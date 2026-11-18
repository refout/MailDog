using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;
using MailDog.Engine.Hosting;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;

namespace MailDog.Engine.Ipc;

public sealed class RequestRouter
{
    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly ImapSyncService _sync;
    private readonly IOutlookAuthService _auth;
    private readonly SmtpSendService _sender;
    private readonly StorageMaintenanceService _storage;

    public RequestRouter(
        SqliteStore store,
        AccountService accounts,
        ImapSyncService sync,
        IOutlookAuthService auth,
        SmtpSendService sender,
        StorageMaintenanceService storage)
    {
        _store = store;
        _accounts = accounts;
        _sync = sync;
        _auth = auth;
        _sender = sender;
        _storage = storage;
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

            case UpdateAccountRequest request:
                return await UpdateAccountAsync(request, cancellationToken).ConfigureAwait(false);

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

            case SendMailRequest request:
                return await _sender.EnqueueAndSendAsync(request, cancellationToken).ConfigureAwait(false);

            case SaveDraftRequest request:
            {
                var result = await _sync.SaveDraftAsync(request, cancellationToken).ConfigureAwait(false);
                QueueSync(request.AccountId);
                return result;
            }

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

            case GetStorageSummaryRequest:
                return new StorageSummaryResponse { Summary = _storage.GetSummary() };

            case CleanStorageRequest request:
                return new StorageCleanupResponse { Result = _storage.Clean(request) };

            case TakePendingNavigationRequest:
                return TakePendingNavigation?.Invoke() ?? new PendingNavigationResponse();

            case UpdateSettingsRequest request:
                _store.SaveSettings(request.Settings);
                StartupRegistration.Apply(request.Settings.RunAtStartup);
                return new OkResponse { Affected = 1 };

            default:
                throw new NotSupportedException($"不支持的 IPC 消息：{envelope.Type}");
        }
    }

    private async Task<IIpcMessage> AddPasswordAccountAsync(
        AddPasswordAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Provider is not (MailProvider.QQ or MailProvider.Custom))
        {
            throw new NotSupportedException("密码登录仅支持 QQ 邮箱或自定义服务器；Outlook 请使用浏览器登录。");
        }

        ProviderConfig? customConfig = null;
        if (request.Provider == MailProvider.Custom)
        {
            if (string.IsNullOrWhiteSpace(request.ImapHost) || string.IsNullOrWhiteSpace(request.SmtpHost))
            {
                throw new ArgumentException("请填写 IMAP 与 SMTP 服务器地址。");
            }

            customConfig = new ProviderConfig(
                request.ImapHost.Trim(),
                request.ImapPort,
                request.ImapStartTls,
                request.SmtpHost.Trim(),
                request.SmtpPort,
                request.SmtpStartTls);
        }

        await _sync.TestPasswordConnectionAsync(
            customConfig ?? ProviderCatalog.Get(request.Provider),
            request.Email,
            request.Secret,
            cancellationToken).ConfigureAwait(false);

        if (customConfig is not null)
        {
            await _sender.TestPasswordConnectionAsync(
                customConfig,
                request.Email,
                request.Secret,
                cancellationToken).ConfigureAwait(false);
        }

        var account = _accounts.AddPasswordAccount(
            request.Provider,
            request.Email,
            request.Secret,
            request.DisplayName,
            customConfig);

        QueueSync(account.Id);
        return new AccountResponse { Account = account };
    }

    private async Task<IIpcMessage> UpdateAccountAsync(
        UpdateAccountRequest request,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(request.AccountId)
            ?? throw new ArgumentException("账户不存在。", nameof(request));

        if (account.Provider == MailProvider.Custom)
        {
            if (string.IsNullOrWhiteSpace(request.ImapHost) || string.IsNullOrWhiteSpace(request.SmtpHost))
            {
                throw new ArgumentException("请填写 IMAP 与 SMTP 服务器地址。");
            }

            if (request.ImapPort is < 1 or > 65535 || request.SmtpPort is < 1 or > 65535)
            {
                throw new ArgumentException("端口必须在 1 到 65535 之间。");
            }

            var config = new ProviderConfig(
                request.ImapHost.Trim(),
                request.ImapPort,
                request.ImapStartTls,
                request.SmtpHost.Trim(),
                request.SmtpPort,
                request.SmtpStartTls);
            var current = _accounts.GetServerConfig(account);
            var secret = string.IsNullOrWhiteSpace(request.Secret)
                ? _accounts.GetPassword(account)
                : request.Secret.Trim();

            var serverChanged = !SameServer(current, config);
            if ((serverChanged || !string.IsNullOrWhiteSpace(request.Secret)) && secret is null)
            {
                throw new InvalidOperationException("账户缺少授权码，请填写新的授权码后重试。");
            }

            if ((serverChanged || !string.IsNullOrWhiteSpace(request.Secret)) && secret is not null)
            {
                await _sync.TestPasswordConnectionAsync(
                    config,
                    account.Email,
                    secret,
                    cancellationToken).ConfigureAwait(false);
                await _sender.TestPasswordConnectionAsync(
                    config,
                    account.Email,
                    secret,
                    cancellationToken).ConfigureAwait(false);
            }

            var updated = _accounts.UpdatePasswordAccount(
                account.Id,
                request.DisplayName,
                request.Secret,
                config);
            QueueSync(account.Id);
            return new AccountResponse { Account = updated };
        }

        if (account.Provider == MailProvider.QQ)
        {
            if (request.ImapHost is not null || request.SmtpHost is not null)
            {
                throw new ArgumentException("QQ 邮箱不支持修改服务器地址。");
            }

            if (!string.IsNullOrWhiteSpace(request.Secret))
            {
                await _sync.TestPasswordConnectionAsync(
                    ProviderCatalog.Get(MailProvider.QQ),
                    account.Email,
                    request.Secret.Trim(),
                    cancellationToken).ConfigureAwait(false);
            }

            var updated = _accounts.UpdatePasswordAccount(
                account.Id,
                request.DisplayName,
                request.Secret,
                null);
            QueueSync(account.Id);
            return new AccountResponse { Account = updated };
        }

        if (!string.IsNullOrWhiteSpace(request.Secret)
            || request.ImapHost is not null
            || request.SmtpHost is not null)
        {
            throw new ArgumentException("Outlook 账户不支持修改授权码或服务器地址。");
        }

        return new AccountResponse
        {
            Account = _accounts.UpdateDisplayName(account.Id, request.DisplayName)
        };
    }

    private static bool SameServer(ProviderConfig left, ProviderConfig right)
        => string.Equals(left.ImapHost, right.ImapHost, StringComparison.OrdinalIgnoreCase)
            && left.ImapPort == right.ImapPort
            && left.ImapStartTls == right.ImapStartTls
            && string.Equals(left.SmtpHost, right.SmtpHost, StringComparison.OrdinalIgnoreCase)
            && left.SmtpPort == right.SmtpPort
            && left.SmtpStartTls == right.SmtpStartTls;

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
