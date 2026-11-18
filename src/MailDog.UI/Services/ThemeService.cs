using System;
using System.Linq;
using Avalonia;
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
        ("MailDogSizeCommandBar", 40, 48),
        ("MailDogSizePagePadding", 16, 24),
        ("MailDogSizeSectionGap", 12, 16)
    ];

    public static bool IsNormalDensity(string? density)
        => string.Equals(density, "normal", StringComparison.OrdinalIgnoreCase);

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
