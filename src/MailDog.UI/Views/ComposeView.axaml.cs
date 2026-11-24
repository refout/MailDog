using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaRichEditor.Documents;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class ComposeView : UserControl
{
    private readonly Dictionary<string, Button> _formatButtons = [];
    private ComposeViewModel? _viewModel;
    private string? _lastRichHtml;
    private bool _suppressRichChanged;

    public ComposeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        foreach (var child in FormatToolbar.Children)
        {
            if (child is Button button && button.Tag is string tag)
            {
                _formatButtons[tag] = button;
            }
        }

        RichEditor.TextChanged += OnRichTextChanged;
        RichEditor.SelectionChanged += OnRichSelectionChanged;
        RichEditor.StatusChanged += OnRichStatusChanged;
        RichEditor.IsReadOnly = true;
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
            LoadCompose(_viewModel);
        }
    }

    private void LoadCompose(ComposeViewModel viewModel)
    {
        LoadRichHtml(viewModel.BuildStartingHtml());
        RichEditor.IsReadOnly = !viewModel.CanEdit;
        RichEditor.Focus();
    }

    private void OnRichTextChanged(object? sender, EventArgs e)
    {
        PublishRichBody();
    }

    // Formatting commands (bold, link, lists) change runs without changing text, so TextChanged
    // alone leaves BodyHtml stale; StatusChanged fires after those commands and keeps it in sync.
    private void OnRichStatusChanged(object? sender, EventArgs e)
    {
        PublishRichBody();
    }

    private void PublishRichBody()
    {
        if (_suppressRichChanged || _viewModel is null)
        {
            return;
        }

        var html = RichEditor.ToHtml();
        if (string.Equals(html, _lastRichHtml, StringComparison.Ordinal))
        {
            return;
        }

        _lastRichHtml = html;
        _viewModel.UpdateRichBody(html);
    }

    private void OnRichSelectionChanged(object? sender, EventArgs e)
    {
        RefreshFormatState();
    }

    private void RefreshFormatState()
    {
        var format = RichEditor.GetCaretFormat();
        SetActive("bold", format.Bold);
        SetActive("italic", format.Italic);
        SetActive("underline", format.Underline);
        SetActive("strike", format.Strike);
        SetActive("bulletList", format.List == ListKind.Bullet);
        SetActive("orderedList", format.List == ListKind.Ordered);
        SetActive("blockquote", format.Quote);
        // 编辑器没有公开“光标处是否在链接内”的查询，链接按钮只作为命令入口。
        SetActive("link", false);
    }

    private void SetActive(string tag, bool active)
    {
        if (!_formatButtons.TryGetValue(tag, out var button))
        {
            return;
        }

        if (active)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposeViewModel.CanEdit))
        {
            RichEditor.IsReadOnly = _viewModel?.CanEdit != true;
        }
    }

    private async void OnFormatCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string command)
        {
            return;
        }

        switch (command)
        {
            case "bold":
                RichEditor.ToggleBold();
                break;
            case "italic":
                RichEditor.ToggleItalic();
                break;
            case "underline":
                RichEditor.ToggleUnderline();
                break;
            case "strike":
                RichEditor.ToggleStrikethrough();
                break;
            case "heading1":
                RichEditor.SetHeading(1);
                break;
            case "heading2":
                RichEditor.SetHeading(2);
                break;
            case "heading3":
                RichEditor.SetHeading(3);
                break;
            case "bodyText":
                RichEditor.SetHeading(0);
                break;
            case "bulletList":
                RichEditor.ToggleBullet();
                break;
            case "orderedList":
                RichEditor.ToggleNumbering();
                break;
            case "blockquote":
                RichEditor.ToggleQuote();
                break;
            case "link":
                OpenLinkDialog();
                break;
            case "image":
                await InsertRichImageAsync();
                break;
            case "undo":
                RichEditor.Undo();
                break;
            case "redo":
                RichEditor.Redo();
                break;
        }

        RefreshFormatState();
        PublishRichBody();
    }

    private async Task InsertRichImageAsync()
    {
        if (_viewModel?.CanEdit != true)
        {
            return;
        }

        try
        {
            await RichEditor.InsertImageFromFileAsync();
            PublishRichBody();
        }
        catch (Exception exception)
        {
            if (_viewModel is not null)
            {
                _viewModel.Error = $"插入图片失败：{exception.Message}";
            }
        }
    }

    private void OpenLinkDialog()
    {
        RichEditor.OpenLinkDialog();
    }

    private void LoadRichHtml(string html)
    {
        _suppressRichChanged = true;
        try
        {
            RichEditor.LoadHtml(html);
        }
        finally
        {
            _suppressRichChanged = false;
        }

        _lastRichHtml = RichEditor.ToHtml();
        RefreshFormatState();
    }

    private async void AddAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposeViewModel viewModel || !viewModel.CanEdit)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            viewModel.Error = "无法打开文件选择器，请重试。";
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择附件",
                AllowMultiple = true
            });

            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    viewModel.Error = "只能添加本机文件。";
                    continue;
                }

                viewModel.AddAttachment(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.Error = $"选择附件失败：{ex.Message}";
        }
    }
}
