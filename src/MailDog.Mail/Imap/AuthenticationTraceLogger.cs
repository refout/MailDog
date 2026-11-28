using System.Text;
using MailKit;

namespace MailDog.Mail.Imap;

/// <summary>
/// 只保留 IMAP 服务端的失败响应，用于把 MailKit 的通用
/// “Authentication failed.” 还原成服务器实际返回的原因。
/// 不记录客户端命令和认证载荷，避免把 OAuth 令牌写进日志或界面。
/// </summary>
internal sealed class AuthenticationTraceLogger : IProtocolLogger
{
    private const int MaxBufferLength = 4096;
    private const int MaxFailureLength = 300;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private string? _lastFailure;

    public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

    public string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    public void LogConnect(Uri uri)
    {
        lock (_gate)
        {
            _buffer.Clear();
            _lastFailure = null;
        }
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
        // 认证命令与令牌一律不保留。
    }

    public void LogServer(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _buffer.Append(Encoding.UTF8.GetString(buffer, offset, count));

            while (true)
            {
                var newline = _buffer.ToString().IndexOf('\n');
                if (newline < 0)
                {
                    if (_buffer.Length > MaxBufferLength)
                    {
                        _buffer.Remove(0, _buffer.Length - MaxBufferLength);
                    }

                    return;
                }

                var line = _buffer.ToString(0, newline).TrimEnd('\r').Trim();
                _buffer.Remove(0, newline + 1);

                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && (parts[1].Equals("NO", StringComparison.OrdinalIgnoreCase)
                        || parts[1].Equals("BAD", StringComparison.OrdinalIgnoreCase)))
                {
                    _lastFailure = line.Length > MaxFailureLength
                        ? line[..MaxFailureLength]
                        : line;
                }
            }
        }
    }

    public void Dispose()
    {
    }
}
