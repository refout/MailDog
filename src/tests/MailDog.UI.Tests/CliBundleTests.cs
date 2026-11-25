using System.Formats.Tar;
using System.IO.Compression;
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

    [Fact]
    public void ExtractUnzipsCliBundleIntoInstallDirectory()
    {
        var root = CreateTempDirectory();

        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "maildog-cli"));
            File.WriteAllText(Path.Combine(source, "MailDog.Cli.exe"), "cli");
            File.WriteAllText(Path.Combine(source, "maildog-cli", "SKILL.md"), "skill");

            var archive = Path.Combine(root, "MailDog-cli-test.zip");
            ZipFile.CreateFromDirectory(source, archive);

            var target = Path.Combine(root, "install");
            Directory.CreateDirectory(target);

            CliBundle.Extract(archive, target);

            Assert.True(File.Exists(Path.Combine(target, "MailDog.Cli.exe")));
            Assert.True(File.Exists(Path.Combine(target, "maildog-cli", "SKILL.md")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExtractUnpacksCliTarGzIntoInstallDirectory()
    {
        var root = CreateTempDirectory();

        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "maildog-cli"));
            File.WriteAllText(Path.Combine(source, "MailDog.Cli"), "cli");
            File.WriteAllText(Path.Combine(source, "maildog-cli", "SKILL.md"), "skill");

            var archive = Path.Combine(root, "MailDog-cli-test.tar.gz");

            using (var output = File.Create(archive))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            {
                TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
            }

            var target = Path.Combine(root, "install");
            Directory.CreateDirectory(target);

            CliBundle.Extract(archive, target);

            Assert.True(File.Exists(Path.Combine(target, "MailDog.Cli")));
            Assert.True(File.Exists(Path.Combine(target, "maildog-cli", "SKILL.md")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
