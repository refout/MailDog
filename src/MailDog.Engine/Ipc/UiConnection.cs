using System.IO.Pipes;
using System.Threading.Channels;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;
using MailKit.Security;

namespace MailDog.Engine.Ipc;

public sealed class UiConnection : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly RequestRouter _router;
    private readonly Action<string>? _diagnostic;
    private readonly Channel<IpcEnvelope> _sendQueue = Channel.CreateUnbounded<IpcEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();

    public UiConnection(NamedPipeServerStream pipe, RequestRouter router, Action<string>? diagnostic = null)
    {
        _pipe = pipe;
        _router = router;
        _diagnostic = diagnostic;
    }

    public bool IsHandshaken { get; private set; }

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        var writeTask = WriteLoopAsync(linked.Token);

        try
        {
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            _sendQueue.Writer.TryComplete();
            try
            {
                await writeTask.ConfigureAwait(false);
            }
            catch
            {
                // 连接关闭时的写入异常忽略。
            }

            _cts.Cancel();
        }
    }

    public void Enqueue(IpcEnvelope envelope)
    {
        _sendQueue.Writer.TryWrite(envelope);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await IpcFrame.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return;
            }

            IpcEnvelope envelope;
            try
            {
                envelope = IpcJson.Deserialize(payload);
            }
            catch (Exception exception)
            {
                EnqueueError(0, IpcErrorCodes.InvalidRequest, "无法解析 IPC 消息。", exception.Message);
                return;
            }

            if (!IsHandshaken)
            {
                if (envelope.Type != IpcMessageTypes.Hello || !TryHandshake(envelope))
                {
                    EnqueueError(envelope.RequestId, IpcErrorCodes.ProtocolMismatch, "协议握手失败。");
                    return;
                }

                continue;
            }

            await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryHandshake(IpcEnvelope envelope)
    {
        var request = IpcCodecRegistry.Read(envelope) as HelloRequest;
        if (request is null || request.ProtocolVersion != IpcProtocol.Version)
        {
            return false;
        }

        var response = _router.CreateHelloAck();
        Enqueue(IpcCodecRegistry.Create(IpcMessageTypes.HelloAck, response, envelope.RequestId));
        IsHandshaken = true;
        return true;
    }

    private async Task DispatchAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _router.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return;
            }

            var responseType = response is ErrorResponse
                ? IpcMessageTypes.Error
                : GetResponseType(envelope.Type);

            Enqueue(IpcCodecRegistry.Create(responseType, response, envelope.RequestId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EnqueueError(envelope.RequestId, IpcErrorCodes.Timeout, "请求已取消或超时。");
        }
        catch (AuthenticationException exception)
        {
            Log($"请求 {envelope.Type} 认证失败：{exception.Message}");
            EnqueueError(envelope.RequestId, IpcErrorCodes.AuthRequired, "认证失败，请重新登录。", exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            Log($"请求 {envelope.Type} 失败：{exception.Message}");
            EnqueueError(envelope.RequestId, IpcErrorCodes.NotFound, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            Log($"请求 {envelope.Type} 失败：{exception.Message}");
            EnqueueError(envelope.RequestId, IpcErrorCodes.InvalidRequest, exception.Message);
        }
        catch (ArgumentException exception)
        {
            Log($"请求 {envelope.Type} 参数错误：{exception}");
            EnqueueError(envelope.RequestId, IpcErrorCodes.InvalidRequest, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            Log($"请求 {envelope.Type} 失败：{exception.Message}");
            EnqueueError(envelope.RequestId, IpcErrorCodes.InvalidRequest, exception.Message);
        }
        catch (Exception exception)
        {
            Log($"请求 {envelope.Type} 异常：{exception}");
            EnqueueError(
                envelope.RequestId,
                IpcErrorCodes.Internal,
                $"Engine 处理请求失败：{exception.Message}",
                exception.ToString());
        }
    }

    private void Log(string message) => _diagnostic?.Invoke(message);

    private void EnqueueError(long requestId, string code, string message, string? detail = null)
    {
        Enqueue(IpcCodecRegistry.Create(
            IpcMessageTypes.Error,
            new ErrorResponse { Code = code, Message = message, Detail = detail },
            requestId));
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _sendQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = IpcJson.Serialize(envelope);
            await IpcFrame.WriteAsync(_pipe, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetResponseType(string requestType)
        => requestType switch
        {
            IpcMessageTypes.GetAccounts => IpcMessageTypes.Accounts,
            IpcMessageTypes.GetAccountPreferences => IpcMessageTypes.AccountPreferences,
            IpcMessageTypes.UpdateAccountPreferences => IpcMessageTypes.AccountPreferences,
            IpcMessageTypes.AddPasswordAccount => IpcMessageTypes.Account,
            IpcMessageTypes.GetFolders => IpcMessageTypes.Folders,
            IpcMessageTypes.GetMessages => IpcMessageTypes.MessagesPage,
            IpcMessageTypes.SearchMessages => IpcMessageTypes.MessageSearch,
            IpcMessageTypes.ListContacts => IpcMessageTypes.Contacts,
            IpcMessageTypes.GetMessageBody => IpcMessageTypes.MessageBody,
            IpcMessageTypes.DownloadAttachment => IpcMessageTypes.AttachmentSaved,
            IpcMessageTypes.ReadAttachment => IpcMessageTypes.AttachmentBytes,
            IpcMessageTypes.SendMail => IpcMessageTypes.SendMailResult,
            IpcMessageTypes.SaveDraft => IpcMessageTypes.DraftSaved,
            IpcMessageTypes.GetDraft => IpcMessageTypes.DraftContent,
            IpcMessageTypes.RetryOutbox => IpcMessageTypes.SendMailResult,
            IpcMessageTypes.CreateBackup => IpcMessageTypes.BackupResult,
            IpcMessageTypes.RestoreBackup => IpcMessageTypes.RestoreResult,
            IpcMessageTypes.ExportMessage => IpcMessageTypes.MessageExport,
            IpcMessageTypes.ListOutbox => IpcMessageTypes.OutboxList,
            IpcMessageTypes.GetRules => IpcMessageTypes.RulesList,
            IpcMessageTypes.GetSettings => IpcMessageTypes.Settings,
            IpcMessageTypes.TakePendingNavigation => IpcMessageTypes.PendingNavigation,
            IpcMessageTypes.MarkRead => IpcMessageTypes.Ok,
            IpcMessageTypes.MarkReadBatch => IpcMessageTypes.Ok,
            IpcMessageTypes.SetFlag => IpcMessageTypes.Ok,
            IpcMessageTypes.SetFlagBatch => IpcMessageTypes.Ok,
            IpcMessageTypes.SyncAccount => IpcMessageTypes.Ok,
            IpcMessageTypes.RemoveAccount => IpcMessageTypes.Ok,
            IpcMessageTypes.UpdateAccount => IpcMessageTypes.Account,
            IpcMessageTypes.StartOAuth => IpcMessageTypes.Ok,
            IpcMessageTypes.CancelOAuth => IpcMessageTypes.Ok,
            IpcMessageTypes.UpdateSettings => IpcMessageTypes.Ok,
            IpcMessageTypes.GetStorageSummary => IpcMessageTypes.StorageSummary,
            IpcMessageTypes.GetAccountHealth => IpcMessageTypes.AccountHealth,
            IpcMessageTypes.GetEmlPreview => IpcMessageTypes.EmlPreview,
            IpcMessageTypes.CleanStorage => IpcMessageTypes.StorageCleanup,
            _ => IpcMessageTypes.Ok
        };

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sendQueue.Writer.TryComplete();
        _pipe.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
