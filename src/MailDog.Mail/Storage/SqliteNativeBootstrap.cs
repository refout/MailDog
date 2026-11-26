using System.Runtime.InteropServices;
using MailDog.Contracts.Platform;

namespace MailDog.Mail.Storage;

/// <summary>
/// 将嵌入的 e_sqlite3 原生库解压到本地，并为其注册 DllImport 解析器。
/// Native AOT 不会把第三方原生库打入单文件，因此必须显式处理，否则部署产物会缺少 e_sqlite3.dll。
/// </summary>
public static class SqliteNativeBootstrap
{
    private const string LibraryName = "e_sqlite3";

    private static readonly string ResourceName = OperatingSystem.IsWindows()
        ? "MailDog.Mail.Native.e_sqlite3.dll"
        : "MailDog.Mail.Native.libe_sqlite3.so";

    private static readonly string TargetFileName = OperatingSystem.IsWindows()
        ? "e_sqlite3.dll"
        : "libe_sqlite3.so";

    private static int _state;

    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _state, 1) == 1)
        {
            return;
        }

        var assembly = typeof(SqliteNativeBootstrap).Assembly;
        using var resource = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"未找到嵌入的 SQLite 原生库：{ResourceName}");

        var directory = Path.Combine(ProductPaths.DataDirectory, "native");

        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, TargetFileName);
        ExtractIfNeeded(resource, target);

        var handle = NativeLibrary.Load(target);
        var providerAssembly = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;

        NativeLibrary.SetDllImportResolver(providerAssembly, (name, _, _) =>
            string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase)
                ? handle
                : IntPtr.Zero);
    }

    private static void ExtractIfNeeded(Stream resource, string target)
    {
        if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
        {
            return;
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
            // 另一个进程已经写入同名文件，直接使用现有副本。
            File.Delete(temp);
        }
    }
}
