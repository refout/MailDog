using System.Diagnostics;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Storage;

namespace MailDog.Engine.Hosting;

public sealed class EngineHost : IAsyncDisposable
{
    private readonly FileLogger _log;
    private readonly string _databasePath;
    private readonly SqliteStore _store;
    private readonly SecretStore _secrets;
    private readonly AccountService _accounts;
    private readonly ImapSyncService _sync;
    private readonly OutlookAuthService _outlookAuth;
    private readonly RequestRouter _router;
    private readonly IpcServer _ipc;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _hideConsole;
    private Task? _scheduler;
    private Win32Tray? _tray;

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
        _router = new RequestRouter(_store, _accounts, _sync, _outlookAuth);
        _ipc = new IpcServer(_router, message => _log.Warn(message));

        _router.Diagnostic += message => _log.Warn(message);
        _sync.Diagnostic += message => _log.Warn(message);
        _sync.NewMail += message =>
        {
            _ipc.Broadcast(IpcMessageTypes.NewMail, message);

            if (message.Latest is not null)
            {
                var settings = _store.GetSettings();

                if (settings.NotificationsEnabled)
                {
                    _tray?.ShowNotification(
                        "新邮件",
                        $"{message.Latest.FromName}: {message.Latest.Subject}",
                        settings.NotificationSoundEnabled,
                        settings.NotificationSound);
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

        _ipc.Start();
        _scheduler = SchedulerLoopAsync(_cts.Token);

        _tray = new Win32Tray(
            OpenUi,
            () => _ = SyncAllAsync(_cts.Token),
            () => _cts.Cancel());

        _tray.UpdateTooltip(BuildTooltip());

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

    private void OpenUi()
    {
        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent());
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
        var unread = accounts.Sum(account => _store.GetFolders(account.Id).Sum(folder => folder.UnreadCount));
        return $"MailDog — {accounts.Count} 个账户，{unread} 封未读";
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _cts.Cancel();
        _tray?.Quit();
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        await SyncAllAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var minutes = Math.Clamp(_store.GetSettings().SyncIntervalMinutes, 1, 60);
                await Task.Delay(TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
                await SyncAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        foreach (var account in _accounts.GetAccounts())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _sync.SyncAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.Warn($"同步账户 {account.Email} 失败：{exception.Message}");
            }
        }

        _tray?.UpdateTooltip(BuildTooltip());
    }

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

        await _sync.DisposeAsync().ConfigureAwait(false);
        await _ipc.DisposeAsync().ConfigureAwait(false);
        _tray?.Dispose();
        _outlookAuth.Dispose();
        _store.Dispose();
        _log.Dispose();
        _cts.Dispose();
    }
}
