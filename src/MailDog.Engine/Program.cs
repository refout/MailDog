using System.Runtime.InteropServices;
using System.Text;
using MailDog.Engine.Hosting;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailDog.Engine;

internal static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static int Main(string[] args)
    {
        var probe = args.Contains("--probe", StringComparer.OrdinalIgnoreCase);
        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

        // 进程是 WinExe：从终端带 --probe / --console 启动时借用父窗口的控制台输出。
        if ((probe || console) && OperatingSystem.IsWindows())
        {
            AttachConsole(AttachParentProcess);
        }

        if (probe)
        {
            return RunProbes();
        }

        var databasePath = GetArgument(args, "--db") ?? SqliteStore.DefaultDatabasePath;

        using var mutex = new Mutex(true, "Local\\MailDog.Engine.Singleton", out var createdNew);
        if (!createdNew)
        {
            Console.WriteLine("MailDog.Engine 已在运行。");
            return 0;
        }

        var host = new EngineHost(
            databasePath,
            hideConsole: !console);

        try
        {
            host.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mutex.ReleaseMutex();
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    private static int RunProbes()
    {
        Console.WriteLine($"MailDog.Engine · .NET {Environment.Version} · AOT probe");

        try
        {
            ProbeSqlite();
            ProbeMime();
            Console.WriteLine("probe: ok");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ProbeSqlite()
    {
        SqliteNativeBootstrap.EnsureLoaded();
        SQLitePCL.Batteries_V2.Init();

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO probe (value) VALUES ('ok');
            SELECT value FROM probe LIMIT 1;
            """;

        var result = command.ExecuteScalar() as string;
        if (result != "ok")
        {
            throw new InvalidOperationException("SQLite probe returned an unexpected value.");
        }

        Console.WriteLine("sqlite: ok");
    }

    private static void ProbeMime()
    {
        const string raw = """
            From: probe@maildog.local
            To: dev@maildog.local
            Subject: AOT probe
            Date: Thu, 13 Nov 2026 12:00:00 +0800

            Hello MailDog.
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var message = MimeMessage.Load(stream);
        if (message.Subject != "AOT probe")
        {
            throw new InvalidOperationException("MIME probe failed.");
        }

        using var client = new ImapClient();
        Console.WriteLine("mime+imap: ok");
    }
}
