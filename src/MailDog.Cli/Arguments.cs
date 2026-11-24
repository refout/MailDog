namespace MailDog.Cli;

/// <summary>命令行参数错误；对应用户看到的 exit 5。</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>手写参数解析：只认白名单里的开关，写错的开关直接报用法错误，不静默吞掉。</summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "--json",
        "--unread",
        "--all",
        "--body",
        "--headers",
        "--no-start",
        "--confirm",
        "--help",
        "-h"
    };

    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--account",
        "--folder",
        "--limit",
        "--max-chars",
        "--out",
        "--engine",
        "--to",
        "--cc",
        "--bcc",
        "--subject",
        "--text",
        "--body-file",
        "--html-file",
        "--attach"
    };

    private readonly HashSet<string> _booleans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    private Arguments(string? command)
    {
        Command = command;
    }

    /// <summary>子命令；为空表示只给了开关，按用法说明处理。</summary>
    public string? Command { get; }

    public IReadOnlyList<string> Positionals => _positionals;

    public static Arguments Parse(string[] raw)
    {
        Arguments? parsed = null;

        for (var index = 0; index < raw.Length; index++)
        {
            var token = raw[index];

            if (token != "-h" && !token.StartsWith("--", StringComparison.Ordinal))
            {
                if (parsed is null)
                {
                    parsed = new Arguments(token);
                }
                else
                {
                    parsed._positionals.Add(token);
                }

                continue;
            }

            if (parsed is null)
            {
                throw new UsageException("先写子命令，再跟开关，例如 maildog list --unread");
            }

            if (BooleanFlags.Contains(token))
            {
                parsed._booleans.Add(token);
                continue;
            }

            if (ValueFlags.Contains(token))
            {
                if (index + 1 >= raw.Length || raw[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"开关 {token} 后面需要一个值。");
                }

                var value = raw[++index];

                if (!parsed._values.TryGetValue(token, out var list))
                {
                    list = [];
                    parsed._values[token] = list;
                }

                list.Add(value);
                continue;
            }

            throw new UsageException($"不认识的开关：{token}（用 maildog schema 看全部开关）");
        }

        return parsed ?? new Arguments(null);
    }

    public bool Has(string flag) => _booleans.Contains(flag) || _values.ContainsKey(flag);

    /// <summary>同名开关写多次时取最后一次，符合"后写的覆盖前写的"直觉。</summary>
    public string? Value(string flag)
        => _values.TryGetValue(flag, out var list) && list.Count > 0 ? list[^1] : null;

    /// <summary>可重复的开关，例如多个 --attach / --to。</summary>
    public IReadOnlyList<string> Values(string flag)
        => _values.TryGetValue(flag, out var list) ? list : [];

    public int IntValue(string flag, int fallback)
    {
        var value = Value(flag);

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new UsageException($"开关 {flag} 需要整数，收到：{value}");
        }

        return parsed;
    }

    public bool Json => Has("--json");
}
