using Moq;
using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class ProtectionServiceTests
{
    private readonly Mock<IDeviceProtectionProvider> _protectionMock;
    private readonly Mock<IDeviceDetector> _detectorMock;
    private readonly Mock<IPlatformCapabilityProvider> _capabilityMock;
    private readonly Mock<IStateStore> _storeMock;
    private readonly ProtectionService _service;

    public ProtectionServiceTests()
    {
        _protectionMock = new Mock<IDeviceProtectionProvider>();
        _detectorMock = new Mock<IDeviceDetector>();
        _capabilityMock = new Mock<IPlatformCapabilityProvider>();
        _storeMock = new Mock<IStateStore>();

        _capabilityMock.Setup(c => c.Capabilities).Returns(new PlatformCapabilities
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        });
        _capabilityMock.Setup(c => c.PlatformInfo).Returns(new PlatformInfo
        {
            OperatingSystemName = "GenericOS",
            OsVersion = "1.0",
            Architecture = "X64",
            Is64Bit = true,
            IsElevated = false
        });

        _detectorMock.Setup(d => d.DetectCamerasAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceInfo>());
        _detectorMock.Setup(d => d.DetectMicrophonesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceInfo>());
        _detectorMock.Setup(d => d.DetectAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceInfo>());

        _protectionMock.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                }
            });

        _storeMock.Setup(s => s.Load()).Returns(new DesiredState());

        _service = new ProtectionService(
            _protectionMock.Object,
            _detectorMock.Object,
            _capabilityMock.Object,
            _storeMock.Object);
    }

    [Fact]
    public async Task EnableStandardProtection_TransitionsSecureToAvailableAndSaves()
    {
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.EnableStandardProtectionAsync(BlockTarget.Camera);

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(
            ds => ds.CameraStandard == StandardProtectionState.Active && ds.CameraSecure == SecureProtectionState.Available)), Times.Once);
    }

    [Fact]
    public async Task EnableSecureProtection_FailsIfStandardProtectionIsNotActive()
    {
        // Arrange: Standard is Inactive
        _protectionMock.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                }
            });

        // Act
        var result = await _service.EnableSecureProtectionAsync(BlockTarget.Camera);

        // Assert: rejected by business rules, no provider call
        Assert.False(result.Success);
        Assert.Contains("must enable Standard Protection", result.ErrorMessage);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnableSecureProtection_SucceedsWhenStandardIsActive()
    {
        // Arrange: Standard is Active
        _protectionMock.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = StandardProtectionState.Active,
                    SecureState = SecureProtectionState.Available
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                }
            });

        _protectionMock.Setup(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        // Act
        var result = await _service.EnableSecureProtectionAsync(BlockTarget.Camera);

        // Assert
        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(ds => ds.CameraSecure == SecureProtectionState.Active)), Times.Once);
    }

    [Fact]
    public async Task DisableSecureProtection_KeepsStandardActiveAndSetsSecureToAvailable()
    {
        _protectionMock.Setup(p => p.DisableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        _storeMock.Setup(s => s.Load()).Returns(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active
        });

        var result = await _service.DisableSecureProtectionAsync(BlockTarget.Camera);

        Assert.True(result.Success);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(
            ds => ds.CameraStandard == StandardProtectionState.Active && ds.CameraSecure == SecureProtectionState.Available)), Times.Once);
    }

    [Fact]
    public async Task DisableStandardProtection_TransitionsSecureToUnavailable()
    {
        _protectionMock.Setup(p => p.DisableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        _storeMock.Setup(s => s.Load()).Returns(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available
        });

        var result = await _service.DisableStandardProtectionAsync(BlockTarget.Camera);

        Assert.True(result.Success);
        _storeMock.Verify(s => s.Save(It.Is<DesiredState>(
            ds => ds.CameraStandard == StandardProtectionState.Inactive && ds.CameraSecure == SecureProtectionState.Unavailable)), Times.Once);
    }

    [Fact]
    public async Task Shutdown_WaitsForAdmittedBlockAndRejectsLaterToggle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<IDeviceProtectionProvider>();
        provider.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return OperationResult.Ok();
            });
        provider.Setup(p => p.DisableSecureProtectionAsync(BlockTarget.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        provider.Setup(p => p.DisableStandardProtectionAsync(BlockTarget.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        provider.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = StandardProtectionState.Active,
                    SecureState = SecureProtectionState.Available
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = StandardProtectionState.Inactive,
                    SecureState = SecureProtectionState.Unavailable
                }
            });
        var service = new ProtectionService(
            provider.Object,
            _detectorMock.Object,
            _capabilityMock.Object,
            _storeMock.Object);

        var admitted = service.EnableStandardProtectionAsync(BlockTarget.Camera);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = service.BeginShutdownAndRestoreAsync("WindowClose");
        var rejected = await service.EnableStandardProtectionAsync(BlockTarget.Microphone);

        Assert.False(rejected.Success);
        Assert.Contains("shutting down", rejected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(shutdown.IsCompleted);

        release.TrySetResult();
        Assert.True((await admitted).Success);
        Assert.True((await shutdown).SafeToExit);
        provider.Verify(
            p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
