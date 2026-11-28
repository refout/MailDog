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
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

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

}
