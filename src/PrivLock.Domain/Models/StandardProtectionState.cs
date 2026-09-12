namespace PrivLock.Domain.Models;

/// <summary>
/// Represents the state of the standard protection level (operates without elevated privileges where possible).
/// </summary>
public enum StandardProtectionState
{
    /// <summary>
    /// Standard protection is inactive (device is accessible).
    /// </summary>
    Inactive,

    /// <summary>
    /// Standard protection is verified and active (device access restricted).
    /// </summary>
    Active,

    /// <summary>
    /// An error occurred while applying or removing standard protection.
    /// </summary>
    Failed,

    /// <summary>
    /// State cannot be reliably determined.
    /// </summary>
    Unknown
}
