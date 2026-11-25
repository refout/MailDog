using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MailDog.Mail.Tests;

/// <summary>本机 TLS 假邮件服务器：只实现测试所需的 SMTP 发送与 IMAP 首屏同步子集。</summary>
internal static class FakeMailCertificate
{
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}

internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = FakeMailCertificate.Create();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly TaskCompletionSource<string> _received = new();

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task<string> Received => _received.Task;

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, false);
            using var reader = new StreamReader(ssl, Encoding.ASCII);
            using var writer = new StreamWriter(ssl, Encoding.ASCII) { AutoFlush = true };

            await writer.WriteAsync("220 localhost MailDog fake SMTP\r\n");
            string? data = null;

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    return;
                }

                var command = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var verb = command.Length > 0 ? command[0].ToUpperInvariant() : string.Empty;

                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteAsync(
                            "250-localhost\r\n250-AUTH PLAIN LOGIN\r\n250-PIPELINING\r\n250-SIZE 10485760\r\n250-8BITMIME\r\n250 OK\r\n");
                        break;
                    case "HELO":
                        await writer.WriteAsync("250 localhost\r\n");
                        break;
                    case "AUTH":
                        if (command.Length > 1 && command[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            if (command.Length == 2)
                            {
                                await writer.WriteAsync("334 \r\n");
                                _ = await reader.ReadLineAsync(_cts.Token);
                            }

                            await writer.WriteAsync("235 2.7.0 Authentication successful\r\n");
                        }
                        else if (command.Length > 1 && command[1].Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteAsync("334 VXNlcm5hbWU6\r\n");
                            _ = await reader.ReadLineAsync(_cts.Token);
                            await writer.WriteAsync("334 UGFzc3dvcmQ6\r\n");
                            _ = await reader.ReadLineAsync(_cts.Token);
                            await writer.WriteAsync("235 2.7.0 Authentication successful\r\n");
                        }
                        else
                        {
                            await writer.WriteAsync("504 Unsupported authentication\r\n");
                        }

                        break;
                    case "MAIL":
                    case "RCPT":
                    case "RSET":
                    case "NOOP":
                        await writer.WriteAsync("250 OK\r\n");
                        break;
                    case "DATA":
                        await writer.WriteAsync("354 End data with <CR><LF>.<CR><LF>\r\n");
                        data = await ReadDataAsync(reader, _cts.Token);
                        await writer.WriteAsync("250 2.0.0 Queued\r\n");
                        break;
                    case "QUIT":
                        await writer.WriteAsync("221 2.0.0 Bye\r\n");
                        if (data is not null)
                        {
                            _received.TrySetResult(data);
                        }

                        return;
                    default:
                        await writer.WriteAsync("250 OK\r\n");
                        break;
                }

                if (data is not null && _received.Task.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            _received.TrySetException(exception);
        }
    }

    private static async Task<string> ReadDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line == ".")
            {
                return builder.ToString();
            }

            builder.Append(line).Append("\r\n");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 测试结束时连接可能已断开。
        }

        _listener.Stop();
        _certificate.Dispose();
        _cts.Dispose();
    }
}

internal sealed class FakeImapServer : IAsyncDisposable
{
    private const string MessageOne =
        "From: Alice <alice@example.com>\r\nTo: Bob <bob@example.com>\r\nSubject: Fake one\r\n"
        + "Message-ID: <one@example.com>\r\nDate: Wed, 25 Nov 2026 12:00:00 +0000\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n\r\nhello from fake imap\r\n";
    private const string MessageTwo =
        "From: Carol <carol@example.com>\r\nTo: Bob <bob@example.com>\r\nSubject: Fake two\r\n"
        + "Message-ID: <two@example.com>\r\nDate: Wed, 25 Nov 2026 13:00:00 +0000\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n\r\nsecond fake message\r\n";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = FakeMailCertificate.Create();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly TaskCompletionSource<bool> _synced = new();

