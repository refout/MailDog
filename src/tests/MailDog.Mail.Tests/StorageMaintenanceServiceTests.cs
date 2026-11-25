using MailDog.Contracts.Messages;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

[Collection("Sqlite")]
public sealed class StorageMaintenanceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteStore _store;
    private readonly StorageMaintenancePaths _paths;
    private readonly StorageMaintenanceService _service;

    public StorageMaintenanceServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maildog-storage", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(_root, "data");
        _paths = new StorageMaintenancePaths(
            data,
            Path.Combine(data, "test.db"),
            Path.Combine(_root, "logs"),
            Path.Combine(_root, "attachments"),
            Path.Combine(data, "outbox"),
            Path.Combine(_root, "native"),
            Path.Combine(_root, "temp"));

        Directory.CreateDirectory(data);
        foreach (var directory in new[]
                 {
                     _paths.LogDirectory,
                     _paths.AttachmentCacheDirectory,
                     _paths.OutboxDirectory,
                     _paths.NativeDirectory,
                     _paths.TempDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }

        _store = new SqliteStore(_paths.DatabasePath);
        _service = new StorageMaintenanceService(_store, _paths);
    }

    [Fact]
    public void SummaryCountsFilesAndFolders()
    {
        WriteFile(Path.Combine(_paths.AttachmentCacheDirectory, "a.txt"), 100);
        WriteFile(Path.Combine(_paths.AttachmentCacheDirectory, "nested", "b.bin"), 200);
        WriteFile(Path.Combine(_paths.LogDirectory, "engine-20200101.log"), 50);
        WriteFile(Path.Combine(_paths.LogDirectory, $"engine-{DateTimeOffset.Now:yyyyMMdd}.log"), 70);
        WriteFile(Path.Combine(_paths.TempDirectory, "temp.bin"), 30);
        WriteFile(Path.Combine(_paths.OutboxDirectory, "pending.bin"), 40);
        WriteFile(Path.Combine(_paths.NativeDirectory, "lib.bin"), 20);

        var summary = _service.GetSummary();

        Assert.Equal(_paths.DataDirectory, summary.DataDirectory);
        Assert.Equal(_paths.DatabasePath, summary.DatabasePath);
        Assert.True(summary.DatabaseBytes > 0);
        Assert.Equal(300, summary.AttachmentCacheBytes);
        Assert.Equal(120, summary.LogBytes);
        Assert.Equal(30, summary.TempBytes);
        Assert.Equal(40, summary.OutboxBytes);
        Assert.Equal(20, summary.NativeBytes);
        Assert.Equal(0, summary.MessageCount);
        Assert.Equal(0, summary.BodyCount);
        Assert.Equal(0, summary.PendingOutboxCount);
    }

    [Fact]
    public void CleanRemovesCachesButKeepsDatabaseAndCurrentLog()
    {
        WriteFile(Path.Combine(_paths.AttachmentCacheDirectory, "a.txt"), 100);
        WriteFile(Path.Combine(_paths.AttachmentCacheDirectory, "nested", "b.bin"), 200);
        WriteFile(Path.Combine(_paths.LogDirectory, "engine-20200101.log"), 50);
        var currentLog = Path.Combine(_paths.LogDirectory, $"engine-{DateTimeOffset.Now:yyyyMMdd}.log");
        WriteFile(currentLog, 70);
        WriteFile(Path.Combine(_paths.TempDirectory, "temp.bin"), 30);

        var result = _service.Clean(new CleanStorageRequest
        {
            CleanAttachmentCache = true,
            CleanLogs = true,
            CleanTempFiles = true
        });

        Assert.True(result.FreedBytes >= 380);
        Assert.Equal(0, result.SkippedFiles);
        Assert.False(File.Exists(Path.Combine(_paths.AttachmentCacheDirectory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_paths.AttachmentCacheDirectory, "nested", "b.bin")));
        Assert.False(File.Exists(Path.Combine(_paths.LogDirectory, "engine-20200101.log")));
        Assert.True(File.Exists(currentLog));
        Assert.False(File.Exists(Path.Combine(_paths.TempDirectory, "temp.bin")));
        Assert.True(File.Exists(_paths.DatabasePath));
    }

    [Fact]
    public void CompactDatabaseRunsAndKeepsDatabase()
    {
        var result = _service.Clean(new CleanStorageRequest { CompactDatabase = true });

        Assert.Contains("数据库压缩", result.Message);
        Assert.True(File.Exists(_paths.DatabasePath));
    }

    [Fact]
    public void CleanWithoutFlagsDoesNothing()
    {
        WriteFile(Path.Combine(_paths.AttachmentCacheDirectory, "a.txt"), 10);

        var result = _service.Clean(new CleanStorageRequest());

        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, result.SkippedFiles);
        Assert.True(File.Exists(Path.Combine(_paths.AttachmentCacheDirectory, "a.txt")));
    }

    public void Dispose()
    {
        _store.Dispose();

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', bytes).ToArray());
    }
}
