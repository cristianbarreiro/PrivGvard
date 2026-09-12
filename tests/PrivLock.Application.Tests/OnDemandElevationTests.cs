using Moq;
using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class OnDemandElevationTests
{
    private readonly Mock<IDeviceProtectionProvider> _protectionProviderMock;
    private readonly Mock<IDeviceDetector> _detectorMock;
    private readonly Mock<IPlatformCapabilityProvider> _capabilityProviderMock;
    private readonly Mock<IStateStore> _stateStoreMock;

    public OnDemandElevationTests()
    {
        _protectionProviderMock = new Mock<IDeviceProtectionProvider>();
        _detectorMock = new Mock<IDeviceDetector>();
        _capabilityProviderMock = new Mock<IPlatformCapabilityProvider>();
        _stateStoreMock = new Mock<IStateStore>();

        _capabilityProviderMock.Setup(c => c.Capabilities).Returns(new PlatformCapabilities
        {
            RequiresElevationForBlock = true,
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        });

        _capabilityProviderMock.Setup(c => c.PlatformInfo).Returns(new PlatformInfo
        {
            OperatingSystemName = "Windows",
            OsVersion = "10.0.22631",
            Architecture = "X64",
            Is64Bit = true,
            IsElevated = false
        });

        _stateStoreMock.Setup(s => s.Load()).Returns(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        });
    }

    [Fact]
    public async Task EnableSecureProtection_WhenStandardActive_TriggersProviderElevationAndSavesState()
    {
        // Arrange
        _protectionProviderMock
            .Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
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
                    StandardState = StandardProtectionState.Active,
                    SecureState = SecureProtectionState.Available
                }
            });

        _protectionProviderMock
            .Setup(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var service = new ProtectionService(
            _protectionProviderMock.Object,
            _detectorMock.Object,
            _capabilityProviderMock.Object,
            _stateStoreMock.Object);

        // Act
        var result = await service.EnableSecureProtectionAsync(BlockTarget.Camera);

        // Assert
        Assert.True(result.Success);
        _protectionProviderMock.Verify(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _stateStoreMock.Verify(s => s.Save(It.Is<DesiredState>(d => d.CameraSecure == SecureProtectionState.Active)), Times.Once);
    }

    [Fact]
    public async Task EnableSecureProtection_WhenUserCancelsElevation_ReturnsFailureWithoutModifyingSavedState()
    {
        // Arrange
        _protectionProviderMock
            .Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
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
                    StandardState = StandardProtectionState.Active,
                    SecureState = SecureProtectionState.Available
                }
            });

        _protectionProviderMock
            .Setup(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Fail("Operation cancelled: Administrator permissions were denied."));

        var service = new ProtectionService(
            _protectionProviderMock.Object,
            _detectorMock.Object,
            _capabilityProviderMock.Object,
            _stateStoreMock.Object);

        // Act
        var result = await service.EnableSecureProtectionAsync(BlockTarget.Camera);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        _stateStoreMock.Verify(s => s.Save(It.IsAny<DesiredState>()), Times.Never);
    }
}
