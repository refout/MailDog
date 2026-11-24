using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MailDog.UI.Controls;

/// <summary>按搜索词拆分文本并在命中片段上铺一层半透明底色。</summary>
public sealed class HighlightedTextBlock : TextBlock
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#4D0F6CBD"));

    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(Query));

    static HighlightedTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightedTextBlock>(
            static (control, _) => control.Rebuild());
        QueryProperty.Changed.AddClassHandler<HighlightedTextBlock>(
            static (control, _) => control.Rebuild());
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    private void Rebuild()
    {
        var text = SourceText ?? string.Empty;
        var query = Query?.Trim() ?? string.Empty;

        if (query.Length == 0 || text.Length == 0)
        {
            Inlines = null;
            Text = text;
            return;
        }

        var inlines = new InlineCollection();
        var start = 0;
        while (start < text.Length)
        {
            var match = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                inlines.Add(new Run { Text = text[start..] });
                break;
            }

            if (match > start)
            {
                inlines.Add(new Run { Text = text[start..match] });
            }

            inlines.Add(new Run
            {
                Text = text.Substring(match, query.Length),
                Background = MatchBrush,
                FontWeight = FontWeight.SemiBold
            });

            start = match + query.Length;
        }

        Text = null;
        Inlines = inlines;
    }
}
