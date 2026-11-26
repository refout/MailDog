using System.Diagnostics;
using System.Runtime.InteropServices;
using MailDog.Contracts.Messages;
using Microsoft.Win32;

namespace MailDog.Engine.Tray;

/// <summary>
/// Windows 用户级集成：mailto / .eml 打开方式、默认邮件程序能力、SendTo 快捷方式和最近邮件。
/// 只写 HKCU，不改系统级关联；默认程序由用户在 Windows 设置里确认。
/// </summary>
internal static class WindowsIntegration
{
    private const string ClassesKey = @"Software\Classes";
    private const string MailToProgId = "MailDog.mailto";
    private const string EmlProgId = "MailDog.Eml";
    private const string CapabilitiesKey = @"Software\MailDog\Capabilities";

    private const uint ShardPathW = 0x00000003;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHAddToRecentDocs(uint flags, string path);

    public static void RegisterAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
        {
            return;
        }

        var ui = ResolveUiPath(engine);
        var icon = !string.IsNullOrWhiteSpace(ui) && File.Exists(ui) ? ui : engine;

        try
        {
            RegisterMailTo(engine, icon);
            RegisterEml(engine, icon);
            RegisterDefaultMailClient(engine, icon);
        }
        catch
        {
            // 注册表被策略限制时不影响收发邮件。
        }

        EnsureSendToShortcut(engine, ui);
    }

    public static void AddRecent(AddRecentRequest request)
    {
        if (!OperatingSystem.IsWindows() || request.MessageId <= 0)
        {
            return;
        }

        var engine = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
        {
            return;
        }

        var ui = ResolveUiPath(engine);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog",
            "JumpList");

        var link = Path.Combine(directory, $"mail-{request.MessageId}.lnk");
        var arguments = $"--activate \"maildog://open?messageId={request.MessageId}"
            + $"&accountId={Uri.EscapeDataString(request.AccountId)}"
            + $"&folderId={request.FolderId}\"";

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);

                foreach (var stale in Directory.EnumerateFiles(directory, "mail-*.lnk")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Skip(14))
                {
                    File.Delete(stale);
                }

                if (!CreateShortcut(link, engine, arguments, request.Subject, ui))
                {
                    return;
                }

                SHAddToRecentDocs(ShardPathW, link);
            }
            catch
            {
                // 最近列表只是锦上添花，失败不打扰用户。
            }
        });
    }

    private static void RegisterMailTo(string engine, string icon)
    {
        using var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{MailToProgId}");

        if (progId is null)
        {
            return;
        }

        progId.SetValue(string.Empty, "MailDog mailto link", RegistryValueKind.String);
        progId.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
        SetCommand(progId, engine, "--activate \"%1\"");
        SetIcon(progId, icon);
    }

    private static void RegisterEml(string engine, string icon)
    {
        using var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{EmlProgId}");

        if (progId is null)
        {
            return;
        }

        progId.SetValue(string.Empty, "MailDog 邮件文件", RegistryValueKind.String);
        SetCommand(progId, engine, "--eml \"%1\"");
        SetIcon(progId, icon);

        using var openWith = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\.eml\OpenWithProgids");
        openWith?.SetValue(EmlProgId, string.Empty, RegistryValueKind.String);
    }

    private static void RegisterDefaultMailClient(string engine, string icon)
    {
        using var client = Registry.CurrentUser.CreateSubKey(@"Software\Clients\Mail\MailDog");
        client?.SetValue(string.Empty, "MailDog", RegistryValueKind.String);
        client?.CreateSubKey(@"shell\open\command")?.SetValue(
            string.Empty,
            $"\"{engine}\" --activate \"mailto:\"",
            RegistryValueKind.String);

        using var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey);

        if (capabilities is null)
        {
            return;
        }

        capabilities.SetValue("ApplicationName", "MailDog", RegistryValueKind.String);
        capabilities.SetValue("ApplicationDescription", "桌面邮件客户端", RegistryValueKind.String);
        capabilities.SetValue("ApplicationIcon", $"\"{icon}\",0", RegistryValueKind.String);

        using var urls = capabilities.CreateSubKey("URLAssociations");
        urls?.SetValue("mailto", MailToProgId, RegistryValueKind.String);

        using var files = capabilities.CreateSubKey("FileAssociations");
        files?.SetValue(".eml", EmlProgId, RegistryValueKind.String);

        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered?.SetValue("MailDog", CapabilitiesKey, RegistryValueKind.String);
    }

    private static void SetCommand(RegistryKey progId, string engine, string arguments)
    {
        using var command = progId.CreateSubKey(@"shell\open\command");
        command?.SetValue(string.Empty, $"\"{engine}\" {arguments}", RegistryValueKind.String);
    }

    private static void SetIcon(RegistryKey progId, string icon)
    {
        using var defaultIcon = progId.CreateSubKey("DefaultIcon");
        defaultIcon?.SetValue(string.Empty, $"\"{icon}\",0", RegistryValueKind.String);
    }

    private static void EnsureSendToShortcut(string engine, string? ui)
    {
        var sendTo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "SendTo");
        var link = Path.Combine(sendTo, "MailDog.lnk");

        if (File.Exists(link))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(sendTo);
                CreateShortcut(link, engine, "--eml", "用 MailDog 打开", ui);
            }
            catch
            {
                // SendTo 不可写时忽略。
            }
        });
    }

    private static bool CreateShortcut(
        string linkPath,
        string target,
        string arguments,
        string description,
        string? icon)
    {
        var script = string.Join(
            Environment.NewLine,
            "$ws = New-Object -ComObject WScript.Shell",
            $"$lnk = $ws.CreateShortcut('{Quote(linkPath)}')",
            $"$lnk.TargetPath = '{Quote(target)}'",
            $"$lnk.Arguments = '{Quote(arguments)}'",
            $"$lnk.WorkingDirectory = '{Quote(Path.GetDirectoryName(target) ?? string.Empty)}'",
            $"$lnk.Description = '{Quote(description)}'",
            !string.IsNullOrWhiteSpace(icon) && File.Exists(icon)
                ? $"$lnk.IconLocation = '{Quote(icon)},0'"
                : "$lnk.IconLocation = ''",
            "$lnk.Save()");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        return process.ExitCode == 0 && File.Exists(linkPath);
    }

    /// <summary>PowerShell 单引号字符串里唯二需要转义的字符。</summary>
    private static string Quote(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? ResolveUiPath(string engine)
    {
        var configured = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var coLocated = Path.Combine(Path.GetDirectoryName(engine) ?? string.Empty, "MailDog.UI.exe");
        return File.Exists(coLocated) ? coLocated : null;
    }
}
