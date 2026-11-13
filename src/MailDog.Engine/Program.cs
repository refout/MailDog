using System.Text;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailDog.Engine;

internal static class Program
{
    public static int Main(string[] args)
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
