using System.Runtime.InteropServices;
using System.Text;

namespace MailDog.Engine.Tray;

/// <summary>
/// 把"通知被点击"从新起的进程送回已经在运行的 Engine：用 WM_COPYDATA 直接写到托盘隐藏窗口，
/// 不必为一次点击再开一条命名管道。
/// </summary>
internal static class ToastActivation
{
    public const string WindowClassName = "MailDog.Tray";
    public const string WindowTitle = "MailDog";

    /// <summary>标识符，用来区分这是我们自己发的数据。</summary>
    public const uint PayloadMarker = 0x4D44; // "MD"

    public const uint CopyDataMessage = 0x004A;

    public static bool Send(string uri)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        var bytes = Encoding.Unicode.GetBytes(uri);
        var buffer = Marshal.AllocHGlobal(bytes.Length + 2);

        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteInt16(buffer, bytes.Length, 0);

            var payload = new CopyData
            {
                Marker = (UIntPtr)PayloadMarker,
                ByteCount = (uint)(bytes.Length + 2),
                Data = buffer
            };

            // Engine 可能刚被系统拉起、托盘窗口还没建好，这里等一会儿再找。
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var window = FindWindow(WindowClassName, WindowTitle);

                if (window != IntPtr.Zero)
                {
                    return SendMessage(window, CopyDataMessage, IntPtr.Zero, ref payload) != IntPtr.Zero;
                }

                Thread.Sleep(200);
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 与 Win32 的 COPYDATASTRUCT 逐字段对齐：dwData 在 64 位下是 ULONG_PTR（8 字节），
    /// 若写成 uint 会让后面的 cbData / lpData 全部错位，读出来就是野指针。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData
    {
        public UIntPtr Marker;
        public uint ByteCount;
        public IntPtr Data;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, ref CopyData lParam);
}
