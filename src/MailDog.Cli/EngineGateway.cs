using System.Diagnostics;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Platform;

namespace MailDog.Cli;

/// <summary>连 Engine：先试已有管道，连不上就按安装目录 / 仓库结构找 exe 拉起来。</summary>
internal static class EngineGateway
{
    private static readonly TimeSpan FirstAttempt = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(15);

    public static async Task<IpcClient> ConnectAsync(
        string? enginePath,
        bool allowStart,
        CancellationToken cancellationToken,
        string? notRunningMessage = null)
    {
        var existing = await IpcClient.TryConnectAsync(FirstAttempt, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return await HandshakeAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        if (!allowStart)
        {
            throw new IpcCallException(
                IpcErrorCodes.UpstreamUnavailable,
                notRunningMessage ?? "没有找到正在运行的 MailDog 后台（已指定 --no-start）。");
        }

        var executable = ResolveEnginePath(enginePath)
            ?? throw new IpcCallException(
                IpcErrorCodes.UpstreamUnavailable,
                $"找不到 {ProductPaths.EngineExecutableName}。请把命令行和程序放在同一目录，或用 --engine 指定路径。");

        StartEngine(executable);

        var deadline = DateTimeOffset.Now + StartupWindow;

        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            var client = await IpcClient.TryConnectAsync(FirstAttempt, cancellationToken).ConfigureAwait(false);

            if (client is not null)
            {
                return await HandshakeAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IpcCallException(
            IpcErrorCodes.UpstreamUnavailable,
            $"后台没有按时就绪（{StartupWindow.TotalSeconds:0} 秒）。可以先手动启动 {ProductPaths.EngineExecutableName} 再试。");
    }

    private static async Task<IpcClient> HandshakeAsync(IpcClient client, CancellationToken cancellationToken)
    {
        var version = typeof(EngineGateway).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        try
        {
            await client.HelloAsync(version, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static string? ResolveEnginePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("MAILDOG_ENGINE_PATH");

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var sameDirectory = Path.Combine(AppContext.BaseDirectory, ProductPaths.EngineExecutableName);

        if (File.Exists(sameDirectory))
        {
            return sameDirectory;
        }

        // 发版包把 CLI 放在 cli/ 子目录，引擎在上一级目录。
        var parentDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", ProductPaths.EngineExecutableName));

        if (File.Exists(parentDirectory))
        {
            return parentDirectory;
        }

        // 开发时命令行与 Engine 分属不同输出目录，按仓库结构向上找一遍。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var runtime = ProductPaths.RuntimeIdentifier;

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var artifact = Path.Combine(
                directory.FullName,
                "artifacts",
                runtime,
                "engine",
                ProductPaths.EngineExecutableName);

            if (File.Exists(artifact))
            {
                return artifact;
            }

            var binRoot = Path.Combine(directory.FullName, "MailDog.Engine", "bin");

            if (!Directory.Exists(binRoot))
            {
                continue;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var configurationRoot = Path.Combine(binRoot, configuration);

                if (!Directory.Exists(configurationRoot))
                {
                    continue;
                }

                foreach (var targetFramework in Directory.GetDirectories(configurationRoot, "net11.0*"))
                {
                    var candidate = Path.Combine(targetFramework, ProductPaths.EngineExecutableName);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static void StartEngine(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // 命令行自己就是控制台程序，子进程默认会共用这个控制台并把日志混进 stdout；
            // 重定向后再读掉，保证 --json 的输出里只有我们的 JSON。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
        };

        var uiPath = Path.Combine(
            Path.GetDirectoryName(executable) ?? string.Empty,
            ProductPaths.UiExecutableName);

        if (File.Exists(uiPath))
        {
            startInfo.Environment["MAILDOG_UI_PATH"] = uiPath;
        }

        var process = Process.Start(startInfo);

        if (process is not null)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }
}
