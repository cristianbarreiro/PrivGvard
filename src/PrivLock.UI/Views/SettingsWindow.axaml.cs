using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PrivLock.UI.ViewModels;

namespace PrivLock.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _autostartRefreshTimer;

    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _autostartRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autostartRefreshTimer.Tick += async (_, _) =>
        {
            if (ViewModel is { } viewModel)
                await viewModel.RefreshAutostartStatusAsync();
        };
        Opened += (_, _) =>
        {
            if (ViewModel is { } viewModel)
            {
                _ = viewModel.RefreshAutostartStatusAsync();
                if (viewModel.RequiresVisibleAutostartPolling)
                    _autostartRefreshTimer.Start();
            }
        };
        Activated += (_, _) =>
        {
            if (ViewModel is { } viewModel)
                _ = viewModel.RefreshAutostartStatusAsync();
        };
        Closed += (_, _) => _autostartRefreshTimer.Stop();
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
