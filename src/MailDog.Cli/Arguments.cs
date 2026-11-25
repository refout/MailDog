using System.CommandLine;
using System.CommandLine.Parsing;

namespace MailDog.Cli;

/// <summary>命令行参数错误；对应用户看到的 exit 5。</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// System.CommandLine 承载词法与开关识别，这里只把它收敛成 MailDog 原来的
/// <see cref="Arguments"/> 外观：命令名、开关、位置参数和中文用法错误都保持不变。
/// </summary>
internal sealed class Arguments
{
    private static readonly string[] CommandNames =
    [
        "accounts", "folders", "list", "search", "show", "attachment",
        "draft", "send", "mark", "move", "cancel", "snooze", "pending", "approve", "deny",
        "quit", "open", "schema", "help"
    ];

    private static readonly string[] BooleanNames =
    [
        "--json", "--unread", "--all", "--body", "--headers", "--html", "--no-start", "--confirm",
        "--force-send", "--read-only", "--queue"
    ];

    private static readonly string[] SingleValueNames =
    [
        "--account", "--folder", "--limit", "--max-chars", "--out", "--engine",
        "--subject", "--text", "--body-file", "--html-file", "--send-at", "--until"
    ];

    private static readonly string[] RepeatValueNames =
    [
        "--to", "--cc", "--bcc", "--attach"
    ];

    private static readonly RootCommand Root;
    private static readonly Dictionary<string, Option<bool>> BooleanOptions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Option<string[]>> SingleOptions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Option<string[]>> RepeatOptions = new(StringComparer.Ordinal);

    private readonly HashSet<string> _booleans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    static Arguments()
    {
        Root = new RootCommand("MailDog 命令行");

        foreach (var name in BooleanNames)
        {
            var option = new Option<bool>(name) { Recursive = true };
            BooleanOptions[name] = option;
            Root.Options.Add(option);
        }

        foreach (var name in SingleValueNames)
        {
            // 单值开关在 MailDog 里允许重复写、后写的覆盖前写的；用数组形态接收，
            // 避免 System.CommandLine 对重复出现的单值直接报错。
            var option = new Option<string[]>(name)
            {
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false,
                Recursive = true
            };
            SingleOptions[name] = option;
            Root.Options.Add(option);
        }

        foreach (var name in RepeatValueNames)
        {
            var option = new Option<string[]>(name)
            {
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false,
                Recursive = true
            };
            RepeatOptions[name] = option;
            Root.Options.Add(option);
        }

        foreach (var name in CommandNames)
        {
            var command = new Command(name, name);
            var positionals = new Argument<string[]>("参数")
            {
                Arity = ArgumentArity.ZeroOrMore
            };
            command.Arguments.Add(positionals);
            Root.Subcommands.Add(command);
        }
    }

    private Arguments(string? command)
    {
        Command = command;
    }

    /// <summary>子命令；为空表示只给了开关或帮助，按用法说明处理。</summary>
    public string? Command { get; }

    public IReadOnlyList<string> Positionals => _positionals;

    public static Arguments Parse(string[] raw)
    {
        var (values, positionals, command, help) = Scan(raw);

        if (command is not null && !CommandNames.Contains(command, StringComparer.Ordinal))
        {
            throw new UsageException($"不认识的命令：{command}（用 maildog schema 看全部命令）");
        }

        var parsed = new Arguments(command);
        parsed._positionals.AddRange(positionals);

        foreach (var (flag, value) in values)
        {
            parsed.AddValue(flag, value);
        }

        if (help)
        {
            parsed._booleans.Add("--help");
        }

        var result = Root.Parse(raw);

        if (result.Errors.Count > 0)
        {
            throw new UsageException(Describe(result.Errors[0]));
        }

        foreach (var (name, option) in BooleanOptions)
        {
            if (result.GetResult(option) is not null && result.GetValue(option))
            {
                parsed._booleans.Add(name);
            }
        }

        return parsed;
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

    public bool ReadOnly => Has("--read-only");

    public bool Queue => Has("--queue");

    private void AddValue(string flag, string value)
    {
        if (!_values.TryGetValue(flag, out var list))
        {
            list = [];
            _values[flag] = list;
        }

        list.Add(value);
    }

    /// <summary>
    /// 先按原版规则扫一遍：只认白名单开关，值不能以 -- 开头，`--` 之后全部当位置参数。
    /// System.CommandLine 负责其余词法，两边冲突时以这里的中文错误为准。
    /// </summary>
    private static (List<(string Flag, string Value)> Values, List<string> Positionals, string? Command, bool Help) Scan(string[] raw)
    {
        var values = new List<(string, string)>();
        var positionals = new List<string>();
        string? command = null;
        var afterTerminator = false;
        var help = false;

        for (var index = 0; index < raw.Length; index++)
        {
            var token = raw[index];

            if (afterTerminator)
            {
                positionals.Add(token);
                continue;
            }

            if (token == "--")
            {
                afterTerminator = true;
                continue;
            }

            if (token is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = token.IndexOf('=');
                var flag = separator < 0 ? token : token[..separator];
                var inline = separator < 0 ? null : token[(separator + 1)..];

                if (SingleValueNames.Contains(flag, StringComparer.Ordinal)
                    || RepeatValueNames.Contains(flag, StringComparer.Ordinal))
                {
                    if (inline is not null)
                    {
                        values.Add((flag, inline));
                        continue;
                    }

                    if (index + 1 >= raw.Length || raw[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException($"开关 {flag} 后面需要一个值。");
                    }

                    values.Add((flag, raw[++index]));
                    continue;
                }

                if (BooleanNames.Contains(flag, StringComparer.Ordinal))
                {
                    if (inline is not null)
                    {
                        throw new UsageException($"开关 {flag} 不接受值。");
                    }

                    continue;
                }

                throw new UsageException($"不认识的开关：{token}（用 maildog schema 看全部开关）");
            }

            if (command is null)
            {
                command = token;
            }
            else
            {
                positionals.Add(token);
            }
        }

        return (values, positionals, command, help);
    }

    private static string Describe(ParseError error)
    {
        var message = error.Message;

        if (message.StartsWith("Unrecognized command or argument", StringComparison.Ordinal))
        {
            return "不认识的命令或参数，用 maildog schema 看全部命令";
        }

        if (message.StartsWith("Unrecognized option", StringComparison.Ordinal))
        {
            return "不认识的开关，用 maildog schema 看全部开关";
        }

        if (message.StartsWith("Required argument missing for option", StringComparison.Ordinal))
        {
            return "开关后面需要一个值";
        }

        return message;
    }
}
