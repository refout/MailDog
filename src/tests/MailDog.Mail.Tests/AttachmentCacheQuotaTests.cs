using MailDog.Contracts.Platform;

namespace MailDog.Mail.Tests;

public sealed class AttachmentCacheQuotaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maildog-quota-{Guid.NewGuid():N}");

    public AttachmentCacheQuotaTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DeletesOldestFilesUntilUnderLimit()
    {
        var oldest = Write("old.bin", 40);
        var middle = Write("middle.bin", 40);
        var newest = Write("new.bin", 40);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

        var removed = AttachmentCacheQuota.Enforce(_root, 50);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void KeepsFilesThatWereJustDraggedOut()
    {
        var old = Write("old.bin", 40);
        var fresh = Write("fresh.bin", 80);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        var removed = AttachmentCacheQuota.Enforce(_root, 60, [fresh]);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void DoesNothingWhenUnderLimit()
    {
        var file = Write("file.bin", 10);

        Assert.Equal(0, AttachmentCacheQuota.Enforce(_root, 100));
        Assert.True(File.Exists(file));
    }

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }
}
