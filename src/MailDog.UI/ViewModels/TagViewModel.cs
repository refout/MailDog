using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class TagViewModel : ObservableObject
{
    private readonly Action<TagViewModel> _edit;
    private readonly Action<TagViewModel> _delete;

    public TagViewModel(TagDto tag, Action<TagViewModel> edit, Action<TagViewModel> delete)
    {
        Tag = tag;
        _edit = edit;
        _delete = delete;
        Brush = new SolidColorBrush(ParseColor(tag.Color));
    }

    public TagDto Tag { get; }

    public long Id => Tag.Id;

    public string Name => Tag.Name;

    public string Color => Tag.Color;

    public IBrush Brush { get; }

    public string ColorHint => Tag.Color;

    [ObservableProperty]
    private bool _deleteConfirmVisible;

    [RelayCommand]
    private void Edit() => _edit(this);

    [RelayCommand]
    private void AskDelete() => DeleteConfirmVisible = true;

    [RelayCommand]
    private void ConfirmDelete()
    {
        DeleteConfirmVisible = false;
        _delete(this);
    }

    [RelayCommand]
    private void CancelDelete() => DeleteConfirmVisible = false;

    internal static Color ParseColor(string? color)
        => Avalonia.Media.Color.TryParse(color, out var parsed)
            ? parsed
            : Avalonia.Media.Color.Parse("#0F7B0F");
}

public sealed class MessageTagChipViewModel
{
    public MessageTagChipViewModel(TagDto tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        Color = tag.Color;
        var parsed = TagViewModel.ParseColor(tag.Color);
        Background = new SolidColorBrush(parsed, 0.14);
        Border = new SolidColorBrush(parsed);
        Foreground = new SolidColorBrush(IsDark(parsed) ? Colors.White : parsed);
    }

    public long Id { get; }

    public string Name { get; }

    public string Color { get; }

    public IBrush Background { get; }

    public IBrush Border { get; }

    public IBrush Foreground { get; }

    private static bool IsDark(Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) < 140;
}

public sealed partial class TagMenuViewModel : ObservableObject
{
    private readonly Action<TagMenuViewModel> _add;
    private readonly Action<TagMenuViewModel> _remove;

    public TagMenuViewModel(TagDto tag, Action<TagMenuViewModel> add, Action<TagMenuViewModel> remove)
    {
        Tag = tag;
        _add = add;
        _remove = remove;
    }

    public TagDto Tag { get; }

    public long Id => Tag.Id;

    public string Name => Tag.Name;

    [RelayCommand]
    private void Add() => _add(this);

    [RelayCommand]
    private void Remove() => _remove(this);
}

public sealed partial class TagFilterViewModel : ObservableObject
{
    private readonly Action<TagFilterViewModel> _select;

    public TagFilterViewModel(TagDto tag, Action<TagFilterViewModel> select)
    {
        Id = tag.Id;
        Name = tag.Name;
        Brush = new SolidColorBrush(TagViewModel.ParseColor(tag.Color));
        _select = select;
    }

    public long Id { get; }

    public string Name { get; }

    public IBrush Brush { get; }

    [RelayCommand]
    private void Select() => _select(this);
}
