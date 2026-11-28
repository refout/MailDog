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

public sealed partial class EngineHost : IAsyncDisposable
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
            ?? ProductPaths.DataDirectory;
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
        _router.ApplyRuleHistory = ApplyRuleHistory;
#if MAILDOG_WINDOWS
        _router.AddRecent = WindowsIntegration.AddRecent;
#endif
        // 先把响应发回给调用方，再收尾；否则界面/命令行只会看到连接被断开。
        _router.ShutdownRequested = () => _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            _tray?.Quit();
        });
        _router.OpenUiRequested = () => _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            OpenUi();
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

}
