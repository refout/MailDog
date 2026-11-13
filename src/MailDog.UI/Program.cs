using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Win32;
using MailDog.UI.Services;

namespace MailDog.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        NativeBootstrap.EnsureLoaded();

        var tracePath = Path.Combine(Path.GetTempPath(), "maildog-ui-trace.log");
        Trace.Listeners.Add(new TextWriterTraceListener(tracePath));
        Trace.AutoFlush = true;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // 不携带 ANGLE，Windows 上固定使用 Skia 软件渲染，避免依赖 av_libGLESv2.dll。
                RenderingMode = new[] { Win32RenderingMode.Software }
            })
            .LogToTrace(LogEventLevel.Debug);
}
