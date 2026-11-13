using Avalonia.Controls;
using Avalonia.Interactivity;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public SettingsWindow()
        : this(new MainWindowViewModel(new Services.EngineClient()))
    {
    }

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.SettingsSaved += OnSettingsSaved;
        Closed += OnClosed;
    }

    private void OnSettingsSaved()
    {
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.SettingsSaved -= OnSettingsSaved;
    }
}
