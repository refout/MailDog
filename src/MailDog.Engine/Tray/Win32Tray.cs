using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MailDog.Engine.Tray;

public sealed class Win32Tray : IDisposable
{
    private const uint WM_TRAYICON = NativeMethods.WM_APP + 1;
    private const uint NIN_BALLOONUSERCLICK = 0x0405;
    private const int CommandOpen = 1;
    private const int CommandSync = 2;
    private const int CommandExit = 3;
    private const uint SndAlias = 0x00010000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    private readonly Action _openUi;
    private readonly Action _syncAll;
    private readonly Action _exit;
    private readonly Func<bool> _isDarkTheme;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly IntPtr _className;

    private IntPtr _window;
    private IntPtr _icon;
    private IntPtr _largeIcon;
    private bool _iconOwned;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private string _tooltip = "MailDog";
    private bool _disposed;

    public Win32Tray(Action openUi, Action syncAll, Action exit, Func<bool> isDarkTheme)
    {
        _openUi = openUi;
        _syncAll = syncAll;
        _exit = exit;
        _isDarkTheme = isDarkTheme;
        _wndProc = WindowProc;
        _className = Marshal.StringToHGlobalUni("MailDog.Tray");
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

        _window = NativeMethods.CreateWindowEx(
            0,
            "MailDog.Tray",
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.HwndMessage,
            IntPtr.Zero,
            windowClass.hInstance,
            IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建托盘窗口失败。");
        }

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var appIcon = LoadAppIcon();
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

    public void ShowNotification(string title, string message, bool playSound = true, string? sound = null)
    {
        if (!_iconAdded)
        {
            return;
        }

        var customSound = playSound ? GetSoundAlias(sound) : null;

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = playSound && customSound is null
            ? (uint)(NativeMethods.NIIF_USER | NativeMethods.NIIF_LARGE_ICON)
            : (uint)(NativeMethods.NIIF_USER | NativeMethods.NIIF_LARGE_ICON | NativeMethods.NIIF_NOSOUND);
        data.hBalloonIcon = _largeIcon != IntPtr.Zero ? _largeIcon : _icon;
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

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        switch (message)
        {
            case WM_TRAYICON:
            {
                var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMessage == NativeMethods.WM_LBUTTONUP || mouseMessage == NIN_BALLOONUSERCLICK)
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

    private static (IntPtr Small, IntPtr Large, bool Owned) LoadAppIcon()
    {
        var executable = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(executable))
        {
            try
            {
                var count = NativeMethods.ExtractIconEx(executable, 0, out var large, out var small, 1);
                if (count > 0)
                {
                    if (small == IntPtr.Zero)
                    {
                        small = large;
                    }

                    if (large == IntPtr.Zero)
                    {
                        large = small;
                    }

                    return (small, large, small != IntPtr.Zero);
                }
            }
            catch
            {
                // 回退到系统图标。
            }
        }

        return (NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication), IntPtr.Zero, false);
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
