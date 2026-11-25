using System.Text;

namespace MailDog.Cli;

/// <summary>
/// 把标准输出原样透传给控制台，同时截留一份给 --json 调用记录。
/// 截留长度有上限，避免 list/show 这类大输出把内存和日志撑爆。
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly StringBuilder _captured;
    private readonly int _limit;

    public TeeTextWriter(TextWriter inner, int limit)
    {
        _inner = inner;
        _limit = Math.Max(0, limit);
        _captured = new StringBuilder(Math.Min(_limit, 16 * 1024));
    }

    public override Encoding Encoding => _inner.Encoding;

    public bool Truncated { get; private set; }

    public string Captured => _captured.ToString();

    public override void Write(char value)
    {
        _inner.Write(value);
        Capture(value.ToString());
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        Capture(value);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        _inner.Write(buffer);
        Capture(buffer);
    }

    public override void WriteLine()
    {
        _inner.WriteLine();
        Capture(Environment.NewLine);
    }

    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        Capture(value);
        Capture(Environment.NewLine);
    }

    public override void Flush() => _inner.Flush();

    private void Capture(string? value)
    {
        if (value is null || _limit == 0)
        {
            return;
        }

        Capture(value.AsSpan());
    }

    private void Capture(ReadOnlySpan<char> value)
    {
        if (_limit == 0 || Truncated || value.IsEmpty)
        {
            return;
        }

        var remaining = _limit - _captured.Length;

        if (value.Length <= remaining)
        {
            _captured.Append(value);
            return;
        }

        if (remaining > 0)
        {
            _captured.Append(value[..remaining]);
        }

        Truncated = true;
    }
}
