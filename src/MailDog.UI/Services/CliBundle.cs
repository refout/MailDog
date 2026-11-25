using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>安装目录里随包携带的 CLI + Skill 压缩包；CLI 本身不再默认展开安装。</summary>
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
}
