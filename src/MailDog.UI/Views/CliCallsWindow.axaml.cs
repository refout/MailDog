using Avalonia.Controls;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class CliCallsWindow : Window
{
    public CliCallsWindow()
    {
        InitializeComponent();
    }

    public CliCallsWindow(CliCallsViewModel calls)
        : this()
    {
        DataContext = calls;
    }
}
