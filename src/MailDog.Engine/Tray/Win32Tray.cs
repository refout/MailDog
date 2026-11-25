using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MailDog.Engine.Tray;

public sealed class Win32Tray : IAppTray
{
    private const uint WM_TRAYICON = NativeMethods.WM_APP + 1;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint NIN_BALLOONUSERCLICK = 0x0405;
    private const int CommandOpen = 1;
    private const int CommandSync = 2;
    private const int CommandExit = 3;
    private const int CommandCheckUpdate = 4;
    private const uint SndAlias = 0x00010000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    private readonly Action _openUi;
    private readonly Action _openAbout;
    private readonly Action _notificationClicked;
    private readonly Action<string> _activated;
    private readonly Action _syncAll;
    private readonly Action _resumed;
    private readonly Action _exit;
    private readonly Func<bool> _isDarkTheme;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly AvatarIconFactory _avatars = new();
    private readonly IntPtr _className;

    private IntPtr _window;
    private IntPtr _icon;
    private IntPtr _largeIcon;
    private int _iconSize = 16;
    private int _pendingUnread;
    private bool _iconOwned;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private string _tooltip = "MailDog";
    private bool _disposed;

    public Win32Tray(
        Action openUi,
        Action openAbout,
        Action notificationClicked,
        Action<string> activated,
        Action syncAll,
        Action resumed,
        Action exit,
        Func<bool> isDarkTheme)
    {
        _openUi = openUi;
        _openAbout = openAbout;
        _notificationClicked = notificationClicked;
        _activated = activated;
        _syncAll = syncAll;
        _resumed = resumed;
        _exit = exit;
        _isDarkTheme = isDarkTheme;
        _wndProc = WindowProc;
        _className = Marshal.StringToHGlobalUni(ToastActivation.WindowClassName);
    }

