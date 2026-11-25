using Avalonia.Controls;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class EmlViewerWindow : Window
{
    public EmlViewerWindow()
    {
        InitializeComponent();
    }

    public EmlViewerWindow(EmlPreviewViewModel preview)
        : this()
    {
        DataContext = preview;
    }
}
