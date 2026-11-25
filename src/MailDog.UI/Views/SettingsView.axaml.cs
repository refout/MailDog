using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { IsSettingsOpen: true } vm)
        {
            vm.BackFromOverlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEditAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            Vm?.BeginEditAccount(account);
        }
    }

    private async void OnReauthorizeAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account } && Vm is { } vm)
        {
            await vm.ReauthorizeAccountAsync(account);
        }
    }

    private async void OnSetDefaultAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account } && Vm is { } vm)
        {
            await vm.SetDefaultAccountAsync(account);
        }
    }

    private async void OnMoveAccountUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account } && Vm is { } vm)
        {
            await vm.MoveAccountAsync(account, -1);
        }
    }

    private async void OnMoveAccountDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account } && Vm is { } vm)
        {
            await vm.MoveAccountAsync(account, 1);
        }
    }

    private void OnDeleteAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            Vm?.RequestDeleteAccount(account);
        }
    }

    private void OnCancelDeleteAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            Vm?.CancelDeleteAccount(account);
        }
    }

    private async void OnConfirmDeleteAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account } && Vm is { } vm)
        {
            await vm.DeleteAccountAsync(account);
        }
    }

    private async void OnRefreshOutboxClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.RefreshOutboxAsync();
        }
    }

    private async void OnRetryOutboxClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutboxItemViewModel item } && Vm is { } vm)
        {
            await vm.RetryOutboxAsync(item);
        }
    }

    private async void OnDeleteOutboxClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutboxItemViewModel item } && Vm is { } vm)
        {
            await vm.DeleteOutboxAsync(item);
        }
    }

    private async void OnCreateBackupClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "创建 MailDog 备份",
            SuggestedFileName = $"MailDog-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && Vm is { } vm)
        {
            await vm.CreateBackupAsync(path);
        }
    }

    private async void OnRestoreBackupClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 MailDog 备份",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MailDog 备份")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path) && Vm is { } vm)
        {
            await vm.RestoreBackupAsync(path);
        }
    }

    private async void OnCopyCliInfoClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var text = vm.BuildCliAgentInfo();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            vm.CliCopyStatus = "当前环境不支持剪贴板。";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            vm.CliCopyStatus = "已复制，可以粘贴给智能体。";
        }
        catch (Exception exception)
        {
            vm.CliCopyStatus = $"复制失败：{exception.Message}";
        }
    }

    private void OnOpenCliCallsClick(object? sender, RoutedEventArgs e)
    {
        new CliCallsWindow(new CliCallsViewModel()).Show();
    }
}
