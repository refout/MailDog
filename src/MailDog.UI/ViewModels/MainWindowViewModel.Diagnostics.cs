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
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (IsExportingDiagnostics)
        {
            return;
        }

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        if (window is null)
        {
            return;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 MailDog 诊断包",
            SuggestedFileName = $"MailDog-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        IsExportingDiagnostics = true;
        DiagnosticsStatus = "正在整理诊断包…";

        try
        {
            AccountHealthDto[] health = [];

            if (_engine.IsConnected)
            {
                try
                {
                    health = (await _engine.RequestAsync<AccountHealthResponse>(
                        IpcMessageTypes.GetAccountHealth,
                        new GetAccountHealthRequest()).ConfigureAwait(true)).Items;
                }
                catch
                {
                    // 健康快照取不到时仍然导出日志与配置。
                }
            }

            var accounts = Accounts
                .Select(account => new DiagnosticsExporter.AccountSnapshot(
                    account.Id,
                    account.Email,
                    account.DisplayName,
                    account.StatusText))
                .ToArray();

            var settings = BuildSettings();

            await Task.Run(() => DiagnosticsExporter.Export(
                path,
                AppVersion,
                settings,
                accounts,
                health)).ConfigureAwait(true);

            DiagnosticsStatus = $"已导出 {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            DiagnosticsStatus = $"导出失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    [RelayCommand]
    private async Task DownloadCliAsync()
    {
        if (IsDownloadingCli)
        {
            return;
        }

        var bundle = CliBundle.Find();

        if (bundle is null)
        {
            CliDownloadStatus = "当前构建没有附带 CLI 包，请使用发布版安装包。";
            return;
        }

        IsDownloadingCli = true;
        CliDownloadStatus = "正在下载并解压到安装目录…";

        var destination = CliBundle.DownloadPath(AppContext.BaseDirectory, AppVersion);

        try
        {
            await Task.Run(() => File.Copy(bundle, destination, true)).ConfigureAwait(true);

            try
            {
                await Task.Run(() => CliBundle.Extract(destination, AppContext.BaseDirectory)).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                CliDownloadStatus = $"已下载，但解压失败：{GetErrorMessage(exception)}；压缩包保留在 {destination}";
                OnPropertyChanged(nameof(HasCliDownload));
                OnPropertyChanged(nameof(CliAgentInfo));
                return;
            }

            try
            {
                File.Delete(destination);
            }
            catch
            {
                // 解压成功后压缩包删不掉就留着；下次下载会覆盖。
            }

            CliDownloadStatus = $"已解压到安装目录：{ProductPaths.CliExecutableName}、{(OperatingSystem.IsWindows() ? "maildog.cmd" : "maildog")}、maildog-cli/";
            CliCopyStatus = string.Empty;
            OnPropertyChanged(nameof(HasCliDownload));
            OnPropertyChanged(nameof(CliAgentInfo));
        }
        catch (Exception exception)
        {
            CliDownloadStatus = $"下载失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsDownloadingCli = false;
        }
    }

    /// <summary>给外部智能体复制一段可直接使用 CLI 的说明；不包含账号、口令或邮件内容。</summary>
    public string BuildCliAgentInfo()
    {
        var directory = AppContext.BaseDirectory;
        var bundle = CliDownloadBundlePath;
        var cli = Path.Combine(directory, ProductPaths.CliExecutableName);
        var shim = Path.Combine(directory, OperatingSystem.IsWindows() ? "maildog.cmd" : "maildog");
        var skill = Path.Combine(directory, "maildog-cli", "SKILL.md");
        var installed = File.Exists(cli) && File.Exists(shim) && File.Exists(skill);
        var hasBundle = File.Exists(bundle);
        var quote = "\"";
        var lines = new List<string>
        {
            $"MailDog CLI（版本 {AppVersion}）",
            $"安装目录：{directory}"
        };

        if (hasBundle)
        {
            lines.Add($"下载包：{bundle}");
        }

        if (installed)
        {
            lines.Add("当前文件已解压，可以直接使用。");
        }
        else if (hasBundle)
        {
            lines.Add("当前文件尚未解压，请先把下载包解压到安装目录，保持包内目录结构。");
            lines.Add("解压命令：");
            lines.Add(
                OperatingSystem.IsWindows()
                    ? $"Expand-Archive -LiteralPath {quote}{bundle}{quote} -DestinationPath {quote}{directory}{quote} -Force"
                    : $"tar -xzf {quote}{bundle}{quote} -C {quote}{directory}{quote}");
        }
        else
        {
            lines.Add("当前文件尚未解压，下载包也不在安装目录；请先在工具页重新下载。");
        }

        lines.Add(string.Empty);
        lines.Add($"CLI：{cli}");
        lines.Add($"启动脚本：{shim}");
        lines.Add($"Skill：{skill}");
        lines.Add(string.Empty);
        lines.Add("给智能体的使用约束：");
        lines.Add($"1. 先运行 {quote}{shim}{quote} schema --json 确认命令、参数和字段。");
        lines.Add("2. accounts / folders / list / search / show / attachment 可以直接执行；draft / send / mark / move 默认只预览，等用户明确确认后再加 --confirm；cancel 会立即撤回还没发出的邮件，也要先经用户确认。");
        lines.Add("3. send 返回退出码 6 时，把附件缺失或过大的提醒转述给用户；用户确认后才加 --force-send。");
        lines.Add("4. show 直接返回原始正文：--json 同时给出 text（原始纯文本）与 html（原始 HTML），缺失为 null，CLI 不做 HTML 转文本；默认全文，需要截断时显式加 --max-chars。--html 只是强制人读输出 HTML。");
        lines.Add("5. 不要直接读写 MailDog 的 SQLite 数据库、凭据文件或日志。客户端会在 logs 目录记录调用摘要，带 --json 时还会保存当时的 JSON 输出（可能含邮件正文）；这些记录只供用户在界面查看，智能体不要把日志当成邮件数据源。");
        lines.Add("6. 只引用与当前问题相关的邮件片段，不要把整封邮件或验证码、账单信息倒进对话。");
        lines.Add("7. 安装升级可能清掉安装目录顶层的 CLI；升级后重新下载即可。");

        return string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task RefreshAccountHealthAsync()
    {
        if (!_engine.IsConnected)
        {
            AccountHealthStatus = "后台尚未连接。";
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<AccountHealthResponse>(
                IpcMessageTypes.GetAccountHealth,
                new GetAccountHealthRequest()).ConfigureAwait(true);

            AccountHealth.Clear();

            foreach (var item in response.Items)
            {
                AccountHealth.Add(new AccountHealthViewModel(item));
            }

            HasAccountHealth = AccountHealth.Count > 0;
            AccountHealthStatus = HasAccountHealth ? string.Empty : "还没有账户。";
        }
        catch (Exception exception)
        {
            AccountHealthStatus = $"读取失败：{GetErrorMessage(exception)}";
        }
    }

}
