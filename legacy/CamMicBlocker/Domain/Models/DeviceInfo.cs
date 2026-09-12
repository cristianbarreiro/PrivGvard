namespace CamMicBlocker.Domain.Models;

/// <summary>
/// Represents a detected camera or microphone device.
/// Identified by PnP InstanceId, which is stable across reboots.
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>
    /// PnP device instance ID (e.g., "USB\VID_046D&PID_0825\12345678").
    /// This is the stable identifier used for enable/disable operations.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Human-readable name (e.g., "Logitech HD Webcam C270").
    /// Used only for display; never for device identification logic.
    /// </summary>
    public required string FriendlyName { get; init; }

    /// <summary>
    /// Whether this is a Camera or Microphone.
    /// </summary>
    public required DeviceType DeviceType { get; init; }

    /// <summary>
    /// Device class name as reported by Windows (e.g., "Camera", "AudioEndpoint").
    /// </summary>
    public string? ClassName { get; init; }

    /// <summary>
    /// Device class GUID as reported by Windows.
    /// </summary>
    public string? ClassGuid { get; init; }

    /// <summary>
    /// Current PnP status of the device (OK, Error, Disabled, etc.).
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Whether the device is currently present (connected) in the system.
    /// </summary>
    public bool IsPresent { get; init; }

    /// <summary>
    /// Whether the device is currently enabled in Device Manager.
    /// </summary>
    public bool IsEnabled { get; init; }

    public override string ToString() =>
        $"[{DeviceType}] {FriendlyName} ({InstanceId}) - {(IsEnabled ? "Enabled" : "Disabled")}";

    public override bool Equals(object? obj) =>
        obj is DeviceInfo other && string.Equals(InstanceId, other.InstanceId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(InstanceId);
}
