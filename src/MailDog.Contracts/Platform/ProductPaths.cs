using System.Runtime.InteropServices;

namespace MailDog.Contracts.Platform;

/// <summary>跨平台的产品路径与可执行文件名，避免各处硬编码 Windows 的 .exe。</summary>
public static class ProductPaths
{
    public static string EngineExecutableName
        => OperatingSystem.IsWindows() ? "MailDog.Engine.exe" : "MailDog.Engine";

    public static string UiExecutableName
        => OperatingSystem.IsWindows() ? "MailDog.UI.exe" : "MailDog.UI";

    public static string CliExecutableName
        => OperatingSystem.IsWindows() ? "MailDog.Cli.exe" : "MailDog.Cli";

    /// <summary>当前进程对应的发布 RID，例如 win-x64、linux-x64、linux-arm64。</summary>
    public static string RuntimeIdentifier
    {
        get
        {
            var os = OperatingSystem.IsWindows()
                ? "win"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : OperatingSystem.IsMacOS()
                        ? "osx"
                        : "unknown";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                _ => "unknown"
            };

            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// 平台数据根目录。Linux 走 XDG：优先 <c>$XDG_DATA_HOME</c>，否则 <c>$HOME/.local/share</c>。
    /// NativeAOT 单文件在部分 Linux 环境下 <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// 会返回空串，若直接使用会退化成当前工作目录下的相对路径。
    /// </summary>
    public static string DataRootDirectory
        => ResolveDataRoot(
            OperatingSystem.IsLinux(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            HomeDirectory,
            AppContext.BaseDirectory);

    public static string DataDirectory => Path.Combine(DataRootDirectory, "MailDog");

    /// <summary>用户主目录；Linux 上 <see cref="Environment.SpecialFolder.UserProfile"/> 也可能为空。</summary>
    public static string HomeDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(home))
            {
                return home;
            }

            return Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }
    }

    internal static string ResolveDataRoot(
        bool isLinux,
        string? localApplicationData,
        string? xdgDataHome,
        string? home,
        string fallback)
    {
        if (isLinux)
        {
            if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
            {
                return xdgDataHome;
            }

            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share");
            }
        }

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return localApplicationData;
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".local", "share");
        }

        return fallback;
    }

    /// <summary>关于页展示用：Linux 下把用户主目录收成 ~，避免把完整用户名写进界面。</summary>
    public static string DisplayDataDirectory
    {
        get
        {
            var directory = DataDirectory;
            var home = HomeDirectory;

            if (string.IsNullOrEmpty(home))
            {
                return directory;
            }

            return directory.StartsWith(home, StringComparison.Ordinal)
                ? "~" + directory[home.Length..]
                : directory;
        }
    }
}
