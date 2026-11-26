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
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private readonly MainWindowViewModel _viewModel;
    private readonly Dictionary<TreeViewItem, AccountViewModel> _accountNodes = [];
    private readonly Dictionary<MailSummaryViewModel, ListBoxItem> _mailContainers = [];
    private const int MaxHtmlRetries = 2;
    private bool _adapterReady;
    private bool _internalNavigation;
    private bool _ignoreNextNavigationFailure;
    private int _htmlRetryCount;
    private int _htmlNavigationGeneration;
    private string? _pendingHtml;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
        : this(new MainWindowViewModel(new Services.EngineClient()))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        if (!OperatingSystem.IsWindows())
        {
            // 自绘标题条只服务于 Windows 的 Mica/Acrylic 客户区延展；
            // Linux/macOS 仍使用系统标题栏，避免出现第二条空白标题栏。
            CustomTitleBar.IsVisible = false;
            WindowChromeRoot.RowDefinitions[0].Height = new GridLength(0);
            ExtendClientAreaToDecorationsHint = false;
        }

        _viewModel = viewModel;
        DataContext = viewModel;

        if (OperatingSystem.IsLinux())
        {
            // Ubuntu 22.04 只有 WebKitGTK；优先用它，WPE 2.0 仅作为新发行版的备选。
            WebView.AdapterPreference = new[]
            {
                Avalonia.Platform.WebViewAdapterType.WebKitGtk,
                Avalonia.Platform.WebViewAdapterType.WpeWebKit
            };
        }

        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnEmlDragOver);
        DragDrop.AddDropHandler(this, OnEmlDrop);

        WebView.EnvironmentRequested += OnEnvironmentRequested;
        WebView.AdapterCreated += OnAdapterCreated;
        WebView.NavigationStarted += OnNavigationStarted;
        WebView.NavigationCompleted += OnNavigationCompleted;
        viewModel.Reader.PropertyChanged += OnReaderPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ShowRequested += OnShowRequested;
        viewModel.ScrollToMessageRequested += OnScrollToMessageRequested;
        FolderTree.ContainerPrepared += OnFolderTreeContainerPrepared;
        FolderTree.ContainerClearing += OnFolderTreeContainerClearing;
        FolderTree.Loaded += (_, _) => ConfigureSidebarScrollViewer(FolderTree);
        MailList.ContainerPrepared += OnMailListContainerPrepared;
        MailList.ContainerClearing += OnMailListContainerClearing;
        MailList.Loaded += (_, _) => ConfigureSidebarScrollViewer(MailList);
        MailList.AddHandler(ScrollViewer.ScrollChangedEvent, OnMailListScrollChanged);
        viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        SizeChanged += OnWindowSizeChanged;
        PointerMoved += (_, _) => _viewModel.NoteUserActivity();
        PointerPressed += (_, _) => _viewModel.NoteUserActivity();
        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        ApplyAppearance();
        UpdateColumns(ClientSize.Width);
        await _viewModel.InitializeAsync();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyAppearance();
        _viewModel.RefreshBackgroundPresets();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateColumns(e.NewSize.Width);
    }

    private static void ConfigureSidebarScrollViewer(Control control)
    {
        foreach (var viewer in control.GetVisualDescendants().OfType<ScrollViewer>())
        {
            // 模板内的 ScrollViewer 会用自己的默认值覆盖窗口级样式；这里直接落到实例上，
            // 保证三栏滚动条都按 10px 槽宽常显，而不是鼠标移上去才出现。
            viewer.AllowAutoHide = false;
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        }
    }

    private void UpdateColumns(double width)
    {
        // 窄窗口把空间让给阅读区；宽窗口恢复完整文件夹与列表宽度。
        var progress = Math.Clamp((width - 900) / 380d, 0d, 1d);
        MailColumns.ColumnDefinitions[0].Width = new GridLength(208 + (48 * progress));
        MailColumns.ColumnDefinitions[1].Width = new GridLength(340 + (28 * progress));
    }

    private void OnShowRequested()
    {        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

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

    private async void OnCreateBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "创建 MailDog 备份",
            SuggestedFileName = $"MailDog-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await _viewModel.CreateBackupAsync(path);
        }
    }

    private async void OnRestoreBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择 MailDog 备份",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("MailDog 备份")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            await _viewModel.RestoreBackupAsync(path);
        }
    }

    private async void OnExportPdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ExportPdfAsync(includeThread: false);

    private async void OnExportThreadPdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ExportPdfAsync(includeThread: true);

    private async Task ExportPdfAsync(bool includeThread)
    {
        if (_viewModel.SelectedMessage is not { } message)
        {
            return;
        }

        var name = SanitizeFileName(message.Subject);
        if (includeThread)
        {
            name += "-会话";
        }

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = includeThread ? "导出会话为 PDF" : "导出邮件为 PDF",
            SuggestedFileName = $"{name}.pdf",
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _viewModel.StatusMessage = "正在准备 PDF…";
        try
        {
            var html = await _viewModel.BuildPrintHtmlAsync(includeThread);
            if (html is null)
            {
                _viewModel.StatusMessage = "没有可导出的邮件。";
                return;
            }

            await RenderPdfAsync(html, path);
            _viewModel.HasError = false;
            _viewModel.StatusMessage = $"已导出 PDF：{path}";
        }
        catch (Exception exception)
        {
            _viewModel.HasError = true;
            _viewModel.StatusMessage = $"导出 PDF 失败：{exception.Message}";
        }
    }

    private async void OnPrintMessageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ShowPrintPreviewAsync(includeThread: false);

    private async void OnPrintThreadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ShowPrintPreviewAsync(includeThread: true);

    private async Task ShowPrintPreviewAsync(bool includeThread)
    {
        _viewModel.StatusMessage = "正在准备打印内容…";
        Window? printWindow = null;
        try
        {
            var html = await _viewModel.BuildPrintHtmlAsync(includeThread);
            if (html is null)
            {
                _viewModel.StatusMessage = "没有可打印的邮件。";
                return;
            }

            var context = CreatePrintWindow(visible: true);
            printWindow = context.Window;
            await WaitForAdapterAsync(context);
            await NavigateAndWaitAsync(context.WebView, html);

            context.WebView.ShowPrintUI();
            printWindow = null;
            _viewModel.StatusMessage = "打印窗口已打开；完成打印后关闭预览窗口。";
        }
        catch (Exception exception)
        {
            printWindow?.Close();
            _viewModel.HasError = true;
            _viewModel.StatusMessage = $"打印失败：{exception.Message}";
        }
    }

    private async Task RenderPdfAsync(string html, string destinationPath)
    {
        var context = CreatePrintWindow(visible: false);
        try
        {
            await WaitForAdapterAsync(context);
            await NavigateAndWaitAsync(context.WebView, html);
            await using var pdf = await context.WebView.PrintToPdfStreamAsync();
            await using var output = File.Create(destinationPath);
            await pdf.CopyToAsync(output);
        }
        finally
        {
            context.Window.Close();
        }
    }

    private sealed record PrintWindowContext(
        Window Window,
        NativeWebView WebView,
        Task AdapterReady);

    private static PrintWindowContext CreatePrintWindow(bool visible)
    {
        var webView = new NativeWebView();
        var userDataFolder = Path.Combine(
            ProductPaths.DataDirectory,
            "WebView2Print",
            Guid.NewGuid().ToString("N"));
        webView.EnvironmentRequested += (_, args) => ConfigurePrintEnvironment(args, userDataFolder);

        var adapterReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        webView.AdapterCreated += (_, _) => adapterReady.TrySetResult(true);

        var window = new Window
        {
            Title = visible ? "MailDog 打印预览" : "MailDog PDF",
            Width = visible ? 1000 : 900,
            Height = visible ? 800 : 700,
            CanResize = visible,
            ShowInTaskbar = visible,
            ShowActivated = visible,
            WindowStartupLocation = visible
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.Manual,
            Position = visible ? default : new PixelPoint(-32000, -32000),
            Content = webView
        };
        window.Closed += (_, _) => ScheduleDeleteDirectory(userDataFolder);

        window.Show();
        return new PrintWindowContext(window, webView, adapterReady.Task);
    }

    private static void ConfigurePrintEnvironment(
        WebViewEnvironmentRequestedEventArgs args,
        string userDataFolder)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            Directory.CreateDirectory(userDataFolder);
            windows.UserDataFolder = userDataFolder;
            windows.ProfileName = "MailDogPrint";
            windows.Language = "zh-CN";
        }
    }

    private static async Task WaitForAdapterAsync(PrintWindowContext context)
    {
        try
        {
            await context.AdapterReady.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            var reason = context.WebView.AdapterInfo?.ToString();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(reason)
                    ? "WebView 初始化超时。"
                    : $"WebView 初始化失败：{reason}");
        }
    }

    private static void ScheduleDeleteDirectory(string path)
    {
        _ = Task.Run(async () =>
        {
            // WebView2 的浏览器进程通常会在窗口关闭后几秒内退出，等一会儿再删用户数据目录。
            await Task.Delay(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(path);
        });
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // WebView2 可能还会短暂占用用户数据目录；临时目录留给系统清理即可。
        }
    }

    private static async Task NavigateAndWaitAsync(NativeWebView webView, string html)
    {
        if (!webView.IsLoaded)
        {
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
                => loaded.TrySetResult(true);

            webView.Loaded += OnLoaded;
            try
            {
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                webView.Loaded -= OnLoaded;
            }
        }

        // WebView 的原生子控件会在 Loaded 之后异步创建，留一拍再导航，避免首次加载丢请求。
        await Task.Delay(100);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(new InvalidOperationException("打印内容加载失败。"));
            }
        }

        webView.NavigationCompleted += OnCompleted;
        try
        {
            webView.NavigateToString(html);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            webView.NavigationCompleted -= OnCompleted;
        }
    }

    private static string SanitizeFileName(string? subject)
    {
        var name = string.IsNullOrWhiteSpace(subject) ? "maildog-message" : subject.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length > 90 ? name[..90] : name;
    }

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

    private void OnToggleSearchScopeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.IsGlobalSearch = !_viewModel.IsGlobalSearch;
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.MaterialIndex)
            or nameof(MainWindowViewModel.BackgroundColorHex)
            or nameof(MainWindowViewModel.BackgroundOpacity))
        {
            ApplyAppearance();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsAppLocked) && _viewModel.IsAppLocked)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UnlockPinBox.Focus());
        }
    }

    private void ApplyAppearance()
    {
        var build = Environment.OSVersion.Version.Build;
        var supportsMica = OperatingSystem.IsWindows() && build >= 22000;
        var supportsAcrylic = OperatingSystem.IsWindows() && build >= 17763;

        // 保存的材质在本机不可用时按纯色处理，避免界面显示已启用、实际却没有任何变化。
        var material = _viewModel.Material switch
        {
            "mica" when supportsMica => "mica",
            "acrylic" when supportsAcrylic => "acrylic",
            _ => "none"
        };
        var materialActive = material != "none";
        var translucent = materialActive || _viewModel.BackgroundOpacity < 100;

        TransparencyLevelHint = material switch
        {
            "mica" => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
            "acrylic" => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None],
            _ => translucent ? [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None]
                : [WindowTransparencyLevel.None]
        };

        var baseColor = ThemeService.ResolveSurfaceColor(
            _viewModel.BackgroundColorHex,
            ActualThemeVariant == ThemeVariant.Dark);
        var opacity = translucent ? Math.Clamp(_viewModel.BackgroundOpacity, 40, 100) / 100d : 1d;

        ApplyPalette(baseColor, opacity, translucent, ActualThemeVariant == ThemeVariant.Dark, materialActive);
        ApplyNativeTitleBarTheme(IsDark(baseColor), baseColor, materialActive);

        if (translucent)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () =>
                {
                    if (ActualTransparencyLevel == WindowTransparencyLevel.None)
                    {
                        ApplyPalette(baseColor, 1d, false, ActualThemeVariant == ThemeVariant.Dark, false);
                        ApplyNativeTitleBarTheme(IsDark(baseColor), baseColor, false);
                    }
                },
                TimeSpan.FromMilliseconds(600));
        }
    }

    private static void ApplyPalette(
        Color baseColor,
        double opacity,
        bool translucent,
        bool themeDark,
        bool materialActive)
    {
        var dark = IsDark(baseColor);
        var baseAlpha = translucent ? ToAlpha(opacity) : (byte)255;

        // 材质模式下不再把所有可承字表面补到近乎不透明，否则云母/亚克力只剩标题栏一条缝：
        // 左侧栏保留更多桌面纹理，列表/阅读区保持较高的底色占比，圆角卡片之间的 12px 间隙
        // 和设置页背景都能直接看到系统材质。纯色/无材质模式保持原来的高对比策略。
        var materialOpacity = Math.Clamp(opacity, 0.4d, 1d);
        var subtleAlpha = !translucent ? (byte)255
            : materialActive
                ? ToAlpha(0.35d + materialOpacity * 0.38d)
                : ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.78d));
        var layerAlpha = !translucent ? (byte)255
            : materialActive
                ? ToAlpha(0.48d + materialOpacity * 0.40d)
                : ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.90d));
        var contentAlpha = !translucent ? (byte)255
            : materialActive
                ? ToAlpha(0.62d + materialOpacity * 0.33d)
                : ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.94d));
        var cardAlpha = !translucent ? (byte)255
            : materialActive
                ? ToAlpha(Math.Clamp(0.84d + materialOpacity * 0.08d, 0.86d, 0.95d))
                : (byte)255;

        var subtleColor = Adjust(baseColor, dark ? 0.02 : -0.02);
        var layerColor = Adjust(baseColor, 0.05);

        SetBrush("MailDogBrushSurfaceWindow", WithAlpha(baseColor, baseAlpha));
        SetBrush("MailDogBrushSurfaceSubtle", WithAlpha(subtleColor, subtleAlpha));
        SetBrush("MailDogBrushSurfacePane", WithAlpha(layerColor, layerAlpha));
        SetBrush("MailDogBrushSurfaceContent", WithAlpha(layerColor, contentAlpha));
        SetBrush("MailDogBrushSurfaceCard", WithAlpha(layerColor, cardAlpha));
        // 弹出菜单、对话框和安全遮罩必须遮挡下方内容，保持完全不透明。
        SetBrush("MailDogBrushSurfacePopup", Color.FromRgb(layerColor.R, layerColor.G, layerColor.B));
        SetBrush("MailDogBrushSurfaceSolid", Color.FromRgb(layerColor.R, layerColor.G, layerColor.B));
        SetBrush("MailDogBrushFillHover", dark ? Color.Parse("#0FFFFFFF") : Color.Parse("#0A000000"));
        SetBrush("MailDogBrushStrokeDefault", dark ? Color.Parse("#3D3D3D") : Color.Parse("#E0E0E0"));
        SetBrush("MailDogBrushStrokeSubtle", dark ? Color.Parse("#333333") : Color.Parse("#EBEBEB"));

        // 背景越透，窗口底下是什么颜色就不再由我们决定（可能是亮壁纸，也可能是深色窗口）。
        // 灰字在这种"背景不可控"的情况下最容易糊掉，所以按透明度把次级/三级文字
        // 逐步拉向正文色，保证任何桌面上都还能读。
        var tension = translucent ? Math.Clamp((1d - opacity) / 0.6d, 0d, 1d) : 0d;

        var primary = dark ? Colors.White : Color.Parse("#1B1B1B");
        var secondary = dark ? Color.Parse("#C7C7C7") : Color.Parse("#616161");
        var tertiary = dark ? Color.Parse("#9A9A9A") : Color.Parse("#8A8A8A");
        var disabled = dark ? Color.Parse("#6E6E6E") : Color.Parse("#BDBDBD");

        SetBrush("MailDogBrushTextPrimary", primary);
        SetBrush("MailDogBrushTextSecondary",
            Blend(secondary, primary, tension * 0.85d));
        SetBrush("MailDogBrushTextTertiary",
            Blend(tertiary, dark ? Color.Parse("#E6E6E6") : Color.Parse("#333333"), tension));
        SetBrush("MailDogBrushTextDisabled",
            Blend(disabled, dark ? Color.Parse("#9A9A9A") : Color.Parse("#8A8A8A"), tension * 0.7d));
        SetBrush("MailDogBrushTextOnAccent", themeDark ? Color.Parse("#0A0A0A") : Colors.White);
    }

    /// <summary>把颜色按 t（0~1）往目标色靠；用来在透明背景上抬高文字对比度。</summary>
    private static Color Blend(Color from, Color to, double t)
    {
        static byte Mix(byte a, byte b, double weight)
            => (byte)Math.Clamp((int)Math.Round(a + (b - a) * weight), 0, 255);

        return Color.FromArgb(
            Mix(from.A, to.A, t),
            Mix(from.R, to.R, t),
            Mix(from.G, to.G, t),
            Mix(from.B, to.B, t));
    }

    private static byte ToAlpha(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color Adjust(Color color, double delta)
    {
        static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

        var offset = delta * 255;
        return Color.FromRgb(Clamp(color.R + offset), Clamp(color.G + offset), Clamp(color.B + offset));
    }

    private static bool IsDark(Color color)
    {
        return (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B) < 140;
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        if (application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value)
            && value is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        application.Resources[key] = new SolidColorBrush(color);
    }

    private void ApplyNativeTitleBarTheme(bool dark, Color baseColor, bool materialActive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
        {
            var legacy = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref legacy, sizeof(int));
        }

        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var text = dark ? 0xFFFFFF : 0x1B1B1B;
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));

        if (materialActive)
        {
            // DWMWA_COLOR_NONE：把标题栏重新交还给 DWM 的系统材质。
            // 之前强制写入不透明 caption color 会覆盖云母/亚克力，只剩客户区能看到材质。
            var none = DwmwaColorNone;
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref none, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref none, sizeof(int));
            return;
        }

        // COLORREF 为 0x00BBGGRR，让标题栏与主页面背景保持一致。
        var caption = baseColor.R | (baseColor.G << 8) | (baseColor.B << 16);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
    }

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

    private async void OnAttachmentDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: AttachmentViewModel attachment } control)
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse
            && !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        await StartAttachmentDragAsync(e, [attachment]);
    }

    private async void OnAttachmentDragSelectedPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse
            && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var selected = _viewModel.Reader.Attachments
            .Where(attachment => attachment.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await StartAttachmentDragAsync(e, selected);
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
