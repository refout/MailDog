using System.Formats.Tar;
using System.IO.Compression;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>安装目录里随包携带的 CLI + Skill 压缩包；安装包本身不展开，下载后由 <see cref="Extract"/> 展开。</summary>
public static class CliBundle
{
    public static string BundledFileName
        => OperatingSystem.IsWindows() ? "MailDog-cli.zip" : "MailDog-cli.tar.gz";

    public static string DefaultExtension
        => OperatingSystem.IsWindows() ? "zip" : "tar.gz";

    public static string? Find()
        => Find(AppContext.BaseDirectory);

    /// <summary>测试与便携版可指定根目录；只认安装目录下的 cli 子目录。</summary>
    public static string? Find(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "cli", BundledFileName);
        return File.Exists(path) ? path : null;
    }

    public static string SuggestedFileName(string version)
    {
        var trimmed = version.Trim();
        var prefix = trimmed.Length == 0 ? "MailDog-cli" : $"MailDog-cli-{trimmed}";
        return $"{prefix}-{ProductPaths.RuntimeIdentifier}.{DefaultExtension}";
    }

    /// <summary>下载目标固定在安装目录根下，不再弹保存对话框。</summary>
    public static string DownloadPath(string baseDirectory, string version)
        => Path.Combine(baseDirectory, SuggestedFileName(version));

    /// <summary>把下载好的 CLI 包解到安装目录；zip 与 tar.gz 都支持，Linux 下补执行权限。</summary>
    public static void Extract(string bundlePath, string destinationDirectory)
    {
        if (bundlePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(bundlePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        if (bundlePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = File.OpenRead(bundlePath);
            using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
            EnsureExecutable(destinationDirectory);
            return;
        }

        throw new NotSupportedException($"不认识的 CLI 包格式：{Path.GetFileName(bundlePath)}");
    }

    private static void EnsureExecutable(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var name in new[] { ProductPaths.CliExecutableName, "maildog" })
        {
            var path = Path.Combine(directory, name);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var mode = File.GetUnixFileMode(path);
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(path, mode);
            }
            catch
            {
                // 权限补不上不影响解压本身；用户仍可 chmod +x。
            }
        }
    }
}
