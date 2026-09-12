using System.Diagnostics;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Platform.Linux.Devices;

/// <summary>
/// Controls camera permissions (V4L2/ACLs/sysfs) and microphone streams (PipeWire/PulseAudio/wpctl/pactl) on Linux.
/// </summary>
public sealed class LinuxDeviceController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxDeviceController>();

    public Task<OperationResult> BlockCameraAsync(IEnumerable<DeviceInfo> cameras)
    {
        Log.Information("Blocking Linux camera devices");
        var details = new List<DeviceOperationDetail>();

        foreach (var cam in cameras)
        {
            try
            {
                // Attempt to revoke access via chmod 000 /dev/video* or setfacl if available
                if (File.Exists(cam.Id))
                {
                    RunProcess("chmod", $"000 \"{cam.Id}\"");
                }

                details.Add(new DeviceOperationDetail
                {
                    DeviceId = cam.Id,
                    FriendlyName = cam.FriendlyName,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to block Linux camera node {Id}", cam.Id);
                details.Add(new DeviceOperationDetail
                {
                    DeviceId = cam.Id,
                    FriendlyName = cam.FriendlyName,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        return Task.FromResult(OperationResult.Ok(details));
    }

    public Task<OperationResult> UnblockCameraAsync(IEnumerable<DeviceInfo> cameras)
    {
        Log.Information("Unblocking Linux camera devices");
        var details = new List<DeviceOperationDetail>();

        foreach (var cam in cameras)
        {
            try
            {
                if (File.Exists(cam.Id))
                {
                    RunProcess("chmod", $"660 \"{cam.Id}\"");
                }

                details.Add(new DeviceOperationDetail
                {
                    DeviceId = cam.Id,
                    FriendlyName = cam.FriendlyName,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to restore Linux camera node {Id}", cam.Id);
                details.Add(new DeviceOperationDetail
                {
                    DeviceId = cam.Id,
                    FriendlyName = cam.FriendlyName,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        return Task.FromResult(OperationResult.Ok(details));
    }

    public Task<OperationResult> BlockMicrophoneAsync()
    {
        Log.Information("Muting and locking Linux microphone sources via PipeWire/PulseAudio");

        // 1. Try WirePlumber (wpctl)
        var wpSuccess = RunProcess("wpctl", "set-mute @DEFAULT_AUDIO_SOURCE@ 1");
        
        // 2. Try PulseAudio (pactl) as fallback
        var paSuccess = RunProcess("pactl", "set-source-mute @DEFAULT_SOURCE@ 1");

        if (wpSuccess || paSuccess)
        {
            return Task.FromResult(OperationResult.Ok());
        }

        return Task.FromResult(OperationResult.Fail("Failed to mute audio sources via wpctl and pactl."));
    }

    public Task<OperationResult> UnblockMicrophoneAsync()
    {
        Log.Information("Unmuting Linux microphone sources via PipeWire/PulseAudio");

        var wpSuccess = RunProcess("wpctl", "set-mute @DEFAULT_AUDIO_SOURCE@ 0");
        var paSuccess = RunProcess("pactl", "set-source-mute @DEFAULT_SOURCE@ 0");

        if (wpSuccess || paSuccess)
        {
            return Task.FromResult(OperationResult.Ok());
        }

        return Task.FromResult(OperationResult.Fail("Failed to unmute audio sources via wpctl and pactl."));
    }

    private static bool RunProcess(string command, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
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
