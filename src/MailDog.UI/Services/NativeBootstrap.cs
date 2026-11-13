using System.Runtime.InteropServices;

namespace MailDog.UI.Services;

/// <summary>
/// 将嵌入的 Skia / HarfBuzz 原生库解压到本地并注册 DllImport 解析器。
/// Native AOT 不会把第三方原生库打入单文件；ANGLE 不携带，Windows 渲染显式走软件模式。
/// </summary>
internal static class NativeBootstrap
{
    private const string SkiaResource = "MailDog.UI.Native.libSkiaSharp.dll";
    private const string HarfBuzzResource = "MailDog.UI.Native.libHarfBuzzSharp.dll";

    private static int _state;

    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _state, 1) == 1)
        {
            return;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog",
            "native",
            "ui");

        Directory.CreateDirectory(directory);

        var skiaPath = Extract(SkiaResource, Path.Combine(directory, "libSkiaSharp.dll"));
        var harfBuzzPath = Extract(HarfBuzzResource, Path.Combine(directory, "libHarfBuzzSharp.dll"));

        var skiaHandle = NativeLibrary.Load(skiaPath);
        var harfBuzzHandle = NativeLibrary.Load(harfBuzzPath);

        NativeLibrary.SetDllImportResolver(
            typeof(SkiaSharp.SKImageInfo).Assembly,
            (name, _, _) => IsLibrary(name, "libSkiaSharp") ? skiaHandle : IntPtr.Zero);

        NativeLibrary.SetDllImportResolver(
            typeof(HarfBuzzSharp.Blob).Assembly,
            (name, _, _) => IsLibrary(name, "libHarfBuzzSharp") ? harfBuzzHandle : IntPtr.Zero);
    }

    private static string Extract(string resourceName, string target)
    {
        var assembly = typeof(NativeBootstrap).Assembly;
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"未找到嵌入的原生库：{resourceName}");

        if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
        {
            return target;
        }

        var temp = target + ".tmp";
        using (var file = File.Create(temp))
        {
            resource.CopyTo(file);
        }

        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (IOException) when (File.Exists(target))
        {
            File.Delete(temp);
        }

        return target;
    }

    private static bool IsLibrary(string name, string expected)
        => name.Equals(expected, StringComparison.OrdinalIgnoreCase)
           || name.Equals(expected + ".dll", StringComparison.OrdinalIgnoreCase);
}
