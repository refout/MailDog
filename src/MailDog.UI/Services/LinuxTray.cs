using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MailDog.UI.ViewModels;
using MailDog.UI.Views;

namespace MailDog.UI.Services;

/// <summary>
/// Linux 桌面托盘入口。能显示时提供“显示主窗口 / 退出 MailDog”，
/// 桌面不支持托盘也不会影响窗口和后台收信。
/// </summary>
internal sealed class LinuxTray : IDisposable
{
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _icon;

    public LinuxTray(MainWindow window, MainWindowViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;

        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => ShowWindow();

        var quit = new NativeMenuItem("退出 MailDog");
        quit.Click += async (_, _) =>
        {
            try
            {
                await _viewModel.QuitFromTrayAsync().ConfigureAwait(true);
            }
            catch
            {
                // 退出路径不把错误再抛回 UI 线程。
            }
        };

        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(quit);

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MailDog.UI/Assets/logo.png"))),
            ToolTipText = "MailDog",
            Menu = menu,
            IsVisible = true
        };
        _icon.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
    }

    private void ShowWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }
}
