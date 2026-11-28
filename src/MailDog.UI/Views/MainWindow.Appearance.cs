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
        ApplyNativeTitleBarTheme(IsDark(baseColor), baseColor, material);

        if (translucent)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () =>
                {
                    if (ActualTransparencyLevel == WindowTransparencyLevel.None)
                    {
                        ApplyPalette(baseColor, 1d, false, ActualThemeVariant == ThemeVariant.Dark, false);
                        ApplyNativeTitleBarTheme(IsDark(baseColor), baseColor, "none");
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

    private void ApplyNativeTitleBarTheme(bool dark, Color baseColor, string material)
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

        // Avalonia 的 Mica/Acrylic 只作用于客户区；要覆盖原生非客户区标题栏，
        // 必须额外告诉 DWM 使用系统背景材质。Build 22621 起才支持该属性。
        var materialActive = !string.Equals(material, "none", StringComparison.Ordinal);
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            var backdrop = material switch
            {
                "mica" => 2,     // DWMSBT_MAINWINDOW
                "acrylic" => 3,  // DWMSBT_TRANSIENTWINDOW
                _ => 1           // DWMSBT_NONE
            };
            _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }

        if (materialActive)
        {
            // 恢复 DWM 默认绘制，让 DWMWA_SYSTEMBACKDROP_TYPE 的材质能覆盖标题栏。
            // 继续写入固定 caption/border 颜色会把系统材质盖回纯色。
            var defaultColor = DwmwaColorDefault;
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref defaultColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref defaultColor, sizeof(int));
            return;
        }

        // COLORREF 为 0x00BBGGRR，让标题栏与主页面背景保持一致。
        var caption = baseColor.R | (baseColor.G << 8) | (baseColor.B << 16);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
    }

}
