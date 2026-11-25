using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Mail.Imap;

/// <summary>IDLE 连接的只读状态，供账号健康面板展示。</summary>
public sealed record IdleConnectionState(
    string AccountId,
    bool IsConnected,
    bool? SupportsIdle,
    DateTimeOffset? ConnectedSinceUtc,
    string? LastError);

/// <summary>
/// 为每个账户维持一条 IMAP IDLE 连接，收到新邮件通知时触发同步。
/// 与同步会话分开连接，避免 IDLE 长时间占用操作通道。
/// </summary>
public sealed class ImapIdleWatcher : IAsyncDisposable
{
    // 多数服务器会在 30 分钟内断开 IDLE，这里提前重建。
    private static readonly TimeSpan IdleRenewInterval = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthBackoff = TimeSpan.FromMinutes(30);

    private readonly AccountService _accounts;
    private readonly IAccessTokenProvider _tokens;
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdleConnectionState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    public ImapIdleWatcher(AccountService accounts, IAccessTokenProvider tokens)
    {
        _accounts = accounts;
        _tokens = tokens;
    }

    /// <summary>某个账户有新邮件到达。</summary>
    public event Action<string>? MailArrived;

    public event Action<string>? Diagnostic;

    /// <summary>按当前账户列表启动/停止监听（幂等）。</summary>
    public void Refresh(IReadOnlyList<AccountRecord> accounts)
    {
        lock (_gate)
        {
            var ids = accounts.Select(account => account.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var id in _loops.Keys.Where(key => !ids.Contains(key)).ToArray())
            {
                Stop(_loops[id]);
                _loops.Remove(id);
            }

            foreach (var id in _states.Keys.Where(key => !ids.Contains(key)).ToArray())
            {
                _states.Remove(id);
            }

            foreach (var account in accounts)
            {
                if (_loops.ContainsKey(account.Id))
                {
                    continue;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var task = Task.Run(() => RunAsync(account, cancellation.Token));
                _loops[account.Id] = new Loop(cancellation, task);
            }
        }
    }

    /// <summary>断开所有监听连接；下一次 Refresh 会重新启动。</summary>
    public void RestartAll()
    {
        Loop[] loops;

        lock (_gate)
        {
            loops = _loops.Values.ToArray();
            _loops.Clear();
        }

        foreach (var loop in loops)
        {
            Stop(loop);
        }
    }

    /// <summary>当前每个账户的 IDLE 连接状态；没有建立过连接时不返回。</summary>
    public IReadOnlyList<IdleConnectionState> GetStates()
    {
        lock (_gate)
        {
            return _states.Values.ToArray();
        }
    }

    private async Task RunAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            ImapClient? client = null;
            TimeSpan delay;
            UpdateState(account.Id, connected: false);

            try
            {
                client = await ImapConnectionFactory
                    .ConnectAsync(account, _accounts, _tokens, cancellationToken)
                    .ConfigureAwait(false);

                if (!client.Capabilities.Contains(ImapCapability.Idle))
                {
                    UpdateState(account.Id, connected: false, supportsIdle: false, error: "服务器不支持 IDLE");
                    Diagnostic?.Invoke($"账户 {account.Email} 的服务器不支持 IDLE，继续使用周期同步。");
                    return;
                }

                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                failures = 0;
                UpdateState(
                    account.Id,
                    connected: true,
                    supportsIdle: true,
                    connectedSince: DateTimeOffset.UtcNow,
                    error: null);
                Diagnostic?.Invoke($"账户 {account.Email} 已启用 IDLE 实时同步。");

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (await IdleOnceAsync(client, inbox, cancellationToken).ConfigureAwait(false))
                    {
                        MailArrived?.Invoke(account.Id);
                    }
                }

                delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (AuthenticationException exception)
            {
                UpdateState(account.Id, connected: false, supportsIdle: true, error: $"认证失败：{exception.Message}");
                Diagnostic?.Invoke($"账户 {account.Email} 的 IDLE 认证失败：{exception.Message}");
                delay = AuthBackoff;
            }
            catch (Exception exception)
            {
                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(
                    MaxBackoff.TotalSeconds,
                    30 * Math.Pow(2, Math.Min(failures - 1, 5))));

                UpdateState(account.Id, connected: false, error: $"连接中断：{exception.Message}");
                Diagnostic?.Invoke($"账户 {account.Email} 的 IDLE 连接中断，{delay.TotalSeconds:0} 秒后重连：{exception.Message}");
            }
            finally
            {
                if (client is not null)
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
                        // 重连前忽略断开异常。
                    }

                    client.Dispose();
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (delay <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void UpdateState(
        string accountId,
        bool connected,
        bool? supportsIdle = null,
        DateTimeOffset? connectedSince = null,
        string? error = null)
    {
        lock (_gate)
        {
            var previous = _states.GetValueOrDefault(accountId);

            _states[accountId] = new IdleConnectionState(
                accountId,
                connected,
                supportsIdle ?? previous?.SupportsIdle,
                connected ? connectedSince ?? previous?.ConnectedSinceUtc : null,
                error);
        }
    }

    /// <summary>进入一次 IDLE；返回 true 表示期间收到新邮件。</summary>
    private static async Task<bool> IdleOnceAsync(
        ImapClient client,
        IMailFolder inbox,
        CancellationToken cancellationToken)
    {
        var arrived = false;
        var countChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCountChanged(object? sender, EventArgs e)
        {
            arrived = true;
            countChanged.TrySetResult();
        }

        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        done.CancelAfter(IdleRenewInterval);
        inbox.CountChanged += OnCountChanged;

        try
        {
            var idle = client.IdleAsync(done.Token, cancellationToken);
            var completed = await Task.WhenAny(idle, countChanged.Task).ConfigureAwait(false);

            if (completed == idle)
            {
                // 到期或被取消：让 IDLE 正常收尾。
                await idle.ConfigureAwait(false);
                return false;
            }

            // 收到新邮件：结束本次 IDLE，回到外层重新进入。
            done.Cancel();

            try
            {
                await idle.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // IDLE 被主动结束。
            }

            return arrived;
        }
        finally
        {
            inbox.CountChanged -= OnCountChanged;
        }
    }

    private static void Stop(Loop loop)
    {
        loop.Cancellation.Cancel();
        _ = DisposeLoopAsync(loop);
    }

    private static async Task DisposeLoopAsync(Loop loop)
    {
        try
        {
            await loop.Task.ConfigureAwait(false);
        }
        catch
        {
            // 停止时忽略。
        }

        loop.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        Loop[] loops;
        lock (_gate)
        {
            loops = _loops.Values.ToArray();
            _loops.Clear();
        }

        foreach (var loop in loops)
        {
            loop.Cancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(loops.Select(loop => loop.Task)).ConfigureAwait(false);
        }
        catch
        {
            // 关闭时忽略。
        }

        foreach (var loop in loops)
        {
            loop.Cancellation.Dispose();
        }

        _cts.Dispose();
    }

    private sealed record Loop(CancellationTokenSource Cancellation, Task Task);
}
