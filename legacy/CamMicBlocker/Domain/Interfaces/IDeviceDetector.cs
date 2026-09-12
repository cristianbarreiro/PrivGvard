using CamMicBlocker.Domain.Models;

namespace CamMicBlocker.Domain.Interfaces;

/// <summary>
/// Detects camera and microphone devices present in the system.
/// Uses device class GUIDs for language-independent, precise detection.
/// </summary>
public interface IDeviceDetector
{
    /// <summary>
    /// Enumerates all camera devices using the Camera device class GUID.
    /// </summary>
    IReadOnlyList<DeviceInfo> DetectCameras();

    /// <summary>
    /// Enumerates all microphone (audio capture) devices.
    /// </summary>
    IReadOnlyList<DeviceInfo> DetectMicrophones();

    /// <summary>
    /// Detects all relevant devices (cameras + microphones).
    /// </summary>
    IReadOnlyList<DeviceInfo> DetectAll();
}
