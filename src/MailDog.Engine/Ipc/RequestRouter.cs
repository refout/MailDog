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
using System.Collections.Concurrent;

namespace MailDog.Engine.Ipc;

public sealed class RequestRouter
{
    private readonly SqliteStore _store;
    private readonly AccountService _accounts;
    private readonly ImapSyncService _sync;
    private readonly IOutlookAuthService _auth;
    private readonly SmtpSendService _sender;
    private readonly StorageMaintenanceService _storage;
    private readonly LocalBackupService _backup;
    private readonly ConcurrentDictionary<string, byte> _queuedSyncs = new(StringComparer.Ordinal);

    public RequestRouter(
        SqliteStore store,
        AccountService accounts,
        ImapSyncService sync,
        IOutlookAuthService auth,
        SmtpSendService sender,
        StorageMaintenanceService storage,
        LocalBackupService backup)
    {
        _store = store;
        _accounts = accounts;
        _sync = sync;
        _auth = auth;
        _sender = sender;
        _storage = storage;
        _backup = backup;
    }

    public event Action<string>? Diagnostic;

    /// <summary>由 EngineHost 注入：取出并清除待跳转的通知目标。</summary>
    public Func<PendingNavigationResponse>? TakePendingNavigation { get; set; }

    /// <summary>由 EngineHost 注入：请求正常退出后台进程。</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>由 EngineHost 注入：CLI 或外部调用要求打开主窗口。</summary>
    public Action? OpenUiRequested { get; set; }

    /// <summary>由 EngineHost 注入：第二个进程带 --activate 启动时，把 URI 交给常驻进程。</summary>
    public Action<string>? ActivationRequested { get; set; }

    /// <summary>由 EngineHost 注入：返回当前账号健康快照。</summary>
    public Func<AccountHealthDto[]>? GetAccountHealth { get; set; }

    /// <summary>由 EngineHost 注入：解析外部 .eml 供界面预览。</summary>
    public Func<string, EmlPreviewDto>? GetEmlPreview { get; set; }

    /// <summary>由 EngineHost 注入：把打开的邮件加入系统最近列表。</summary>
    public Action<AddRecentRequest>? AddRecent { get; set; }

    /// <summary>由 EngineHost 注入：在后台把规则回溯到历史邮件，返回本次接受的数量。</summary>
    public Func<long, int, RuleHistoryApplyResultDto>? ApplyRuleHistory { get; set; }

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

            case GetAccountPreferencesRequest:
                return new AccountPreferencesResponse { Preferences = _accounts.GetPreferences() };

            case UpdateAccountPreferencesRequest request:
                return new AccountPreferencesResponse
                {
                    Preferences = _accounts.UpdatePreferences(request.Preferences)
                };

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

