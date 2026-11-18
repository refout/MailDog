using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class ComposeView : UserControl
{
    private ComposeViewModel? _viewModel;
    private bool _previewAdapterReady;
    private bool _previewInternalNavigation;
    private string? _pendingPreviewHtml;

    public ComposeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => BodyBox.Focus();
        DataContextChanged += OnDataContextChanged;
        MarkdownPreview.EnvironmentRequested += OnPreviewEnvironmentRequested;
        MarkdownPreview.AdapterCreated += OnPreviewAdapterCreated;
        MarkdownPreview.NavigationStarted += OnPreviewNavigationStarted;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ComposeViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposeViewModel.BodyText) or nameof(ComposeViewModel.IsMarkdown))
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (_viewModel is null || !_viewModel.IsMarkdown)
        {
            _pendingPreviewHtml = null;
            return;
        }

        _pendingPreviewHtml = _viewModel.BuildMarkdownPreviewHtml();
        if (_previewAdapterReady)
        {
            NavigatePreview();
        }
    }

    private void NavigatePreview()
    {
        if (!_previewAdapterReady || string.IsNullOrEmpty(_pendingPreviewHtml))
        {
            return;
        }

        _previewInternalNavigation = true;
        MarkdownPreview.NavigateToString(_pendingPreviewHtml);
    }

    private void OnPreviewEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailDog",
                "WebView2");

            Directory.CreateDirectory(folder);
            windows.UserDataFolder = folder;
            windows.ProfileName = "MailDog";
            windows.Language = "zh-CN";
        }
    }

    private void OnPreviewAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _previewAdapterReady = true;
        NavigatePreview();
    }

    private void OnPreviewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_previewInternalNavigation)
        {
            _previewInternalNavigation = false;
            return;
        }

        var request = e.Request;
        e.Cancel = true;
        if (request is { Scheme: "http" or "https" or "mailto" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(request.ToString())
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 预览中的链接打不开时保持安静。
            }
        }
    }

    private async void AddAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposeViewModel viewModel)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择附件",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                viewModel.AddAttachment(path);
            }
        }
    }
}
