using System.Diagnostics;
using System.Management;
using PrivLock.Domain.Models;
using PrivLock.Infrastructure.Common.Logging;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.Devices;

/// <summary>
/// Detects cameras and audio capture endpoints on Windows using WMI queries filtered strictly by Class GUIDs.
/// </summary>
public sealed class WindowsDeviceDetector : IDeviceDetector
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsDeviceDetector>();

    // Windows Camera device class GUID (exclusive to cameras)
    private const string CameraClassGuid = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";

    // Windows AudioEndpoint device class GUID (contains render + capture endpoints)
    private const string AudioEndpointClassGuid = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";

    // Instance ID pattern for audio capture (microphone) endpoints: SWD\MMDEVAPI\{0.0.1.*
    private const string CaptureEndpointPattern = "{0.0.1.";

    public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log.Debug("Detecting Windows camera devices via ClassGuid {ClassGuid}", CameraClassGuid);

        var devices = QueryDevicesByClassGuid(CameraClassGuid, DeviceType.Camera);

        sw.Stop();
        Log.Information("Detected {Count} camera device(s) in {DurationMs}ms",
            devices.Count,
            sw.ElapsedMilliseconds);

        return Task.FromResult<IReadOnlyList<DeviceInfo>>(devices);
    }

    public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log.Debug("Detecting Windows microphone devices via AudioEndpoint ClassGuid {ClassGuid}", AudioEndpointClassGuid);

        var allEndpoints = QueryDevicesByClassGuid(AudioEndpointClassGuid, DeviceType.Microphone);

        var microphones = allEndpoints
            .Where(d => d.Id.Contains(CaptureEndpointPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        sw.Stop();
        Log.Information("Detected {Total} audio endpoint(s) in {DurationMs}ms; {CaptureCount} are capture endpoint(s)",
            allEndpoints.Count,
            sw.ElapsedMilliseconds,
            microphones.Count);

        return Task.FromResult<IReadOnlyList<DeviceInfo>>(microphones);
    }

    public async Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default)
    {
        var cameras = await DetectCamerasAsync(cancellationToken);
        var microphones = await DetectMicrophonesAsync(cancellationToken);
        return cameras.Concat(microphones).ToList();
    }

    private static List<DeviceInfo> QueryDevicesByClassGuid(string classGuid, DeviceType deviceType)
    {
        var results = new List<DeviceInfo>();

        try
        {
            var query = $"SELECT * FROM Win32_PnPEntity WHERE ClassGuid = '{classGuid}'";
            using var searcher = new ManagementObjectSearcher(query);
            using var collection = searcher.Get();

            foreach (var obj in collection)
            {
                using (obj)
                {
                    var instanceId = obj["DeviceID"]?.ToString();
                    var friendlyName = obj["Name"]?.ToString() ?? obj["Caption"]?.ToString() ?? "Unknown Device";
                    var status = obj["Status"]?.ToString() ?? "Unknown";
                    var pnpClass = obj["PNPClass"]?.ToString();

                    if (string.IsNullOrEmpty(instanceId))
                    {
                        Log.Warning("Skipping a privacy device with an empty InstanceId");
                        continue;
                    }

                    var errorCodeValue = obj["ConfigManagerErrorCode"];
                    uint? problemCode = errorCodeValue == null ? null : Convert.ToUInt32(errorCodeValue);
                    var stateKnown = problemCode.HasValue;
                    // Only problem code 0 is a clean, safely reversible enabled state. Code 22 is
                    // explicitly disabled; every other problem state is preserved and not toggled.
                    var isEnabled = problemCode == 0;

                    results.Add(new DeviceInfo
                    {
                        Id = instanceId,
                        FriendlyName = friendlyName,
                        DeviceType = deviceType,
                        ClassName = pnpClass,
                        PlatformIdentifier = classGuid,
                        Status = status,
                        IsPresent = true,
                        IsEnabled = isEnabled,
                        ConfigurationProblemCode = problemCode,
                        IsStateKnown = stateKnown
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to query Win32_PnPEntity for ClassGuid {ClassGuid}", classGuid);
            CrashReporter.GenerateCrashReport(ex, "WindowsDeviceDetector.WmiEnumeration");
            throw new InvalidOperationException(
                "Windows privacy-device enumeration failed; no hardware mutation is safe without a complete snapshot.",
                ex);
        }

        return results;
    }
}
