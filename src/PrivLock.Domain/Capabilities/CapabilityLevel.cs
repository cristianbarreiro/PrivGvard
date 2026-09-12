namespace PrivLock.Domain.Capabilities;

/// <summary>
/// Describes the depth and security strength of protection available on the platform.
/// </summary>
public enum CapabilityLevel
{
    /// <summary>No protection mechanisms available for this capability on this platform.</summary>
    None,

    /// <summary>Protection via software level (e.g. TCC permissions, application sound server mute).</summary>
    Software,

    /// <summary>Protection via operating system group policy or privacy direct subsystem.</summary>
    SystemPolicy,

    /// <summary>Protection via physical/driver node disablement or kernel driver unbind.</summary>
    Hardware,

    /// <summary>Dual-layer protection combining both System Policy and Hardware/Driver disablement.</summary>
    DualLayer
}
