using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Mime;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Engine.Hosting;

public sealed class EngineHost : IAsyncDisposable
{
    private readonly FileLogger _log;
    private readonly string _databasePath;
    private readonly SqliteStore _store;
    private readonly SecretStore _secrets;
    private readonly AccountService _accounts;
    private readonly ImapSyncService _sync;
    private readonly ImapIdleWatcher _idle;
    private readonly OutlookAuthService _outlookAuth;
    private readonly SmtpSendService _smtp;
    private readonly StorageMaintenanceService _storage;
    private readonly LocalBackupService _backup;
    private readonly OutboxWorker _outbox;
    private readonly FlagSyncWorker _flagSync;
    private readonly RequestRouter _router;
    private readonly IpcServer _ipc;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _hideConsole;
    private readonly ConcurrentDictionary<string, byte> _synchronizing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextSyncAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _syncFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Kind, string Message)> _syncErrors = new(StringComparer.Ordinal);
    private readonly object _syncStateGate = new();
    private Task? _scheduler;
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private IAppTray? _tray;
    private (long MessageId, string AccountId, long FolderId)? _lastNotification;
    private (long MessageId, string AccountId, long FolderId)? _pendingNavigation;
    private string? _pendingCompose;
    private string? _pendingEml;
    private bool _pendingOpenAbout;

    // 同一批同步里可能一次进来好几封，攒一小会儿再弹，避免通知刷屏。
    private readonly object _notificationGate = new();
    private readonly List<NewMailBatchItem> _pendingNotifications = [];
    private Timer? _notificationTimer;
    private DateTimeOffset _notificationBatchStart = DateTimeOffset.MinValue;
    private const int NotificationBatchDelayMs = 1500;
    private static readonly TimeSpan NotificationBatchWindow = TimeSpan.FromSeconds(4);

    private sealed record NewMailBatchItem(MailSummaryDto Summary, string AccountId, long FolderId, int Count);

    private readonly string? _startupActivation;
    private readonly string? _startupEml;

    public EngineHost(
        string databasePath,
        bool hideConsole = true,
        string? startupActivation = null,
        string? startupEml = null)
    {
        _startupActivation = startupActivation;
        _startupEml = startupEml;
        _log = new FileLogger();
        _databasePath = databasePath;
        _hideConsole = hideConsole;
        LocalBackupService.ApplyPendingRestore(databasePath);
        _store = new SqliteStore(databasePath);
        _secrets = new SecretStore(_store);
        _accounts = new AccountService(_store, _secrets);
        _outlookAuth = new OutlookAuthService(_store, _accounts, _secrets);
        _sync = new ImapSyncService(_store, _accounts, _outlookAuth);
        _idle = new ImapIdleWatcher(_accounts, _outlookAuth);
        var dataDirectory = Path.GetDirectoryName(_databasePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailDog");
        _smtp = new SmtpSendService(_store, _accounts, _outlookAuth, dataDirectory, _sync);
        _storage = new StorageMaintenanceService(_store, databasePath);
        _backup = new LocalBackupService(_store, databasePath);
        _outbox = new OutboxWorker(_store, _smtp);
        _flagSync = new FlagSyncWorker(_store, _sync);
        _router = new RequestRouter(_store, _accounts, _sync, _outlookAuth, _smtp, _storage, _backup);
        _ipc = new IpcServer(_router, message => _log.Warn(message));

        _router.Diagnostic += message => _log.Warn(message);
        _router.TakePendingNavigation = TakePendingNavigation;
        // 激活请求本身也是一条 IPC 连接；先让调用方拿到响应并断开，再决定弹窗口还是广播。
        _router.ActivationRequested = uri => _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            Activate(uri);
        });
        _router.GetAccountHealth = GetAccountHealth;
        _router.GetEmlPreview = EmlPreviewService.Load;
#if MAILDOG_WINDOWS
        _router.AddRecent = WindowsIntegration.AddRecent;
