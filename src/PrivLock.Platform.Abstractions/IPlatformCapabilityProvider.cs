using PrivLock.Domain.Capabilities;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Provides declarative information about what security capabilities and features are supported on the running platform.
/// </summary>
public interface IPlatformCapabilityProvider
{
    /// <summary>
    /// The capabilities matrix supported on this operating system and environment.
    /// </summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// Operating system and environment diagnostic details.
    /// </summary>
    PlatformInfo PlatformInfo { get; }
}
