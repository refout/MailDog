using System.Runtime.InteropServices;

namespace MailDog.UI.Services;

/// <summary>在 UI 进程内试听系统提示音，与 Engine 通知使用同一组别名。</summary>
internal static class SystemSoundPlayer
{
    private const uint SndAlias = 0x00010000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

    public static void Play(string sound)
    {
        PlaySound(GetAlias(sound), IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
    }

    private static string GetAlias(string sound)
        => sound switch
        {
            "mail" => "SystemMailBeep",
            "reminder" => "SystemExclamation",
            "asterisk" => "SystemAsterisk",
            "warning" => "SystemHand",
            "beep" => "SystemBeep",
            "question" => "SystemQuestion",
            "start" => "SystemStart",
            _ => "SystemNotification"
        };
}
