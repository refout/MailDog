using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Html;
using MailDog.UI.Services;
using MailDog.UI.Views;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>托盘“退出 MailDog”：后台没响应也把界面关掉，不留下看不见的窗口。</summary>
    public async Task QuitFromTrayAsync()
    {
        try
        {
            if (_engine.IsConnected)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.Shutdown,
                    new ShutdownRequest(),
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
        }
        catch
        {
            // 后台已经退出或管道断开时直接关界面。
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Linux 无托盘时的退出入口：先让 Engine 收尾，再关掉界面。</summary>
    [RelayCommand]
    private async Task QuitBackgroundAsync()
    {
        if (IsBackgroundExiting)
        {
            return;
        }

        IsBackgroundExiting = true;
        BackgroundExitStatus = "正在退出后台…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.Shutdown,
                new ShutdownRequest(),
                TimeSpan.FromSeconds(5)).ConfigureAwait(true);

            BackgroundExitStatus = "后台已退出。";
            await Task.Delay(300).ConfigureAwait(true);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception exception)
        {
            BackgroundExitStatus = $"退出失败：{GetErrorMessage(exception)}";
            IsBackgroundExiting = false;
        }
    }

    [RelayCommand]
    private void OpenAbout()
    {
        SettingsSectionIndex = 10;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void BackFromOverlay()
    {
        CloseOverlay();
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        IsSettingsOpen = false;
        RefreshReaderAppearance();
    }

    private void RefreshReaderAppearance()
    {
        if (!_readerAppearanceDirty)
        {
            return;
        }

        _readerAppearanceDirty = false;
        RenderCurrentBody();
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        HasUpdateAvailable = false;
        UpdateStatusText = "正在检查更新…";

        try
        {
            var result = await _updates.CheckAsync(AppVersion);
            _updateResult = result;

            if (result.Error is { } error)
            {
                // 检查失败只说一句，不弹窗、不反复重试。
                UpdateStatusText = $"检查更新失败：{error}";
                return;
            }

            if (!result.HasUpdate)
            {
                UpdateStatusText = "已是最新版本。";
                return;
            }

            var canInstall = UpdateService.IsInstalledLayout() && result.InstallerUrl is not null;
            UpdateActionText = canInstall ? "下载并安装" : "去下载页";
            UpdateStatusText = OperatingSystem.IsLinux()
                ? result.PortableUrl is not null
                    ? $"发现新版本 {result.LatestVersion}，Linux 包已发布，请下载 tar.gz 解压替换。"
                    : $"发现新版本 {result.LatestVersion}。"
                : $"发现新版本 {result.LatestVersion}。";
            HasUpdateAvailable = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>安装版下载并静默升级；便携版不擅自改成安装版，只把人送去下载页。</summary>
    [RelayCommand]
    private async Task RunUpdateActionAsync()
    {
        if (IsUpdateBusy || _updateResult is not { HasUpdate: true } result)
        {
            return;
        }

        if (!UpdateService.IsInstalledLayout() || result.InstallerUrl is null)
        {
            OpenReleasePage();
            return;
        }

        IsUpdateBusy = true;
        UpdateStatusText = "正在下载安装包…";

        try
        {
            var progress = new Progress<double>(value => UpdateStatusText = $"正在下载安装包… {value:P0}");
            var path = await _updates.DownloadInstallerAsync(result, progress);

            if (path is null)
            {
                UpdateStatusText = "下载或校验失败，请到发布页手动下载。";
                return;
            }

            UpdateStatusText = "正在安装，完成后会自动重新启动…";
            var installed = await _updates.RunInstallerAsync(path);

            if (!installed)
            {
                UpdateStatusText = "安装没有完成，请到发布页手动下载。";
                return;
            }

            // 新版本已经在跑，当前窗口退出即可。
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch
        {
            UpdateStatusText = "更新失败，请到发布页手动下载。";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasePageUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开浏览器失败不影响关于页。
        }
    }

    [RelayCommand]
    private void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = $"打开系统设置失败：{GetErrorMessage(exception)}";
        }
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/refout/MailDog")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开浏览器失败时不影响关于页。
        }
    }

    [RelayCommand]
    private void ToggleThirdPartyNotices()
    {
        IsThirdPartyNoticesExpanded = !IsThirdPartyNoticesExpanded;
    }

    [RelayCommand]
    private async Task RefreshStorageAsync()
    {
        if (IsStorageBusy)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatus = "正在读取本地数据…";

        try
        {
            await LoadStorageSummaryCoreAsync();
            await LoadOutboxAsync();
            StorageStatus = "本地数据统计已更新。";
        }
        catch (Exception exception)
        {
            StorageStatus = $"读取失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    private async Task LoadStorageSummaryCoreAsync()
    {
        var response = await _engine.RequestAsync<StorageSummaryResponse>(
            IpcMessageTypes.GetStorageSummary,
            new GetStorageSummaryRequest(),
            TimeSpan.FromSeconds(30));

        StorageSummary = response.Summary;
    }

    private async Task LoadOutboxAsync()
    {
        var response = await _engine.RequestAsync<OutboxListResponse>(
            IpcMessageTypes.ListOutbox,
            new ListOutboxRequest(),
            TimeSpan.FromSeconds(30));

        OutboxItems.Clear();
        foreach (var item in response.Items)
        {
            OutboxItems.Add(new OutboxItemViewModel(item));
        }

        OnPropertyChanged(nameof(HasOutboxItems));
        OutboxStatus = OutboxItems.Count == 0
            ? "没有待发送或发送失败的邮件。"
            : $"共 {OutboxItems.Count} 封待发送/失败邮件。";
    }

    public async Task RefreshOutboxAsync()
    {
        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;
        try
        {
            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"读取发件箱失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    public async Task RetryOutboxAsync(OutboxItemViewModel item)
    {        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;
        OutboxStatus = "正在重试发送…";

        try
        {
            var response = await _engine.RequestAsync<SendMailResponse>(
                IpcMessageTypes.RetryOutbox,
                new RetryOutboxRequest { OutboxId = item.Id },
                TimeSpan.FromSeconds(75));

            OutboxStatus = response.Sent
                ? "邮件已发送。"
                : response.Queued
                    ? $"仍在发件箱：{response.Error ?? "稍后自动重试"}"
                    : $"发送失败：{response.Error ?? "未知错误"}";

            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"重试失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    public async Task CreateBackupAsync(string destinationPath)
    {
        if (IsBackupBusy)
        {
            return;
        }

        IsBackupBusy = true;
        BackupStatus = "正在创建数据库备份…";
        try
        {
            var response = await _engine.RequestAsync<BackupResponse>(
                IpcMessageTypes.CreateBackup,
                new CreateBackupRequest
                {
                    DestinationPath = destinationPath,
                    Password = string.IsNullOrWhiteSpace(BackupPassword) ? null : BackupPassword
                },
                TimeSpan.FromSeconds(120));

            BackupStatus = response.Result.Encrypted
                ? $"加密备份已保存：{response.Result.Path}（{FormatStorageSize(response.Result.Size)}）"
                : $"备份已保存：{response.Result.Path}（{FormatStorageSize(response.Result.Size)}）";
            BackupPassword = string.Empty;
            await LoadStorageSummaryCoreAsync();
        }
        catch (Exception exception)
        {
            BackupStatus = $"备份失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    public async Task RestoreBackupAsync(string archivePath)
    {
        if (IsBackupBusy)
        {
            return;
        }

        IsBackupBusy = true;
        BackupStatus = "正在校验备份…";
        try
        {
            var response = await _engine.RequestAsync<RestoreResponse>(
                IpcMessageTypes.RestoreBackup,
                new RestoreBackupRequest
                {
                    ArchivePath = archivePath,
                    Password = string.IsNullOrWhiteSpace(BackupPassword) ? null : BackupPassword
                },
                TimeSpan.FromSeconds(60));

            BackupStatus = response.Result.RestartRequired
                ? "恢复已安排。请从托盘完全退出 MailDog 后重新打开，恢复将在下次启动时生效；当前数据库会先自动保留一份副本。"
                : "恢复已完成。";
            BackupPassword = string.Empty;
        }
        catch (Exception exception)
        {
            BackupStatus = $"恢复失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    /// <summary>把当前邮件或整个会话渲染成适合打印的独立 HTML；正文沿用阅读区的净化流程。</summary>
    public async Task<string?> BuildPrintHtmlAsync(bool includeThread)
    {
        if (SelectedMessage is not { } selected)
        {
            return null;
        }

        var targets = includeThread && selected.ThreadCount > 1
            ? selected.ThreadMessages.OrderBy(message => message.Date).ToArray()
            : [selected];

        var content = new System.Text.StringBuilder(targets.Length * 4096);

        foreach (var message in targets)
        {
            try
            {
                var response = await _engine.RequestAsync<MessageBodyResponse>(
                    IpcMessageTypes.GetMessageBody,
                    new GetMessageBodyRequest { MessageId = message.Id },
                    TimeSpan.FromSeconds(IpcProtocol.BodyRequestTimeoutSeconds));

                var body = response.Body;
                var sanitized = _sanitizer.Sanitize(body.Html, _allowRemoteImages);
                var hasHtml = !string.IsNullOrWhiteSpace(sanitized.Html);

                content.Append("<section class=\"maildog-thread-message\">")
                    .Append("<div class=\"maildog-print-subject\">")
                    .Append(System.Net.WebUtility.HtmlEncode(
                        string.IsNullOrWhiteSpace(body.Subject) ? "（无主题）" : body.Subject))
                    .Append("</div>")
                    .Append("<div class=\"maildog-print-meta\">")
                    .Append(System.Net.WebUtility.HtmlEncode(
                        string.IsNullOrWhiteSpace(body.FromName) ? body.FromAddress : body.FromName))
                    .Append(" &lt;")
                    .Append(System.Net.WebUtility.HtmlEncode(body.FromAddress))
                    .Append("&gt; · ")
                    .Append(System.Net.WebUtility.HtmlEncode(body.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
                    .Append("</div>");

                if (body.Attachments.Length > 0)
                {
                    content.Append("<div class=\"maildog-print-meta\">附件：")
                        .Append(System.Net.WebUtility.HtmlEncode(
                            string.Join("、", body.Attachments.Select(attachment => attachment.FileName))))
                        .Append("</div>");
                }

                content.Append("<div class=\"maildog-print-body\">");
                if (hasHtml)
                {
                    content.Append(sanitized.Html);
                }
                else
                {
                    var plain = string.IsNullOrWhiteSpace(body.PlainText)
                        ? "此邮件没有可显示的正文。"
                        : body.PlainText;
                    content.Append("<pre class=\"maildog-print-plain\">")
                        .Append(System.Net.WebUtility.HtmlEncode(plain))
                        .Append("</pre>");
                }

                content.Append("</div></section>");
            }
            catch (Exception exception)
            {
                content.Append("<section class=\"maildog-thread-message\"><p class=\"maildog-print-error\">")
                    .Append(System.Net.WebUtility.HtmlEncode($"这封邮件正文加载失败：{GetErrorMessage(exception)}"))
                    .Append("</p></section>");
            }
        }

        return _builder.Build(
            content.ToString(),
            darkMode: false,
            allowRemoteImages: _allowRemoteImages,
            bodyBackground: null,
            extraCss: PrintCss);
    }

    private const string PrintCss = """
        @page { size: A4; margin: 12mm; }
        .maildog-thread-message { margin: 0 0 22px; padding-bottom: 16px; border-bottom: 1px solid #dddddd; }
        .maildog-thread-message:last-child { border-bottom: none; }
        .maildog-print-subject { font-size: 18px; font-weight: 600; line-height: 1.35; margin: 0 0 6px; }
        .maildog-print-meta { color: #5c5c5c; font-size: 12px; margin: 0 0 12px; }
        .maildog-print-body { font-size: 14px; }
        .maildog-print-plain { white-space: pre-wrap; background: #f6f6f6; padding: 12px; border-radius: 6px; }
        .maildog-print-error { color: #a4262c; }
        @media print {
          body { padding: 0; background: #ffffff; }
          .maildog-mail { padding: 0; border-radius: 0; }
          a { color: #000000; text-decoration: underline; }
        }
        """;

    public async Task ExportSelectedMessageAsync(string destinationPath)
    {
        if (SelectedMessage is not { } message)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<MessageExportResponse>(
                IpcMessageTypes.ExportMessage,
                new ExportMessageRequest
                {
                    MessageId = message.Id,
                    DestinationPath = destinationPath
                },
                TimeSpan.FromSeconds(120));

            StatusMessage = $"已导出邮件：{response.Path}（{FormatStorageSize(response.Size)}）";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public async Task DeleteOutboxAsync(OutboxItemViewModel item)
    {
        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteOutbox,
                new DeleteOutboxRequest { OutboxId = item.Id });

            OutboxStatus = "已从发件箱删除。";
            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"删除失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    private async Task CleanStorageAsync(CleanStorageRequest request)
    {
        if (IsStorageBusy)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatus = "正在清理…";

        try
        {
            var response = await _engine.RequestAsync<StorageCleanupResponse>(
                IpcMessageTypes.CleanStorage,
                request,
                TimeSpan.FromSeconds(60));

            try
            {
                await LoadStorageSummaryCoreAsync();
            }
            catch
            {
                // 清理已成功，统计刷新失败不应改变清理结果。
            }

            var skipped = response.Result.SkippedFiles > 0
                ? $"，{response.Result.SkippedFiles} 个文件被占用未清理"
                : string.Empty;
            StorageStatus = $"{response.Result.Message} 释放 {FormatStorageSize(response.Result.FreedBytes)}{skipped}";
        }
        catch (Exception exception)
        {
            StorageStatus = $"清理失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    [RelayCommand]
    private Task CleanAttachmentCacheAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanAttachmentCache = true });

    [RelayCommand]
    private Task CleanLogsAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanLogs = true });

    [RelayCommand]
    private Task CleanTempFilesAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanTempFiles = true });

    [RelayCommand]
    private Task CompactDatabaseAsync()
        => CleanStorageAsync(new CleanStorageRequest { CompactDatabase = true });

    public async Task ImportMailboxAsync(IReadOnlyList<string> paths)
    {
        if (IsMigrationBusy || paths.Count == 0)
        {
            return;
        }

        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        var inbox = account?.Folders.FirstOrDefault(folder => folder.Role == FolderRole.Inbox)
            ?? account?.Folders.FirstOrDefault();
        if (account is null || inbox is null)
        {
            MigrationStatus = "请先添加账户并同步文件夹。";
            return;
        }

        IsMigrationBusy = true;
        MigrationStatus = $"正在导入 {paths.Count} 个文件…";

        try
        {
            var response = await _engine.RequestAsync<MailboxImportResponse>(
                IpcMessageTypes.ImportMailbox,
                new ImportMailboxRequest
                {
                    AccountId = account.Id,
                    FolderId = inbox.Id,
                    Paths = paths.ToArray()
                },
                TimeSpan.FromMinutes(10));

            var errorText = response.Result.Errors.Length > 0
                ? $" 示例：{string.Join("；", response.Result.Errors.Take(3))}"
                : string.Empty;
            MigrationStatus =
                $"{response.Result.Message}（目标：{account.DisplayName} / {inbox.Name}）{errorText}";
        }
        catch (Exception exception)
        {
            MigrationStatus = $"导入失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsMigrationBusy = false;
        }
    }

    public async Task ExportMailboxAsync(string destinationPath)
    {
        if (IsMigrationBusy)
        {
            return;
        }

        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        if (account is null)
        {
            MigrationStatus = "请先添加账户。";
            return;
        }

        IsMigrationBusy = true;
        MigrationStatus = $"正在导出 {account.DisplayName} 的全部邮件…";

        try
        {
            var response = await _engine.RequestAsync<MailboxExportResponse>(
                IpcMessageTypes.ExportMailbox,
                new ExportMailboxRequest
                {
                    AccountId = account.Id,
                    DestinationPath = destinationPath
                },
                TimeSpan.FromMinutes(30));

            MigrationStatus =
                $"{response.Result.Message} 文件大小 {FormatStorageSize(response.Result.Size)}，"
                + $"路径：{response.Result.Path}";
        }
        catch (Exception exception)
        {
            MigrationStatus = $"导出失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsMigrationBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StorageStatus = "目录不存在或尚未读取。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StorageStatus = $"打开目录失败：{GetErrorMessage(exception)}";
        }
    }

}
