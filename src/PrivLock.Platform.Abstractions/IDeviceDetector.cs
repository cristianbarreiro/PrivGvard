using PrivLock.Domain.Models;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Detects physical and virtual camera and microphone devices present on the system.
/// </summary>
public interface IDeviceDetector
{
    /// <summary>
    /// Enumerates all camera devices.
    /// </summary>
    Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates all audio capture (microphone) devices.
    /// </summary>
    Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates all relevant devices (cameras + microphones).
    /// </summary>
    Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default);
}
