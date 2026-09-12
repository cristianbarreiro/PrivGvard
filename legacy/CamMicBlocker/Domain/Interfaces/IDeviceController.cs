using CamMicBlocker.Domain.Models;

namespace CamMicBlocker.Domain.Interfaces;

/// <summary>
/// Controls device enable/disable operations.
/// These operations require administrative privileges and are
/// performed via the elevated helper process.
/// </summary>
public interface IDeviceController
{
    /// <summary>
    /// Disables the specified devices. Triggers UAC elevation.
    /// </summary>
    /// <param name="devices">Devices to disable, identified by InstanceId.</param>
    /// <returns>Result indicating success/failure per device.</returns>
    Task<OperationResult> DisableDevicesAsync(IEnumerable<DeviceInfo> devices);

    /// <summary>
    /// Enables the specified devices. Triggers UAC elevation.
    /// </summary>
    /// <param name="devices">Devices to enable, identified by InstanceId.</param>
    /// <returns>Result indicating success/failure per device.</returns>
    Task<OperationResult> EnableDevicesAsync(IEnumerable<DeviceInfo> devices);
}

/// <summary>
/// Result of a privileged operation (device enable/disable or policy change).
/// </summary>
public sealed class OperationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DeviceOperationDetail> Details { get; init; } = [];

    public static OperationResult Ok(IReadOnlyList<DeviceOperationDetail>? details = null) =>
        new() { Success = true, Details = details ?? [] };

    public static OperationResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Detail of an operation on a specific device.
/// </summary>
public sealed class DeviceOperationDetail
{
    public required string InstanceId { get; init; }
    public required string FriendlyName { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
