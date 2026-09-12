using PrivLock.Domain.Capabilities;
using Xunit;

namespace PrivLock.Domain.Tests;

public class PlatformCapabilitiesTests
{
    [Fact]
    public void PlatformCapabilities_DefaultsToNone()
    {
        var capabilities = new PlatformCapabilities();

        Assert.Equal(CapabilityLevel.None, capabilities.CameraProtectionLevel);
        Assert.Equal(CapabilityLevel.None, capabilities.MicrophoneProtectionLevel);
        Assert.False(capabilities.SupportsHardwareDisable);
        Assert.False(capabilities.SupportsSystemPolicy);
        Assert.False(capabilities.SupportsAudioMuteLock);
    }

    [Fact]
    public void PlatformInfo_HoldsDiagnosticData()
    {
        var info = new PlatformInfo
        {
            OperatingSystemName = "Linux",
            OsVersion = "6.8.0",
            Architecture = "X64",
            Is64Bit = true,
            IsElevated = false,
            DesktopEnvironment = "GNOME"
        };

        Assert.Equal("Linux", info.OperatingSystemName);
        Assert.Equal("GNOME", info.DesktopEnvironment);
        Assert.False(info.IsElevated);
    }
}
