namespace PrivLock.Domain.Models;

/// <summary>
/// Represents the state of the secure / administrator protection level.
/// Dependent on Standard Protection being active first.
/// </summary>
public enum SecureProtectionState
{
    /// <summary>
    /// Secure protection cannot be activated because Standard Protection is not yet active
    /// or the platform does not support secure level protection.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Standard protection is active; secure protection is available to be enabled on-demand.
    /// </summary>
    Available,

    /// <summary>
    /// Secure protection is verified and active (hardware disabled / machine-wide policy enforced).
    /// </summary>
    Active,

    /// <summary>
    /// An error occurred (or elevation was denied) while attempting to enable/disable secure protection.
    /// </summary>
    Failed,

    /// <summary>
    /// State cannot be reliably determined.
    /// </summary>
    Unknown
}
