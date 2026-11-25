using System.Runtime.InteropServices;

namespace MailDog.UI.Services;

/// <summary>
/// Linux 的 HTML 阅读依赖系统 WebKitGTK（Avalonia 的 Linux WebView 默认先找 WPE 2.0，
/// 找不到时可回退 WebKitGTK）。没有运行库时保持纯文本，不创建 WebView 控件。
/// </summary>
internal static class HtmlRendering
{
    private static readonly Lazy<bool> s_available = new(Resolve);

    public static bool IsAvailable => s_available.Value;

    private static bool Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return TryLoad("libwebkit2gtk-4.1.so.0")
            || TryLoad("libwebkit2gtk-4.0.so.37")
            || TryLoad("libWPEWebKit-2.0.so.1");
    }

    private static bool TryLoad(string name)
    {
        try
        {
            if (!NativeLibrary.TryLoad(name, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
