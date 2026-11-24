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

        if (args.Contains("--test-notify", StringComparer.OrdinalIgnoreCase))
        {
            return RunNotificationTest();
        }

        var databasePath = GetArgument(args, "--db") ?? SqliteStore.DefaultDatabasePath;
        var activationUri = GetArgument(args, "--activate");

        using var mutex = new Mutex(true, "Local\\MailDog.Engine.Singleton", out var createdNew);
        if (!createdNew)
        {
            // 通知被点击：Windows 按 maildog: 协议起了一个新进程，这里把它转给已经常驻的那个。
            if (!string.IsNullOrWhiteSpace(activationUri))
            {
                MailDog.Engine.Tray.ToastActivation.Send(activationUri);
            }

            Console.WriteLine("MailDog.Engine 已在运行。");
            return 0;
        }

        var host = new EngineHost(
            databasePath,
            hideConsole: !console,
            startupActivation: activationUri);

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

    /// <summary>弹出一次真实通知，用来核对标题栏小图标与气泡里的大图标。</summary>
    private static int RunNotificationTest()
    {
        MailDog.Engine.Tray.AppIdentity.EnsureRegistered("MailDog");

        // --aumid <id>：用另一个应用标识单独发一条，用来排查通知系统是否认到了图标。
        var probeAppId = GetArgument(Environment.GetCommandLineArgs(), "--aumid");

        if (!string.IsNullOrWhiteSpace(probeAppId))
        {
            var sent = MailDog.Engine.Tray.ToastNotifier.TryShow(
                "测试发件人",
                "探针通知：只为核查通知头部的应用图标。",
                preview: null,
                attribution: "test@maildog.local",
                avatarPath: MailDog.Engine.Tray.AppIdentity.EnsureIconFile(),
                playSound: false,
                tag: "probe",
                activationUri: null,
                appId: probeAppId);

            Console.WriteLine($"探针通知发送：{sent}（标识 {probeAppId}）");
            Thread.Sleep(2000);
            return 0;
        }

        MailDog.Engine.Tray.ShellIntegration.EnsureProtocolRegistered();

        using var tray = new MailDog.Engine.Tray.Win32Tray(
            () => { },
            () => { },
            uri => Console.WriteLine($"收到激活：{uri}"),
            () => { },
            () => { },
            () => { },
            () => false);

        var loop = new Thread(() => tray.Run(hideConsole: false)) { IsBackground = true };
        loop.Start();
        Thread.Sleep(500);

        tray.ShowNotification(
            "测试发件人",
            "这是一封测试邮件的主题",
            playSound: false,
            sound: null,
            senderName: "测试发件人",
            senderAddress: "test@maildog.local",
            preview: "点一下这条通知，应该会拉起 MailDog 主窗口。",
            activationUri: "maildog://open");

        Thread.Sleep(8000);
        tray.Quit();
        return 0;
    }

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
