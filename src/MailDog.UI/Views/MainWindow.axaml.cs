using Avalonia.Controls;
using Avalonia.Platform;

namespace MailDog.UI.Views;

public partial class MainWindow : Window
{
    private bool _isRendered;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "maildog-ui.log");

    private const string ProbeHtml = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <style>
            body {
              margin: 0;
              padding: 32px;
              font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif;
              color: #1b1b1b;
              background: #ffffff;
            }
            h1 { font-size: 24px; font-weight: 600; margin: 0 0 8px; }
            p { font-size: 14px; line-height: 1.6; color: #616161; margin: 0 0 4px; }
          </style>
        </head>
        <body>
          <h1>MailDog WebView probe</h1>
          <p>Native AOT + Avalonia 12 + WebView2</p>
          <p>如果你看到这段内容，渲染链路是通的。</p>
        </body>
        </html>
        """;

    public MainWindow()
    {
        InitializeComponent();
        Log("window constructed");

        WebView.EnvironmentRequested += OnEnvironmentRequested;
        WebView.AdapterCreated += OnAdapterCreated;
        WebView.AdapterDestroyed += OnAdapterDestroyed;
        WebView.NavigationStarted += (_, _) => Log("navigation started");
        WebView.NavigationCompleted += (_, args) => Log($"navigation completed: success={args.IsSuccess}");
        Closed += OnClosed;
        Loaded += (_, _) => Log("window loaded");
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailDog",
                "WebView2");

            Directory.CreateDirectory(folder);

            windows.UserDataFolder = folder;
            windows.ProfileName = "MailDog";
            windows.Language = "zh-CN";
            Log($"WebView2 user data folder: {folder}");
        }
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        Log("adapter created");

        if (_isRendered)
        {
            return;
        }

        _isRendered = true;

        try
        {
            WebView.NavigateToString(ProbeHtml);
            Log("NavigateToString called");
        }
        catch (Exception exception)
        {
            Log($"NavigateToString failed: {exception}");
        }
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        Log("adapter destroyed");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // NativeWebView 在 12.1.2 中没有公开 Dispose()；
        // 原生适配器随窗口关闭与视觉树卸载释放，这里只负责退订事件。
        Log("window closed");
        WebView.EnvironmentRequested -= OnEnvironmentRequested;
        WebView.AdapterCreated -= OnAdapterCreated;
        WebView.AdapterDestroyed -= OnAdapterDestroyed;
        Closed -= OnClosed;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不应影响 UI。
        }
    }
}
