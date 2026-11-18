using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class ComposeView : UserControl
{
    public ComposeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => BodyBox.Focus();
    }

    private async void AddAttachment_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposeViewModel viewModel)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择附件",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                viewModel.AddAttachment(path);
            }
        }
    }
}
