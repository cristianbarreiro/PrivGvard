using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using PrivLock.Infrastructure.Common.Logging;
using PrivLock.Infrastructure.Common.Storage;
using PrivLock.Platform.Abstractions;
using PrivLock.UI;
using PrivLock.UI.ViewModels;
using Serilog;

namespace PrivLock.Desktop;

public static class Program
{
    private static ShutdownCoordinator? _shutdownCoordinator;
    private static int _privilegedBarrierFailed;

    [STAThread]
    public static int Main(string[] args)
    {
        // 1. Accept only the authenticated, PID-bound elevated worker entry point.
        // The former --privileged-exec dispatcher was intentionally removed because a public
        // command-line mutation path could bypass the durable privacy-session journal.
        if (args.Length > 0 && args[0].Equals("--privileged-worker", StringComparison.OrdinalIgnoreCase))
        {
            return RunPrivilegedWorker(args);
        }

        if (args.Length > 0 && args[0].Equals("--privileged-exec", StringComparison.OrdinalIgnoreCase))
            return RejectRemovedPrivilegedDispatcher();

        // The user-facing process must always remain unelevated. Privileged work is accepted only
        // through the authenticated worker branch above.
        if (OperatingSystem.IsWindows() &&
            !SafeUninstallLauncher.TryConfirmStandardUserToken(out var elevationError))
        {
            System.Diagnostics.Trace.TraceError(
                "Rejected elevated PrivLock user process: {0}",
                elevationError ?? "token verification failed");
            return 1;
        }

        SafeUninstallPlan? safeUninstallPlan = null;
        if (SafeUninstallLauncher.IsRequested(args))
        {
            if (!SafeUninstallLauncher.TryCreatePlan(
                    args,
                    Environment.ProcessPath,
                    out safeUninstallPlan,
                    out var validationError))
            {
                System.Diagnostics.Trace.TraceError(
                    "Rejected safe-uninstall wrapper request: {0}",
                    validationError ?? "unknown validation failure");
                return 1;
            }
        }

        // 2. Initialize standard logging
        LoggingConfiguration.Initialize();
        Log.Information("=== PrivLock Desktop Starting ===");

        // 3. Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                var reportPath = CrashReporter.GenerateCrashReport(
                    ex,
                    "AppDomain.UnhandledException",
                    _shutdownCoordinator?.GetDiagnosticSummary());
                Log.Fatal(ex, "Fatal domain exception. Crash report: {ReportPath}", reportPath);
            }
            if (Volatile.Read(ref _privilegedBarrierFailed) == 0)
            {
                _shutdownCoordinator?.TryRestoreWithin(
                    "AppDomain.UnhandledException",
                    TimeSpan.FromSeconds(8),
                    out _);
            }
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            if (e.Exception is Exception ex)
            {
                var reportPath = CrashReporter.GenerateCrashReport(ex, "TaskScheduler.UnobservedTaskException");
                Log.Error(ex, "Unobserved task exception: {ReportPath}", reportPath);
            }
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            if (Volatile.Read(ref _privilegedBarrierFailed) == 0)
            {
                _shutdownCoordinator?.TryRestoreWithin(
                    "ProcessExit",
                    TimeSpan.FromSeconds(5),
                    out _);
            }
            Log.CloseAndFlush();
        };

        ServiceProvider? serviceProvider = null;
        ISingleInstanceGuard? singleInstanceGuard = null;
        try
        {
            // 4. Build Dependency Injection Service Provider
            var services = new ServiceCollection();
            ConfigureServices(services);
            serviceProvider = services.BuildServiceProvider();

            // 5. Acquire single-instance ownership before recovery or CLI cleanup touches shared state.
            singleInstanceGuard = serviceProvider.GetRequiredService<ISingleInstanceGuard>();
            if (!singleInstanceGuard.TryAcquireSingleInstance())
            {
                Log.Warning("Another instance of PrivLock is already running. Exiting without changing system state.");
                Log.CloseAndFlush();
                return 2;
            }

            _shutdownCoordinator = serviceProvider.GetRequiredService<ShutdownCoordinator>();

            if (OperatingSystem.IsWindows())
            {
                var quiesce = Platform.Windows.Privileged.WindowsPrivilegedExecutor
                    .WaitForPreviousPrivilegedOperation(TimeSpan.FromSeconds(30));
                if (!quiesce.Success)
                {
                    Interlocked.Exchange(ref _privilegedBarrierFailed, 1);
                    throw new InvalidOperationException(
                        quiesce.ErrorMessage ?? "A previous privileged operation is still running.");
                }
            }


            // 6. Recover a previous unfinished session before creating ViewModels or accepting actions.
            var recoveryService = serviceProvider.GetRequiredService<PrivacyRecoveryService>();
            var startupRecovery = recoveryService.RecoverAtStartupAsync().GetAwaiter().GetResult();

            // 7. Handle CLI recovery and the standard-user uninstall wrapper. The wrapper is
            // the only path that may elevate Inno Setup; PrivLock itself remains asInvoker.
            if (safeUninstallPlan != null ||
                args.Any(a => a.Equals("--unblock-and-exit", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-u", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Information("CLI recovery requested. Restoring only journal-owned changes...");
                var settingsService = serviceProvider.GetRequiredService<SettingsService>();
                var restoreReason = safeUninstallPlan == null
                    ? "CommandLineUnblockAndExit"
                    : "SafeUninstallWrapper";
                var restore = _shutdownCoordinator.RestoreAsync(restoreReason).GetAwaiter().GetResult();
                if (restore.SafeToExit)
                    settingsService.SetAutostart(false);

                var workerStopped = true;
                if (safeUninstallPlan != null && restore.SafeToExit)
                {
                    workerStopped = !OperatingSystem.IsWindows() ||
                        Platform.Windows.Privileged.WindowsPrivilegedSession.Instance.CloseSession();
                    if (!workerStopped)
                        Log.Error("Safe uninstall stopped because elevated worker quiescence could not be proven");
                }

                _shutdownCoordinator = null;

                var uninstallerStarted = true;
                if (safeUninstallPlan != null && restore.SafeToExit && workerStopped)
                {
                    string? launchError;
                    if (!OperatingSystem.IsWindows())
                    {
                        uninstallerStarted = false;
                        launchError = "Safe uninstall is supported only on Windows.";
                    }
                    else if (singleInstanceGuard is not
                             Platform.Windows.System.WindowsSingleInstanceGuard windowsGuard)
                    {
                        uninstallerStarted = false;
                        launchError = "The Windows uninstall gate is unavailable.";
                    }
                    else
                    {
                        uninstallerStarted = SafeUninstallLauncher.TryLaunch(
                            safeUninstallPlan,
                            windowsGuard.ReleaseUninstallGateForHandoff,
                            out launchError);
                    }

                    if (!uninstallerStarted)
                        Log.Error("Safe uninstall launch failed: {Error}", launchError);
                }

                Log.Information(
                    "CLI recovery complete. SafeToExit={SafeToExit}, UninstallerStarted={UninstallerStarted}",
                    restore.SafeToExit && workerStopped,
                    uninstallerStarted);
                Log.CloseAndFlush();
                return restore.SafeToExit && workerStopped && uninstallerStarted ? 0 : 1;
            }

            // 8. Initialize localization
            var localizationService = serviceProvider.GetRequiredService<LocalizationService>();
            localizationService.Initialize();

            // 9. Start Avalonia Application
            var mainViewModel = serviceProvider.GetRequiredService<MainViewModel>();
            var settingsViewModel = serviceProvider.GetRequiredService<SettingsViewModel>();
            if (!startupRecovery.SafeToExit || startupRecovery.ConflictCount > 0)
            {
                mainViewModel.ReportExternalError(
                    startupRecovery.ErrorMessage ?? "A previous privacy session could not be fully restored.");
            }

            var exitCode = BuildAvaloniaApp(mainViewModel, _shutdownCoordinator, settingsViewModel)
                .StartWithClassicDesktopLifetime(args);

            var finalRestoreFinished = _shutdownCoordinator.TryRestoreWithin(
                "ApplicationLifetimeExited",
                TimeSpan.FromSeconds(10),
                out var finalRestore);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Platform.Windows.Privileged.WindowsPrivilegedSession.Instance.CloseSession();
            }
            Log.Information(
                "=== PrivLock Exited (Code: {Code}, RestoreSafe={RestoreSafe}) ===",
                exitCode,
                finalRestore?.SafeToExit == true);
            _shutdownCoordinator = null;
            Log.CloseAndFlush();
            return finalRestoreFinished && finalRestore is { SafeToExit: true } ? exitCode : 1;
        }
        catch (Exception ex)
        {
            var reportPath = CrashReporter.GenerateCrashReport(
                ex,
                "Program.Main",
                _shutdownCoordinator?.GetDiagnosticSummary());
            Log.Fatal(ex, "Unhandled exception during application lifecycle: {ReportPath}", reportPath);
            if (Volatile.Read(ref _privilegedBarrierFailed) == 0)
                _shutdownCoordinator?.TryRestoreWithin("Program.Main.Catch", TimeSpan.FromSeconds(10), out _);
            _shutdownCoordinator = null;
            Log.CloseAndFlush();
            return 1;
        }
        finally
        {
            // Keep both DI services and the single-instance lease alive through fatal recovery.
            // Releasing either earlier permits a new process to race the old process's cleanup.
            singleInstanceGuard?.Release();
            serviceProvider?.Dispose();
        }
    }

    private static int RunPrivilegedWorker(string[] args)
    {
        // The elevated worker must never create logs/crash files below LocalAppData. That tree is
        // controlled by the unelevated user and may contain hostile reparse points.
        CrashReporter.SuppressFileOutputForCurrentProcess();
        try
        {
            if (OperatingSystem.IsWindows() &&
                args.Length == 4 &&
                int.TryParse(args[2], out var parentProcessId) &&
                Platform.Windows.Privileged.WindowsPrivilegedWorker.IsValidLaunchContext(
                    args[1],
                    parentProcessId,
                    args[3]))
            {
                return Platform.Windows.Privileged.WindowsPrivilegedWorker.Run(
                    args[1],
                    parentProcessId,
                    args[3]);
            }

            System.Diagnostics.Trace.TraceError("Rejected an invalid privileged-worker launch context");
            return 1;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "Privileged worker bootstrap failed: {0}: {1}",
                ex.GetType().FullName,
                ex.Message);
            return 1;
        }
    }

    private static int RejectRemovedPrivilegedDispatcher()
    {
        // This branch precedes token validation and may be invoked elevated. Do not open
        // user-controlled log paths merely to reject an obsolete command line.
        System.Diagnostics.Trace.TraceError("Rejected removed --privileged-exec entry point");
        return 1;
    }

    public static AppBuilder BuildAvaloniaApp(
        MainViewModel viewModel,
        ShutdownCoordinator shutdownCoordinator,
        SettingsViewModel? settingsViewModel = null) =>
        AppBuilder.Configure<App>(() => new App(viewModel, shutdownCoordinator, settingsViewModel))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ConfigureServices(IServiceCollection services)
    {
        // 1. Common Storage & Infrastructure
        services.AddSingleton<IStateStore, FileStateStore>();
        services.AddSingleton<IActivePrivacySessionMarker>(_ =>
            OperatingSystem.IsWindows()
                ? new FileActivePrivacySessionMarker(GetWindowsActiveMarkerDirectory())
                : new NoOpActivePrivacySessionMarker());
        services.AddSingleton<IPrivacySessionStore>(serviceProvider =>
            new FilePrivacySessionStore(
                serviceProvider.GetRequiredService<IActivePrivacySessionMarker>()));

        // 2. Platform-Specific Native Providers
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ConfigureWindowsServices(services);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ConfigureLinuxServices(services);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ConfigureMacServices(services);
        }
        else
        {
            throw new PlatformNotSupportedException($"Unsupported operating system: {RuntimeInformation.OSDescription}");
        }

        // 3. Application Services
        services.AddSingleton<ProtectionService>();
        services.AddSingleton<PrivacySessionService>();
        services.AddSingleton<PrivacyRecoveryService>();
        services.AddSingleton<ShutdownCoordinator>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LocalizationService>();

        // 4. UI ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ConfigureWindowsServices(IServiceCollection services)
    {
        services.AddSingleton<Platform.Windows.Devices.WindowsDeviceDetector>();
        services.AddSingleton<Platform.Windows.Devices.WindowsDeviceController>();
        services.AddSingleton<Platform.Windows.Devices.WindowsCoreAudioController>();
        services.AddSingleton<Platform.Windows.Policies.WindowsPolicyManager>();
        services.AddSingleton<Platform.Windows.Policies.WindowsUserPrivacyManager>();

        services.AddSingleton<IDeviceDetector>(sp => sp.GetRequiredService<Platform.Windows.Devices.WindowsDeviceDetector>());
        services.AddSingleton<IDeviceProtectionProvider, Platform.Windows.WindowsProtectionProvider>();
        services.AddSingleton<IPrivacySessionPlatformAdapter, Platform.Windows.WindowsPrivacySessionPlatformAdapter>();
        services.AddSingleton<IElevationProvider, Platform.Windows.Elevation.WindowsElevationProvider>();
        services.AddSingleton<IPlatformCapabilityProvider, Platform.Windows.WindowsCapabilityProvider>();
        services.AddSingleton<IAutostartProvider, Platform.Windows.System.WindowsAutostartProvider>();
        services.AddSingleton<IGlobalHotkeyProvider, Platform.Windows.System.WindowsHotkeyProvider>();
        services.AddSingleton<ISingleInstanceGuard, Platform.Windows.System.WindowsSingleInstanceGuard>();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void ConfigureLinuxServices(IServiceCollection services)
    {
        services.AddSingleton<Platform.Linux.Devices.LinuxDeviceDetector>();
        services.AddSingleton<Platform.Linux.Devices.LinuxDeviceController>();

        services.AddSingleton<IDeviceDetector>(sp => sp.GetRequiredService<Platform.Linux.Devices.LinuxDeviceDetector>());
        services.AddSingleton<IDeviceProtectionProvider, Platform.Linux.LinuxProtectionProvider>();
        services.AddSingleton<IPrivacySessionPlatformAdapter, UnsupportedPrivacySessionPlatformAdapter>();
        services.AddSingleton<IElevationProvider, Platform.Linux.Elevation.LinuxElevationProvider>();
        services.AddSingleton<IPlatformCapabilityProvider, Platform.Linux.LinuxCapabilityProvider>();
        services.AddSingleton<IAutostartProvider, Platform.Linux.System.LinuxAutostartProvider>();
        services.AddSingleton<ISingleInstanceGuard, Platform.Linux.System.LinuxSingleInstanceGuard>();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    private static void ConfigureMacServices(IServiceCollection services)
    {
        services.AddSingleton<Platform.MacOS.Devices.MacOSDeviceDetector>();
        services.AddSingleton<Platform.MacOS.Devices.MacOSDeviceController>();

        services.AddSingleton<IDeviceDetector>(sp => sp.GetRequiredService<Platform.MacOS.Devices.MacOSDeviceDetector>());
        services.AddSingleton<IDeviceProtectionProvider, Platform.MacOS.MacOSProtectionProvider>();
        services.AddSingleton<IPrivacySessionPlatformAdapter, UnsupportedPrivacySessionPlatformAdapter>();
        services.AddSingleton<IElevationProvider, Platform.MacOS.Elevation.MacOSElevationProvider>();
        services.AddSingleton<IPlatformCapabilityProvider, Platform.MacOS.MacOSCapabilityProvider>();
        services.AddSingleton<IAutostartProvider, Platform.MacOS.System.MacOSAutostartProvider>();
        services.AddSingleton<ISingleInstanceGuard, Platform.MacOS.System.MacOSSingleInstanceGuard>();
    }

    private static string GetWindowsActiveMarkerDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
            throw new InvalidOperationException("Windows did not provide a canonical current-user profile path.");

        return Path.Combine(userProfile, "AppData", "Local", "PrivLock", "Recovery");
    }

}
