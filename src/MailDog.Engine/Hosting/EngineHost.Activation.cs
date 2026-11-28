using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Mime;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Engine.Hosting;

public sealed partial class EngineHost
{
    /// <summary>点通知后把用户带到那封邮件：能广播就广播，界面没开就顺手拉起。</summary>
    public void Activate(string uri)
    {
        _log.Info($"收到激活：{uri}");

        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            ActivateCompose(uri);
            return;
        }

        if (ParseNotificationAction(uri) is { } notificationAction)
        {
            _ = Task.Run(() => HandleNotificationActionAsync(notificationAction));
            return;
        }

        if (ParseEmlActivation(uri) is { Length: > 0 } emlPath)
        {
            ActivateFile(emlPath);
            return;
        }

        if (ParseActivation(uri) is { } target)
        {
            _pendingNavigation = target;
        }

        OpenUi();
    }

    /// <summary>直接打开外部 .eml：界面已在运行时发事件，否则记下来并拉起界面。</summary>
    public void ActivateFile(string path)
    {
        _log.Info($"打开 .eml：{path}");

        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { EmlPath = path });
            return;
        }

        _pendingEml = path;
        OpenUi();
    }

    private void ActivateCompose(string uri)
    {
        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { ComposeUri = uri });
            return;
        }

        _pendingCompose = uri;
        OpenUi();
    }

    private static string? ParseEmlActivation(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "maildog", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, "eml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2 && parts[0] == "path")
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static (string Action, long MessageId, string AccountId, long FolderId)? ParseNotificationAction(string uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !string.Equals(parsed.Scheme, "maildog", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            long messageId = 0;
            long folderId = 0;
            var accountId = string.Empty;
            var action = string.Empty;

            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "do":
                        action = parts[1];
                        break;
                    case "m":
                        long.TryParse(parts[1], out messageId);
                        break;
                    case "f":
                        long.TryParse(parts[1], out folderId);
                        break;
                    case "a":
                        accountId = Uri.UnescapeDataString(parts[1]);
                        break;
                }
            }

            return action is "read" or "archive" && messageId > 0 && accountId.Length > 0
                ? (action, messageId, accountId, folderId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleNotificationActionAsync(
        (string Action, long MessageId, string AccountId, long FolderId) action)
    {
        try
        {
            var message = _store.GetMessage(action.MessageId);
            if (message is null
                || !string.Equals(message.AccountId, action.AccountId, StringComparison.Ordinal))
            {
                _log.Warn($"通知操作找不到邮件：{action.MessageId}");
                return;
            }

            var sourceFolderId = action.FolderId > 0 ? action.FolderId : message.FolderId;

            if (action.Action == "read")
            {
                await _sync.SetSeenAsync(action.MessageId, true, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                var archive = _store.GetFolders(action.AccountId).FirstOrDefault(folder =>
                    folder.Role == FolderRole.Archive && folder.Id != message.FolderId);

                if (archive is null)
                {
                    _log.Warn($"通知归档失败：账户 {action.AccountId} 没有归档文件夹。");
                    return;
                }

                await _sync.MoveMessageAsync(action.MessageId, archive.Id, _cts.Token).ConfigureAwait(false);
            }

            _ipc.Broadcast(IpcMessageTypes.MailChanged, new MailChangedEvent
            {
                AccountId = action.AccountId,
                MessageId = action.MessageId,
                FolderId = sourceFolderId,
                Change = action.Action == "read" ? "flags" : "moved"
            });

            _tray?.UpdateUnreadCount(TotalUnread());
        }
        catch (Exception exception)
        {
            _log.Warn($"通知操作执行失败：{exception.Message}");
        }
    }

    private static (long MessageId, string AccountId, long FolderId)? ParseActivation(string uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return null;
            }

            long messageId = 0;
            long folderId = 0;
            var accountId = string.Empty;

            foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);

                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "m":
                        long.TryParse(parts[1], out messageId);
                        break;
                    case "f":
                        long.TryParse(parts[1], out folderId);
                        break;
                    case "a":
                        accountId = Uri.UnescapeDataString(parts[1]);
                        break;
                }
            }

            return messageId > 0 && accountId.Length > 0
                ? (messageId, accountId, folderId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private PendingNavigationResponse TakePendingNavigation()
    {
        var target = _pendingNavigation;
        _pendingNavigation = null;

        var openAbout = _pendingOpenAbout;
        _pendingOpenAbout = false;
        var compose = _pendingCompose;
        _pendingCompose = null;
        var eml = _pendingEml;
        _pendingEml = null;

        return new PendingNavigationResponse
        {
            HasTarget = target is not null,
            MessageId = target?.MessageId,
            AccountId = target?.AccountId,
            FolderId = target?.FolderId,
            ComposeUri = compose,
            EmlPath = eml,
            OpenAbout = openAbout,
            CheckUpdate = openAbout
        };
    }

    /// <summary>托盘里的"检查更新"：把窗口叫出来并落到关于页，检查与下载都在界面里做。</summary>
    private void OpenAbout()
    {
        if (_ipc.HasConnections)
        {
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, new ShowMainWindowEvent { OpenAbout = true, CheckUpdate = true });
            return;
        }

        // 界面没开：先记下诉求，等它连上来取待办导航时一并交代。
        _pendingOpenAbout = true;
        OpenUi();
    }

    private void OpenUi()
    {
        var navigation = _pendingNavigation is { } target
            ? new ShowMainWindowEvent
            {
                MessageId = target.MessageId,
                AccountId = target.AccountId,
                FolderId = target.FolderId,
                ComposeUri = _pendingCompose,
                EmlPath = _pendingEml
            }
            : new ShowMainWindowEvent
            {
                ComposeUri = _pendingCompose,
                EmlPath = _pendingEml
            };

        if (_ipc.HasConnections)
        {
            _pendingNavigation = null;
            _pendingCompose = null;
            _pendingEml = null;
            _ipc.Broadcast(IpcMessageTypes.ShowMainWindow, navigation);
            return;
        }

        var uiPath = Environment.GetEnvironmentVariable("MAILDOG_UI_PATH");

        if (string.IsNullOrWhiteSpace(uiPath) || !File.Exists(uiPath))
        {
            // 打包后 UI 与 Engine 同目录；开发时两者在不同项目的输出目录里，
            // 光看同目录会一直"未找到 UI"，所以按仓库结构向上找一遍。
            uiPath = LocateUi();
        }

        if (string.IsNullOrWhiteSpace(uiPath) || !File.Exists(uiPath))
        {
            _log.Warn($"未找到 UI：{uiPath}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uiPath)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception exception)
        {
            _log.Error("启动 UI 失败。", exception);
        }
    }

    /// <summary>同目录优先（安装后就是这样）；找不到再按仓库里的 UI 构建目录向上搜。</summary>
    private static string? LocateUi()
    {
        var sameDirectory = Path.Combine(AppContext.BaseDirectory, ProductPaths.UiExecutableName);

        if (File.Exists(sameDirectory))
        {
            return sameDirectory;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var runtime = ProductPaths.RuntimeIdentifier;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var uiRoot = Path.Combine(directory.FullName, "MailDog.UI", "bin", configuration);

            if (!Directory.Exists(uiRoot))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(uiRoot, "net11.0", runtime, ProductPaths.UiExecutableName),
                         Path.Combine(uiRoot, "net11.0", ProductPaths.UiExecutableName)
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string BuildTooltip()
    {
        var accounts = _store.GetAccounts();
        return $"MailDog — {accounts.Count} 个账户，{TotalUnread()} 封未读";
    }

    private int TotalUnread()
        => _store.GetAccounts().Sum(account => _store.GetFolders(account.Id).Sum(folder => folder.UnreadCount));

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        _cts.Cancel();
        _tray?.Quit();
    }

}
