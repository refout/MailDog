using System.Globalization;
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
        // 界面为中文，固定区域设置，避免日期控件与排序出现英文或异形占位。
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        using var mutex = new Mutex(true, "Local\\MailDog.UI.Singleton", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            NativeBootstrap.EnsureLoaded();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                // 优先 ANGLE 以启用透明窗口与云母/亚克力材质，加载失败时回退软件渲染。
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
            });
        }

        return builder.LogToTrace(LogEventLevel.Warning);
    }
}
