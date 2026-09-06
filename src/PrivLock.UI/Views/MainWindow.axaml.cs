using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PrivLock.UI.Views;

public partial class MainWindow : Window
{
    private bool _allowApplicationClose;

    public event EventHandler? ApplicationExitRequested;

    public MainWindow()
    {
        InitializeComponent();
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
        ApplicationExitRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowApplicationClose)
        {
            e.Cancel = true;
            ApplicationExitRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    public void AllowApplicationClose() => _allowApplicationClose = true;
}
