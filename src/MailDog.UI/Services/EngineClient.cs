using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public event Action? EngineStarting;

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
                throw new InvalidOperationException(
                    "无法连接到 MailDog 后台。请确认 MailDog.Engine.exe 与 MailDog.UI.exe 位于同一目录，或先启动 MailDog.Engine.exe。");
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
        for (var attempt = 0; attempt < 40; attempt++)
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

    private void StartEngine()
    {
        var path = ResolveEnginePath();

        if (path is null)
        {
            return;
        }

        EngineStarting?.Invoke();

        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            };

            var uiPath = Environment.ProcessPath;
            if (!string.Equals(Path.GetFileName(uiPath), "MailDog.UI.exe", StringComparison.OrdinalIgnoreCase))
            {
                uiPath = Path.Combine(AppContext.BaseDirectory, "MailDog.UI.exe");
            }

            if (File.Exists(uiPath))
            {
                startInfo.Environment["MAILDOG_UI_PATH"] = uiPath;
            }

            Process.Start(startInfo);
        }
        catch
        {
            // 由后续连接超时给出用户可见错误。
        }
    }

    private static string? ResolveEnginePath()
    {
        var path = Environment.GetEnvironmentVariable("MAILDOG_ENGINE_PATH");

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        path = Path.Combine(AppContext.BaseDirectory, "MailDog.Engine.exe");

        if (File.Exists(path))
        {
            return path;
        }

        // 开发时 UI 与 Engine 分属不同输出目录，向上查找同仓库的 Engine 构建产物。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var configurations = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var binRoot = Path.Combine(directory.FullName, "MailDog.Engine", "bin");

            if (!Directory.Exists(binRoot))
            {
                continue;
            }

            foreach (var configuration in configurations)
            {
                if (configuration == "Release")
                {
                    // 优先使用最近一次 AOT 发布产物，避免误用陈旧的 Release apphost。
                    var artifact = Path.Combine(
                        directory.FullName,
                        "artifacts",
                        runtime,
                        "engine",
                        "MailDog.Engine.exe");

                    if (File.Exists(artifact))
                    {
                        return artifact;
                    }
                }

                // Engine 的目标框架带 Windows 版本（它要用 WinRT 发通知），
                // 目录名不是固定的 net11.0，所以按前缀找一遍。
                string[] targetFrameworks;

                try
                {
                    targetFrameworks = Directory.GetDirectories(
                        Path.Combine(binRoot, configuration),
                        "net11.0*");
                }
                catch (DirectoryNotFoundException)
                {
                    targetFrameworks = [];
                }

                foreach (var targetFramework in targetFrameworks)
                {
                    // 优先使用 RID 发布产物，避免误用旧的非 RID apphost。
                    var published = Path.Combine(targetFramework, runtime, "MailDog.Engine.exe");

                    if (File.Exists(published))
                    {
                        return published;
                    }

                    var candidate = Path.Combine(targetFramework, "MailDog.Engine.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
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
