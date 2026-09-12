using System.Runtime.InteropServices;
using PrivLock.Domain.Capabilities;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Platform.Windows;

/// <summary>
/// Declares capabilities and platform diagnostics on Windows.
/// </summary>
public sealed class WindowsCapabilityProvider : IPlatformCapabilityProvider
{
    private readonly IElevationProvider _elevationProvider;

    public WindowsCapabilityProvider(IElevationProvider elevationProvider)
    {
        _elevationProvider = elevationProvider;
    }

    public PlatformCapabilities Capabilities => new()
    {
        CameraProtectionLevel = CapabilityLevel.DualLayer,
        MicrophoneProtectionLevel = CapabilityLevel.DualLayer,
        SupportsHardwareDisable = true,
        SupportsSystemPolicy = true,
        SupportsAudioMuteLock = true,
        RequiresElevationForBlock = true,
        SupportsGlobalHotkey = true,
        SupportsAutostart = true,
        SupportsSystemTray = true
    };

    public PlatformInfo PlatformInfo => new()
    {
        OperatingSystemName = "Windows",
        OsVersion = Environment.OSVersion.VersionString,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Is64Bit = Environment.Is64BitOperatingSystem,
        IsElevated = _elevationProvider.IsElevated,
        DesktopEnvironment = "Windows Shell (Explorer)"
    };
}
