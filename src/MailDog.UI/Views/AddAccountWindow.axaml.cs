using Avalonia.Controls;
using Avalonia.Interactivity;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Views;

public partial class AddAccountWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public AddAccountWindow()
        : this(new MainWindowViewModel(new Services.EngineClient()))
    {
    }

    public AddAccountWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.AccountFlowCompleted += OnAccountFlowCompleted;
        Closed += OnClosed;
    }

    private void OnAccountFlowCompleted()
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
        _viewModel.AccountFlowCompleted -= OnAccountFlowCompleted;
    }
}
