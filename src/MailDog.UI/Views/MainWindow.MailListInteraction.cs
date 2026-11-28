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
    private async void OnExportMessageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedMessage is not { } message)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(message.Subject) ? "maildog-message" : message.Subject.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (name.Length > 100)
        {
            name = name[..100];
        }

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出邮件",
            SuggestedFileName = $"{name}.eml",
            DefaultExtension = "eml",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await _viewModel.ExportSelectedMessageAsync(path);
        }
    }

    /// <summary>把账户的展开状态同步到 TreeViewItem（框架容器不直接支持双向绑定）。</summary>
    private void OnFolderTreeContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not TreeViewItem item)
        {
            return;
        }

        if (item.DataContext is FolderViewModel folder)
        {
            AutomationProperties.SetName(item, folder.Name);
            return;
        }

        if (item.DataContext is not AccountViewModel account)
        {
            return;
        }

        AutomationProperties.SetName(item, account.Title);

        if (_accountNodes.TryGetValue(item, out var previous))
        {
            previous.PropertyChanged -= OnAccountExpansionChanged;
        }

        _accountNodes[item] = account;
        account.PropertyChanged += OnAccountExpansionChanged;
        item.PropertyChanged -= OnAccountItemExpandedChanged;
        item.PropertyChanged += OnAccountItemExpandedChanged;
        item.IsExpanded = account.IsExpanded;
    }

    private void OnMailListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem { DataContext: MailSummaryViewModel message } item)
        {
            item.PointerEntered -= OnMailRowPointerEntered;
            item.PointerEntered += OnMailRowPointerEntered;
            item.PointerExited -= OnMailRowPointerExited;
            item.PointerExited += OnMailRowPointerExited;
            message.PropertyChanged -= OnMailMessagePropertyChanged;
            message.PropertyChanged += OnMailMessagePropertyChanged;
            _mailContainers[message] = item;
            AutomationProperties.SetName(item, message.AccessibilityName);
            AutomationProperties.SetHelpText(item, message.Preview);
        }
    }

    private void OnMailListContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.PointerEntered -= OnMailRowPointerEntered;
            item.PointerExited -= OnMailRowPointerExited;
            if (item.DataContext is MailSummaryViewModel message)
            {
                message.IsHovered = false;
                message.PropertyChanged -= OnMailMessagePropertyChanged;
                if (_mailContainers.TryGetValue(message, out var container)
                    && ReferenceEquals(container, item))
                {
                    _mailContainers.Remove(message);
                }
            }

            AutomationProperties.SetName(item, null);
            AutomationProperties.SetHelpText(item, null);
        }
    }

    private void OnMailMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MailSummaryViewModel message
            || e.PropertyName != nameof(MailSummaryViewModel.IsRead)
            || !_mailContainers.TryGetValue(message, out var item))
        {
            return;
        }

        AutomationProperties.SetName(item, message.AccessibilityName);
    }

    private void OnFolderTreeContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is TreeViewItem item && _accountNodes.Remove(item, out var account))
        {
            account.PropertyChanged -= OnAccountExpansionChanged;
        }
    }

    private void OnAccountExpansionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountViewModel.IsExpanded) || sender is not AccountViewModel account)
        {
            return;
        }

        foreach (var (item, node) in _accountNodes)
        {
            if (ReferenceEquals(node, account))
            {
                item.IsExpanded = account.IsExpanded;
            }
        }
    }

    private void OnAccountItemExpandedChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TreeViewItem.IsExpandedProperty || sender is not TreeViewItem item)
        {
            return;
        }

        if (_accountNodes.TryGetValue(item, out var account))
        {
            account.IsExpanded = item.IsExpanded;
        }
    }

    private void OnMailRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: MailSummaryViewModel message })
        {
            message.IsHovered = true;
        }
    }

    private void OnMailRowPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: MailSummaryViewModel message })
        {
            message.IsHovered = false;
        }
    }

    private async void OnQuickArchiveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: MailSummaryViewModel message })
        {
            await _viewModel.ArchiveMessageAsync(message);
        }
    }

    private async void OnQuickDeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: MailSummaryViewModel message })
        {
            await _viewModel.DeleteMessageAsync(message);
        }
    }

    private async void OnQuickToggleReadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: MailSummaryViewModel message })
        {
            await _viewModel.ToggleReadMessageAsync(message);
        }
    }

    private void OnClearSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.SearchText = string.Empty;
        _viewModel.IsGlobalSearch = false;
        SearchBox.Focus();
        e.Handled = true;
    }

    private async void OnThreadMessageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MailSummaryViewModel message })
        {
            await _viewModel.ShowThreadMessageAsync(message);
        }
    }

    private void OnScrollToMessageRequested(MailSummaryViewModel message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => MailList.ScrollIntoView(message));
    }

    private void OnJumpRangeItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
        {
            // 再次点击当前范围可清除高亮，不再跳转。
            _viewModel.JumpRangeIndex = _viewModel.JumpRangeIndex == index ? 0 : index;
        }

        JumpButton.Flyout?.Hide();
    }

    private void OnApplyCustomRangeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.ApplyCustomRangeCommand.Execute(null);
        _viewModel.IsDatePanelOpen = false;
        JumpButton.Flyout?.Hide();
    }

    private void OnToggleDatePanelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.CustomDate is { } date)
        {
            var month = new DateTime(date.Year, date.Month, 1);
            if (_viewModel.CalendarMonth != month)
            {
                _viewModel.CalendarMonth = month;
            }
        }

        _viewModel.IsDatePanelOpen = !_viewModel.IsDatePanelOpen;
    }

    private void OnDatePanelClosed(object? sender, EventArgs e)
    {
        _viewModel.IsDatePanelOpen = false;
    }

    private void OnJumpFlyoutClosed(object? sender, EventArgs e)
    {
        _viewModel.IsDatePanelOpen = false;
    }

    private void OnCalendarDayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalendarDayViewModel day })
        {
            _viewModel.CustomDate = day.Date;

            var month = new DateTime(day.Date.Year, day.Date.Month, 1);
            if (_viewModel.CalendarMonth != month)
            {
                _viewModel.CalendarMonth = month;
            }
        }
    }

}
