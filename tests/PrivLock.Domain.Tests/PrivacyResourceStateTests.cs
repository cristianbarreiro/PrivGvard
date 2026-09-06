using PrivLock.Domain.Models;
using Xunit;

namespace PrivLock.Domain.Tests;

public sealed class PrivacyResourceStateTests
{
    [Fact]
    public void DeviceRequiresModification_OnlyWhenStateDiffersAndCaptureIsSafelyRestorable()
    {
        var resource = CreateDeviceState();

        resource.IsSafelyRestorable = true;
        resource.OriginalEnabledState = true;
        resource.ProtectedEnabledState = false;
        Assert.True(resource.RequiresModification);

        resource.IsSafelyRestorable = false;
        Assert.False(resource.RequiresModification);

        resource.IsSafelyRestorable = true;
        resource.ProtectedEnabledState = true;
        Assert.False(resource.RequiresModification);
    }

    [Fact]
    public void PolicyRequiresModification_IsFalseForAnExactExistingValueMatch()
    {
        var resource = CreatePolicyState();

        Assert.False(resource.RequiresModification);
    }

    [Fact]
    public void PolicyRequiresModification_IsTrueWhenExistenceKindOrCanonicalValueDiffers()
    {
        var resource = CreatePolicyState();

        resource.ProtectedValueExists = false;
        Assert.True(resource.RequiresModification);

        resource.ProtectedValueExists = true;
        resource.ProtectedValueKind = PrivacyRegistryValueKind.QWord;
        Assert.True(resource.RequiresModification);

        resource.ProtectedValueKind = PrivacyRegistryValueKind.DWord;
        resource.ProtectedValue = "0";
        Assert.True(resource.RequiresModification);
    }

    [Fact]
    public void PolicyRequiresModification_IsFalseWhenBothOriginalAndProtectedValuesAreAbsent()
    {
        var resource = CreatePolicyState();
        resource.ValueExisted = false;
        resource.OriginalValueKind = PrivacyRegistryValueKind.None;
        resource.OriginalValue = null;
        resource.ProtectedValueExists = false;
        resource.ProtectedValueKind = PrivacyRegistryValueKind.None;
        resource.ProtectedValue = null;

        Assert.False(resource.RequiresModification);
    }

    [Fact]
    public void AudioEndpointRequiresModification_TracksMuteStateDifference()
    {
        var resource = new OriginalAudioEndpointState
        {
            ResourceId = "audio:test",
            OperationId = "mute-test",
            EndpointId = "endpoint-test",
            OriginalMutedState = false,
            ProtectedMutedState = true
        };

        Assert.True(resource.RequiresModification);

        resource.OriginalMutedState = true;
        Assert.False(resource.RequiresModification);
    }

    private static OriginalDeviceState CreateDeviceState() => new()
    {
        ResourceId = "device:test",
        OperationId = "disable-test",
        InstanceId = "device-instance-test",
        FriendlyName = "Test camera",
        DeviceClass = "Camera"
    };

    private static OriginalPolicyState CreatePolicyState() => new()
    {
        ResourceId = "registry:test",
        OperationId = "set-policy-test",
        RegistryPath = @"SOFTWARE\Policies\PrivLockTest",
        ValueName = "TestValue",
        ValueExisted = true,
        OriginalValueKind = PrivacyRegistryValueKind.DWord,
        OriginalValue = "1",
        ProtectedValueExists = true,
        ProtectedValueKind = PrivacyRegistryValueKind.DWord,
        ProtectedValue = "1"
    };
}
