using MailDog.Contracts.Ipc;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
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
    private readonly IOutlookAuthService _auth;
    private readonly RequestRouter _router;
    private readonly IpcServer _ipc;
    private readonly CancellationTokenSource _cts = new();
    private Task? _scheduler;

    public EngineHost(string databasePath)
    {
        _log = new FileLogger();
        _databasePath = databasePath;
        _store = new SqliteStore(databasePath);
        _secrets = new SecretStore(_store);
        _accounts = new AccountService(_store, _secrets);
        _sync = new ImapSyncService(_store, _accounts, new NullAccessTokenProvider());
        _auth = new NotConfiguredOutlookAuthService();
        _router = new RequestRouter(_store, _accounts, _sync, _auth);
        _ipc = new IpcServer(_router);

        _router.Diagnostic += message => _log.Warn(message);
        _sync.Diagnostic += message => _log.Warn(message);
        _sync.NewMail += message => _ipc.Broadcast(IpcMessageTypes.NewMail, message);
        _sync.SyncStateChanged += message => _ipc.Broadcast(IpcMessageTypes.SyncState, message);
        _sync.AuthStateChanged += message => _ipc.Broadcast(IpcMessageTypes.AuthState, message);
    }

    public async Task RunAsync()
    {
        _log.Info($"Engine 启动，数据库：{_databasePath}");

        _ipc.Start();
        _scheduler = SchedulerLoopAsync(_cts.Token);

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _cts.Cancel();
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        await SyncAllAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
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
        _store.Dispose();
        _log.Dispose();
        _cts.Dispose();
    }
}
