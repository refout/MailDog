using System.IO.Pipes;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Serialization;

namespace MailDog.Engine.Ipc;

public sealed class IpcServer : IAsyncDisposable
{
    private readonly RequestRouter _router;
    private readonly Action<string>? _diagnostic;
    private readonly List<UiConnection> _connections = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public IpcServer(RequestRouter router, Action<string>? diagnostic = null)
    {
        _router = router;
        _diagnostic = diagnostic;
    }

    public bool HasConnections
    {
        get
        {
            lock (_gate)
            {
                return _connections.Any(connection => connection.IsHandshaken);
            }
        }
    }

    public void Start()
    {
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public void Broadcast<T>(string type, T message) where T : IIpcMessage
    {
        UiConnection[] snapshot;
        lock (_gate)
        {
            snapshot = _connections.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var envelope = IpcCodecRegistry.Create(type, message);
        foreach (var connection in snapshot)
        {
            if (connection.IsHandshaken)
            {
                connection.Enqueue(envelope);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;

            try
            {
                pipe = new NamedPipeServerStream(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var connection = new UiConnection(pipe, _router, _diagnostic);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            _ = RunConnectionAsync(connection, cancellationToken);
        }
    }

    private async Task RunConnectionAsync(UiConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch
            {
                // 关闭时忽略。
            }
        }

        UiConnection[] snapshot;
        lock (_gate)
        {
            snapshot = _connections.ToArray();
            _connections.Clear();
        }

        foreach (var connection in snapshot)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}
