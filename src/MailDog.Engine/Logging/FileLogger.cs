using MailDog.Contracts.Platform;

namespace MailDog.Engine.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public FileLogger(string? path = null)
    {
        var directory = Path.Combine(ProductPaths.DataDirectory, "logs");

        Directory.CreateDirectory(directory);

        var file = path ?? Path.Combine(directory, $"engine-{DateTimeOffset.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {level} {message}";

        lock (_gate)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);

            if (exception is not null)
            {
                _writer.WriteLine(exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
