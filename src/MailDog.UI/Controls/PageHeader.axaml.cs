using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MailDog.UI.Controls;

/// <summary>内容列顶栏：返回按钮 + 标题，只有一条下边框，不跨导航列。</summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<PageHeader, ICommand?>(nameof(BackCommand));

    public static readonly StyledProperty<bool> ShowBackProperty =
        AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowBack), true);

    public PageHeader()
    {
        InitializeComponent();
        TitleText.Text = Title;
        TitleText.IsVisible = !string.IsNullOrWhiteSpace(Title);
        BackButton.IsVisible = ShowBack;
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public bool ShowBack
    {
        get => GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
            TitleText.IsVisible = !string.IsNullOrWhiteSpace(Title);
        }
        else if (change.Property == ShowBackProperty)
        {
            BackButton.IsVisible = ShowBack;
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
        => BackCommand?.Execute(null);
}
