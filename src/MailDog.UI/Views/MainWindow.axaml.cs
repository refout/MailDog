using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    private readonly MainWindowViewModel _viewModel;
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

        WebView.EnvironmentRequested += OnEnvironmentRequested;
        WebView.AdapterCreated += OnAdapterCreated;
        WebView.NavigationStarted += OnNavigationStarted;
        WebView.NavigationCompleted += OnNavigationCompleted;
        viewModel.Reader.PropertyChanged += OnReaderPropertyChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        ApplyAppearance();
        await _viewModel.InitializeAsync();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyAppearance();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.MaterialIndex)
            or nameof(MainWindowViewModel.BackgroundColorHex)
            or nameof(MainWindowViewModel.BackgroundOpacity))
        {
            ApplyAppearance();
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

        var baseColor = ParseColor(_viewModel.BackgroundColorHex) ?? GetThemeBaseColor();
        var opacity = translucent ? Math.Clamp(_viewModel.BackgroundOpacity, 40, 100) / 100d : 1d;

        ApplyPalette(baseColor, opacity, translucent, ActualThemeVariant == ThemeVariant.Dark);
        ApplyNativeTitleBarTheme(IsDark(baseColor));

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
        var subtleAlpha = translucent ? ToAlpha(Math.Min(1d, opacity + 0.06)) : (byte)255;
        var layerAlpha = translucent ? ToAlpha(Math.Min(1d, opacity + 0.14)) : (byte)255;

        var subtleColor = Adjust(baseColor, dark ? 0.02 : -0.02);
        var layerColor = Adjust(baseColor, 0.05);

        SetBrush("MailDogBrushBgBase", WithAlpha(baseColor, baseAlpha));
        SetBrush("MailDogBrushBgSubtle", WithAlpha(subtleColor, subtleAlpha));
        SetBrush("MailDogBrushBgLayer", WithAlpha(layerColor, layerAlpha));
        SetBrush("MailDogBrushBgOverlay", WithAlpha(baseColor, 255));
        SetBrush("MailDogBrushHover", dark ? Color.Parse("#0FFFFFFF") : Color.Parse("#0A000000"));
        SetBrush("MailDogBrushStroke", dark ? Color.Parse("#3D3D3D") : Color.Parse("#E0E0E0"));
        SetBrush("MailDogBrushStrokeSubtle", dark ? Color.Parse("#333333") : Color.Parse("#EBEBEB"));
        SetBrush("MailDogBrushTextPrimary", dark ? Colors.White : Color.Parse("#1B1B1B"));
        SetBrush("MailDogBrushTextSecondary", dark ? Color.Parse("#C7C7C7") : Color.Parse("#616161"));
        SetBrush("MailDogBrushTextTertiary", dark ? Color.Parse("#9A9A9A") : Color.Parse("#8A8A8A"));
        SetBrush("MailDogBrushTextDisabled", dark ? Color.Parse("#6E6E6E") : Color.Parse("#BDBDBD"));
        SetBrush("MailDogBrushTextOnAccent", themeDark ? Color.Parse("#0A0A0A") : Colors.White);
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

    private static Color? ParseColor(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color) ? color : null;
    }

    private Color GetThemeBaseColor()
    {
        return ActualThemeVariant == ThemeVariant.Dark
            ? Color.Parse("#202020")
            : Color.Parse("#F3F3F3");
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

    private void ApplyNativeTitleBarTheme(bool dark)
    {
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
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

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        KeyDown -= OnWindowKeyDown;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        WebView.EnvironmentRequested -= OnEnvironmentRequested;
        WebView.AdapterCreated -= OnAdapterCreated;
        WebView.NavigationStarted -= OnNavigationStarted;
        WebView.NavigationCompleted -= OnNavigationCompleted;
        _viewModel.Reader.PropertyChanged -= OnReaderPropertyChanged;
        _viewModel.Dispose();
    }
}
