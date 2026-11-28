using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MailDog.Contracts.Platform;
using MailDog.UI.Services;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class MainWindow : Window
{
    private void OnUnifiedFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: UnifiedFolderViewModel folder })
        {
            _viewModel.SelectedUnifiedFolder = folder;
        }
    }

    private void OnEditAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            _viewModel.BeginEditAccount(account);
        }
    }

    private async void OnReauthorizeAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            await _viewModel.ReauthorizeAccountAsync(account);
        }
    }

    private async void OnSetDefaultAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            await _viewModel.SetDefaultAccountAsync(account);
        }
    }

    private async void OnMoveAccountUpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            await _viewModel.MoveAccountAsync(account, -1);
        }
    }

    private async void OnMoveAccountDownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            await _viewModel.MoveAccountAsync(account, 1);
        }
    }

    private void OnDeleteAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            _viewModel.RequestDeleteAccount(account);
        }
    }

    private void OnCancelDeleteAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            _viewModel.CancelDeleteAccount(account);
        }
    }

    private async void OnConfirmDeleteAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountViewModel account })
        {
            await _viewModel.DeleteAccountAsync(account);
        }
    }

    private void OnCreateFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: AccountViewModel account })
        {
            _viewModel.BeginCreateFolder(account.Id);
        }
    }

    private void OnRenameFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderViewModel folder })
        {
            _viewModel.BeginRenameFolder(folder);
        }
    }

    private void OnDeleteFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderViewModel folder })
        {
            _viewModel.RequestDeleteFolder(folder);
        }
    }

    private async void OnRefreshOutboxClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await _viewModel.RefreshOutboxAsync();
    }

    private async void OnRetryOutboxClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutboxItemViewModel item })
        {
            await _viewModel.RetryOutboxAsync(item);
        }
    }

    private async void OnDeleteOutboxClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutboxItemViewModel item })
        {
            await _viewModel.DeleteOutboxAsync(item);
        }
    }

}
