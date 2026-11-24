using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;

namespace MailDog.Engine.Hosting;

/// <summary>后台发件箱：每 5 秒取到期记录重试，失败按指数退避。</summary>
public sealed class OutboxWorker : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly SqliteStore _store;
    private readonly SmtpSendService _sender;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public OutboxWorker(SqliteStore store, SmtpSendService sender)
    {
        _store = store;
        _sender = sender;
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
        // 启动后先处理一次上次遗留的队列。
        await ProcessAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
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
        var due = _store.GetDueOutbox(DateTimeOffset.UtcNow, 3);

        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _sender.TrySendAsync(item.Id, cancellationToken).ConfigureAwait(false);
                if (result.Sent)
                {
                    Diagnostic?.Invoke($"发件箱邮件 #{item.Id} 已发送。");
                }
                else if (!result.Queued)
                {
                    Diagnostic?.Invoke($"发件箱邮件 #{item.Id} 最终失败：{result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke($"发件箱邮件 #{item.Id} 处理失败：{exception.Message}");
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
