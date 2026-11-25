using System.Diagnostics;

namespace MailDog.Engine.Tray;

/// <summary>用桌面自带的 notify-send 发通知；没有该命令时静默降级，不影响收信。</summary>
internal static class LinuxNotifier
{
    private static int _missingWarned;
    private static int _actionSupport = -1;

    public static void Show(
        string title,
        string message,
        string? activationUri = null,
        Action<string>? activated = null,
        Action? activatedWithoutTarget = null,
        IReadOnlyList<NotificationAction>? actions = null)
    {
        try
        {
            var useAction = (activationUri is not null || actions is { Count: > 0 }) && SupportsActions();
            var startInfo = CreateStartInfo(title, message, useAction, actions);

            if (!useAction)
            {
                using var plain = Process.Start(startInfo);
                return;
            }

            // 0.8 起的 notify-send 支持 -A/--action：动作名会写到 stdout，
            // 进程会一直等到用户点击或通知过期，所以这里异步等，不阻塞收信线程。
            var process = new Process { StartInfo = startInfo };
            process.Start();
            _ = Task.Run(() =>
                ReadActionAndCloseAsync(process, activationUri, actions, activated, activatedWithoutTarget));
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _missingWarned, 1) == 0)
            {
                Console.Error.WriteLine($"无法发送 Linux 桌面通知：{exception.Message}");
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string title,
        string message,
        bool useAction,
        IReadOnlyList<NotificationAction>? actions)
    {
        var startInfo = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = useAction
        };

        startInfo.ArgumentList.Add("--app-name=MailDog");
        startInfo.ArgumentList.Add("--expire-time=8000");

        if (useAction)
        {
            // default 是桌面环境约定的“点通知正文”动作。
            startInfo.ArgumentList.Add("--action=default=打开");

            foreach (var action in actions ?? [])
            {
                startInfo.ArgumentList.Add($"--action={action.Id}={action.Label}");
            }

            startInfo.ArgumentList.Add("--wait");
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add(message);
        return startInfo;
    }

    /// <summary>老版本 0.7.x 没有 action 参数；先探一次，避免每次通知都跑 help。</summary>
    private static bool SupportsActions()
    {
        var cached = Volatile.Read(ref _actionSupport);
        if (cached >= 0)
        {
            return cached == 1;
        }

        var supported = false;

        try
        {
            var startInfo = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--help");

            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(2000);
                supported = output.Contains("--action", StringComparison.Ordinal)
                    || output.Contains("-A,", StringComparison.Ordinal);
            }
        }
        catch
        {
            supported = false;
        }

        Volatile.Write(ref _actionSupport, supported ? 1 : 0);
        return supported;
    }

    /// <summary>
    /// 部分桌面会忽略 --expire-time；带 action 的 notify-send 会一直等点击，
    /// 这里兜一道 30 秒上限，避免每次新邮件都留下一个常驻子进程。
    /// 同时把 stdout 读完再判断动作，避免进程退出和回调赛跑丢点击。
    /// </summary>
    private static async Task ReadActionAndCloseAsync(
        Process process,
        string? activationUri,
        IReadOnlyList<NotificationAction>? actions,
        Action<string>? activated,
        Action? activatedWithoutTarget)
    {
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 已经退出或句柄已释放。
                }
            }

            var output = await outputTask.ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                if (value.StartsWith("action=", StringComparison.Ordinal))
                {
                    value = value["action=".Length..];
                }

                if (value == "default")
                {
                    try
                    {
                        if (activationUri is { Length: > 0 })
                        {
                            activated?.Invoke(activationUri);
                        }
                        else
                        {
                            activatedWithoutTarget?.Invoke();
                        }
                    }
                    catch
                    {
                        // 通知点击只是锦上添花，回调自身出错也不该结束引擎。
                    }

                    return;
                }

                var action = actions?.FirstOrDefault(item =>
                    string.Equals(item.Id, value, StringComparison.Ordinal));
                if (action is not null)
                {
                    try
                    {
                        activated?.Invoke(action.Uri);
                    }
                    catch
                    {
                        // 同上。
                    }

                    return;
                }

                if (IsAnyAction(value))
                {
                    activatedWithoutTarget?.Invoke();
                    return;
                }
            }
        }
        catch
        {
            // 子进程没起来或管道提前关闭，忽略。
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsAnyAction(string? line)
        => line is not null
            && (line.TrimStart().StartsWith("action=", StringComparison.Ordinal)
                || line.Trim() is "1" or "2" or "3");
}
