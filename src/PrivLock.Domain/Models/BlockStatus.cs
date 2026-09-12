namespace PrivLock.Domain.Models;

/// <summary>
/// Represents the blocking status of a device, policy, or audio/video stream.
/// </summary>
public enum BlockStatus
{
    /// <summary>Device/policy is in its normal, unrestricted state.</summary>
    Allowed,

    /// <summary>Device/policy is actively blocked/denied/muted.</summary>
    Blocked,

    /// <summary>Status cannot be determined (device absent, unsupported platform call, error, etc.).</summary>
    Unknown
}
