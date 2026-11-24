using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace MailDog.UI.Services;

/// <summary>
/// 把设置里的外观项落到运行时：密度、主题变体。样式本身仍由 Themes/*.axaml 提供。
/// 紧凑档数值定义在 Tokens.axaml；标准档数值只在这里维护，切换时写回同名资源。
/// </summary>
public static class ThemeService
{
    private static readonly (string Key, double Compact, double Normal)[] DoubleTokens =
    [
        ("MailDogSizeControlHeight", 28, 32),
        ("MailDogSizeControlHeightSm", 24, 28),
        ("MailDogSizeControlHeightLg", 32, 36),
        ("MailDogSizeRowOneLine", 32, 36),
        ("MailDogSizeRowTwoLine", 56, 64),
        ("MailDogSizeMailRow", 64, 76),
        ("MailDogSizeCommandBar", 40, 48),
        ("MailDogSizePagePadding", 16, 24),
        ("MailDogSizeSectionGap", 12, 16)
    ];

    public static bool IsNormalDensity(string? density)
        => string.Equals(density, "normal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 将自定义背景色收敛到当前主题可读的明度范围。
    /// 保留色相，只向主题底色混合，避免浅色背景带进深色主题后出现白字白底。
    /// </summary>
    public static Color ResolveSurfaceColor(string? customHex, bool dark)
    {
        var fallback = dark ? Color.Parse("#202020") : Color.Parse("#F3F3F3");
        if (string.IsNullOrWhiteSpace(customHex) || !Color.TryParse(customHex, out var custom))
        {
            return fallback;
        }

        var luminance = ((0.2126 * custom.R) + (0.7152 * custom.G) + (0.0722 * custom.B)) / 255d;
        if (dark && luminance > 0.30)
        {
            return Mix(custom, fallback, 0.84);
        }

        if (!dark && luminance < 0.62)
        {
            return Mix(custom, fallback, 0.90);
        }

        return custom;
    }

    private static Color Mix(Color color, Color target, double targetAmount)
    {
        static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

        var sourceAmount = 1d - targetAmount;
        return Color.FromRgb(
            Clamp((color.R * sourceAmount) + (target.R * targetAmount)),
            Clamp((color.G * sourceAmount) + (target.G * targetAmount)),
            Clamp((color.B * sourceAmount) + (target.B * targetAmount)));
    }

    /// <summary>切换 Fluent 模板密度，并重设尺寸令牌（行高、控件高、页面留白）。</summary>
    public static void ApplyDensity(string? density)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var normal = IsNormalDensity(density);

        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is not null)
        {
            fluent.DensityStyle = normal ? DensityStyle.Normal : DensityStyle.Compact;
        }

        foreach (var token in DoubleTokens)
        {
            app.Resources[token.Key] = normal ? token.Normal : token.Compact;
        }

        app.Resources["MailDogPadPage"] = new Thickness(normal ? 24 : 16);
    }
}
