using System.Management;
using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using Serilog;

namespace CamMicBlocker.Infrastructure;

/// <summary>
/// Detects camera and microphone devices using WMI queries filtered by device class GUIDs.
/// This is language-independent and far more precise than name-based matching.
/// 
/// Camera detection: Uses the Camera device class GUID {ca3e7ab9-b4c3-4ae6-8251-579ef933890f}.
/// This class contains ONLY camera devices — no scanners, no imaging devices.
/// 
/// Microphone detection: Uses the AudioEndpoint device class GUID {c166523c-fe0c-4a94-a586-f1a80cfbbf3e}.
/// AudioEndpoint devices include both speakers (render) and microphones (capture).
/// We differentiate capture endpoints by examining the device instance ID pattern:
///   - Render (speakers): SWD\MMDEVAPI\{0.0.0.*
///   - Capture (microphones): SWD\MMDEVAPI\{0.0.1.*
/// This pattern is based on the MMDevice API's endpoint data flow enumeration.
/// </summary>
public sealed class DeviceDetector : IDeviceDetector
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DeviceDetector>();

    // Windows Camera device class GUID — contains ONLY cameras
    private const string CameraClassGuid = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";

    // Windows AudioEndpoint device class GUID — contains speakers AND microphones
    private const string AudioEndpointClassGuid = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";

    // Instance ID pattern for audio capture (microphone) endpoints
    // The "0.0.1" segment indicates eCapture data flow direction
    private const string CaptureEndpointPattern = "{0.0.1.";

    public IReadOnlyList<DeviceInfo> DetectCameras()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("Detecting camera devices via ClassGuid {ClassGuid}", CameraClassGuid);

        var devices = QueryDevicesByClassGuid(CameraClassGuid, DeviceType.Camera);

        sw.Stop();
        Log.Information("Detected {Count} camera device(s) in {DurationMs}ms: {Devices}",
            devices.Count,
            sw.ElapsedMilliseconds,
            string.Join(", ", devices.Select(d => $"{d.FriendlyName} [{d.InstanceId}]")));

        return devices;
    }

    public IReadOnlyList<DeviceInfo> DetectMicrophones()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("Detecting microphone devices via AudioEndpoint ClassGuid {ClassGuid}", AudioEndpointClassGuid);

        var allEndpoints = QueryDevicesByClassGuid(AudioEndpointClassGuid, DeviceType.Microphone);

        var microphones = allEndpoints
            .Where(d => IsCaptureEndpoint(d))
            .ToList();

        sw.Stop();
        Log.Information("Detected {Total} audio endpoint(s) in {DurationMs}ms, {CaptureCount} are capture (microphone) endpoint(s): {Devices}",
            allEndpoints.Count,
            sw.ElapsedMilliseconds,
            microphones.Count,
            string.Join(", ", microphones.Select(d => $"{d.FriendlyName} [{d.InstanceId}]")));

        var excluded = allEndpoints.Except(microphones).ToList();
        if (excluded.Count > 0)
        {
            Log.Debug("Excluded {Count} render (output) endpoint(s): {Devices}",
                excluded.Count,
                string.Join(", ", excluded.Select(d => $"{d.FriendlyName} [{d.InstanceId}]")));
        }

        return microphones;
    }

    public IReadOnlyList<DeviceInfo> DetectAll()
    {
        var cameras = DetectCameras();
        var microphones = DetectMicrophones();
        return cameras.Concat(microphones).ToList();
    }

    /// <summary>
    /// Determines if an AudioEndpoint device is a capture (microphone) endpoint
    /// based on its instance ID pattern. The MMDevice API uses a convention where
    /// capture endpoints have "0.0.1" in their instance ID path.
    /// </summary>
    private static bool IsCaptureEndpoint(DeviceInfo device)
    {
        // AudioEndpoint instance IDs follow the pattern:
        // SWD\MMDEVAPI\{0.0.1.00000000}.{GUID} for capture
        // SWD\MMDEVAPI\{0.0.0.00000000}.{GUID} for render
        return device.InstanceId.Contains(CaptureEndpointPattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Queries Win32_PnPEntity via WMI for devices matching the specified class GUID.
    /// </summary>
    private static List<DeviceInfo> QueryDevicesByClassGuid(string classGuid, DeviceType deviceType)
    {
        var results = new List<DeviceInfo>();

        try
        {
            // WMI query filtered by ClassGuid — language-independent device identification
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
                    var availability = obj["Availability"];

                    if (string.IsNullOrEmpty(instanceId))
                    {
                        Log.Warning("Skipping device with empty InstanceId: {Name}", friendlyName);
                        continue;
                    }

                    // Check if device is enabled by examining its status and ConfigManagerErrorCode
                    var errorCode = obj["ConfigManagerErrorCode"];
                    bool isEnabled = errorCode != null && Convert.ToUInt32(errorCode) != 22; // 22 = CM_PROB_DISABLED

                    results.Add(new DeviceInfo
                    {
                        InstanceId = instanceId,
                        FriendlyName = friendlyName,
                        DeviceType = deviceType,
                        ClassName = pnpClass,
                        ClassGuid = classGuid,
                        Status = status,
                        IsPresent = true,
                        IsEnabled = isEnabled
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to query devices for ClassGuid {ClassGuid}", classGuid);
        }

        return results;
    }
}
