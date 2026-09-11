using CamMicBlocker.Domain.Models;
using Xunit;

namespace CamMicBlocker.Tests.Domain;

public class BlockStateTests
{
    [Fact]
    public void EffectiveStatus_BothBlocked_ReturnsBlocked()
    {
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Blocked,
            PolicyStatus = BlockStatus.Blocked,
            DeviceStatus = BlockStatus.Blocked
        };

        Assert.Equal(BlockStatus.Blocked, state.EffectiveStatus);
    }

    [Fact]
    public void EffectiveStatus_BothAllowed_ReturnsAllowed()
    {
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Allowed,
            PolicyStatus = BlockStatus.Allowed,
            DeviceStatus = BlockStatus.Allowed
        };

        Assert.Equal(BlockStatus.Allowed, state.EffectiveStatus);
    }

    [Fact]
    public void EffectiveStatus_PolicyBlockedDeviceAllowed_ReturnsUnknown()
    {
        // Mismatch between policy and device state → inconsistency
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Blocked,
            PolicyStatus = BlockStatus.Blocked,
            DeviceStatus = BlockStatus.Allowed
        };

        Assert.Equal(BlockStatus.Unknown, state.EffectiveStatus);
    }

    [Fact]
    public void EffectiveStatus_PolicyAllowedDeviceBlocked_ReturnsUnknown()
    {
        // Someone disabled the device manually while policy allows it
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Allowed,
            PolicyStatus = BlockStatus.Allowed,
            DeviceStatus = BlockStatus.Blocked
        };

        Assert.Equal(BlockStatus.Unknown, state.EffectiveStatus);
    }

    [Fact]
    public void IsConsistent_DesiredMatchesEffective_ReturnsTrue()
    {
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Blocked,
            PolicyStatus = BlockStatus.Blocked,
            DeviceStatus = BlockStatus.Blocked
        };

        Assert.True(state.IsConsistent);
    }

    [Fact]
    public void IsConsistent_DesiredDiffersFromEffective_ReturnsFalse()
    {
        var state = new DeviceBlockState
        {
            DesiredStatus = BlockStatus.Blocked,
            PolicyStatus = BlockStatus.Allowed,
            DeviceStatus = BlockStatus.Allowed
        };

        Assert.False(state.IsConsistent);
    }

    [Fact]
    public void BlockState_AllBlocked_WhenBothCameraAndMicBlocked()
    {
        var state = new BlockState
        {
            Camera = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Blocked,
                DeviceStatus = BlockStatus.Blocked
            },
            Microphone = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Blocked,
                DeviceStatus = BlockStatus.Blocked
            }
        };

        Assert.True(state.AllBlocked);
        Assert.False(state.AllAllowed);
    }

    [Fact]
    public void BlockState_AllAllowed_WhenBothCameraAndMicAllowed()
    {
        var state = new BlockState
        {
            Camera = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Allowed,
                DeviceStatus = BlockStatus.Allowed
            },
            Microphone = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Allowed,
                DeviceStatus = BlockStatus.Allowed
            }
        };

        Assert.False(state.AllBlocked);
        Assert.True(state.AllAllowed);
    }

    [Fact]
    public void BlockState_PartialBlock_NeitherAllBlockedNorAllAllowed()
    {
        var state = new BlockState
        {
            Camera = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Blocked,
                DeviceStatus = BlockStatus.Blocked
            },
            Microphone = new DeviceBlockState
            {
                PolicyStatus = BlockStatus.Allowed,
                DeviceStatus = BlockStatus.Allowed
            }
        };

        Assert.False(state.AllBlocked);
        Assert.False(state.AllAllowed);
    }

    [Fact]
    public void BlockState_IsFullyConsistent_WhenAllStatesMatch()
    {
        var state = new BlockState
        {
            Camera = new DeviceBlockState
            {
                DesiredStatus = BlockStatus.Blocked,
                PolicyStatus = BlockStatus.Blocked,
                DeviceStatus = BlockStatus.Blocked
            },
            Microphone = new DeviceBlockState
            {
                DesiredStatus = BlockStatus.Allowed,
                PolicyStatus = BlockStatus.Allowed,
                DeviceStatus = BlockStatus.Allowed
            }
        };

        Assert.True(state.IsFullyConsistent);
    }
}
