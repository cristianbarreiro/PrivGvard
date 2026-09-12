using PrivLock.Domain.Models;
using Xunit;

namespace PrivLock.Domain.Tests;

public class TargetProtectionStatusTests
{
    [Fact]
    public void CanEnableSecure_OnlyWhenStandardIsActiveAndSecureIsAvailable()
    {
        var status = new TargetProtectionStatus
        {
            Target = BlockTarget.Camera,
            StandardState = StandardProtectionState.Active,
            SecureState = SecureProtectionState.Available,
            IsVerified = true
        };

        Assert.True(status.CanEnableSecure);
        Assert.True(status.IsProtected);
    }

    [Fact]
    public void CanEnableSecure_FalseWhenStandardIsInactive()
    {
        var status = new TargetProtectionStatus
        {
            Target = BlockTarget.Camera,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable,
            IsVerified = true
        };

        Assert.False(status.CanEnableSecure);
        Assert.False(status.IsProtected);
    }

    [Fact]
    public void CanDisableSecure_TrueOnlyWhenSecureIsActive()
    {
        var status = new TargetProtectionStatus
        {
            Target = BlockTarget.Microphone,
            StandardState = StandardProtectionState.Active,
            SecureState = SecureProtectionState.Active,
            IsVerified = true
        };

        Assert.True(status.CanDisableSecure);
        Assert.True(status.IsProtected);
    }

    [Fact]
    public void FullProtectionState_BothSecure_ReturnsTrueWhenBothAreActive()
    {
        var full = new FullProtectionState
        {
            Camera = new TargetProtectionStatus
            {
                Target = BlockTarget.Camera,
                StandardState = StandardProtectionState.Active,
                SecureState = SecureProtectionState.Active,
                IsVerified = true
            },
            Microphone = new TargetProtectionStatus
            {
                Target = BlockTarget.Microphone,
                StandardState = StandardProtectionState.Active,
                SecureState = SecureProtectionState.Active,
                IsVerified = true
            }
        };

        Assert.True(full.BothSecure);
        Assert.True(full.BothProtected);
    }
}
