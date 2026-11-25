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

    public static string DataDirectory
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog");

    /// <summary>关于页展示用：Linux 下把用户主目录收成 ~，避免把完整用户名写进界面。</summary>
    public static string DisplayDataDirectory
    {
        get
        {
            var directory = DataDirectory;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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
