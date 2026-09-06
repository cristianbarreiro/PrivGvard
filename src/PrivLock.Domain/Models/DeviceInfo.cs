namespace PrivLock.Domain.Models;

/// <summary>
/// Represents a detected camera or microphone device across platforms.
/// Identified by a stable unique Id (PnP instance ID on Windows, sysfs/v4l2 path on Linux, CoreAudio/AVFoundation UID on macOS).
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>
    /// Unique identifier stable across reboots/sessions.
    /// Windows: PnP Device Instance Id (e.g. "USB\VID_046D&PID_0825\12345678").
    /// Linux: sysfs path or udev ID (e.g. "/sys/devices/pci0000:00/.../video4linux/video0").
    /// macOS: Device UID (e.g. "AppleHDAEngineInput:1B,0,1,0:1").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name for display (e.g. "Logitech HD Webcam C270", "Built-in Microphone").
    /// </summary>
    public required string FriendlyName { get; init; }

    /// <summary>
    /// Whether this device is a Camera or Microphone.
    /// </summary>
    public required DeviceType DeviceType { get; init; }

    /// <summary>
    /// Platform-specific class/category name (e.g. "Camera", "AudioEndpoint", "v4l2_cap", "CoreAudioInput").
    /// </summary>
    public string? ClassName { get; init; }

    /// <summary>
    /// Platform-specific GUID or subsystem identifier.
    /// </summary>
    public string? PlatformIdentifier { get; init; }

    /// <summary>
    /// Current hardware/driver status string.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Whether the device is physically present / connected to the system.
    /// </summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>
    /// Whether the device is currently enabled and capable of capturing data.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Native Configuration Manager problem code when the platform exposes one.
    /// Windows uses 0 for no problem and 22 (CM_PROB_DISABLED) for an administratively disabled node.
    /// Other non-zero values must not be treated as safely restorable by simply enabling the node.
    /// </summary>
    public uint? ConfigurationProblemCode { get; init; }

    /// <summary>
    /// True only when the provider could authoritatively read the device's native state.
    /// </summary>
    public bool IsStateKnown { get; init; } = true;

    public override string ToString() =>
        $"[{DeviceType}] {FriendlyName} ({Id}) - {(IsEnabled ? "Enabled" : "Disabled")}";

    public override bool Equals(object? obj) =>
        obj is DeviceInfo other && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
}
