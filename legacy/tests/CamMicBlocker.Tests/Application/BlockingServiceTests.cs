using CamMicBlocker.Application;
using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using Moq;
using Xunit;

namespace CamMicBlocker.Tests.Application;

public class BlockingServiceTests
{
    private readonly Mock<IDeviceDetector> _detectorMock;
    private readonly Mock<IDeviceController> _controllerMock;
    private readonly Mock<IPolicyManager> _policyMock;
    private readonly Mock<IStateStore> _storeMock;
    private readonly BlockingService _service;

    public BlockingServiceTests()
    {
        _detectorMock = new Mock<IDeviceDetector>();
        _controllerMock = new Mock<IDeviceController>();
        _policyMock = new Mock<IPolicyManager>();
        _storeMock = new Mock<IStateStore>();

        // Default: return empty device lists and allowed policies
        _detectorMock.Setup(d => d.DetectCameras()).Returns(new List<DeviceInfo>());
        _detectorMock.Setup(d => d.DetectMicrophones()).Returns(new List<DeviceInfo>());
        _policyMock.Setup(p => p.GetCameraPolicyStatus()).Returns(BlockStatus.Allowed);
        _policyMock.Setup(p => p.GetMicrophonePolicyStatus()).Returns(BlockStatus.Allowed);
        _storeMock.Setup(s => s.Load()).Returns(new DesiredState());

        _service = new BlockingService(
            _detectorMock.Object,
            _controllerMock.Object,
            _policyMock.Object,
            _storeMock.Object);
    }

    [Fact]
    public async Task BlockAsync_Both_SetsPolicyAndDisablesDevices()
    {
        // Arrange
        var camera = new DeviceInfo { InstanceId = "CAM1", FriendlyName = "Camera", DeviceType = DeviceType.Camera, IsEnabled = true };
        var mic = new DeviceInfo { InstanceId = "MIC1", FriendlyName = "Mic", DeviceType = DeviceType.Microphone, IsEnabled = true };

        _detectorMock.Setup(d => d.DetectCameras()).Returns(new List<DeviceInfo> { camera });
        _detectorMock.Setup(d => d.DetectMicrophones()).Returns(new List<DeviceInfo> { mic });
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked))
            .ReturnsAsync(OperationResult.Ok());
        _controllerMock.Setup(c => c.DisableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()))
            .ReturnsAsync(OperationResult.Ok());

        // Act
        var result = await _service.BlockAsync(BlockTarget.Both);

        // Assert
        Assert.True(result.Success);
        _policyMock.Verify(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked), Times.Once);
        _controllerMock.Verify(c => c.DisableDevicesAsync(It.Is<IEnumerable<DeviceInfo>>(
            devs => devs.Count() == 2)), Times.Once);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(
            ds => ds.Camera == BlockStatus.Blocked && ds.Microphone == BlockStatus.Blocked)), Times.Once);
    }

    [Fact]
    public async Task BlockAsync_PolicyFails_ReturnsFailure()
    {
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked))
            .ReturnsAsync(OperationResult.Fail("Access denied"));

        var result = await _service.BlockAsync(BlockTarget.Both);

        Assert.False(result.Success);
        Assert.Equal("Access denied", result.ErrorMessage);
        // Devices should NOT be disabled if policy failed
        _controllerMock.Verify(c => c.DisableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()), Times.Never);
    }

    [Fact]
    public async Task BlockAsync_DeviceDisableFails_StillSavesState()
    {
        // Policy succeeds but device disable fails → still save state
        // (policy is still applied, providing protection)
        var camera = new DeviceInfo { InstanceId = "CAM1", FriendlyName = "Camera", DeviceType = DeviceType.Camera, IsEnabled = true };
        _detectorMock.Setup(d => d.DetectCameras()).Returns(new List<DeviceInfo> { camera });
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Camera, BlockStatus.Blocked))
            .ReturnsAsync(OperationResult.Ok());
        _controllerMock.Setup(c => c.DisableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()))
            .ReturnsAsync(OperationResult.Fail("Device error"));

        var result = await _service.BlockAsync(BlockTarget.Camera);

        Assert.True(result.Success); // Overall success because policy was applied
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(ds => ds.Camera == BlockStatus.Blocked)), Times.Once);
    }

    [Fact]
    public async Task UnblockAsync_CameraOnly_OnlyAffectsCamera()
    {
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Camera, BlockStatus.Allowed))
            .ReturnsAsync(OperationResult.Ok());
        _controllerMock.Setup(c => c.EnableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()))
            .ReturnsAsync(OperationResult.Ok());
        _storeMock.Setup(s => s.Load()).Returns(new DesiredState
        {
            Camera = BlockStatus.Blocked,
            Microphone = BlockStatus.Blocked
        });

        var result = await _service.UnblockAsync(BlockTarget.Camera);

        Assert.True(result.Success);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(
            ds => ds.Camera == BlockStatus.Allowed && ds.Microphone == BlockStatus.Blocked)), Times.Once);
    }

    [Fact]
    public async Task ToggleAsync_WhenAllowed_Blocks()
    {
        _policyMock.Setup(p => p.GetCameraPolicyStatus()).Returns(BlockStatus.Allowed);
        _policyMock.Setup(p => p.GetMicrophonePolicyStatus()).Returns(BlockStatus.Allowed);
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked))
            .ReturnsAsync(OperationResult.Ok());
        _controllerMock.Setup(c => c.DisableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()))
            .ReturnsAsync(OperationResult.Ok());

        await _service.ToggleAsync(BlockTarget.Both);

        _policyMock.Verify(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked), Times.Once);
    }

    [Fact]
    public void GetCurrentState_ReadsFromAllSources()
    {
        _policyMock.Setup(p => p.GetCameraPolicyStatus()).Returns(BlockStatus.Blocked);
        _policyMock.Setup(p => p.GetMicrophonePolicyStatus()).Returns(BlockStatus.Allowed);
        _storeMock.Setup(s => s.Load()).Returns(new DesiredState
        {
            Camera = BlockStatus.Blocked,
            Microphone = BlockStatus.Allowed
        });

        var state = _service.GetCurrentState();

        Assert.Equal(BlockStatus.Blocked, state.Camera.PolicyStatus);
        Assert.Equal(BlockStatus.Allowed, state.Microphone.PolicyStatus);
        Assert.Equal(BlockStatus.Blocked, state.Camera.DesiredStatus);
        Assert.Equal(BlockStatus.Allowed, state.Microphone.DesiredStatus);
    }

    [Fact]
    public async Task BlockAsync_NoDevicesDetected_PolicyStillApplied()
    {
        _detectorMock.Setup(d => d.DetectCameras()).Returns(new List<DeviceInfo>());
        _detectorMock.Setup(d => d.DetectMicrophones()).Returns(new List<DeviceInfo>());
        _policyMock.Setup(p => p.SetPolicyAsync(BlockTarget.Both, BlockStatus.Blocked))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.BlockAsync(BlockTarget.Both);

        Assert.True(result.Success);
        // No device disable should be attempted with empty list
        _controllerMock.Verify(c => c.DisableDevicesAsync(It.IsAny<IEnumerable<DeviceInfo>>()), Times.Never);
    }
}