    public FakeImapServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task Synced => _synced.Task;

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, false);
            using var reader = new StreamReader(ssl, new UTF8Encoding(false));
            using var writer = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteAsync("* OK MailDog fake IMAP ready\r\n");

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    return;
                }

                var space = line.IndexOf(' ');
                if (space < 0)
                {
                    continue;
                }

                var tag = line[..space];
                var command = line[(space + 1)..];
                var verbEnd = command.IndexOf(' ');
                var verb = verbEnd < 0 ? command : command[..verbEnd];
                verb = verb.ToUpperInvariant();
                var rest = verbEnd < 0 ? string.Empty : command[(verbEnd + 1)..];

                switch (verb)
                {
                    case "CAPABILITY":
                        await writer.WriteAsync(
                            $"* CAPABILITY IMAP4rev1 AUTH=PLAIN NAMESPACE SPECIAL-USE\r\n{tag} OK CAPABILITY completed\r\n");
                        break;
                    case "NOOP":
                        await writer.WriteAsync($"{tag} OK NOOP completed\r\n");
                        break;
                    case "ID":
                        await writer.WriteAsync($"* ID (\"name\" \"MailDogFake\")\r\n{tag} OK ID completed\r\n");
                        break;
                    case "NAMESPACE":
                        await writer.WriteAsync($"* NAMESPACE ((\"\" \"/\")) NIL NIL\r\n{tag} OK NAMESPACE completed\r\n");
                        break;
                    case "LOGIN":
                        await writer.WriteAsync($"{tag} OK LOGIN completed\r\n");
                        break;
                    case "AUTHENTICATE":
                        if (rest.StartsWith("PLAIN", StringComparison.OrdinalIgnoreCase) && !rest.Contains(' '))
                        {
                            await writer.WriteAsync("+ \r\n");
                            _ = await reader.ReadLineAsync(_cts.Token);
                        }

                        await writer.WriteAsync($"{tag} OK AUTHENTICATE completed\r\n");
                        break;
                    case "LIST":
                        if (rest.EndsWith("\"*\"", StringComparison.Ordinal)
                            || rest.EndsWith("\"%\"", StringComparison.Ordinal)
                            || rest.EndsWith("\"INBOX\"", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteAsync(
                                $"* LIST (\\HasNoChildren \\Inbox) \"/\" \"INBOX\"\r\n{tag} OK LIST completed\r\n");
                        }
                        else
                        {
                            await writer.WriteAsync($"{tag} OK LIST completed\r\n");
                        }

                        break;
                    case "STATUS":
                        await writer.WriteAsync(
                            $"* STATUS INBOX (MESSAGES 2 UNSEEN 1 UIDVALIDITY 1)\r\n{tag} OK STATUS completed\r\n");
                        break;
                    case "SELECT":
                    case "EXAMINE":
                        await writer.WriteAsync(
                            "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)\r\n"
                            + "* 2 EXISTS\r\n"
                            + "* 0 RECENT\r\n"
                            + "* OK [UIDVALIDITY 1] UIDs valid\r\n"
                            + "* OK [UIDNEXT 3] Predicted next UID\r\n"
                            + "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)] Limited\r\n"
                            + $"{tag} OK [READ-WRITE] SELECT completed\r\n");
                        break;
                    case "SEARCH":
                        await writer.WriteAsync($"* SEARCH 1 2\r\n{tag} OK SEARCH completed\r\n");
                        break;
                    case "FETCH":
                        await WriteFetchAsync(writer, rest);
                        await writer.WriteAsync($"{tag} OK FETCH completed\r\n");
                        break;
                    case "UID":
                        if (rest.StartsWith("SEARCH", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteAsync($"* SEARCH 1 2\r\n{tag} OK SEARCH completed\r\n");
                        }
                        else if (rest.StartsWith("FETCH", StringComparison.OrdinalIgnoreCase))
                        {
                            await WriteFetchAsync(writer, rest["FETCH".Length..].Trim());
                            await writer.WriteAsync($"{tag} OK FETCH completed\r\n");
                        }
                        else
                        {
                            await writer.WriteAsync($"{tag} OK UID completed\r\n");
                        }

                        break;
                    case "CLOSE":
                        await writer.WriteAsync($"{tag} OK CLOSE completed\r\n");
                        break;
                    case "LOGOUT":
                        await writer.WriteAsync($"* BYE bye\r\n{tag} OK LOGOUT completed\r\n");
                        return;
                    default:
                        await writer.WriteAsync($"{tag} OK {verb} completed\r\n");
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            _synced.TrySetException(exception);
        }
    }

    private async Task WriteFetchAsync(StreamWriter writer, string arguments)
    {
        var response = new StringBuilder();
        foreach (var (uid, raw) in new[] { (1, MessageOne), (2, MessageTwo) })
        {
            var envelope = uid == 1
                ? "(\"Wed, 25 Nov 2026 12:00:00 +0000\" \"Fake one\" "
                  + "((\"Alice\" NIL \"alice\" \"example.com\")) "
                  + "((\"Alice\" NIL \"alice\" \"example.com\")) "
                  + "((\"Alice\" NIL \"alice\" \"example.com\")) "
                  + "((\"Bob\" NIL \"bob\" \"example.com\")) NIL NIL NIL \"<one@example.com>\")"
                : "(\"Wed, 25 Nov 2026 13:00:00 +0000\" \"Fake two\" "
                  + "((\"Carol\" NIL \"carol\" \"example.com\")) "
                  + "((\"Carol\" NIL \"carol\" \"example.com\")) "
                  + "((\"Carol\" NIL \"carol\" \"example.com\")) "
                  + "((\"Bob\" NIL \"bob\" \"example.com\")) NIL NIL NIL \"<two@example.com>\")";

            response.Append("* ").Append(uid)
                .Append(" FETCH (UID ").Append(uid)
                .Append(" RFC822.SIZE ").Append(raw.Length)
                .Append(" FLAGS (\\Seen) INTERNALDATE \"25-Nov-2026 12:00:00 +0000\"")
                .Append(" BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" ")
                .Append(raw.Length).Append(" 1 NIL NIL NIL NIL)")
                .Append(" ENVELOPE ").Append(envelope);
            if (arguments.Contains("BODY.PEEK[]", StringComparison.OrdinalIgnoreCase))
            {
                response.Append(" BODY[] {").Append(raw.Length).Append("}\r\n").Append(raw);
            }

            response.Append(")\r\n");
        }

        await writer.WriteAsync(response.ToString());
        _synced.TrySetResult(true);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 测试结束时连接可能已断开。
        }

        _listener.Stop();
        _certificate.Dispose();
        _cts.Dispose();
    }
}