    public void Run(bool hideConsole)
    {
        if (hideConsole)
        {
            var console = NativeMethods.GetConsoleWindow();
            if (console != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(console, 0);
            }
        }

        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _className
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "注册托盘窗口类失败。");
        }

        // 用隐藏的顶层窗口（而不是 message-only 窗口）才能收到 Broadcast：
        // TaskbarCreated 与 WM_POWERBROADCAST 都不会发给 message-only 窗口。
        _window = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW,
            ToastActivation.WindowClassName,
            ToastActivation.WindowTitle,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            windowClass.hInstance,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建托盘窗口失败。");
        }

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _iconSize = GetSmallIconSize();
        var appIcon = LoadAppIcon(_iconSize);
        _icon = appIcon.Small;
        _largeIcon = appIcon.Large;
        _iconOwned = appIcon.Owned;
        AddIcon();

        try
        {
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            RemoveIcon();
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        _tooltip = tooltip;

        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_TIP;
        data.szTip = _tooltip;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    public void UpdateUnreadCount(int unreadCount)
    {
        // Run() 在后台线程里才加载图标，启动瞬间外层可能先调用到这里；
        // 先记下未读数，AddIcon() 完成后再补上红点，否则有未读也不会显示。
        _pendingUnread = unreadCount;

        if (!_iconAdded)
        {
            return;
        }

        ApplyUnreadIcon();
    }

    private void ApplyUnreadIcon()
    {
        var dot = _pendingUnread > 0 ? _avatars.GetUnreadDotIcon(_icon, _iconSize) : IntPtr.Zero;
        var icon = dot != IntPtr.Zero ? dot : _icon;

        if (icon == IntPtr.Zero)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_ICON;
        data.hIcon = icon;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    public void ShowNotification(
        string title,
        string message,
        bool playSound = true,
        string? sound = null,
        string? senderName = null,
        string? senderAddress = null,
        string? preview = null,
        string? activationUri = null,
        string? tag = null)
    {
        // 优先走正式 toast：只有 toast 会落进通知中心，也才有头部应用图标。
        var avatarPath = _avatars.GetAvatarPng(senderName, senderAddress);
        var effectiveTag = tag ?? (string.IsNullOrWhiteSpace(senderAddress)
            ? message
            : $"{senderAddress}_{message}");

        if (ToastNotifier.TryShow(title, message, preview, senderAddress, avatarPath, playSound, effectiveTag, activationUri))
        {
            return;
        }

        ShowBalloon(title, message, playSound, sound, senderName, senderAddress);
    }

    private void ShowBalloon(
        string title,
        string message,
        bool playSound,
        string? sound,
        string? senderName,
        string? senderAddress)
    {
        if (!_iconAdded)
        {
            return;
        }

        var customSound = playSound ? GetSoundAlias(sound) : null;
        var avatar = _avatars.GetIcon(senderName, senderAddress);

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = playSound && customSound is null
            ? (uint)(NativeMethods.NIIF_USER | NativeMethods.NIIF_LARGE_ICON)
            : (uint)(NativeMethods.NIIF_USER | NativeMethods.NIIF_LARGE_ICON | NativeMethods.NIIF_NOSOUND);
        // 气泡里的大图标用发件人头像；标题栏上方的小图标由应用标识（AUMID）决定。
        data.hBalloonIcon = avatar != IntPtr.Zero
            ? avatar
            : _largeIcon != IntPtr.Zero ? _largeIcon : _icon;
        data.uTimeoutOrVersion = 5000;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);

        if (customSound is not null)
        {
            NativeMethods.PlaySound(customSound, IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        }
    }

    private void ApplyMenuTheme()
    {
        // 2 = ForceDark, 3 = ForceLight；让托盘菜单跟随应用主题而不是系统默认。
        NativeMethods.SetPreferredAppMode(_isDarkTheme() ? 2 : 3);
        NativeMethods.FlushMenuThemes();
    }

    private static string? GetSoundAlias(string? sound)
        => sound switch
        {
            "mail" => "SystemMailBeep",
            "reminder" => "SystemExclamation",
            "asterisk" => "SystemAsterisk",
            "warning" => "SystemHand",
            "beep" => "SystemBeep",
            "question" => "SystemQuestion",
            "start" => "SystemStart",
            _ => null
        };

    public void Quit()
    {
        if (_window != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        _iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);

        if (_iconAdded && _pendingUnread > 0)
        {
            ApplyUnreadIcon();
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private NativeMethods.NOTIFYICONDATA CreateData()
        => new()
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _window,
            uID = 1,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

    private IntPtr HandleCopyData(IntPtr lParam)
    {
        try
        {
            var payload = Marshal.PtrToStructure<ToastActivation.CopyData>(lParam);

            if (payload.Marker.ToUInt64() != ToastActivation.PayloadMarker || payload.Data == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // 字符数按字节数折算，但先卡一个上限，避免收到异常长度时读越界。
            var characters = (int)Math.Min(payload.ByteCount / 2u, 2048u);
            var uri = Marshal.PtrToStringUni(payload.Data, characters)?.TrimEnd('\0');

            if (!string.IsNullOrWhiteSpace(uri))
            {
                _activated(uri);
            }

            return new IntPtr(1);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (message == WM_POWERBROADCAST)
        {
            var powerEvent = (int)(wParam.ToInt64() & 0xFFFF);

            if (powerEvent is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
            {
                _resumed();
                return new IntPtr(1);
            }

            return IntPtr.Zero;
        }

        switch (message)
        {
            case ToastActivation.CopyDataMessage:
                // 通知被点击时系统重新起了一个进程，那边把 maildog:// 地址通过 WM_COPYDATA 送进来。
                // 这里任何异常都不能放出去：消息循环里抛出会直接带走整个后台进程。
                return HandleCopyData(lParam);

            case WM_TRAYICON:
            {
                var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMessage == NIN_BALLOONUSERCLICK)
                {
                    _notificationClicked();
                    _openUi();
                }
                else if (mouseMessage == NativeMethods.WM_LBUTTONUP)
                {
                    _openUi();
                }
                else if (mouseMessage == NativeMethods.WM_RBUTTONUP)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_COMMAND:
            {
                var command = (int)(wParam.ToInt64() & 0xFFFF);
                switch (command)
                {
                    case CommandOpen:
                        _openUi();
                        break;
                    case CommandSync:
                        _syncAll();
                        break;
                    case CommandExit:
                        _exit();
                        Quit();
                        break;
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_CLOSE:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        ApplyMenuTheme();

        var menu = NativeMethods.CreatePopupMenu();

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CommandOpen, "打开 MailDog");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CommandSync, "立即同步");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CommandCheckUpdate, "检查更新");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, CommandExit, "退出");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_window);

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                _window,
                IntPtr.Zero);

            NativeMethods.PostMessage(_window, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandOpen:
                    _openUi();
                    break;
                case CommandSync:
                    _syncAll();
                    break;
                case CommandCheckUpdate:
                    _openAbout();
                    break;
                case CommandExit:
                    _exit();
                    Quit();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static (IntPtr Small, IntPtr Large, bool Owned) LoadAppIcon(int smallSize)
    {
        var executable = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(executable))
        {
            try
            {
                var small = ExtractIcon(executable, smallSize);
                var large = ExtractIcon(executable, 256);

                if (small != IntPtr.Zero || large != IntPtr.Zero)
                {
                    if (small == IntPtr.Zero)
                    {
                        small = large;
                    }

                    if (large == IntPtr.Zero)
                    {
                        large = small;
                    }

                    return (small, large, true);
                }
            }
            catch
            {
                // 回退到系统图标。
            }
        }

        return (NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication), IntPtr.Zero, false);
    }

    /// <summary>托盘按 SM_CXSMICON 显示，取同尺寸的 ico 帧才不会先被系统缩放。</summary>
    private static int GetSmallIconSize()
    {
        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        return size is >= 16 and <= 64 ? size : 16;
    }

    private static IntPtr ExtractIcon(string executable, int size)
    {
        return NativeMethods.PrivateExtractIcons(
            executable,
            0,
            size,
            size,
            out var icon,
            out _,
            1,
            0) > 0
            ? icon
            : IntPtr.Zero;
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();

        if (_window != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        _avatars.Dispose();

        if (_className != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_className);
        }

        if (_iconOwned)
        {
            if (_icon != IntPtr.Zero && _icon != _largeIcon)
            {
                NativeMethods.DestroyIcon(_icon);
            }

            if (_largeIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_largeIcon);
            }

            _icon = IntPtr.Zero;
            _largeIcon = IntPtr.Zero;
        }
    }
}
