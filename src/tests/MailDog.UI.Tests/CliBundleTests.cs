using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class CliBundleTests
{
    [Fact]
    public void FindReturnsBundleUnderCliDirectory()
    {
        var root = CreateTempDirectory();

        try
        {
            var cliDirectory = Path.Combine(root, "cli");
            Directory.CreateDirectory(cliDirectory);
            var path = Path.Combine(cliDirectory, CliBundle.BundledFileName);
            File.WriteAllText(path, "test");

            Assert.Equal(path, CliBundle.Find(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FindReturnsNullWhenBundleIsMissing()
    {
        var root = CreateTempDirectory();

        try
        {
            Assert.Null(CliBundle.Find(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SuggestedFileNameIncludesVersionAndPlatform()
    {
        var name = CliBundle.SuggestedFileName("0.3.0");

        Assert.StartsWith("MailDog-cli-0.3.0-", name);
        Assert.EndsWith($".{CliBundle.DefaultExtension}", name);
    }

    [Fact]
    public void DownloadPathUsesInstallDirectory()
    {
        var root = Path.GetTempPath();

        Assert.Equal(
            Path.Combine(root, CliBundle.SuggestedFileName("0.3.0")),
            CliBundle.DownloadPath(root, "0.3.0"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
