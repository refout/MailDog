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
    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            var folder = Path.Combine(ProductPaths.DataDirectory, "WebView2");

            Directory.CreateDirectory(folder);
            windows.UserDataFolder = folder;
            windows.ProfileName = "MailDog";
            windows.Language = "zh-CN";
        }
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = true;
        _htmlRetryCount = 0;
        NavigatePending();
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_internalNavigation)
        {
            _internalNavigation = false;
            _ignoreNextNavigationFailure = false;
            return;
        }

        var request = e.Request;
        e.Cancel = true;
        _ignoreNextNavigationFailure = true;

        if (request is null)
        {
            return;
        }

        if (request.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                Process.Start(new ProcessStartInfo(request.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // 外部打开失败时不影响阅读。
            }
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_ignoreNextNavigationFailure)
        {
            // 外部链接被主动取消不算渲染失败，不能让一次点击把正文永久切回纯文本。
            _ignoreNextNavigationFailure = false;
            return;
        }

        if (e.IsSuccess)
        {
            _htmlNavigationGeneration++;
            _htmlRetryCount = 0;
            return;
        }

        if (_viewModel.Reader.UseHtml)
        {
            HandleHtmlRenderFailure();
        }
    }

    private void HandleHtmlRenderFailure()
    {
        _htmlNavigationGeneration++;

        if (!_viewModel.Reader.UseHtml || string.IsNullOrEmpty(_pendingHtml))
        {
            return;
        }

        if (_htmlRetryCount < MaxHtmlRetries)
        {
            _htmlRetryCount++;
            var html = _pendingHtml;
            var attempt = _htmlRetryCount;
            _ = RetryHtmlAsync(html, attempt);
            return;
        }

        Debug.WriteLine("HTML 渲染重试后仍失败，切换为纯文本。");
        _viewModel.Reader.UseHtml = false;
        _viewModel.Reader.Error = "HTML 渲染失败，已重试 2 次并切换为纯文本。";
    }

    private async Task RetryHtmlAsync(string html, int attempt)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_pendingHtml != html || !_viewModel.Reader.UseHtml || !_adapterReady)
            {
                return;
            }

            _internalNavigation = true;
            WebView.NavigateToString(html);
            var generation = ++_htmlNavigationGeneration;
            _ = WatchHtmlNavigationAsync(generation, html);
        });
    }

    private async Task WatchHtmlNavigationAsync(int generation, string html)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _htmlNavigationGeneration
                || _pendingHtml != html
                || !_viewModel.Reader.UseHtml)
            {
                return;
            }

            HandleHtmlRenderFailure();
        });
    }

    private async void OnAttachmentRowDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AttachmentViewModel attachment } control)
        {
            return;
        }

        if (e.Source is Visual source
            && (source.FindAncestorOfType<Button>(includeSelf: true) is not null
                || source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null))
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse
            && !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var selected = _viewModel.Reader.Attachments
            .Where(item => item.IsSelected)
            .ToArray();
        IReadOnlyList<AttachmentViewModel> attachments =
            attachment.IsSelected && selected.Length > 0
                ? selected
                : new[] { attachment };

        e.Handled = true;
        await StartAttachmentDragAsync(e, attachments);
    }

    private async Task StartAttachmentDragAsync(
        PointerPressedEventArgs trigger,
        IReadOnlyList<AttachmentViewModel> attachments)
    {
        try
        {
            var paths = await _viewModel.PrepareAttachmentDragFilesAsync(attachments);
            if (paths.Count == 0)
            {
                return;
            }

            var dataTransfer = new DataTransfer();
            foreach (var path in paths)
            {
                var file = await StorageProvider.TryGetFileFromPathAsync(path);
                if (file is not null)
                {
                    dataTransfer.Add(DataTransferItem.CreateFile(file));
                }
            }

            if (dataTransfer.Items.Count == 0)
            {
                return;
            }

            await DragDrop.DoDragDropAsync(trigger, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            _viewModel.HasError = true;
            _viewModel.StatusMessage = $"拖出附件失败：{exception.Message}";
        }
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.HtmlDocument))
        {
            _pendingHtml = _viewModel.Reader.HtmlDocument;
            _htmlRetryCount = 0;
            _htmlNavigationGeneration++;
            NavigatePending();
        }
    }

    private void NavigatePending()
    {
        if (!_adapterReady || string.IsNullOrEmpty(_pendingHtml))
        {
            return;
        }

        var html = _pendingHtml;
        _internalNavigation = true;
        WebView.NavigateToString(html);
        var generation = ++_htmlNavigationGeneration;
        _ = WatchHtmlNavigationAsync(generation, html);
    }

    /// <summary>搜索结果列表重建后，列表容器会丢掉选中项；在 UI 刷新后补选第一条。</summary>
    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchText))
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (!string.IsNullOrWhiteSpace(_viewModel.SearchText)
                    && MailList.SelectedIndex < 0
                    && MailList.ItemCount > 0)
                {
                    MailList.SelectedIndex = 0;
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnMailListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer viewer
            || viewer.Viewport.Height <= 0
            || viewer.Extent.Height <= 0)
        {
            return;
        }

        const double threshold = 240;
        var remaining = viewer.Extent.Height - (viewer.Offset.Y + viewer.Viewport.Height);
        if (remaining > threshold)
        {
            return;
        }

        if (_viewModel.IsLoadingMore || !_viewModel.HasMore)
        {
            return;
        }

        _viewModel.LoadMoreCommand.Execute(null);
    }

    /// <summary>Esc 必须走隧道阶段，否则写作页里的 TextBox 会先吃掉按键。</summary>
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        _viewModel.NoteUserActivity();
        if (_viewModel.IsAppLocked)
        {
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.IsComposeOpen)
        {
            _viewModel.CloseComposeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_viewModel.IsOverlayOpen)
        {
            _viewModel.BackFromOverlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _viewModel.NoteUserActivity();
        if (_viewModel.IsAppLocked)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var editable = e.Source is TextBox;

        if (control
            && !shift
            && e.Key == Key.N
            && !_viewModel.IsOverlayOpen
            && !_viewModel.IsComposeOpen)
        {
            _viewModel.NewMailCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control
            && e.Key == Key.R
            && !_viewModel.IsOverlayOpen
            && !_viewModel.IsComposeOpen)
        {
            if (shift)
            {
                _viewModel.ReplyAllCommand.Execute(null);
            }
            else
            {
                _viewModel.ReplyCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (control
            && shift
            && e.Key == Key.F
            && !_viewModel.IsOverlayOpen
            && !_viewModel.IsComposeOpen)
        {
            _viewModel.ForwardCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete
            && !editable
            && !_viewModel.IsOverlayOpen
            && !_viewModel.IsComposeOpen
            && _viewModel.SelectedMessage is not null)
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && !_viewModel.IsOverlayOpen && !_viewModel.IsComposeOpen)
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (control
            && e.Key == Key.A
            && !_viewModel.IsOverlayOpen
            && !editable)
        {
            _viewModel.SelectAllMessagesCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsOverlayOpen)
        {
            _viewModel.BackFromOverlayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F && !_viewModel.IsOverlayOpen)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.OemComma)
        {
            _viewModel.OpenSettingsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEmlDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = FindEmlPath(e.DataTransfer) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnEmlDrop(object? sender, DragEventArgs e)
    {
        if (FindEmlPath(e.DataTransfer) is { } path)
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await _viewModel.OpenEmlFileAsync(path);
        }
    }

    private static string? FindEmlPath(IDataTransfer data)
    {
        if (data.TryGetFiles() is not { } files)
        {
            return null;
        }

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();

            if (path is not null && path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        KeyDown -= OnWindowKeyDown;
        RemoveHandler(KeyDownEvent, OnWindowPreviewKeyDown);
        DragDrop.RemoveDragOverHandler(this, OnEmlDragOver);
        DragDrop.RemoveDropHandler(this, OnEmlDrop);
        MailList.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnMailListScrollChanged);
        _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        FolderTree.ContainerPrepared -= OnFolderTreeContainerPrepared;
        FolderTree.ContainerClearing -= OnFolderTreeContainerClearing;
        MailList.ContainerPrepared -= OnMailListContainerPrepared;
        MailList.ContainerClearing -= OnMailListContainerClearing;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        SizeChanged -= OnWindowSizeChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ShowRequested -= OnShowRequested;
        WebView.EnvironmentRequested -= OnEnvironmentRequested;
        WebView.AdapterCreated -= OnAdapterCreated;
        WebView.NavigationStarted -= OnNavigationStarted;
        WebView.NavigationCompleted -= OnNavigationCompleted;
        _viewModel.Reader.PropertyChanged -= OnReaderPropertyChanged;
        _viewModel.Dispose();
    }
}
