namespace CamMicBlocker.Domain.Models;

/// <summary>
/// Represents the blocking status of a device or policy.
/// </summary>
public enum BlockStatus
{
    /// <summary>Device/policy is in its normal, unrestricted state.</summary>
    Allowed,

    /// <summary>Device/policy is actively blocked/denied.</summary>
    Blocked,

    /// <summary>Status cannot be determined (device absent, error, etc.).</summary>
    Unknown
}
