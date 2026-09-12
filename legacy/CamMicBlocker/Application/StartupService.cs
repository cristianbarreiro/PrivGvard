using Microsoft.Win32;
using Serilog;

namespace CamMicBlocker.Application;

/// <summary>
/// Manages the "Start with Windows" feature.
/// Uses the HKCU\Software\Microsoft\Windows\CurrentVersion\Run registry key
/// which does NOT require admin privileges (it's per-user).
/// 
/// The startup entry points to the compiled EXE (not a PowerShell command),
/// which avoids path escaping issues and ExecutionPolicy concerns.
/// </summary>
public sealed class StartupService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<StartupService>();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CamMicBlocker";

    /// <summary>
    /// Checks if the app is currently set to start with Windows.
    /// </summary>
    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(AppName);
            return value != null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check startup registry key");
            return false;
        }
    }

    /// <summary>
    /// Enables starting with Windows by adding a registry entry pointing to the current EXE.
    /// </summary>
    public bool EnableStartup()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Error("Cannot determine current process path for startup registration");
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                Log.Error("Failed to open Run registry key for writing");
                return false;
            }

            // Quote the path to handle spaces/special characters and include --minimized flag
            key.SetValue(AppName, $"\"{exePath}\" --minimized");
            Log.Information("Startup enabled: {Path}", exePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable startup");
            return false;
        }
    }

    /// <summary>
    /// Disables starting with Windows by removing the registry entry.
    /// </summary>
    public bool DisableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            Log.Information("Startup disabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable startup");
            return false;
        }
    }
}
