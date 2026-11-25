using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MailDog.UI.Services;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly MainWindowViewModel _viewModel;
    private readonly Dictionary<TreeViewItem, AccountViewModel> _accountNodes = [];
    private readonly Dictionary<MailSummaryViewModel, ListBoxItem> _mailContainers = [];
    private bool _adapterReady;
    private bool _internalNavigation;
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
        MailList.ContainerPrepared += OnMailListContainerPrepared;
        MailList.ContainerClearing += OnMailListContainerClearing;
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

    private void UpdateColumns(double width)
    {
        // 窄窗口把空间让给阅读区；宽窗口恢复完整文件夹与列表宽度。
        var progress = Math.Clamp((width - 900) / 380d, 0d, 1d);
        MailColumns.ColumnDefinitions[0].Width = new GridLength(208 + (32 * progress));
        MailColumns.ColumnDefinitions[1].Width = new GridLength(320 + (40 * progress));
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
        var supportsMaterial = Environment.OSVersion.Version.Build >= 22000;
        var material = supportsMaterial ? _viewModel.Material : "none";
        var translucent = material != "none" || _viewModel.BackgroundOpacity < 100;

        TransparencyLevelHint = material switch
        {
            "mica" => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur],
            "acrylic" => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur],
            _ => translucent ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None]
        };

        var baseColor = ThemeService.ResolveSurfaceColor(
            _viewModel.BackgroundColorHex,
            ActualThemeVariant == ThemeVariant.Dark);
        var opacity = translucent ? Math.Clamp(_viewModel.BackgroundOpacity, 40, 100) / 100d : 1d;

        ApplyPalette(baseColor, opacity, translucent, ActualThemeVariant == ThemeVariant.Dark);
        ApplyNativeTitleBarTheme(IsDark(baseColor), baseColor);

        if (translucent)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () =>
                {
                    if (ActualTransparencyLevel == WindowTransparencyLevel.None)
                    {
                        ApplyPalette(baseColor, 1d, false, ActualThemeVariant == ThemeVariant.Dark);
                    }
                },
                TimeSpan.FromMilliseconds(500));
        }
    }

    private static void ApplyPalette(Color baseColor, double opacity, bool translucent, bool themeDark)
    {
        var dark = IsDark(baseColor);
        var baseAlpha = translucent ? ToAlpha(opacity) : (byte)255;
        var subtleAlpha = translucent ? ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.78)) : (byte)255;
        var layerAlpha = translucent ? ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.90)) : (byte)255;
        // 列表与阅读区整片都是字：桌面上的深色/高对比内容透过来会和文字笔画叠在一起，
        // 实测 6% 的透出就足以让灰字糊掉，所以承字面的补强几乎贴到不透明（透明度 64% 时 ≈98%）。
        // 想看到壁纸的话，留给窗口背景和左侧栏，那里没有成片的正文。
        var contentAlpha = translucent ? ToAlpha(Math.Min(1d, opacity + (1d - opacity) * 0.94)) : (byte)255;

        var subtleColor = Adjust(baseColor, dark ? 0.02 : -0.02);
        var layerColor = Adjust(baseColor, 0.05);

        SetBrush("MailDogBrushSurfaceWindow", WithAlpha(baseColor, baseAlpha));
        SetBrush("MailDogBrushSurfaceSubtle", WithAlpha(subtleColor, subtleAlpha));
        SetBrush("MailDogBrushSurfacePane", WithAlpha(layerColor, layerAlpha));
        SetBrush("MailDogBrushSurfaceContent", WithAlpha(layerColor, contentAlpha));
        // 弹出菜单/浮层/设置页使用不透明版本，避免透出下方邮件内容。
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

    private void ApplyNativeTitleBarTheme(bool dark, Color baseColor)
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

        // COLORREF 为 0x00BBGGRR，让标题栏与主页面背景保持一致。
        var caption = baseColor.R | (baseColor.G << 8) | (baseColor.B << 16);
        var text = dark ? 0xFFFFFF : 0x1B1B1B;
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
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

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = true;
        NavigatePending();
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_internalNavigation)
        {
            _internalNavigation = false;
            return;
        }

        var request = e.Request;
        e.Cancel = true;

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
        if (!e.IsSuccess && _viewModel.Reader.UseHtml)
        {
            _viewModel.Reader.UseHtml = false;
            _viewModel.Reader.Error = "HTML 渲染失败，已切换为纯文本。";
        }
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.HtmlDocument))
        {
            _pendingHtml = _viewModel.Reader.HtmlDocument;
            NavigatePending();
        }
    }

    private void NavigatePending()
    {
        if (!_adapterReady || string.IsNullOrEmpty(_pendingHtml))
        {
            return;
        }

        _internalNavigation = true;
        WebView.NavigateToString(_pendingHtml);
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
