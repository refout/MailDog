using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace MailDog.UI.ViewModels;

public sealed class BackgroundPresetViewModel
{
    public BackgroundPresetViewModel(string name, string? value, string preview, bool isSelected, Action<string?> select)
    {
        Name = name;
        Value = value;
        Brush = new SolidColorBrush(Color.Parse(preview));
        IsSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(value));
    }

    public string Name { get; }

    public string? Value { get; }

    public IBrush Brush { get; }

    public bool IsSelected { get; }

    public IRelayCommand SelectCommand { get; }
}
