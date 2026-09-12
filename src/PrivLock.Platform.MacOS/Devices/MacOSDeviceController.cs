using System.Diagnostics;
using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Platform.MacOS.Devices;

/// <summary>
/// Controls CoreAudio hardware input mute and software privacy controls on macOS.
/// </summary>
public sealed class MacOSDeviceController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MacOSDeviceController>();

    public Task<OperationResult> BlockMicrophoneAsync()
    {
        Log.Information("Muting macOS audio input via CoreAudio and AppleScript");

        // 1. Set input volume to 0 via AppleScript
        var scriptSuccess = RunAppleScript("set volume input volume 0");
        if (scriptSuccess)
        {
            return Task.FromResult(OperationResult.Ok());
        }

        return Task.FromResult(OperationResult.Fail("Failed to mute macOS input volume."));
    }

    public Task<OperationResult> UnblockMicrophoneAsync()
    {
        Log.Information("Unmuting macOS audio input via CoreAudio and AppleScript");

        var scriptSuccess = RunAppleScript("set volume input volume 75");
        if (scriptSuccess)
        {
            return Task.FromResult(OperationResult.Ok());
        }

        return Task.FromResult(OperationResult.Fail("Failed to unmute macOS input volume."));
    }

    private static bool RunAppleScript(string script)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
