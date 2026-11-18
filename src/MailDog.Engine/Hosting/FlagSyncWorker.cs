using MailDog.Mail.Imap;
using MailDog.Mail.Storage;

namespace MailDog.Engine.Hosting;

/// <summary>后台重试已读/旗标写回，失败后按 1/2/5/10/30 分钟退避。</summary>
public sealed class FlagSyncWorker : IAsyncDisposable
{
    private readonly SqliteStore _store;
    private readonly ImapSyncService _sync;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public FlagSyncWorker(SqliteStore store, ImapSyncService sync)
    {
        _store = store;
        _sync = sync;
    }

    public event Action<string>? Diagnostic;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await ProcessAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await ProcessAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭中。
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var due = _store.GetDuePendingFlags(DateTimeOffset.UtcNow, 20);

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await _sync.TryApplyPendingFlagAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    Diagnostic?.Invoke($"邮件标记 #{item.Id} 已写回服务器。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke($"邮件标记 #{item.Id} 重试失败：{exception.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // 关闭时忽略。
            }
        }

        _cts.Dispose();
    }
}
