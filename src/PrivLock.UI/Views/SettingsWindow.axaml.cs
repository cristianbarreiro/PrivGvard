using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrivLock.UI.ViewModels;

namespace PrivLock.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
