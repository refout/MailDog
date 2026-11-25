using System.Collections.Concurrent;
using System.IO.Pipes;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;

namespace MailDog.Contracts.Ipc;

/// <summary>Engine 回了一个错误包；不要和界面层的 IpcException 混用。</summary>
public sealed class IpcCallException : Exception
{
    public IpcCallException(string code, string message, string? detail = null)
        : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string? Detail { get; }
}

/// <summary>
/// 面向命令行与脚本的最小 IPC 客户端：只管连上、发请求、收回应，不处理事件与重连。
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readTask;
    private long _requestId;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>连上管道并完成 Hello 握手；连不上返回 null，由调用方决定是否先拉起 Engine。</summary>
    public static async Task<IpcClient?> TryConnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? pipeName = null)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName ?? IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync((int)Math.Max(250, timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var client = new IpcClient { _pipe = pipe };
        client._readTask = client.ReadLoopAsync(client._cts.Token);

        return client;
    }

    public async Task<HelloAck> HelloAsync(string clientVersion, CancellationToken cancellationToken)
    {
        var ack = await RequestAsync<HelloAck>(
            IpcMessageTypes.Hello,
            new HelloRequest
            {
                ProtocolVersion = IpcProtocol.Version,
                ClientVersion = clientVersion
            },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        if (ack.ProtocolVersion != IpcProtocol.Version)
        {
            throw new IpcCallException(
                IpcErrorCodes.ProtocolMismatch,
                $"Engine 协议版本 {ack.ProtocolVersion} 与命令行不兼容（期望 {IpcProtocol.Version}）。");
        }

        return ack;
    }

    public async Task<TResponse> RequestAsync<TResponse>(
        string type,
        IIpcMessage request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TResponse : IIpcMessage
    {
        var pipe = _pipe ?? throw new InvalidOperationException("尚未连接 Engine。");
        var requestId = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        try
        {
            var envelope = IpcCodecRegistry.Create(type, request, requestId);
            var payload = IpcJson.Serialize(envelope);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFrame.WriteAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(IpcProtocol.DefaultRequestTimeoutSeconds));

            var response = await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            var message = IpcCodecRegistry.Read(response);

            if (message is ErrorResponse error)
            {
                throw new IpcCallException(error.Code, error.Message, error.Detail);
            }

            return (TResponse)message;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe;

        try
        {
            while (pipe is not null && !cancellationToken.IsCancellationRequested)
            {
                var payload = await IpcFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);

                if (payload is null)
                {
                    break;
                }

                var envelope = IpcJson.Deserialize(payload);

                if (envelope.RequestId != 0 && _pending.TryRemove(envelope.RequestId, out var completion))
                {
                    completion.TrySetResult(envelope);
                }
            }
        }
        catch
        {
            // 断开后由待处理请求统一抛错。
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IpcCallException(IpcErrorCodes.UpstreamUnavailable, "与 Engine 的连接已断开。"));
            }

            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch
            {
                // 关闭时忽略。
            }
        }

        _writeGate.Dispose();
        _cts.Dispose();
    }
}