            case CreateFolderRequest request:
                await _sync.CreateFolderAsync(request.AccountId, request.Name, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case RenameFolderRequest request:
                await _sync.RenameFolderAsync(request.FolderId, request.Name, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case DeleteFolderRequest request:
                await _sync.DeleteFolderAsync(request.FolderId, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case GetMessagesRequest request:
                return GetMessages(request);

            case ListContactsRequest request:
                return new ContactsResponse { Items = _store.QueryContacts(request.Limit).ToArray() };

            case SearchMessagesRequest request:
                if (request.AllFolders)
                {
                    var items = _store.SearchMessageSummaries(
                        request.Query,
                        null,
                        null,
                        request.Limit);
                    return new MessageSearchResponse
                    {
                        MessageIds = items.Select(item => item.Id).ToArray(),
                        Items = items.ToArray()
                    };
                }

                return new MessageSearchResponse
                {
                    MessageIds = _store.SearchMessageIds(
                        request.Query,
                        request.Role is null && request.FolderId > 0 ? request.FolderId : null,
                        request.Role,
                        request.Limit) is { Count: > 0 } ids
                        ? ids.ToArray()
                        : []
                };

            case GetMessageBodyRequest request:
                return new MessageBodyResponse
                {
                    Body = await _sync.GetBodyAsync(request.MessageId, cancellationToken).ConfigureAwait(false)
                };

            case MoveMessageRequest request:
                await _sync.MoveMessageAsync(request.MessageId, request.TargetFolderId, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case MoveMessagesRequest request:
                foreach (var messageId in request.MessageIds)
                {
                    await _sync.MoveMessageAsync(messageId, request.TargetFolderId, cancellationToken).ConfigureAwait(false);
                }

                return new OkResponse { Affected = request.MessageIds.Length };

            case DeleteMessageRequest request:
                await _sync.DeleteMessageAsync(request.MessageId, request.Permanent, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case DeleteMessagesRequest request:
                foreach (var messageId in request.MessageIds)
                {
                    await _sync.DeleteMessageAsync(messageId, request.Permanent, cancellationToken).ConfigureAwait(false);
                }

                return new OkResponse { Affected = request.MessageIds.Length };

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

            case ReadAttachmentRequest request:
                return new AttachmentBytesResponse
                {
                    Attachment = await _sync.ReadAttachmentAsync(
                        request.MessageId,
                        request.Index,
                        request.MaxBytes,
                        cancellationToken).ConfigureAwait(false)
                };

            case GetUnsubscribeInfoRequest request:
                return new UnsubscribeInfoResponse
                {
                    Info = await _sync.GetUnsubscribeInfoAsync(request.MessageId, cancellationToken).ConfigureAwait(false)
                };

            case MarkReadRequest request:
                await _sync.SetSeenAsync(request.MessageId, request.IsRead, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case SendMailRequest request:
            {
                if (!request.SkipSendChecks)
                {
                    var guard = SendGuard.Inspect(request.Subject, request.BodyText, request.BodyHtml, request.AttachmentPaths);

                    if (guard.NeedsConfirmation)
                    {
                        return new SendMailResponse
                        {
                            Status = SendStatuses.ConfirmRequired,
                            Error = guard.Summary
                        };
                    }
                }

                return await _sender.EnqueueAndSendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            case SaveDraftRequest request:
            {
                var result = await _sync.SaveDraftAsync(request, cancellationToken).ConfigureAwait(false);
                QueueSync(request.AccountId);
                return result;
            }

            case GetDraftRequest request:
                return new DraftContentResponse
                {
                    Draft = await _sync.GetDraftAsync(request.MessageId, cancellationToken).ConfigureAwait(false)
                };

            case MarkReadBatchRequest request:
                return new OkResponse
                {
                    Affected = await _sync.SetSeenManyAsync(request.MessageIds, request.IsRead, cancellationToken)
                        .ConfigureAwait(false)
                };

            case SnoozeMessageRequest request:
            {
                var until = request.UntilUtc.ToUniversalTime();
                if (until <= DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    throw new ArgumentException("稍后提醒时间必须至少在 1 分钟之后。");
                }

                _store.SnoozeMessage(request.MessageId, until);
                return new OkResponse { Affected = 1 };
            }

            case SetFlagRequest request:
                await _sync.SetFlaggedAsync(request.MessageId, request.Value, cancellationToken).ConfigureAwait(false);
                return new OkResponse { Affected = 1 };

            case SetFlagBatchRequest request:
                return new OkResponse
                {
                    Affected = await _sync.SetFlaggedManyAsync(request.MessageIds, request.Value, cancellationToken)
                        .ConfigureAwait(false)
                };

            case GetTagsRequest:
                return new TagsResponse { Items = _store.GetTags().ToArray() };

            case SaveTagRequest request:
                return new TagsResponse { Items = [_store.SaveTag(request.Tag)] };

            case DeleteTagRequest request:
                _store.DeleteTag(request.TagId);
                return new OkResponse { Affected = 1 };

            case GetMessageTagsRequest request:
                return new MessageTagsResponse
                {
                    Items = _store.GetMessageTags(request.MessageIds).ToArray()
                };

            case SetMessageTagsRequest request:
                return new OkResponse
                {
                    Affected = _store.SetMessageTags(request.MessageIds, request.TagIds, request.Add)
                };

            case SyncAccountRequest request:
                if (string.IsNullOrEmpty(request.AccountId))
                {
                    foreach (var account in _accounts.GetAccounts())
                    {
                        QueueSync(account.Id, force: true);
                    }
                }
                else
                {
                    QueueSync(request.AccountId, force: true);
                }

                return new OkResponse { Affected = 1 };

            case GetSettingsRequest:
                return new SettingsResponse { Settings = _store.GetSettings() };

            case GetStorageSummaryRequest:
                return new StorageSummaryResponse { Summary = _storage.GetSummary() };

            case CleanStorageRequest request:
                return new StorageCleanupResponse { Result = _storage.Clean(request) };

            case CreateBackupRequest request:
                return new BackupResponse
                {
                    Result = _backup.CreateBackup(request.DestinationPath, request.Password)
                };

            case RestoreBackupRequest request:
                return new RestoreResponse
                {
                    Result = _backup.StageRestore(request.ArchivePath, request.Password)
                };

            case ExportMessageRequest request:
            {
                var size = await _sync.ExportMessageAsync(
                    request.MessageId,
                    request.DestinationPath,
                    cancellationToken).ConfigureAwait(false);
                return new MessageExportResponse
                {
                    Path = request.DestinationPath,
                    Size = size
                };
            }

            case ImportMailboxRequest request:
                return new MailboxImportResponse
                {
                    Result = await _sync.ImportMailboxAsync(
                        request.AccountId,
                        request.FolderId,
                        request.Paths,
                        cancellationToken).ConfigureAwait(false)
                };

            case ExportMailboxRequest request:
                return new MailboxExportResponse
                {
                    Result = await _sync.ExportMailboxAsync(
                        request.AccountId,
                        request.FolderId,
                        request.DestinationPath,
                        cancellationToken).ConfigureAwait(false)
                };

            case ListOutboxRequest:
                return new OutboxListResponse
                {
                    Items = _store.GetActiveOutbox()
                        .Select(record => new OutboxItemDto
                        {
                            Id = record.Id,
                            AccountId = record.AccountId,
                            ToAddresses = record.ToAddresses,
                            Subject = record.Subject,
                            Status = record.Status,
                            Attempts = record.Attempts,
                            LastError = record.LastError,
                            CreatedUtc = record.CreatedUtc,
                            NextAttemptUtc = record.NextAttemptUtc,
                            AttachmentCount = record.Attachments.Count
                        })
                        .ToArray()
                };

            case RetryOutboxRequest request:
                return await _sender.TrySendAsync(request.OutboxId, cancellationToken).ConfigureAwait(false);

            case DeleteOutboxRequest request:
                return new OkResponse
                {
                    Affected = await _sender.DeleteOutboxAsync(request.OutboxId, cancellationToken).ConfigureAwait(false)
                        ? 1
                        : 0
                };

            case GetRulesRequest:
                return new RulesResponse { Items = _store.GetRules().ToArray() };

            case SaveRuleRequest request:
                return new OkResponse { Affected = (int)_store.SaveRule(request.Rule) };

            case DeleteRuleRequest request:
                return new OkResponse { Affected = _store.DeleteRule(request.Id) ? 1 : 0 };

            case PreviewRuleMatchesRequest request:
            {
                var rule = _store.GetRules().FirstOrDefault(item => item.Id == request.RuleId)
                    ?? throw new KeyNotFoundException("规则不存在。");
                var matches = _store.FindRuleMatches(rule, 200);
                var target = rule.TargetFolderId is { } folderId ? _store.GetFolder(folderId) : null;
                return new RuleMatchPreviewResponse
                {
                    Preview = new RuleMatchPreviewDto
                    {
                        RuleId = rule.Id,
                        Matched = matches.Count,
                        ScanLimit = 200,
                        TargetFolderName = target?.DisplayName,
                        Samples = matches.Take(5).Select(item => new RuleMatchSampleDto
                        {
                            MessageId = item.Id,
                            FolderId = item.FolderId,
                            Subject = item.Subject,
                            From = $"{item.FromName} <{item.FromAddress}>",
                            Date = item.Date
                        }).ToArray()
                    }
                };
            }

            case ApplyRuleHistoryRequest request:
                return new RuleHistoryApplyResponse
                {
                    Result = ApplyRuleHistory?.Invoke(request.RuleId, request.Limit)
                        ?? new RuleHistoryApplyResultDto { Message = "当前引擎不支持历史回溯。" }
                };

            case TakePendingNavigationRequest:
                return TakePendingNavigation?.Invoke() ?? new PendingNavigationResponse();

            case ActivateRequest activate:
                ActivationRequested?.Invoke(activate.Uri);
                return new OkResponse();

            case AddRecentRequest recent:
                AddRecent?.Invoke(recent);
                return new OkResponse();

            case ShutdownRequest:
                ShutdownRequested?.Invoke();
                return new OkResponse();

            case OpenUiRequest:
                OpenUiRequested?.Invoke();
                return new OkResponse();

            case GetAccountHealthRequest:
                return new AccountHealthResponse { Items = GetAccountHealth?.Invoke() ?? [] };

            case GetEmlPreviewRequest request:
                return new EmlPreviewResponse
                {
                    Preview = GetEmlPreview?.Invoke(request.Path)
                        ?? new EmlPreviewDto { FileName = Path.GetFileName(request.Path), Error = "引擎不支持预览。" }
                };

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

    private void QueueSync(string accountId, bool force = false)
    {
        var account = _accounts.GetRecord(accountId);
        if (account is null)
        {
            return;
        }

        // 协议被邮箱禁用或等待重新授权时，自动同步没有意义；手动“立即同步”仍允许重试。
        if (!force && account.Status is AccountStatus.AuthRequired or AccountStatus.ProtocolBlocked)
        {
            return;
        }

        // 同一账户只保留一个排队中的同步任务，避免文件夹为空时被界面反复触发。
        if (!_queuedSyncs.TryAdd(accountId, 0))
        {
            return;
        }

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
            finally
            {
                _queuedSyncs.TryRemove(accountId, out _);
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