#endif
        // 先把响应发回给调用方，再收尾；否则界面/命令行只会看到连接被断开。
        _router.ShutdownRequested = () => _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            _tray?.Quit();
        });
        _sync.Diagnostic += message => _log.Warn(message);
        _smtp.Diagnostic += message => _log.Warn(message);
        _outbox.Diagnostic += message => _log.Info(message);
        _flagSync.Diagnostic += message => _log.Info(message);
        _idle.Diagnostic += message => _log.Info(message);
        _idle.MailArrived += accountId => _ = SyncFromIdleAsync(accountId);
        _sync.NewMail += message =>
        {
            var muted = ApplyRules(message);
            _ipc.Broadcast(IpcMessageTypes.NewMail, message);
            _tray?.UpdateUnreadCount(TotalUnread());

            if (message.Latest is not null && !muted)
            {
                var settings = _store.GetSettings();

                if (settings.NotificationsEnabled)
                {
                    _lastNotification = (message.Latest.Id, message.AccountId, message.FolderId);
                    QueueNotification(message.Latest, message.AccountId, message.FolderId, settings, message.Count);
                }
            }
        };
        _sync.SyncStateChanged += message => _ipc.Broadcast(IpcMessageTypes.SyncState, message);
        _sync.AuthStateChanged += message => _ipc.Broadcast(IpcMessageTypes.AuthState, message);
        _outlookAuth.AuthStateChanged += message => _ipc.Broadcast(IpcMessageTypes.AuthState, message);
        _outlookAuth.AccountChanged += message => _ipc.Broadcast(IpcMessageTypes.AccountChanged, message);
        _outlookAuth.Diagnostic += message => _log.Warn(message);
    }

    public Task RunAsync()
    {
        _log.Info($"Engine 启动，数据库：{_databasePath}");
        StartupRegistration.Apply(_store.GetSettings().RunAtStartup);

        // 通知标题栏的小图标跟随应用标识：每次启动刷新一次，保证换图后能立刻生效。
        Tray.AppIdentity.EnsureRegistered("MailDog");

        // 点击通知（含通知中心里的历史）时，Windows 通过 maildog: 协议把请求交回本进程。
        Tray.ShellIntegration.EnsureProtocolRegistered();
#if MAILDOG_WINDOWS
        WindowsIntegration.RegisterAll();
#endif

        _ipc.Start();
        _scheduler = SchedulerLoopAsync(_cts.Token);
        _outbox.Start();
        _flagSync.Start();

        _tray = PlatformTray.Create(
            OpenUi,
            OpenAbout,
            HandleNotificationClicked,
            Activate,
            () => _ = SyncAllAsync(_cts.Token),
            OnSystemResumed,
            () => _cts.Cancel(),
            IsDarkTheme);

        _tray.UpdateTooltip(BuildTooltip());
        _tray.UpdateUnreadCount(TotalUnread());

        // 冷启动时就带着点击来源：直接把界面拉起来并落到那封邮件，而不是只留一条待办导航。
        if (!string.IsNullOrWhiteSpace(_startupActivation))
        {
            Activate(_startupActivation);
        }

        if (!string.IsNullOrWhiteSpace(_startupEml))
        {
            ActivateFile(_startupEml);
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            _tray.Run(_hideConsole);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        return Task.CompletedTask;
    }

    private bool IsDarkTheme()
    {
        var theme = _store.GetSettings().Theme;

        if (theme == "dark")
        {
            return true;
        }

        if (theme == "light")
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void HandleNotificationClicked()
    {
        _pendingNavigation = _lastNotification;
    }

    /// <summary>要不要弹、弹一条还是合并，全在这里决定；真正弹窗放到批次末尾统一做。</summary>
    /// <summary>把本地规则套到这一批新邮件上，返回最新一封是否应该静音通知。</summary>
    private bool ApplyRules(NewMailEvent message)
    {
        try
        {
            if (message.NewMessageIds.Length == 0)
            {
                return false;
            }

            var rules = _store.GetRules();

            if (rules.Count == 0)
            {
                return false;
            }

            var hits = new List<long>();
            var mutedIds = new HashSet<long>();

            foreach (var id in message.NewMessageIds)
            {
                var record = _store.GetMessage(id);

                if (record is null)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(rule.AccountId) && rule.AccountId != message.AccountId)
                    {
                        continue;
                    }

                    if (!MatchesRule(rule, record))
                    {
                        continue;
                    }

                    hits.Add(rule.Id);

                    if (rule.Mute)
                    {
                        mutedIds.Add(id);
                    }

                    if (rule.MarkRead || rule.Flag)
                    {
                        ApplyRuleFlagsAsync(id, rule.MarkRead, rule.Flag);
                    }

                    if (rule.MoveToJunk)
                    {
                        ApplyRuleMoveToJunkAsync(id, message.AccountId);
                    }
                }
            }

            if (hits.Count > 0)
            {
                _store.IncrementRuleHits(hits.Distinct().ToArray());
            }

            return message.Latest is not null && mutedIds.Contains(message.Latest.Id);
        }
        catch (Exception exception)
        {
            _log.Warn($"应用本地规则失败：{exception.Message}");
            return false;
        }
    }

    private static bool MatchesRule(RuleDto rule, MessageRecord record)
        => RuleMatcher.Matches(rule, record.Subject, record.FromName, record.FromAddress);

    private void ApplyRuleFlagsAsync(long messageId, bool markRead, bool flag)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (markRead)
                {
                    await _sync.SetSeenAsync(messageId, true, CancellationToken.None).ConfigureAwait(false);
                }

                if (flag)
                {
                    await _sync.SetFlaggedAsync(messageId, true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _log.Warn($"规则应用到邮件 #{messageId} 失败：{exception.Message}");
            }
        });
    }

    private void ApplyRuleMoveToJunkAsync(long messageId, string accountId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var junk = _store.GetFolders(accountId).FirstOrDefault(folder => folder.Role == FolderRole.Junk);
                if (junk is null)
                {
                    _log.Warn($"账号 {accountId} 没有垃圾邮件文件夹，规则无法移动邮件 #{messageId}。");
                    return;
                }

                await _sync.MoveMessageAsync(messageId, junk.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn($"规则把邮件 #{messageId} 移到垃圾邮件失败：{exception.Message}");
            }
        });
    }

    private void QueueNotification(MailSummaryDto latest, string accountId, long folderId, AppSettingsDto settings, int count)
    {
        if (settings.NotificationInboxOnly && _store.GetFolder(folderId)?.Role != FolderRole.Inbox)
        {
            // 订阅文件夹、自定义文件夹的新邮件只入库，不打扰。
            return;
        }

        if (NotificationPolicy.IsQuietTime(settings, DateTimeOffset.Now))
        {
            _log.Info($"免打扰时段，跳过通知：{latest.Subject}");
            return;
        }

        lock (_notificationGate)
        {
            _pendingNotifications.Add(new NewMailBatchItem(latest, accountId, folderId, Math.Max(count, 1)));

            if (_notificationBatchStart == DateTimeOffset.MinValue)
            {
                _notificationBatchStart = DateTimeOffset.Now;
            }

            _notificationTimer ??= new Timer(_ => FlushNotifications(), null, Timeout.Infinite, Timeout.Infinite);

            // 攒够窗口就立刻弹，否则再等一小会儿，让同批邮件合并成一条。
            var waited = DateTimeOffset.Now - _notificationBatchStart;
            var delay = waited >= NotificationBatchWindow ? 0 : NotificationBatchDelayMs;
            _notificationTimer.Change(delay, Timeout.Infinite);
        }
    }

    private void FlushNotifications()
    {
        List<NewMailBatchItem> batch;

        lock (_notificationGate)
        {
            if (_pendingNotifications.Count == 0)
            {
                return;
            }

            batch = [.. _pendingNotifications];
            _pendingNotifications.Clear();
            _notificationBatchStart = DateTimeOffset.MinValue;
        }

        var settings = _store.GetSettings();

        if (!settings.NotificationsEnabled)
        {
            // 攒的过程中用户把通知关掉了。
            return;
        }

        if (NotificationPolicy.IsPrivacyMode())
        {
            var first = batch[0];
            ShowNewMailNotification(first.Summary, first.AccountId, settings, privacy: true);
            return;
        }

        if (settings.NotificationMerge && batch.Count > 1)
        {
            var total = batch.Sum(item => item.Count);
            ShowMergedNotification(batch, total, settings);
            return;
        }

        foreach (var item in batch)
        {
            ShowNewMailNotification(item.Summary, item.AccountId, settings, privacy: false);
        }
    }

    /// <summary>通知正文按设置拼：主题与正文摘要都可以单独关掉。</summary>
    private void ShowNewMailNotification(MailSummaryDto latest, string accountId, AppSettingsDto settings, bool privacy)
    {
        var subject = string.IsNullOrWhiteSpace(latest.Subject) ? "（无主题）" : Collapse(latest.Subject);
        var preview = Truncate(Collapse(latest.Preview), 90);
        var sender = string.IsNullOrWhiteSpace(latest.FromName) ? latest.FromAddress : latest.FromName;

        string title;
        string body;
        string? extraLine = null;

        if (privacy)
        {
            // 锁屏时把主题、摘要、发件人都收起来，只留一句"有新邮件"。
            title = "MailDog";
            body = "收到一封新邮件";
        }
        else if (!settings.NotificationShowSubject)
        {
            // 不显示主题时至少给一句能看懂的话，避免通知看起来是空的。
            title = string.IsNullOrWhiteSpace(sender) ? "新邮件" : sender;
            body = settings.NotificationShowPreview && preview.Length > 0 ? preview : "收到一封新邮件";
        }
        else
        {
            title = string.IsNullOrWhiteSpace(sender) ? "新邮件" : sender;
            body = subject;

            if (settings.NotificationShowPreview && preview.Length > 0)
            {
                extraLine = preview;
            }
        }

        var sound = ResolveNotificationSound(settings, accountId);
        var playSound = settings.NotificationSoundEnabled
            && !string.Equals(sound, "none", StringComparison.OrdinalIgnoreCase);

        _tray?.ShowNotification(
            title,
            body,
            playSound,
            sound,
            privacy ? null : latest.FromName,
            privacy ? null : latest.FromAddress,
            privacy ? null : extraLine,
            BuildActivationUri(latest.Id, accountId, latest.FolderId),
            actions: privacy ? null : BuildNotificationActions(latest, accountId));
    }

    /// <summary>同一批到达的多封邮件合并成一条，只显示数量与最新一封。</summary>
    private void ShowMergedNotification(IReadOnlyList<NewMailBatchItem> batch, int total, AppSettingsDto settings)
    {
        var newest = batch[^1];
        var subject = string.IsNullOrWhiteSpace(newest.Summary.Subject) ? "（无主题）" : Collapse(newest.Summary.Subject);
        var sender = string.IsNullOrWhiteSpace(newest.Summary.FromName)
            ? newest.Summary.FromAddress
            : newest.Summary.FromName;

        var title = $"收到 {total} 封新邮件";
        var body = settings.NotificationShowSubject
            ? $"{Collapse(sender)}：{subject}"
            : "打开 MailDog 查看收件箱";

        var sound = ResolveNotificationSound(settings, newest.AccountId);
        var playSound = settings.NotificationSoundEnabled
            && !string.Equals(sound, "none", StringComparison.OrdinalIgnoreCase);

        _tray?.ShowNotification(
            title,
            body,
            playSound,
            sound,
            null,
            null,
            null,
            BuildActivationUri(newest.Summary.Id, newest.AccountId, newest.Summary.FolderId),
            tag: "digest");
    }

    private static string BuildActivationUri(long messageId, string accountId, long folderId)
        => $"{ShellIntegration.UriPrefix}mail?m={messageId}&a={Uri.EscapeDataString(accountId)}&f={folderId}";

    private static string ResolveNotificationSound(AppSettingsDto settings, string accountId)
    {
        var perAccount = settings.AccountNotificationSounds
            .FirstOrDefault(item => string.Equals(item.AccountId, accountId, StringComparison.Ordinal))
            ?.Sound;

        return string.IsNullOrWhiteSpace(perAccount) ? settings.NotificationSound : perAccount;
    }

    private IReadOnlyList<NotificationAction>? BuildNotificationActions(
        MailSummaryDto latest,
        string accountId)
    {
        var actions = new List<NotificationAction>(2);

        if (!latest.Flags.HasFlag(MailFlags.Seen))
        {
            actions.Add(new NotificationAction(
                "read",
                "标记已读",
                BuildActionUri("read", latest.Id, accountId, latest.FolderId)));
        }

        var archive = _store.GetFolders(accountId).FirstOrDefault(folder =>
            folder.Role == FolderRole.Archive && folder.Id != latest.FolderId);
        if (archive is not null)
        {
            actions.Add(new NotificationAction(
                "archive",
                "归档",
                BuildActionUri("archive", latest.Id, accountId, latest.FolderId)));
        }

        return actions.Count == 0 ? null : actions;
    }

    private static string BuildActionUri(string action, long messageId, string accountId, long folderId)
        => $"{ShellIntegration.UriPrefix}action?do={action}&m={messageId}&a={Uri.EscapeDataString(accountId)}&f={folderId}";

    private static string Collapse(string value)
        => string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length].TrimEnd() + "…";

    /// <summary>点通知后把用户带到那封邮件：能广播就广播，界面没开就顺手拉起。</summary>
    public void Activate(string uri)
    {
        _log.Info($"收到激活：{uri}");

        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            ActivateCompose(uri);
            return;
        }

        if (ParseNotificationAction(uri) is { } notificationAction)
        {
            _ = Task.Run(() => HandleNotificationActionAsync(notificationAction));
            return;
        }

        if (ParseEmlActivation(uri) is { Length: > 0 } emlPath)
        {
            ActivateFile(emlPath);
            return;
        }

        if (ParseActivation(uri) is { } target)
        {
            _pendingNavigation = target;
        }

        OpenUi();
    }

    /// <summary>直接打开外部 .eml：界面已在运行时发事件，否则记下来并拉起界面。</summary>
    public void ActivateFile(string path)
    {
        _log.Info($"打开 .eml：{path}");

        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { EmlPath = path });
            return;
        }

        _pendingEml = path;
        OpenUi();
    }

    private void ActivateCompose(string uri)
    {
        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { ComposeUri = uri });
            return;
        }

        _pendingCompose = uri;
        OpenUi();
    }

    private static string? ParseEmlActivation(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "maildog", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, "eml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2 && parts[0] == "path")
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static (string Action, long MessageId, string AccountId, long FolderId)? ParseNotificationAction(string uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !string.Equals(parsed.Scheme, "maildog", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            long messageId = 0;
            long folderId = 0;
            var accountId = string.Empty;
            var action = string.Empty;

            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "do":
                        action = parts[1];
                        break;
                    case "m":
                        long.TryParse(parts[1], out messageId);
                        break;
                    case "f":
                        long.TryParse(parts[1], out folderId);
                        break;
                    case "a":
                        accountId = Uri.UnescapeDataString(parts[1]);
                        break;
                }
            }

            return action is "read" or "archive" && messageId > 0 && accountId.Length > 0
                ? (action, messageId, accountId, folderId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleNotificationActionAsync(
        (string Action, long MessageId, string AccountId, long FolderId) action)
    {
        try
        {
            var message = _store.GetMessage(action.MessageId);
            if (message is null
                || !string.Equals(message.AccountId, action.AccountId, StringComparison.Ordinal))
            {
                _log.Warn($"通知操作找不到邮件：{action.MessageId}");
                return;
            }

            var sourceFolderId = action.FolderId > 0 ? action.FolderId : message.FolderId;

            if (action.Action == "read")
            {
                await _sync.SetSeenAsync(action.MessageId, true, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                var archive = _store.GetFolders(action.AccountId).FirstOrDefault(folder =>
                    folder.Role == FolderRole.Archive && folder.Id != message.FolderId);

                if (archive is null)
                {
                    _log.Warn($"通知归档失败：账户 {action.AccountId} 没有归档文件夹。");
                    return;
                }

                await _sync.MoveMessageAsync(action.MessageId, archive.Id, _cts.Token).ConfigureAwait(false);
            }

            _ipc.Broadcast(IpcMessageTypes.MailChanged, new MailChangedEvent
            {
                AccountId = action.AccountId,
                MessageId = action.MessageId,
                FolderId = sourceFolderId,
                Change = action.Action == "read" ? "flags" : "moved"
            });

            _tray?.UpdateUnreadCount(TotalUnread());
        }
        catch (Exception exception)
        {
            _log.Warn($"通知操作执行失败：{exception.Message}");
        }
    }

    private static (long MessageId, string AccountId, long FolderId)? ParseActivation(string uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return null;
            }

            long messageId = 0;
            long folderId = 0;
            var accountId = string.Empty;

            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);

                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "m":
                        long.TryParse(parts[1], out messageId);
                        break;
                    case "f":
                        long.TryParse(parts[1], out folderId);
                        break;
                    case "a":
                        accountId = Uri.UnescapeDataString(parts[1]);
                        break;
                }
            }

            return messageId > 0 && accountId.Length > 0
                ? (messageId, accountId, folderId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private PendingNavigationResponse TakePendingNavigation()
    {
        var target = _pendingNavigation;
        _pendingNavigation = null;

        var openAbout = _pendingOpenAbout;
        _pendingOpenAbout = false;
        var compose = _pendingCompose;
        _pendingCompose = null;
        var eml = _pendingEml;
        _pendingEml = null;

        return new PendingNavigationResponse
        {
            HasTarget = target is not null,
            MessageId = target?.MessageId,
            AccountId = target?.AccountId,
            FolderId = target?.FolderId,
            ComposeUri = compose,
            EmlPath = eml,
            OpenAbout = openAbout,
            CheckUpdate = openAbout
        };
    }

    /// <summary>托盘里的"检查更新"：把窗口叫出来并落到关于页，检查与下载都在界面里做。</summary>
    private void OpenAbout()
    {
        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { OpenAbout = true, CheckUpdate = true });
            return;
        }

        // 界面没开：先记下诉求，等它连上来取待办导航时一并交代。
        _pendingOpenAbout = true;
        OpenUi();
    }

    private void OpenUi()
    {
        var navigation = _pendingNavigation is { } target
            ? new ShowMainWindowEvent
            {
                MessageId = target.MessageId,
                AccountId = target.AccountId,
                FolderId = target.FolderId,
                ComposeUri = _pendingCompose,
                EmlPath = _pendingEml
            }
            : new ShowMainWindowEvent
            {
                ComposeUri = _pendingCompose,
                EmlPath = _pendingEml
            };

        if (_ipc.HasConnections)
        {
            _pendingNavigation = null;
            _pendingCompose = null;
            _pendingEml = null;
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, navigation);
            return;
        }

        var uiPath = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

        if (string.IsNullOrWhiteSpace(uiPath) || !File.Exists(uiPath))
        {
            // 打包后 UI 与 Engine 同目录；开发时两者在不同项目的输出目录里，
            // 光看同目录会一直"未找到 UI"，所以按仓库结构向上找一遍。
            uiPath = LocateUi();
        }

        if (string.IsNullOrWhiteSpace(uiPath) || !File.Exists(uiPath))
        {
            _log.Warn($"未找到 UI：{uiPath}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uiPath)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception exception)
        {
            _log.Error("启动 UI 失败。", exception);
        }
    }

    /// <summary>同目录优先（安装后就是这样）；找不到再按仓库里的 UI 构建目录向上搜。</summary>
    private static string? LocateUi()
    {
        var sameDirectory = Path.Combine(AppContext.BaseDirectory, ProductPaths.UiExecutableName);

        if (File.Exists(sameDirectory))
        {
            return sameDirectory;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var runtime = ProductPaths.RuntimeIdentifier;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var uiRoot = Path.Combine(directory.FullName, "MailDog.UI", "bin", configuration);

            if (!Directory.Exists(uiRoot))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(uiRoot, "net11.0", runtime, ProductPaths.UiExecutableName),
                         Path.Combine(uiRoot, "net11.0", ProductPaths.UiExecutableName)
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string BuildTooltip()
    {
        var accounts = _store.GetAccounts();
        return $"MailDog — {accounts.Count} 个账户，{TotalUnread()} 封未读";
    }

    private int TotalUnread()
        => _store.GetAccounts().Sum(account => _store.GetFolders(account.Id).Sum(folder => folder.UnreadCount));

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _cts.Cancel();
        _tray?.Quit();
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        PruneCacheIfDue();
        WakeDueSnoozes();
        await SyncAllAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 每 30 秒检查一次：到期的账户才同步，失败账户按退避时间重试。
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                PruneCacheIfDue();
                WakeDueSnoozes();
                await SyncAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WakeDueSnoozes()
    {
        try
        {
            var due = _store.WakeDueSnoozes(DateTimeOffset.UtcNow);
            if (due.Count == 0)
            {
                return;
            }

            foreach (var item in due)
            {
                _ipc.Broadcast(IpcMessageTypes.MailChanged, new MailChangedEvent
                {
                    AccountId = item.AccountId,
                    MessageId = item.MessageId,
                    FolderId = item.FolderId,
                    Change = "flags"
                });
            }

            _tray?.UpdateUnreadCount(TotalUnread());
            _log.Info($"已唤醒 {due.Count} 封稍后提醒邮件。");
        }
        catch (Exception exception)
        {
            _log.Warn($"唤醒稍后提醒失败：{exception.Message}");
        }
    }

    private void PruneCacheIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(24))
        {
            return;
        }

        _lastPruneUtc = now;

        try
        {
            var days = _store.GetSettings().CacheDays;
            var removed = _store.PruneMessages(days);

            if (removed > 0)
            {
                _log.Info($"已清理 {removed} 封超过 {Math.Clamp(days, 1, 3650)} 天的本地缓存邮件。");
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"清理本地缓存失败：{exception.Message}");
        }
    }

    private async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        var accounts = _store.GetAccounts();

        // 顺带让 IDLE 监听与账户列表保持一致。
        _idle.Refresh(accounts);

        var now = DateTimeOffset.UtcNow;
        var synced = false;

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_syncStateGate)
            {
                if (_nextSyncAt.TryGetValue(account.Id, out var due) && due > now)
                {
                    continue;
                }
            }

            synced |= await TrySyncAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }

        if (synced)
        {
            _tray?.UpdateTooltip(BuildTooltip());
            _tray?.UpdateUnreadCount(TotalUnread());
        }
    }

    /// <summary>系统从睡眠恢复：IDLE 长连接多半已断，重建监听并立即同步全部账户。</summary>
    private void OnSystemResumed()
    {
        _log.Info("系统已从睡眠恢复，重新建立同步连接。");

        _idle.RestartAll();

        lock (_syncStateGate)
        {
            _nextSyncAt.Clear();
        }

        _ = SyncAfterResumeAsync();
    }

    private async Task SyncAfterResumeAsync()
    {
        try
        {
            // 唤醒后网络通常需要几秒才恢复。
            await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
            await SyncAllAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭中。
        }
    }

    /// <summary>IDLE 收到新邮件后的即时同步。</summary>
    private async Task SyncFromIdleAsync(string accountId)
    {
        try
        {
            if (await TrySyncAccountAsync(accountId, _cts.Token).ConfigureAwait(false))
            {
                _tray?.UpdateTooltip(BuildTooltip());
                _tray?.UpdateUnreadCount(TotalUnread());
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭中。
        }
    }

    private async Task<bool> TrySyncAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        // 同一账户同一时间只跑一个同步，IDLE 与轮询不会叠加。
        if (!_synchronizing.TryAdd(accountId, 0))
        {
            return false;
        }

        try
        {
            await _sync.SyncAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

            lock (_syncStateGate)
            {
                _syncFailures.Remove(accountId);
                _syncErrors.Remove(accountId);
                _nextSyncAt[accountId] = DateTimeOffset.UtcNow.AddMinutes(CurrentIntervalMinutes);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationException exception)
        {
            // 认证失败通常需要用户重新登录，避免频繁重试。
            lock (_syncStateGate)
            {
                _nextSyncAt[accountId] = DateTimeOffset.UtcNow.AddMinutes(30);
                _syncErrors[accountId] = ("authentication", $"认证失败：{exception.Message}");
            }

            _log.Warn($"账户认证失败，30 分钟后重试：{exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            int failures;
            TimeSpan delay;

            lock (_syncStateGate)
            {
                failures = _syncFailures.GetValueOrDefault(accountId) + 1;
                _syncFailures[accountId] = failures;
                delay = TimeSpan.FromSeconds(Math.Min(
                    600,
                    30 * Math.Pow(2, Math.Min(failures - 1, 4))));
                _nextSyncAt[accountId] = DateTimeOffset.UtcNow + delay;
                _syncErrors[accountId] = (ClassifySyncError(exception), exception.Message);
            }

            _log.Warn($"同步账户失败，{delay.TotalSeconds:0} 秒后重试：{exception.Message}");
            return false;
        }
        finally
        {
            _synchronizing.TryRemove(accountId, out _);
        }
    }

    private AccountHealthDto[] GetAccountHealth()
    {
        var idleStates = _idle.GetStates().ToDictionary(state => state.AccountId, StringComparer.Ordinal);
        var accounts = _store.GetAccounts();

        return accounts.Select(account =>
        {
            var idle = idleStates.GetValueOrDefault(account.Id);
            int failures;
            DateTimeOffset? nextRetry;
            string? errorKind;
            string? errorMessage;

            lock (_syncStateGate)
            {
                failures = _syncFailures.GetValueOrDefault(account.Id);
                nextRetry = _nextSyncAt.GetValueOrDefault(account.Id) is { } due && due > DateTimeOffset.UtcNow
                    ? due
                    : null;
                var error = _syncErrors.GetValueOrDefault(account.Id);
                errorKind = error.Kind;
                errorMessage = error.Message;
            }

            return new AccountHealthDto
            {
                AccountId = account.Id,
                Email = account.Email,
                DisplayName = account.DisplayName,
                Status = account.Status,
                LastSyncUtc = account.LastSyncUtc,
                IdleConnected = idle?.IsConnected ?? false,
                IdleSupported = idle?.SupportsIdle,
                IdleSinceUtc = idle?.ConnectedSinceUtc,
                IdleError = idle?.LastError,
                LastSyncErrorKind = errorKind,
                LastSyncError = errorMessage,
                ConsecutiveFailures = failures > 0
                    ? failures
                    : errorKind is "authentication" ? 1 : 0,
                NextRetryUtc = failures > 0 || errorKind is "authentication" ? nextRetry : null
            };
        }).ToArray();
    }

    private static string ClassifySyncError(Exception exception)
        => exception switch
        {
            AuthenticationException => "authentication",
            ImapProtocolException => "protocol",
            TimeoutException or OperationCanceledException => "timeout",
            SocketException or IOException => "network",
            _ => "other"
        };

    private int CurrentIntervalMinutes
        => Math.Clamp(_store.GetSettings().SyncIntervalMinutes, 1, 60);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_scheduler is not null)
        {
            try
            {
                await _scheduler.ConfigureAwait(false);
            }
            catch
            {
                // 关闭时忽略。
            }
        }

        await _idle.DisposeAsync().ConfigureAwait(false);
        await _outbox.DisposeAsync().ConfigureAwait(false);
        await _flagSync.DisposeAsync().ConfigureAwait(false);
        await _sync.DisposeAsync().ConfigureAwait(false);
        await _ipc.DisposeAsync().ConfigureAwait(false);

        lock (_notificationGate)
        {
            _notificationTimer?.Dispose();
            _notificationTimer = null;
            _pendingNotifications.Clear();
        }

        _tray?.Dispose();
        _outlookAuth.Dispose();
        _store.Dispose();
        _log.Dispose();
        _cts.Dispose();
    }
}
