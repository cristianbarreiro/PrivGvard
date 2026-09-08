using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Linux.System;

/// <summary>
/// Manages XDG Autostart (.desktop entry in ~/.config/autostart) on Linux.
/// </summary>
public sealed class LinuxAutostartProvider : IAutostartProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxAutostartProvider>();

    private readonly string _desktopFilePath;

    public LinuxAutostartProvider()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var autostartDir = Path.Combine(home, ".config", "autostart");
        _desktopFilePath = Path.Combine(autostartDir, "privgvard.desktop");
    }

    public bool IsAutostartEnabled()
    {
        return File.Exists(_desktopFilePath);
    }

    public OperationResult EnableAutostart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return OperationResult.Fail("Cannot determine executable path for Linux autostart.");
            }

            var dir = Path.GetDirectoryName(_desktopFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var desktopEntry = $"""
                [Desktop Entry]
                Type=Application
                Name=PrivGvard
                Comment=Camera & Microphone Privacy Guard
                Exec="{exePath}" --minimized
                Icon=privgvard
                Terminal=false
                Categories=Utility;Security;
                X-GNOME-Autostart-enabled=true
                """;

            File.WriteAllText(_desktopFilePath, desktopEntry);
            Log.Information("Linux autostart enabled at {Path}", _desktopFilePath);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create Linux autostart desktop entry");
            return OperationResult.Fail(ex.Message);
        }
    }

    public OperationResult DisableAutostart()
    {
        try
        {
            if (File.Exists(_desktopFilePath))
            {
                File.Delete(_desktopFilePath);
                Log.Information("Linux autostart disabled");
            }
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to remove Linux autostart desktop entry");
            return OperationResult.Fail(ex.Message);
        }
    }
}
