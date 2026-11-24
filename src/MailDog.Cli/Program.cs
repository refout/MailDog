using System.Text;
using MailDog.Contracts.Ipc;

namespace MailDog.Cli;

/// <summary>所有命令都靠退出码说话，脚本能直接判成败。</summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitNotFound = 2;
    private const int ExitUnavailable = 3;
    private const int ExitRefused = 4;
    private const int ExitUsage = 5;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            var arguments = Arguments.Parse(args);

            if (arguments.Command is null
                || arguments.Command is "help"
                || arguments.Has("--help")
                || arguments.Has("-h"))
            {
                Console.WriteLine(Usage.Text);
                return ExitOk;
            }

            if (arguments.Command is "schema")
            {
                Console.WriteLine(Schema.Json());
                return ExitOk;
            }

            if (arguments.Command is not ("accounts" or "folders" or "list" or "search" or "show" or "attachment" or "draft" or "send" or "mark" or "move" or "cancel"))
            {
                throw new UsageException($"不认识的命令：{arguments.Command}（用 maildog schema 看全部命令）");
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var client = await EngineGateway.ConnectAsync(
                arguments.Value("--engine"),
                !arguments.Has("--no-start"),
                cancellation.Token).ConfigureAwait(false);

            try
            {
                return arguments.Command switch
                {
                    "accounts" => await Commands.AccountsAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "folders" => await Commands.FoldersAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "list" => await Commands.ListAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "search" => await Commands.SearchAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "show" => await Commands.ShowAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "attachment" => await Commands.AttachmentAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "draft" => await Commands.DraftAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "send" => await Commands.SendAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "mark" => await Commands.MarkAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "move" => await Commands.MoveAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    "cancel" => await Commands.CancelAsync(client, arguments, cancellation.Token).ConfigureAwait(false),
                    _ => ExitUsage
                };
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine($"用法错误：{exception.Message}");
            Console.Error.WriteLine("用 maildog schema 看全部命令与开关。");
            return ExitUsage;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return exception.ExitCode;
        }
        catch (IpcCallException exception)
        {
            Console.Error.WriteLine($"Engine 返回错误：{exception.Message}");

            if (!string.IsNullOrWhiteSpace(exception.Detail))
            {
                Console.Error.WriteLine($"详情：{exception.Detail}");
            }

            return exception.Code switch
            {
                IpcErrorCodes.NotFound => ExitNotFound,
                IpcErrorCodes.UpstreamUnavailable => ExitUnavailable,
                IpcErrorCodes.AuthRequired => ExitUnavailable,
                IpcErrorCodes.Timeout => ExitUnavailable,
                IpcErrorCodes.InvalidRequest => ExitUsage,
                IpcErrorCodes.ProtocolMismatch => ExitRefused,
                _ => ExitFailure
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"失败：{exception.Message}");
            return ExitFailure;
        }
    }

    public static string Version
        => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>命令自己抛的业务错误，带退出码。</summary>
internal sealed class CliException : Exception
{
    public CliException(int exitCode, string message) : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
