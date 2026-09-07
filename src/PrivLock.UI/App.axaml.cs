using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PrivLock.Application.Services;
using PrivLock.Infrastructure.Common.Logging;
using PrivLock.UI.ViewModels;
using PrivLock.UI.Views;
using Serilog;

namespace PrivLock.UI;

public partial class App : Avalonia.Application
{
    private static readonly ILogger Log = Serilog.Log.ForContext<App>();

    private readonly MainViewModel? _mainViewModel;
    private readonly ShutdownCoordinator? _shutdownCoordinator;
    private readonly SettingsViewModel? _settingsViewModel;
    private TrayIcon? _trayIcon;
    private Window? _mainWindow;
    private bool _shutdownCommitted;
    private bool _shutdownInProgress;

    // Default constructor for designer
    public App()
    {
    }

    public App(MainViewModel mainViewModel, ShutdownCoordinator shutdownCoordinator, SettingsViewModel? settingsViewModel = null)
    {
        _mainViewModel = mainViewModel;
        _shutdownCoordinator = shutdownCoordinator;
        _settingsViewModel = settingsViewModel;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                var reportPath = CrashReporter.GenerateCrashReport(
                    e.Exception,
                    "Dispatcher.UnhandledException",
                    _shutdownCoordinator?.GetDiagnosticSummary());
                Log.Fatal(e.Exception, "Unhandled UI dispatcher exception. Crash report: {ReportPath}", reportPath);
                // Do not pretend the process can always recover in-place. Let the exception escape
                // to Program.Main; its centralized fallback will attempt restoration.
                e.Handled = false;
            };

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow(_settingsViewModel)
            {
                DataContext = _mainViewModel
            };
            mainWindow.ApplicationExitRequested += (_, _) =>
                _ = RequestShutdownAsync(desktop, "MainWindowClose", allowIncomplete: false);
            _mainWindow = mainWindow;
            desktop.MainWindow = _mainWindow;

            desktop.ShutdownRequested += (_, e) =>
            {
                if (_shutdownCommitted)
                    return;

                // Do not cancel Windows logout/shutdown and attempt to re-issue it as an app exit:
                // that can abort the original OS request. Start centralized recovery and return
                // immediately so the UI dispatcher can finish any already-admitted operation.
                // The observer is bounded, while the underlying durable recovery pass is allowed
                // to continue and Program's lifetime fallback joins the same coordinator task.
                _shutdownInProgress = true;
                _shutdownCommitted = true;
                _trayIcon?.Dispose();
                if (_mainWindow is MainWindow shutdownWindow)
                    shutdownWindow.AllowApplicationClose();

                if (_shutdownCoordinator != null)
                {
                    _ = ObserveOperatingSystemShutdownRestoreAsync(
                        _shutdownCoordinator,
                        TimeSpan.FromSeconds(8));
                }

                e.Cancel = false;
            };

            desktop.Exit += (_, _) => _trayIcon?.Dispose();

            // Setup Tray Icon
            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            WindowIcon? windowIcon = null;

            try
            {
                var uri = new Uri("avares://PrivLock.UI/Assets/logo.png");
                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    var bitmap = new Bitmap(stream);
                    windowIcon = new WindowIcon(bitmap);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load logo asset for tray icon, using system default icon");
            }

            if (_mainWindow != null && windowIcon != null)
            {
                _mainWindow.Icon = windowIcon;
            }

            var nativeMenu = new NativeMenu();

            var openItem = new NativeMenuItem("Abrir / Open PrivLock");
            openItem.Click += (_, _) => ShowMainWindow();

            var exitItem = new NativeMenuItem("Salir / Exit");
            exitItem.Click += (_, _) =>
                _ = RequestShutdownAsync(desktop, "TrayExit", allowIncomplete: false);

            nativeMenu.Items.Add(openItem);
            nativeMenu.Items.Add(new NativeMenuItemSeparator());
            nativeMenu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "PrivLock - Camera & Microphone Blocker",
                IsVisible = true,
                Menu = nativeMenu
            };

            if (windowIcon != null)
            {
                _trayIcon.Icon = windowIcon;
            }

            _trayIcon.Clicked += (_, _) => ShowMainWindow();

            var trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, trayIcons);

            Log.Information("System Tray Icon initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize System Tray Icon");
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.BringIntoView();
    }

    private static async Task ObserveOperatingSystemShutdownRestoreAsync(
        ShutdownCoordinator shutdownCoordinator,
        TimeSpan timeout)
    {
        try
        {
            var recovery = await shutdownCoordinator.RestoreWithinAsync(
                    "OperatingSystemShutdownOrLogout",
                    timeout)
                .ConfigureAwait(false);
            if (recovery is null)
            {
                Log.Error(
                    "OS shutdown is continuing before restoration completed; the durable recovery pass remains active and its WAL is retained");
            }
            else if (!recovery.SafeToExit)
            {
                Log.Error(
                    "OS shutdown is continuing with an incomplete restore; durable recovery remains for next startup: {Error}",
                    recovery.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "OS shutdown restoration observer failed; durable recovery remains for next startup");
        }
    }

    private async Task RequestShutdownAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string reason,
        bool allowIncomplete)
    {
        if (_shutdownCommitted || _shutdownInProgress || _shutdownCoordinator == null)
            return;

        _shutdownInProgress = true;
        try
        {
            var recovery = await _shutdownCoordinator.RestoreAsync(reason);
            if (!recovery.SafeToExit && !allowIncomplete)
            {
                _shutdownCoordinator.AbortShutdownAfterFailedUserExit();
                _shutdownInProgress = false;
                _mainViewModel?.ReportExternalError(
                    recovery.ErrorMessage ?? "PrivLock could not safely restore every owned change. Exit was cancelled.");
                ShowMainWindow();
                return;
            }

            _shutdownCommitted = true;
            _trayIcon?.Dispose();
            if (_mainWindow is MainWindow mainWindow)
                mainWindow.AllowApplicationClose();
            desktop.Shutdown(recovery.SafeToExit ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Centralized UI shutdown failed for reason {Reason}", reason);
            if (allowIncomplete)
            {
                // Windows logout/shutdown cannot be guaranteed to wait for application cleanup.
                // Preserve the durable journal for next startup and release the UI lifetime.
                _shutdownCommitted = true;
                _trayIcon?.Dispose();
                if (_mainWindow is MainWindow mainWindow)
                    mainWindow.AllowApplicationClose();
                desktop.Shutdown(1);
                return;
            }

            _shutdownCoordinator.AbortShutdownAfterFailedUserExit();
            _shutdownInProgress = false;
            _mainViewModel?.ReportExternalError($"Shutdown restoration failed: {ex.Message}");
            ShowMainWindow();
        }
    }
}
