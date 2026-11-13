using System.IO.Pipes;
using System.Diagnostics;
using System.Collections.Concurrent;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;

namespace MailDog.UI.Services;

public sealed class IpcException : Exception
{
    public IpcException(string code, string message, string? detail = null)
        : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string? Detail { get; }
}

public sealed class EngineClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readTask;
    private long _requestId;

    public bool IsConnected => _pipe?.IsConnected == true;

    public event Action<IIpcMessage>? EventReceived;

    public event Action<string>? Disconnected;

    public async Task<HelloAck> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            var pipe = await TryConnectAsync(cancellationToken).ConfigureAwait(false);

            if (pipe is null)
            {
                StartEngine();
                pipe = await WaitForEngineAsync(cancellationToken).ConfigureAwait(false);
            }

            if (pipe is null)
            {
                throw new InvalidOperationException("无法连接到 MailDog Engine。");
            }

            _pipe = pipe;
            _readTask = ReadLoopAsync(_cts.Token);
        }

        var ack = await RequestAsync<HelloAck>(
            IpcMessageTypes.Hello,
            new HelloRequest
            {
                ProtocolVersion = IpcProtocol.Version,
                ClientVersion = typeof(EngineClient).Assembly.GetName().Version?.ToString() ?? "0.1.0"
            },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        if (ack.ProtocolVersion != IpcProtocol.Version)
        {
            throw new InvalidOperationException($"Engine 协议版本不兼容：{ack.ProtocolVersion}");
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
                throw new IpcException(error.Code, error.Message, error.Detail);
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
                    continue;
                }

                try
                {
                    EventReceived?.Invoke(IpcCodecRegistry.Read(envelope));
                }
                catch
                {
                    // 未知事件类型忽略。
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Disconnected?.Invoke(exception.Message);
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new InvalidOperationException("与 Engine 的连接已断开。"));
            }

            _pending.Clear();
        }
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<NamedPipeClientStream?> WaitForEngineAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var pipe = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
            if (pipe is not null)
            {
                return pipe;
            }
        }

        return null;
    }

    private static void StartEngine()
    {
        var path = Environment.GetEnvironmentVariable("MAILDOG_ENGINE_PATH");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "MailDog.Engine.exe");
        }

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // 由后续连接超时给出用户可见错误。
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
