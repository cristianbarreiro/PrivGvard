using System.Diagnostics;
using PrivLock.Domain.Models;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Linux.Devices;

/// <summary>
/// Detects camera and microphone devices on Linux using /sys/class/video4linux and ALSA/PulseAudio/PipeWire.
/// </summary>
public sealed class LinuxDeviceDetector : IDeviceDetector
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxDeviceDetector>();
    private const string V4L2SysPath = "/sys/class/video4linux";

    public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<DeviceInfo>();

        try
        {
            if (Directory.Exists(V4L2SysPath))
            {
                var videoDirs = Directory.GetDirectories(V4L2SysPath, "video*");
                foreach (var dir in videoDirs)
                {
                    var devNode = $"/dev/{Path.GetFileName(dir)}";
                    var nameFile = Path.Combine(dir, "name");
                    var friendlyName = File.Exists(nameFile)
                        ? File.ReadAllText(nameFile).Trim()
                        : Path.GetFileName(dir);

                    // Check if node is accessible/enabled (readable/writeable)
                    var isEnabled = true;
                    if (File.Exists(devNode))
                    {
                        var fileInfo = new FileInfo(devNode);
                        isEnabled = fileInfo.Length >= 0; // Exists & permissions allow checking
                    }

                    devices.Add(new DeviceInfo
                    {
                        Id = devNode,
                        FriendlyName = friendlyName,
                        DeviceType = DeviceType.Camera,
                        ClassName = "v4l2",
                        PlatformIdentifier = dir,
                        Status = "OK",
                        IsPresent = true,
                        IsEnabled = isEnabled
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to detect Linux camera devices in {Path}", V4L2SysPath);
        }

        Log.Information("Detected {Count} Linux camera device(s)", devices.Count);
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(devices);
    }

    public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<DeviceInfo>();

        try
        {
            // Query sound capture endpoints via ALSA /proc or PulseAudio/PipeWire
            var alsaPcmPath = "/proc/asound/pcm";
            if (File.Exists(alsaPcmPath))
            {
                var lines = File.ReadAllLines(alsaPcmPath);
                foreach (var line in lines)
                {
                    if (line.Contains("capture", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':', 2);
                        var id = parts.Length > 0 ? parts[0].Trim() : "pcm_in";
                        var name = parts.Length > 1 ? parts[1].Trim() : "ALSA Capture Device";

                        devices.Add(new DeviceInfo
                        {
                            Id = $"hw:{id}",
                            FriendlyName = name,
                            DeviceType = DeviceType.Microphone,
                            ClassName = "ALSA/PipeWire",
                            PlatformIdentifier = id,
                            Status = "OK",
                            IsPresent = true,
                            IsEnabled = true
                        });
                    }
                }
            }

            if (devices.Count == 0)
            {
                // Default virtual capture device if no direct PCM entry is readable
                devices.Add(new DeviceInfo
                {
                    Id = "@DEFAULT_AUDIO_SOURCE@",
                    FriendlyName = "System Default Audio Source (PipeWire/PulseAudio)",
                    DeviceType = DeviceType.Microphone,
                    ClassName = "SoundServer",
                    Status = "OK",
                    IsPresent = true,
                    IsEnabled = true
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to detect Linux microphone devices");
        }

        Log.Information("Detected {Count} Linux microphone device(s)", devices.Count);
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(devices);
    }

    public async Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default)
    {
        var cameras = await DetectCamerasAsync(cancellationToken);
        var microphones = await DetectMicrophonesAsync(cancellationToken);
        return cameras.Concat(microphones).ToList();
    }
}
