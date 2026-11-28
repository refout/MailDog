using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MailDog.Contracts.Platform;
using MailDog.UI.Services;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class MainWindow : Window
{
    private async void OnCreateBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "创建 MailDog 备份",
            SuggestedFileName = $"MailDog-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await _viewModel.CreateBackupAsync(path);
        }
    }

    private async void OnRestoreBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择 MailDog 备份",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("MailDog 备份")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            await _viewModel.RestoreBackupAsync(path);
        }
    }

    private async void OnExportPdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ExportPdfAsync(includeThread: false);

    private async void OnExportThreadPdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ExportPdfAsync(includeThread: true);

    private async Task ExportPdfAsync(bool includeThread)
    {
        if (_viewModel.SelectedMessage is not { } message)
        {
            return;
        }

        var name = SanitizeFileName(message.Subject);
        if (includeThread)
        {
            name += "-会话";
        }

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = includeThread ? "导出会话为 PDF" : "导出邮件为 PDF",
            SuggestedFileName = $"{name}.pdf",
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _viewModel.StatusMessage = "正在准备 PDF…";
        try
        {
            var html = await _viewModel.BuildPrintHtmlAsync(includeThread);
            if (html is null)
            {
                _viewModel.StatusMessage = "没有可导出的邮件。";
                return;
            }

            await RenderPdfAsync(html, path);
            _viewModel.HasError = false;
            _viewModel.StatusMessage = $"已导出 PDF：{path}";
        }
        catch (Exception exception)
        {
            _viewModel.HasError = true;
            _viewModel.StatusMessage = $"导出 PDF 失败：{exception.Message}";
        }
    }

    private async void OnPrintMessageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ShowPrintPreviewAsync(includeThread: false);

    private async void OnPrintThreadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ShowPrintPreviewAsync(includeThread: true);

    private async Task ShowPrintPreviewAsync(bool includeThread)
    {
        _viewModel.StatusMessage = "正在准备打印内容…";
        Window? printWindow = null;
        try
        {
            var html = await _viewModel.BuildPrintHtmlAsync(includeThread);
            if (html is null)
            {
                _viewModel.StatusMessage = "没有可打印的邮件。";
                return;
            }

            var context = CreatePrintWindow(visible: true);
            printWindow = context.Window;
            await WaitForAdapterAsync(context);
            await NavigateAndWaitAsync(context.WebView, html);

            context.WebView.ShowPrintUI();
            printWindow = null;
            _viewModel.StatusMessage = "打印窗口已打开；完成打印后关闭预览窗口。";
        }
        catch (Exception exception)
        {
            printWindow?.Close();
            _viewModel.HasError = true;
            _viewModel.StatusMessage = $"打印失败：{exception.Message}";
        }
    }

    private async Task RenderPdfAsync(string html, string destinationPath)
    {
        var context = CreatePrintWindow(visible: false);
        try
        {
            await WaitForAdapterAsync(context);
            await NavigateAndWaitAsync(context.WebView, html);
            await using var pdf = await context.WebView.PrintToPdfStreamAsync();
            await using var output = File.Create(destinationPath);
            await pdf.CopyToAsync(output);
        }
        finally
        {
            context.Window.Close();
        }
    }

    private sealed record PrintWindowContext(
        Window Window,
        NativeWebView WebView,
        Task AdapterReady);

    private static PrintWindowContext CreatePrintWindow(bool visible)
    {
        var webView = new NativeWebView();
        var userDataFolder = Path.Combine(
            ProductPaths.DataDirectory,
            "WebView2Print",
            Guid.NewGuid().ToString("N"));
        webView.EnvironmentRequested += (_, args) => ConfigurePrintEnvironment(args, userDataFolder);

        var adapterReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        webView.AdapterCreated += (_, _) => adapterReady.TrySetResult(true);

        var window = new Window
        {
            Title = visible ? "MailDog 打印预览" : "MailDog PDF",
            Width = visible ? 1000 : 900,
            Height = visible ? 800 : 700,
            CanResize = visible,
            ShowInTaskbar = visible,
            ShowActivated = visible,
            WindowStartupLocation = visible
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.Manual,
            Position = visible ? default : new PixelPoint(-32000, -32000),
            Content = webView
        };
        window.Closed += (_, _) => ScheduleDeleteDirectory(userDataFolder);

        window.Show();
        return new PrintWindowContext(window, webView, adapterReady.Task);
    }

    private static void ConfigurePrintEnvironment(
        WebViewEnvironmentRequestedEventArgs args,
        string userDataFolder)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            Directory.CreateDirectory(userDataFolder);
            windows.UserDataFolder = userDataFolder;
            windows.ProfileName = "MailDogPrint";
            windows.Language = "zh-CN";
        }
    }

    private static async Task WaitForAdapterAsync(PrintWindowContext context)
    {
        try
        {
            await context.AdapterReady.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            var reason = context.WebView.AdapterInfo?.ToString();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(reason)
                    ? "WebView 初始化超时。"
                    : $"WebView 初始化失败：{reason}");
        }
    }

    private static void ScheduleDeleteDirectory(string path)
    {
        _ = Task.Run(async () =>
        {
            // WebView2 的浏览器进程通常会在窗口关闭后几秒内退出，等一会儿再删用户数据目录。
            await Task.Delay(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(path);
        });
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // WebView2 可能还会短暂占用用户数据目录；临时目录留给系统清理即可。
        }
    }

    private static async Task NavigateAndWaitAsync(NativeWebView webView, string html)
    {
        if (!webView.IsLoaded)
        {
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
                => loaded.TrySetResult(true);

            webView.Loaded += OnLoaded;
            try
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                webView.Loaded -= OnLoaded;
            }
        }

        // WebView 的原生子控件会在 Loaded 之后异步创建，留一拍再导航，避免首次加载丢请求。
        await Task.Delay(100);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(new InvalidOperationException("打印内容加载失败。"));
            }
        }

        webView.NavigationCompleted += OnCompleted;
        try
        {
            webView.NavigateToString(html);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            webView.NavigationCompleted -= OnCompleted;
        }
    }

    private static string SanitizeFileName(string? subject)
    {
        var name = string.IsNullOrWhiteSpace(subject) ? "maildog-message" : subject.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length > 90 ? name[..90] : name;
    }

}
