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

public sealed partial class EngineHost
{
    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexed = _store.BackfillSearchIndex();
            if (indexed > 0)
            {
                _log.Info($"后台补齐全文索引 {indexed} 条。");
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"补齐全文索引失败：{exception.Message}");
        }

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
