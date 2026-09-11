using System.Windows;
using CamMicBlocker.Application;
using CamMicBlocker.Domain.Models;
using CamMicBlocker.Infrastructure;
using CamMicBlocker.Logging;
using CamMicBlocker.UI.Notification;
using CamMicBlocker.UI.TrayIcon;
using Serilog;

namespace CamMicBlocker;

/// <summary>
/// Application entry point.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private TrayIconManager? _trayIconManager;
    private HotkeyService? _hotkeyService;
    private BlockingService? _blockingService;
    private UI.MainWindow.MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isUnblockMode = e.Args.Any(arg =>
            arg.Equals("--unblock-and-exit", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-u", StringComparison.OrdinalIgnoreCase));

        bool isMinimizedMode = e.Args.Any(arg =>
            arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-m", StringComparison.OrdinalIgnoreCase));

        // Step 1: Initialize Serilog logging
        LoggingConfiguration.Initialize();

        if (isUnblockMode)
        {
            Log.Information("App launched with --unblock-and-exit flag. Performing silent cleanup...");
            try
            {
                var detector = new DeviceDetector();
                var controller = new DeviceController();
                var policy = new PolicyManager();
                var store = new StateStore();
                var service = new BlockingService(detector, controller, policy, store);
                var startup = new StartupService();

                service.UnblockAsync(BlockTarget.Both).GetAwaiter().GetResult();
                startup.DisableStartup();
                Log.Information("Cleanup completed successfully. Exiting.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to perform silent unblock during cleanup");
            }
            finally
            {
                Log.CloseAndFlush();
                Shutdown(0);
            }
            return;
        }

        // Step 2: Single-instance check
        _singleInstanceMutex = new Mutex(true, @"Global\CamMicBlocker_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "CamMicBlocker is already running.\nCheck the system tray (notification area).",
                "CamMicBlocker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Information("[Startup 1/8] Single instance acquired and logging initialized");

        // Global exception handlers with structured CrashReporter dumps
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                var reportPath = CrashReporter.GenerateCrashReport(ex, "AppDomain.UnhandledException");
                Log.Fatal(ex, "Unhandled domain exception. Crash report: {ReportPath}", reportPath);
            }
            Log.CloseAndFlush();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var reportPath = CrashReporter.GenerateCrashReport(args.Exception, "DispatcherUnhandledException");
            Log.Error(args.Exception, "Unhandled dispatcher exception. Crash report: {ReportPath}", reportPath);
            args.Handled = true; // Prevent crash for recoverable UI errors
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (args.Exception is Exception ex)
            {
                var reportPath = CrashReporter.GenerateCrashReport(ex, "TaskScheduler.UnobservedTaskException");
                Log.Error(ex, "Unobserved task exception. Crash report: {ReportPath}", reportPath);
            }
            args.SetObserved();
        };

        try
        {
            // Step 3: Wire core infrastructure services
            Log.Information("[Startup 2/8] Instantiating in-process infrastructure services (DeviceDetector, DeviceController, PolicyManager, StateStore)");
            var deviceDetector = new DeviceDetector();
            var deviceController = new DeviceController();
            var policyManager = new PolicyManager();
            var stateStore = new StateStore();

            // Step 4: Instantiate application services
            Log.Information("[Startup 3/8] Instantiating application services (BlockingService, StartupService, LanguageService)");
            _blockingService = new BlockingService(
                deviceDetector,
                deviceController,
                policyManager,
                stateStore);

            var startupService = new StartupService();
            var languageService = new LanguageService(stateStore);
            languageService.Initialize();

            // Step 5: Instantiate main UI window
            Log.Information("[Startup 4/8] Instantiating MainWindow WPF UI");
            _mainWindow = new UI.MainWindow.MainWindow(_blockingService, startupService, languageService);

            // Step 6: Wire state change event handlers
            Log.Information("[Startup 5/8] Wiring StateChanged notification handlers");
            _blockingService.StateChanged += OnBlockStateChanged;

            // Step 7: Create system tray icon and menu
            Log.Information("[Startup 6/8] Initializing TrayIconManager and context menu");
            _trayIconManager = new TrayIconManager(_blockingService, startupService, languageService);
            _trayIconManager.ShowMainWindowRequested += ShowMainWindow;
            _trayIconManager.HideMainWindowRequested += HideMainWindow;
            _trayIconManager.ExitRequested += () =>
            {
                Log.Information("Exit requested by user via tray menu");
                Shutdown();
            };

            // Step 8: Register global hotkey
            Log.Information("[Startup 7/8] Registering global hotkey (Ctrl + Alt + B)");
            _hotkeyService = new HotkeyService();
            _hotkeyService.HotkeyPressed += async () =>
            {
                Log.Debug("Global hotkey triggered state toggle");
                await _blockingService.ToggleAsync(BlockTarget.Both);
            };

            if (!_hotkeyService.Register())
            {
                Log.Warning("Failed to register global hotkey Ctrl+Alt+B — shortcut may be in use by another app");
                _trayIconManager.ShowNotification(
                    "CamMicBlocker",
                    "Could not register Ctrl+Alt+B hotkey. Shortcut may be in use by another app.",
                    System.Windows.Forms.ToolTipIcon.Warning);
            }

            // Step 9: Reconcile initial system state
            Log.Information("[Startup 8/8] Reconciling initial device & policy state");
            var initialState = _blockingService.GetCurrentState();
            _trayIconManager.UpdateState(initialState);

            // Step 10: Show main window on launch unless starting minimized
            if (isMinimizedMode)
            {
                Log.Information("App launched with --minimized. Running in system tray.");
            }
            else
            {
                ShowMainWindow();
            }

            Log.Information("=== [Startup Complete] Application ready. Effective Camera={Camera}, Mic={Mic} ===",
                initialState.Camera.EffectiveStatus, initialState.Microphone.EffectiveStatus);
        }
        catch (Exception ex)
        {
            var reportPath = CrashReporter.GenerateCrashReport(ex, "App.OnStartup");
            Log.Fatal(ex, "Fatal error during startup. Crash report saved to {ReportPath}", reportPath);

            System.Windows.MessageBox.Show(
                $"CamMicBlocker failed to start:\n{ex.Message}\n\nCrash report saved to:\n{reportPath}\nLogs directory:\n{LoggingConfiguration.GetLogDirectory()}",
                "CamMicBlocker — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow == null) return;
            Log.Debug("Showing MainWindow");
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.RefreshState();
        });
    }

    private void HideMainWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow == null) return;
            Log.Debug("Hiding MainWindow");
            _mainWindow.Hide();
        });
    }

    private void OnBlockStateChanged(BlockState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (state.AllBlocked)
            {
                NotificationWindow.Show("Camera & Microphone: BLOCKED", isBlocked: true);
            }
            else if (state.AllAllowed)
            {
                NotificationWindow.Show("Camera & Microphone: Allowed", isBlocked: false);
            }
            else
            {
                var cameraText = state.Camera.EffectiveStatus == BlockStatus.Blocked ? "Blocked" : "Allowed";
                var micText = state.Microphone.EffectiveStatus == BlockStatus.Blocked ? "Blocked" : "Allowed";
                NotificationWindow.Show($"Camera: {cameraText}, Mic: {micText}",
                    isBlocked: state.Camera.EffectiveStatus == BlockStatus.Blocked);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application shutting down cleanly");

        _hotkeyService?.Dispose();
        _trayIconManager?.Dispose();

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
        }

        Log.Information("=== CamMicBlocker Stopped ===");
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
