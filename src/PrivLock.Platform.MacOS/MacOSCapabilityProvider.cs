using System.Runtime.InteropServices;
using PrivLock.Domain.Capabilities;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Platform.MacOS;

/// <summary>
/// Declares capabilities and diagnostic information for macOS.
/// Reflects the honest security model of macOS (CoreAudio HAL Hardware Mute, TCC Camera level).
/// </summary>
public sealed class MacOSCapabilityProvider : IPlatformCapabilityProvider
{
    private readonly IElevationProvider _elevationProvider;

    public MacOSCapabilityProvider(IElevationProvider elevationProvider)
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
        OperatingSystemName = "macOS",
        OsVersion = Environment.OSVersion.VersionString,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Is64Bit = Environment.Is64BitOperatingSystem,
        IsElevated = _elevationProvider.IsElevated,
        DesktopEnvironment = "Aqua (Cocoa)"
    };
}
