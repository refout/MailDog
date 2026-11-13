using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MailDog.Mail.Auth;

internal sealed class LoopbackOAuthServer : IAsyncDisposable
{
    private const string SuccessHtml = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
          <title>MailDog 登录完成</title>
          <style>
            body { font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif; margin: 48px; color: #1b1b1b; }
            h1 { font-size: 22px; font-weight: 600; }
            p { color: #616161; }
          </style>
        </head>
        <body>
          <h1>登录完成</h1>
          <p>请关闭此页面并返回 MailDog。</p>
        </body>
        </html>
        """;

    private const string ErrorHtml = """
        <!doctype html>
        <html lang="zh-CN">
        <head><meta charset="utf-8"><title>MailDog 登录失败</title></head>
        <body style="font-family: sans-serif; margin: 48px;">
          <h1>登录未完成</h1>
          <p>请返回 MailDog 重试。</p>
        </body>
        </html>
        """;

    private readonly TcpListener _listener;
    private readonly string _expectedState;
    private readonly TaskCompletionSource<OAuthCallbackResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    public LoopbackOAuthServer(string expectedState)
    {
        _expectedState = expectedState;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public Uri RedirectUri => new($"http://127.0.0.1:{Port}/callback");

    public async Task<OAuthCallbackResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(timeout);

        var acceptTask = AcceptLoopAsync(linked.Token);

        try
        {
            return await _result.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("浏览器登录超时，请重试。");
        }
        finally
        {
            _cts.Cancel();
            _listener.Stop();

            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch
            {
                // 关闭监听时的异常忽略。
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestLine is null)
                {
                    return;
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 400, ErrorHtml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var target = parts[1];
                var uri = new Uri("http://127.0.0.1" + target);
                if (!uri.AbsolutePath.Equals("/callback", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, ErrorHtml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var parameters = ParseQuery(uri.Query);

                if (parameters.TryGetValue("error", out var error))
                {
                    await WriteResponseAsync(stream, 400, ErrorHtml, cancellationToken).ConfigureAwait(false);
                    _result.TrySetResult(new OAuthCallbackResult(null, null, error, parameters.GetValueOrDefault("error_description")));
                    return;
                }

                if (!parameters.TryGetValue("state", out var state)
                    || !FixedTimeEquals(state, _expectedState))
                {
                    await WriteResponseAsync(stream, 400, ErrorHtml, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteResponseAsync(stream, 400, ErrorHtml, cancellationToken).ConfigureAwait(false);
                    _result.TrySetResult(new OAuthCallbackResult(null, state, "invalid_callback", "回调中没有授权码。"));
                    return;
                }

                await WriteResponseAsync(stream, 200, SuccessHtml, cancellationToken).ConfigureAwait(false);
                _result.TrySetResult(new OAuthCallbackResult(code, state, null, null));
            }
            catch
            {
                // 单个连接失败不应终止整个登录流程。
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read <= 0)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        return lines.Length == 0 ? null : lines[0];
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = statusCode == 200 ? "OK" : "Bad Request";
        var header = $"HTTP/1.1 {statusCode} {reason}\r\n"
                     + "Content-Type: text/html; charset=utf-8\r\n"
                     + "Cache-Control: no-store\r\n"
                     + "Pragma: no-cache\r\n"
                     + "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n"
                     + $"Content-Length: {bodyBytes.Length}\r\n"
                     + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);

            // OAuth 2.0 不允许重复参数；重复时直接拒绝。
            if (!result.TryAdd(key, value))
            {
                result.Remove(key);
            }
        }

        return result;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record OAuthCallbackResult(string? Code, string? State, string? Error, string? ErrorDescription);
