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

    [Fact]
    public async Task EnablingProtection_PersistsDesiredState_ForCameraAndMicrophone()
    {
        var storedState = new DesiredState();
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        // Act 1: Enable camera
        var camResult = await _service.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(camResult.Success);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(SecureProtectionState.Available, storedState.CameraSecure);
        Assert.Equal(StandardProtectionState.Inactive, storedState.MicrophoneStandard);

        // Act 2: Enable microphone
        var micResult = await _service.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(micResult.Success);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Available, storedState.MicrophoneSecure);
    }

    [Fact]
    public async Task ShutdownRestoration_DoesNotEraseDesiredState()
    {
        var activeState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Active
        };
        _storeMock.Setup(s => s.Load()).Returns(activeState);

        _protectionMock.Setup(p => p.DisableSecureProtectionAsync(BlockTarget.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.DisableStandardProtectionAsync(BlockTarget.Both, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var shutdownResult = await _service.BeginShutdownAndRestoreAsync("UserExit");

        Assert.True(shutdownResult.SafeToExit);
        // Shutdown restoration restores OS state, but MUST NOT erase or overwrite DesiredState:
        _storeMock.Verify(s => s.Save(It.IsAny<DesiredState>()), Times.Never);
        var remaining = _storeMock.Object.Load();
        Assert.Equal(StandardProtectionState.Active, remaining.CameraStandard);
        Assert.Equal(SecureProtectionState.Active, remaining.CameraSecure);
        Assert.Equal(StandardProtectionState.Active, remaining.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Active, remaining.MicrophoneSecure);
    }

    [Fact]
    public async Task ExplicitUserDisable_ErasesCorrespondingDesiredState()
    {
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

        _protectionMock.Setup(p => p.DisableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        // Explicit disable of camera
        var disableCam = await _service.DisableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(disableCam.Success);
        Assert.Equal(StandardProtectionState.Inactive, storedState.CameraStandard);
        Assert.Equal(SecureProtectionState.Unavailable, storedState.CameraSecure);
        // Microphone remains protected:
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Available, storedState.MicrophoneSecure);

        // Explicit disable of microphone
        var disableMic = await _service.DisableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(disableMic.Success);
        Assert.Equal(StandardProtectionState.Inactive, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, storedState.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Unavailable, storedState.MicrophoneSecure);
    }

    [Fact]
    public async Task CameraAndMicrophoneStates_RemainIndependent()
    {
        var storedState = new DesiredState();
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.DisableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        // 1. Enable Camera only
        await _service.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, storedState.MicrophoneStandard);

        // 2. Enable Microphone
        await _service.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);

        // 3. Disable Camera only -> Microphone remains active
        await _service.DisableStandardProtectionAsync(BlockTarget.Camera);
        Assert.Equal(StandardProtectionState.Inactive, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);

        // 4. Disable Microphone -> Both inactive
        await _service.DisableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.Equal(StandardProtectionState.Inactive, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_DesiredCameraProtected_ActualCameraUnprotected_ProtectsCamera()
    {
        // 1. Desired camera protected + actual camera unprotected → protect camera.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Inactive,
            MicrophoneSecure = SecureProtectionState.Unavailable
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

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

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_DesiredCameraProtected_ActualCameraAlreadyProtected_DoesNotPerformUnnecessaryMutation()
    {
        // 2. Desired camera protected + actual camera already protected → do not perform unnecessary mutation.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Inactive
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);

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

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_DesiredCameraInactive_ActualCameraUnprotected_NoAction()
    {
        // 3. Desired camera inactive + actual camera unprotected → no action.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            CameraSecure = SecureProtectionState.Unavailable,
            MicrophoneStandard = StandardProtectionState.Inactive
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);

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

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_DesiredMicrophoneProtected_ActualMicrophoneUnprotected_ProtectsMicrophone()
    {
        // 4. Desired microphone protected + actual microphone unprotected → protect microphone.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            CameraSecure = SecureProtectionState.Unavailable,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

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

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Once);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(StandardProtectionState.Inactive, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_BothProtected_ReconcilesBoth()
    {
        // 5. Both protected → reconcile both.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

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

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_NoDesiredProtections_NoMutation()
    {
        // 6. No desired protections → no mutation.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            CameraSecure = SecureProtectionState.Unavailable,
            MicrophoneStandard = StandardProtectionState.Inactive,
            MicrophoneSecure = SecureProtectionState.Unavailable
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);

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

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _storeMock.Verify(s => s.Save(It.IsAny<DesiredState>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_RecoveryFailure_PerformsSafeBehaviorWithoutMutation()
    {
        // 7. Recovery failure → safe behavior.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);

        var failedRecovery = new PrivacyRecoveryResult
        {
            IsComplete = false,
            FailedCount = 1,
            ErrorMessage = "PnP device node restoration failed"
        };

        var result = await _service.ReconcileDesiredProtectionAsync(failedRecovery);

        Assert.False(result.Success);
        Assert.Contains("PnP device node restoration failed", result.ErrorMessage);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _storeMock.Verify(s => s.Save(It.IsAny<DesiredState>()), Times.Never);
        // DesiredState must remain untouched:
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_StartupMustNotClearDesiredState()
    {
        // 8. Startup must not clear DesiredState.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

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

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success);
        // DesiredState must remain Active and never reset to Inactive:
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_CameraFails_MicrophoneStillReconciledIndependently()
    {
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

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

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Fail("Camera device disabled by external administrator policy"));
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.False(result.Success);
        Assert.Contains("Camera standard protection failed", result.ErrorMessage);
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        // Crucial: microphone was STILL reconciled despite camera failure!
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Once);
        // DesiredState was NOT cleared by the partial failure:
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_WhenCameraSecureFailsWithElevationDenied_SkipsMicrophoneSecureAndPreservesMicrophoneStandard()
    {
        // When user denies/cancels UAC elevation on Camera Secure during startup reconciliation,
        // Microphone Secure must NOT prompt UAC again (must be skipped), while Microphone Standard
        // is still applied independently, and DesiredState is preserved.
        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Active
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);
        _storeMock.Setup(s => s.Save(It.IsAny<DesiredState>()))
            .Callback<DesiredState>(s => storedState = s);

        var camStandard = StandardProtectionState.Inactive;
        var micStandard = StandardProtectionState.Inactive;

        _protectionMock.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = camStandard,
                    SecureState = SecureProtectionState.Unavailable
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = micStandard,
                    SecureState = SecureProtectionState.Unavailable
                }
            });

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .Callback(() => camStandard = StandardProtectionState.Active)
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Fail("Operation cancelled: Administrator permissions were denied."));
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .Callback(() => micStandard = StandardProtectionState.Active)
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableSecureProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await _service.ReconcileDesiredProtectionAsync();

        Assert.False(result.Success);
        Assert.Contains("Camera secure protection failed", result.ErrorMessage);
        Assert.Contains("Microphone secure protection skipped: Administrator permissions were denied", result.ErrorMessage);

        // Camera Standard was applied
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        // Camera Secure was attempted and denied
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        // Microphone Standard was still applied independently
        _protectionMock.Verify(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Once);
        // Crucial: Microphone Secure was NEVER attempted, avoiding a second UAC prompt!
        _protectionMock.Verify(p => p.EnableSecureProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Never);
        // DesiredState was NOT wiped
        Assert.Equal(StandardProtectionState.Active, storedState.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, storedState.MicrophoneStandard);
    }

    [Fact]
    public async Task ReconcileDesiredProtection_WrapsMutationsInPlatformSuppressionScope()
    {
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CaptureFactory = (layer, target, op) => target switch
            {
                BlockTarget.Camera => [PrivacyRecoveryTestData.Device("cam1", originalEnabled: true, protectedEnabled: false, target: BlockTarget.Camera, layer: layer)],
                BlockTarget.Microphone => [PrivacyRecoveryTestData.Device("mic1", originalEnabled: true, protectedEnabled: false, target: BlockTarget.Microphone, layer: layer)],
                _ => []
            }
        };
        platform.SetObservation("device:cam1", PrivacyResourceObservationKind.MatchesOriginal);
        platform.SetObservation("device:mic1", PrivacyResourceObservationKind.MatchesOriginal);

        var sessionStore = new RecordingPrivacySessionStore();

        var privacySessionService = new PrivacySessionService(
            sessionStore,
            platform);

        var serviceWithAdapter = new ProtectionService(
            _protectionMock.Object,
            _detectorMock.Object,
            _capabilityMock.Object,
            _storeMock.Object,
            privacySessionService,
            legacyDetector: null,
            allowUntrackedMutations: true);

        var storedState = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Active
        };
        _storeMock.Setup(s => s.Load()).Returns(() => storedState);

        var camStd = StandardProtectionState.Inactive;
        var micStd = StandardProtectionState.Inactive;

        _protectionMock.Setup(p => p.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = camStd,
                    SecureState = SecureProtectionState.Unavailable
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = micStd,
                    SecureState = SecureProtectionState.Unavailable
                }
            });

        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                camStd = StandardProtectionState.Active;
                platform.SetObservation("device:cam1", PrivacyResourceObservationKind.MatchesProtected);
            })
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableStandardProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                micStd = StandardProtectionState.Active;
                platform.SetObservation("device:mic1", PrivacyResourceObservationKind.MatchesProtected);
            })
            .ReturnsAsync(OperationResult.Ok());
        _protectionMock.Setup(p => p.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult.Ok());

        var result = await serviceWithAdapter.ReconcileDesiredProtectionAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, platform.SuppressCalls);
        Assert.Equal(1, platform.ResumeCalls);
    }
}
