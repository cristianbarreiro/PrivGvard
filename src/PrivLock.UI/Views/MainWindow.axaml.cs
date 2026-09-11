using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrivLock.UI.ViewModels;

namespace PrivLock.UI.Views;

public partial class MainWindow : Window
{
    private bool _allowApplicationClose;
    private readonly SettingsViewModel? _settingsViewModel;
    private SettingsWindow? _settingsWindow;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(SettingsViewModel? settingsViewModel)
    {
        _settingsViewModel = settingsViewModel;
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.OpenSettingsRequested -= OnOpenSettingsRequested;
            vm.OpenSettingsRequested += OnOpenSettingsRequested;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel vm)
        {
            vm.OpenSettingsRequested -= OnOpenSettingsRequested;
            vm.OpenSettingsRequested += OnOpenSettingsRequested;
        }
    }

    private void OnOpenSettingsRequested(SettingsSection section)
    {
        if (_settingsViewModel == null) return;

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settingsViewModel);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsViewModel.SelectedSection = section;

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Activate();
            _settingsWindow.BringIntoView();
        }
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowApplicationClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _settingsWindow?.Close();
        _settingsWindow = null;

        base.OnClosing(e);
    }

    public void AllowApplicationClose()
    {
        _allowApplicationClose = true;
        _settingsWindow?.Close();
        _settingsWindow = null;
    }
}

