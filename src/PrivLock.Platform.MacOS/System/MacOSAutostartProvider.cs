using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.MacOS.System;

/// <summary>
/// Manages LaunchAgents (.plist in ~/Library/LaunchAgents) on macOS.
/// </summary>
public sealed class MacOSAutostartProvider : IAutostartProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MacOSAutostartProvider>();

    private readonly string _plistFilePath;

    public MacOSAutostartProvider()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launchAgentsDir = Path.Combine(home, "Library", "LaunchAgents");
        _plistFilePath = Path.Combine(launchAgentsDir, "com.cdevstudio.privgvard.plist");
    }

    public bool IsAutostartEnabled()
    {
        return File.Exists(_plistFilePath);
    }

    public OperationResult EnableAutostart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return OperationResult.Fail("Cannot determine executable path for macOS autostart.");
            }

            var dir = Path.GetDirectoryName(_plistFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var plistContent = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.cdevstudio.privgvard</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{exePath}</string>
                        <string>--minimized</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;

            File.WriteAllText(_plistFilePath, plistContent);
            Log.Information("macOS autostart enabled at {Path}", _plistFilePath);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create macOS LaunchAgent plist");
            return OperationResult.Fail(ex.Message);
        }
    }

    public OperationResult DisableAutostart()
    {
        try
        {
            if (File.Exists(_plistFilePath))
            {
                File.Delete(_plistFilePath);
                Log.Information("macOS autostart disabled");
            }
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to remove macOS LaunchAgent plist");
            return OperationResult.Fail(ex.Message);
        }
    }
}
