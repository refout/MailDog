using System.Collections.Concurrent;
using System.Diagnostics;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;
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
    private readonly OutboxWorker _outbox;
    private readonly FlagSyncWorker _flagSync;
    private readonly RequestRouter _router;
    private readonly IpcServer _ipc;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _hideConsole;
    private readonly ConcurrentDictionary<string, byte> _synchronizing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextSyncAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _syncFailures = new(StringComparer.Ordinal);
    private readonly object _syncStateGate = new();
    private Task? _scheduler;
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private Win32Tray? _tray;
    private (long MessageId, string AccountId, long FolderId)? _lastNotification;
    private (long MessageId, string AccountId, long FolderId)? _pendingNavigation;

    public EngineHost(string databasePath, bool hideConsole = true)
    {
        _log = new FileLogger();
        _databasePath = databasePath;
        _hideConsole = hideConsole;
        _store = new SqliteStore(databasePath);
        _secrets = new SecretStore(_store);
        _accounts = new AccountService(_store, _secrets);
        _outlookAuth = new OutlookAuthService(_store, _accounts, _secrets);
        _sync = new ImapSyncService(_store, _accounts, _outlookAuth);
        _idle = new ImapIdleWatcher(_accounts, _outlookAuth);
        var dataDirectory = Path.GetDirectoryName(_databasePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailDog");
        _smtp = new SmtpSendService(_store, _accounts, _outlookAuth, dataDirectory);
        _outbox = new OutboxWorker(_store, _smtp);
        _flagSync = new FlagSyncWorker(_store, _sync);
        _router = new RequestRouter(_store, _accounts, _sync, _outlookAuth, _smtp);
        _ipc = new IpcServer(_router, message => _log.Warn(message));

        _router.Diagnostic += message => _log.Warn(message);
        _router.TakePendingNavigation = TakePendingNavigation;
        _sync.Diagnostic += message => _log.Warn(message);
        _smtp.Diagnostic += message => _log.Warn(message);
        _outbox.Diagnostic += message => _log.Info(message);
        _flagSync.Diagnostic += message => _log.Info(message);
        _idle.Diagnostic += message => _log.Info(message);
        _idle.MailArrived += accountId => _ = SyncFromIdleAsync(accountId);
        _sync.NewMail += message =>
        {
            _ipc.Broadcast(IpcMessageTypes.NewMail, message);
            _tray?.UpdateUnreadCount(TotalUnread());

            if (message.Latest is not null)
            {
                var settings = _store.GetSettings();

                if (settings.NotificationsEnabled)
                {
                    _lastNotification = (message.Latest.Id, message.AccountId, message.FolderId);
                    _tray?.ShowNotification(
                        "新邮件",
                        $"{message.Latest.FromName}: {message.Latest.Subject}",
                        settings.NotificationSoundEnabled,
                        settings.NotificationSound,
                        message.Latest.FromName,
                        message.Latest.FromAddress);
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

        _ipc.Start();
        _scheduler = SchedulerLoopAsync(_cts.Token);
        _outbox.Start();
        _flagSync.Start();

        _tray = new Win32Tray(
            OpenUi,
            HandleNotificationClicked,
            () => _ = SyncAllAsync(_cts.Token),
            OnSystemResumed,
            () => _cts.Cancel(),
            IsDarkTheme);

        _tray.UpdateTooltip(BuildTooltip());
        _tray.UpdateUnreadCount(TotalUnread());

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

    private PendingNavigationResponse TakePendingNavigation()
    {
        var target = _pendingNavigation;
        _pendingNavigation = null;

        return target is { } value
            ? new PendingNavigationResponse
            {
                HasTarget = true,
                MessageId = value.MessageId,
                AccountId = value.AccountId,
                FolderId = value.FolderId
            }
            : new PendingNavigationResponse();
    }

    private void OpenUi()
    {
        var navigation = _pendingNavigation is { } target
            ? new ShowMainWindowEvent
            {
                MessageId = target.MessageId,
                AccountId = target.AccountId,
                FolderId = target.FolderId
            }
            : new ShowMainWindowEvent();

        if (_ipc.HasConnections)
        {
            _pendingNavigation = null;
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, navigation);
            return;
        }

        var uiPath = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

        if (string.IsNullOrWhiteSpace(uiPath) || !File.Exists(uiPath))
        {
            uiPath = Path.Combine(AppContext.BaseDirectory, "MailDog.UI.exe");
        }

        if (!File.Exists(uiPath))
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
        await SyncAllAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 每 30 秒检查一次：到期的账户才同步，失败账户按退避时间重试。
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                PruneCacheIfDue();
                await SyncAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
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
            }

            _log.Warn($"同步账户失败，{delay.TotalSeconds:0} 秒后重试：{exception.Message}");
            return false;
        }
        finally
        {
            _synchronizing.TryRemove(accountId, out _);
        }
    }

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
        _tray?.Dispose();
        _outlookAuth.Dispose();
        _store.Dispose();
        _log.Dispose();
        _cts.Dispose();
    }
}
