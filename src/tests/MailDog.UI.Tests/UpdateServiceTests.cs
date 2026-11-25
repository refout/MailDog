using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void CleanupRemovesOldInstallersAndKeepsNamedFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"maildog-updates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var oldA = Path.Combine(directory, "MailDog-0.3.0-win-x64-setup.exe");
            var oldB = Path.Combine(directory, "MailDog-0.3.1-win-x64-setup.exe");
            var keep = Path.Combine(directory, "MailDog-0.3.2-win-x64-setup.exe");
            var unrelated = Path.Combine(directory, "note.txt");

            File.WriteAllText(oldA, "a");
            File.WriteAllText(oldB, "b");
            File.WriteAllText(keep, "c");
            File.WriteAllText(unrelated, "d");

            var removed = UpdateService.CleanupDownloadedInstallers(directory, keep);

            Assert.Equal(2, removed);
            Assert.False(File.Exists(oldA));
            Assert.False(File.Exists(oldB));
            Assert.True(File.Exists(keep));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // 临时目录清不掉不影响测试结论。
            }
        }
    }
}
