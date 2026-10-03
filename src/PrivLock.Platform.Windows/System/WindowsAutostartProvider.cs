using Microsoft.Win32;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.System;

/// <summary>
/// Manages autostart on Windows using HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Respects the Windows Task Manager's StartupApproved\Run registry overlay so that the
/// PrivGvard checkbox stays synchronized with the effective OS startup state.
/// </summary>
public sealed class WindowsAutostartProvider : IAutostartProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsAutostartProvider>();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "PrivGvard";
    private const string LegacyAppName = "PrivLock";

    private readonly string _runKeyPath;
    private readonly string _startupApprovedKeyPath;
    private readonly Func<string?> _processPathProvider;

    public WindowsAutostartProvider()
        : this(RunKeyPath, StartupApprovedKeyPath, () => Environment.ProcessPath)
    {
    }

    internal WindowsAutostartProvider(
        string runKeyPath,
        string startupApprovedKeyPath,
        Func<string?>? processPathProvider = null)
    {
        _runKeyPath = runKeyPath;
        _startupApprovedKeyPath = startupApprovedKeyPath;
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
    }

    /// <summary>
    /// Returns true only when the Run registry value exists, points to a valid file on disk,
    /// AND has not been disabled by the user via Task Manager or Settings > Apps > Startup.
    ///
    /// Windows disables startup entries by writing a 12-byte REG_BINARY value into
    /// HKCU\...\StartupApproved\Run. When the first three bytes are 03 00 00 (or any
    /// value with bit 0 of byte[0] set), the entry is disabled. When the entry is
    /// enabled (or the value is absent), the first byte is 02 (or 06) with bit 0 clear.
    /// </summary>
    public bool IsAutostartEnabled()
    {
        try
        {
            if (PackageIdentityHelper.IsRunningAsPackaged)
            {
                Log.Debug("Checking autostart in MSIX package context ({Package})", PackageIdentityHelper.PackageFullName);
            }

            // 1. Check whether the Run value exists at all
            using var runKey = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            var runValue = runKey?.GetValue(AppName) ?? runKey?.GetValue(LegacyAppName);
            if (runValue == null)
            {
                Log.Debug("Autostart registry value not found in Run key");
                return false;
            }

            // 2. Validate that the registered executable path still exists on disk
            var registeredPath = ExtractExecutablePath(runValue.ToString());
            if (!string.IsNullOrEmpty(registeredPath) && !File.Exists(registeredPath))
            {
                Log.Warning(
                    "Autostart registry value points to an executable that no longer exists on disk: {Path}",
                    registeredPath);
                return false;
            }

            // 3. Check whether Windows has disabled it through Task Manager / Settings.
            // Determine which name is actually registered and check only that one.
            var activeName = runKey?.GetValue(AppName) != null ? AppName : LegacyAppName;
            if (IsDisabledByStartupApproved(activeName))
            {
                Log.Debug("Autostart registry value exists but is disabled by Windows StartupApproved for {Name}", activeName);
                return false;
            }

            Log.Debug("Autostart is enabled: {Name}={Value}", activeName, runValue);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check Windows startup registry key");
            return false;
        }
    }

    public OperationResult EnableAutostart()
    {
        try
        {
            var exePath = _processPathProvider();
            if (string.IsNullOrEmpty(exePath))
            {
                var error = "Cannot determine current process path for autostart registration";
                Log.Error(error);
                return OperationResult.Fail(error);
            }

            // Warn if the path looks like a development build (but don't block it)
            if (exePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                exePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning(
                    "Autostart is being registered with a development build path. " +
                    "This registration will not survive after the installed application replaces this executable. " +
                    "Path: {Path}", exePath);
            }

            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            if (key == null)
            {
                var error = "Failed to open or create Run registry key for writing";
                Log.Error(error);
                return OperationResult.Fail(error);
            }

            if (PackageIdentityHelper.IsRunningAsPackaged)
            {
                Log.Information("Enabling startup in MSIX packaged context ({Package})", PackageIdentityHelper.PackageFullName);
            }

            key.SetValue(AppName, $"\"{exePath}\" --minimized");

            // Clean up any legacy PrivLock startup entry to avoid duplicate executions
            try
            {
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Legacy startup key cleanup skipped");
            }

            // Remove any Task Manager disable override so the entry becomes effective immediately
            RemoveStartupApprovedDisable(AppName);

            Log.Information("Windows startup enabled: Path={Path}, RegistryName={Name}", exePath, AppName);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable Windows startup");
            return OperationResult.Fail(ex.Message);
        }
    }

    public OperationResult DisableAutostart()
    {
        try
        {
            if (PackageIdentityHelper.IsRunningAsPackaged)
            {
                Log.Information("Disabling startup in MSIX packaged context ({Package})", PackageIdentityHelper.PackageFullName);
            }

            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            if (key != null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }

            // Also clean up StartupApproved entries to leave no trace
            RemoveStartupApprovedDisable(AppName);
            RemoveStartupApprovedDisable(LegacyAppName);

            Log.Information("Windows startup disabled");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable Windows startup");
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Checks whether a specific startup entry has been disabled by Windows Task Manager
    /// via the StartupApproved\Run registry overlay.
    /// </summary>
    private bool IsDisabledByStartupApproved(string valueName)
    {
        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath);
            if (approvedKey == null)
                return false;

            var rawValue = approvedKey.GetValue(valueName);
            if (rawValue is not byte[] bytes || bytes.Length < 1)
                return false;

            // Windows uses a 12-byte REG_BINARY in StartupApproved\Run.
            // Byte[0] bit 0 (0x01): when set (e.g. 0x03), the entry is DISABLED.
            // When clear (e.g. 0x02 or 0x06), the entry is ENABLED.
            var isDisabled = (bytes[0] & 0x01) != 0;
            Log.Debug("StartupApproved\\Run check: {Name} bytes[0]=0x{Byte:X2}, disabled={Disabled}",
                valueName, bytes[0], isDisabled);
            return isDisabled;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read StartupApproved\\Run for {Name}; assuming not disabled", valueName);
            return false;
        }
    }

    /// <summary>
    /// Removes the StartupApproved\Run override for a given value name so that Windows
    /// treats the Run entry as enabled. This is necessary when the user explicitly enables
    /// autostart from PrivGvard after having previously disabled it from Task Manager.
    /// </summary>
    private void RemoveStartupApprovedDisable(string valueName)
    {
        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, writable: true);
            if (approvedKey?.GetValue(valueName) != null)
            {
                approvedKey.DeleteValue(valueName, throwOnMissingValue: false);
                Log.Debug("Removed StartupApproved\\Run override for {Name}", valueName);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not remove StartupApproved\\Run override for {Name}", valueName);
        }
    }

    /// <summary>
    /// Extracts the executable path from a Run registry command-line string.
    /// Handles quoted paths (\"C:\Program Files\...\PrivGvard.exe\" --minimized)
    /// and unquoted paths (C:\PrivGvard\PrivGvard.exe --minimized).
    /// </summary>
    internal static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }
}
