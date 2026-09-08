using Microsoft.Win32;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.System;

/// <summary>
/// Manages autostart on Windows using HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class WindowsAutostartProvider : IAutostartProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsAutostartProvider>();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PrivGvard";
    private const string LegacyAppName = "PrivLock";

    public bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(AppName) ?? key?.GetValue(LegacyAppName);
            return value != null;
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
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                var error = "Cannot determine current process path for autostart registration";
                Log.Error(error);
                return OperationResult.Fail(error);
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                var error = "Failed to open Run registry key for writing";
                Log.Error(error);
                return OperationResult.Fail(error);
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

            Log.Information("Windows startup enabled: {Path}", exePath);
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key != null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
            Log.Information("Windows startup disabled");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable Windows startup");
            return OperationResult.Fail(ex.Message);
        }
    }
}
