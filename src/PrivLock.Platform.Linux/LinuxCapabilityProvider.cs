using System.Runtime.InteropServices;
using PrivLock.Domain.Capabilities;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Platform.Linux;

/// <summary>
/// Declares capabilities and diagnostic information for Linux.
/// </summary>
public sealed class LinuxCapabilityProvider : IPlatformCapabilityProvider
{
    private readonly IElevationProvider _elevationProvider;

    public LinuxCapabilityProvider(IElevationProvider elevationProvider)
    {
        _elevationProvider = elevationProvider;
    }

    public PlatformCapabilities Capabilities => new()
    {
        CameraProtectionLevel = CapabilityLevel.None,
        MicrophoneProtectionLevel = CapabilityLevel.None,
        SupportsHardwareDisable = false,
        SupportsSystemPolicy = false,
        SupportsAudioMuteLock = false,
        RequiresElevationForBlock = false,
        SupportsGlobalHotkey = true,
        SupportsAutostart = true,
        SupportsSystemTray = true
    };

    public PlatformInfo PlatformInfo => new()
    {
        OperatingSystemName = "Linux",
        OsVersion = Environment.OSVersion.VersionString,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Is64Bit = Environment.Is64BitOperatingSystem,
        IsElevated = _elevationProvider.IsElevated,
        DesktopEnvironment = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "Linux Desktop"
    };
}
