using Moq;
using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class AdvancedProtectionCoordinationTests
{
    private readonly Mock<IDeviceProtectionProvider> _nativeMock;
    private readonly Mock<IDeviceDetector> _detectorMock;
    private readonly Mock<IPlatformCapabilityProvider> _capabilityMock;
    private readonly InMemoryStateStore _stateStore;

    private TargetProtectionStatus _cameraStatus;
    private TargetProtectionStatus _micStatus;

    public AdvancedProtectionCoordinationTests()
    {
        _nativeMock = new Mock<IDeviceProtectionProvider>();
        _detectorMock = new Mock<IDeviceDetector>();
        _capabilityMock = new Mock<IPlatformCapabilityProvider>();
        _stateStore = new InMemoryStateStore();

        _capabilityMock.Setup(c => c.Capabilities).Returns(new PlatformCapabilities
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        });
        _capabilityMock.Setup(c => c.PlatformInfo).Returns(new PlatformInfo
        {
            OperatingSystemName = "TestOS",
            OsVersion = "1.0",
            Architecture = "X64",
            Is64Bit = true,
            IsElevated = false
        });

        _detectorMock.Setup(d => d.DetectAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceInfo>());

        _cameraStatus = new TargetProtectionStatus
        {
            Target = BlockTarget.Camera,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable
        };
        _micStatus = new TargetProtectionStatus
        {
            Target = BlockTarget.Microphone,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable
        };

        _nativeMock.Setup(n => n.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new FullProtectionState
            {
                Camera = _cameraStatus,
                Microphone = _micStatus
            }));

        _nativeMock.Setup(n => n.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .Returns<BlockTarget, CancellationToken>((target, _) =>
            {
                if (target is BlockTarget.Camera or BlockTarget.Both)
                {
                    _cameraStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Camera,
                        StandardState = StandardProtectionState.Active,
                        SecureState = _cameraStatus.SecureState == SecureProtectionState.Active
                            ? SecureProtectionState.Active
                            : SecureProtectionState.Available
                    };
                }
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                {
                    _micStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Microphone,
                        StandardState = StandardProtectionState.Active,
                        SecureState = _micStatus.SecureState == SecureProtectionState.Active
                            ? SecureProtectionState.Active
                            : SecureProtectionState.Available
                    };
                }
                return Task.FromResult(OperationResult.Ok());
            });

        _nativeMock.Setup(n => n.DisableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .Returns<BlockTarget, CancellationToken>((target, _) =>
            {
                if (target is BlockTarget.Camera or BlockTarget.Both)
                {
                    _cameraStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Camera,
                        StandardState = StandardProtectionState.Inactive,
                        SecureState = SecureProtectionState.Unavailable
                    };
                }
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                {
                    _micStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Microphone,
                        StandardState = StandardProtectionState.Inactive,
                        SecureState = SecureProtectionState.Unavailable
                    };
                }
                return Task.FromResult(OperationResult.Ok());
            });

        _nativeMock.Setup(n => n.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .Returns<BlockTarget, CancellationToken>((target, _) =>
            {
                if (target is BlockTarget.Camera or BlockTarget.Both)
                {
                    _cameraStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Camera,
                        StandardState = _cameraStatus.StandardState,
                        SecureState = SecureProtectionState.Active
                    };
                }
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                {
                    _micStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Microphone,
                        StandardState = _micStatus.StandardState,
                        SecureState = SecureProtectionState.Active
                    };
                }
                return Task.FromResult(OperationResult.Ok());
            });

        _nativeMock.Setup(n => n.DisableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()))
            .Returns<BlockTarget, CancellationToken>((target, _) =>
            {
                if (target is BlockTarget.Camera or BlockTarget.Both)
                {
                    _cameraStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Camera,
                        StandardState = _cameraStatus.StandardState,
                        SecureState = SecureProtectionState.Available
                    };
                }
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                {
                    _micStatus = new TargetProtectionStatus
                    {
                        Target = BlockTarget.Microphone,
                        StandardState = _micStatus.StandardState,
                        SecureState = SecureProtectionState.Available
                    };
                }
                return Task.FromResult(OperationResult.Ok());
            });
    }

    private ProtectionService CreateService() =>
        new(_nativeMock.Object, _detectorMock.Object, _capabilityMock.Object, _stateStore);

    /// <summary>
    /// Test 1: Camera blocked, Mic unlocked, Advanced OFF -> Advanced ON.
    /// Expected: Camera advanced, Mic untouched.
    /// </summary>
    [Fact]
    public async Task Test01_CameraBlocked_MicUnlocked_AdvancedOff_To_AdvancedOn()
    {
        var service = CreateService();

        // 1. Block Camera with Advanced OFF
        var blockResult = await service.BlockDeviceAsync(BlockTarget.Camera);
        Assert.True(blockResult.Success);
        Assert.False(service.IsAdvancedProtectionEnabled);

        var intermediateState = await service.GetCurrentStateAsync();
        Assert.True(intermediateState.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Available, intermediateState.Camera.SecureState);
        Assert.False(intermediateState.Microphone.IsProtected);

        // 2. Turn Advanced ON
        var advResult = await service.SetAdvancedProtectionAsync(true);
        Assert.True(advResult.Success);
        Assert.True(service.IsAdvancedProtectionEnabled);

        // 3. Verify: Camera received secure, Mic was never touched
        _nativeMock.Verify(n => n.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);
        _nativeMock.Verify(n => n.EnableSecureProtectionAsync(BlockTarget.Microphone, It.IsAny<CancellationToken>()), Times.Never);

        var finalState = await service.GetCurrentStateAsync();
        Assert.True(finalState.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Active, finalState.Camera.SecureState);
        Assert.True(finalState.CameraAdvancedProtected);

        Assert.False(finalState.Microphone.IsProtected);
        Assert.False(finalState.MicrophoneAdvancedProtected);
    }

    /// <summary>
    /// Test 2: Camera blocked, Advanced ON, Mic unlocked -> Block Mic.
    /// Expected: Camera advanced, Mic blocked + advanced.
    /// </summary>
    [Fact]
    public async Task Test02_CameraBlocked_AdvancedOn_MicUnlocked_BlockMic_ReceivesAdvanced()
    {
        var service = CreateService();

        // Setup: Advanced ON, Camera blocked
        await service.SetAdvancedProtectionAsync(true);
        await service.BlockDeviceAsync(BlockTarget.Camera);

        var state1 = await service.GetCurrentStateAsync();
        Assert.True(state1.CameraAdvancedProtected);
        Assert.False(state1.Microphone.IsProtected);

        // Action: Block Mic
        var blockMicResult = await service.BlockDeviceAsync(BlockTarget.Microphone);
        Assert.True(blockMicResult.Success);

        // Verify: Mic received both standard and secure automatically
        var state2 = await service.GetCurrentStateAsync();
        Assert.True(state2.CameraAdvancedProtected);
        Assert.True(state2.Microphone.IsProtected);
        Assert.Equal(SecureProtectionState.Active, state2.Microphone.SecureState);
        Assert.True(state2.MicrophoneAdvancedProtected);
        Assert.True(state2.BothSecure);
    }

    /// <summary>
    /// Test 3: Camera + Mic blocked, Advanced ON -> Advanced OFF.
    /// Expected: Camera still blocked, Mic still blocked, Advanced removed from both.
    /// </summary>
    [Fact]
    public async Task Test03_BothBlocked_AdvancedOn_To_AdvancedOff_LeavesBothBlockedStandard()
    {
        var service = CreateService();

        // Setup: Both blocked with Advanced ON
        await service.SetAdvancedProtectionAsync(true);
        await service.BlockDeviceAsync(BlockTarget.Both);

        var stateBefore = await service.GetCurrentStateAsync();
        Assert.True(stateBefore.BothProtected);
        Assert.True(stateBefore.BothSecure);

        // Action: Advanced OFF
        var advOffResult = await service.SetAdvancedProtectionAsync(false);
        Assert.True(advOffResult.Success);
        Assert.False(service.IsAdvancedProtectionEnabled);

        // Verify: Native DisableSecureProtectionAsync was called for both
        _nativeMock.Verify(n => n.DisableSecureProtectionAsync(BlockTarget.Both, It.IsAny<CancellationToken>()), Times.Once);

        // Verify: Neither device was unblocked from Standard protection
        _nativeMock.Verify(n => n.DisableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);

        var stateAfter = await service.GetCurrentStateAsync();
        Assert.True(stateAfter.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Available, stateAfter.Camera.SecureState);
        Assert.False(stateAfter.CameraAdvancedProtected);

        Assert.True(stateAfter.Microphone.IsProtected);
        Assert.Equal(SecureProtectionState.Available, stateAfter.Microphone.SecureState);
        Assert.False(stateAfter.MicrophoneAdvancedProtected);
    }

    /// <summary>
    /// Test 4: Camera + Mic blocked + advanced -> Unlock Camera.
    /// Expected: Camera restored, Mic remains blocked + advanced, Advanced global remains ON.
    /// </summary>
    [Fact]
    public async Task Test04_BothBlockedAdvanced_UnlockCamera_LeavesMicAdvancedAndGlobalOn()
    {
        var service = CreateService();

        // Setup: Both blocked + advanced, Advanced ON
        await service.SetAdvancedProtectionAsync(true);
        await service.BlockDeviceAsync(BlockTarget.Both);

        // Action: Unlock Camera
        var unblockResult = await service.UnblockDeviceAsync(BlockTarget.Camera);
        Assert.True(unblockResult.Success);

        // Verify: Camera is unblocked
        var state = await service.GetCurrentStateAsync();
        Assert.False(state.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Unavailable, state.Camera.SecureState);
        Assert.False(state.CameraAdvancedProtected);

        // Verify: Mic remains blocked + advanced
        Assert.True(state.Microphone.IsProtected);
        Assert.Equal(SecureProtectionState.Active, state.Microphone.SecureState);
        Assert.True(state.MicrophoneAdvancedProtected);

        // Verify: Global Advanced Protection remains ON
        Assert.True(service.IsAdvancedProtectionEnabled);
        Assert.True(state.AdvancedProtectionEnabled);
    }

    /// <summary>
    /// Test 5: Advanced ON, No devices blocked -> Block Camera.
    /// Expected: Camera blocked + advanced.
    /// </summary>
    [Fact]
    public async Task Test05_AdvancedOn_NoDevicesBlocked_BlockCamera_ResultsInBlockedAdvanced()
    {
        var service = CreateService();

        // Action 1: Turn Advanced ON with no devices blocked (must succeed without error)
        var advResult = await service.SetAdvancedProtectionAsync(true);
        Assert.True(advResult.Success);
        Assert.True(service.IsAdvancedProtectionEnabled);

        // No native mutation should have been dispatched because no devices were blocked
        _nativeMock.Verify(n => n.EnableSecureProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _nativeMock.Verify(n => n.EnableStandardProtectionAsync(It.IsAny<BlockTarget>(), It.IsAny<CancellationToken>()), Times.Never);

        // Action 2: Block Camera
        var blockCamResult = await service.BlockDeviceAsync(BlockTarget.Camera);
        Assert.True(blockCamResult.Success);

        // Verify: Camera is blocked + advanced; Mic remains unlocked
        var state = await service.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Active, state.Camera.SecureState);
        Assert.True(state.CameraAdvancedProtected);

        Assert.False(state.Microphone.IsProtected);
        Assert.False(state.MicrophoneAdvancedProtected);
    }

    /// <summary>
    /// Test 6: Advanced ON, No devices blocked -> Block Mic.
    /// Expected: Mic blocked + advanced.
    /// </summary>
    [Fact]
    public async Task Test06_AdvancedOn_NoDevicesBlocked_BlockMic_ResultsInBlockedAdvanced()
    {
        var service = CreateService();

        // Action 1: Turn Advanced ON
        await service.SetAdvancedProtectionAsync(true);

        // Action 2: Block Mic
        var blockMicResult = await service.BlockDeviceAsync(BlockTarget.Microphone);
        Assert.True(blockMicResult.Success);

        // Verify: Mic is blocked + advanced; Camera remains unlocked
        var state = await service.GetCurrentStateAsync();
        Assert.True(state.Microphone.IsProtected);
        Assert.Equal(SecureProtectionState.Active, state.Microphone.SecureState);
        Assert.True(state.MicrophoneAdvancedProtected);

        Assert.False(state.Camera.IsProtected);
        Assert.False(state.CameraAdvancedProtected);
    }

    /// <summary>
    /// Test 7: Advanced ON, Camera blocked -> Unlock Camera -> Block Camera again.
    /// Expected: advanced automatically reapplied.
    /// </summary>
    [Fact]
    public async Task Test07_AdvancedOn_UnlockCamera_Then_RelockCamera_AutomaticallyReappliesAdvanced()
    {
        var service = CreateService();

        // 1. Initial: Advanced ON, Camera blocked
        await service.SetAdvancedProtectionAsync(true);
        await service.BlockDeviceAsync(BlockTarget.Camera);

        var state1 = await service.GetCurrentStateAsync();
        Assert.True(state1.CameraAdvancedProtected);

        // 2. Unlock Camera
        await service.UnblockDeviceAsync(BlockTarget.Camera);
        var state2 = await service.GetCurrentStateAsync();
        Assert.False(state2.Camera.IsProtected);
        Assert.True(service.IsAdvancedProtectionEnabled);

        // 3. Block Camera again
        await service.BlockDeviceAsync(BlockTarget.Camera);
        var state3 = await service.GetCurrentStateAsync();
        Assert.True(state3.Camera.IsProtected);
        Assert.Equal(SecureProtectionState.Active, state3.Camera.SecureState);
        Assert.True(state3.CameraAdvancedProtected);
    }

    /// <summary>
    /// Test 8: Camera blocked, Mic unlocked, Advanced ON -> restart / recovery scenario.
    /// Expected: state restoration remains correct.
    /// </summary>
    [Fact]
    public async Task Test08_CameraBlocked_AdvancedOn_ShutdownRecovery_RestoresCorrectly()
    {
        var recoveryDeps = new RecoveryHostDependencies();
        var sessionStore = new RecordingPrivacySessionStore();
        var platformAdapter = new ScriptedPrivacySessionPlatformAdapter();

        var privacySessionService = new PrivacySessionService(sessionStore, platformAdapter);
        var service = new ProtectionService(
            recoveryDeps,
            recoveryDeps,
            recoveryDeps,
            recoveryDeps,
            privacySessionService);

        // 1. Turn Advanced ON and block Camera
        await service.SetAdvancedProtectionAsync(true);
        await service.BlockDeviceAsync(BlockTarget.Camera);

        // 2. Simulate shutdown & restore
        var recoveryResult = await service.BeginShutdownAndRestoreAsync("TestRestart");

        // Verify: Recovery executed cleanly
        Assert.True(recoveryResult.SafeToExit);
        Assert.Equal(0, recoveryResult.FailedCount);

        // Desired device state is cleared
        var desired = recoveryDeps.Load();
        Assert.Equal(StandardProtectionState.Inactive, desired.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, desired.MicrophoneStandard);
    }

    /// <summary>
    /// Test 9: Device previously disabled externally. Advanced operations executed.
    /// Expected: restore does not overwrite external state.
    /// </summary>
    [Fact]
    public async Task Test09_DeviceDisabledExternally_RestoreDoesNotOverwriteExternalState()
    {
        var recoveryDeps = new RecoveryHostDependencies();
        var sessionStore = new RecordingPrivacySessionStore();
        var platformAdapter = new ScriptedPrivacySessionPlatformAdapter();

        // Simulate external state where Camera was already disabled prior to PrivLock
        // so no snapshot belongs to PrivLock.
        recoveryDeps.CurrentProtectionState = new FullProtectionState
        {
            Camera = new TargetProtectionStatus
            {
                Target = BlockTarget.Camera,
                StandardState = StandardProtectionState.Active,
                SecureState = SecureProtectionState.Active
            },
            Microphone = new TargetProtectionStatus
            {
                Target = BlockTarget.Microphone,
                StandardState = StandardProtectionState.Inactive,
                SecureState = SecureProtectionState.Unavailable
            }
        };

        var privacySessionService = new PrivacySessionService(sessionStore, platformAdapter);
        var service = new ProtectionService(
            recoveryDeps,
            recoveryDeps,
            recoveryDeps,
            recoveryDeps,
            privacySessionService);

        // User requests disable on Camera without an owned PrivLock session
        var result = await service.UnblockDeviceAsync(BlockTarget.Camera);

        // External conflict is preserved, NOT blind unblock
        Assert.False(result.Success);
        Assert.Contains("external", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test 10: Repeated: Advanced ON, Advanced ON, Advanced OFF, Advanced OFF.
    /// Expected: Idempotent behavior, no corruption.
    /// </summary>
    [Fact]
    public async Task Test10_RepeatedAdvancedOnAndOff_IsIdempotentWithoutCorruption()
    {
        var service = CreateService();

        // 1. Block Camera first
        await service.BlockDeviceAsync(BlockTarget.Camera);
        _nativeMock.Invocations.Clear();

        // 2. Advanced ON (First call)
        var on1 = await service.SetAdvancedProtectionAsync(true);
        Assert.True(on1.Success);
        Assert.True(service.IsAdvancedProtectionEnabled);
        _nativeMock.Verify(n => n.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);

        // 3. Advanced ON (Second call - duplicate)
        var on2 = await service.SetAdvancedProtectionAsync(true);
        Assert.True(on2.Success);
        Assert.True(service.IsAdvancedProtectionEnabled);
        // Verify: Not called again!
        _nativeMock.Verify(n => n.EnableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);

        // 4. Advanced OFF (First call)
        var off1 = await service.SetAdvancedProtectionAsync(false);
        Assert.True(off1.Success);
        Assert.False(service.IsAdvancedProtectionEnabled);
        _nativeMock.Verify(n => n.DisableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);

        // 5. Advanced OFF (Second call - duplicate)
        var off2 = await service.SetAdvancedProtectionAsync(false);
        Assert.True(off2.Success);
        Assert.False(service.IsAdvancedProtectionEnabled);
        // Verify: Not called again!
        _nativeMock.Verify(n => n.DisableSecureProtectionAsync(BlockTarget.Camera, It.IsAny<CancellationToken>()), Times.Once);

        // Device remains blocked at standard level
        var finalState = await service.GetCurrentStateAsync();
        Assert.True(finalState.Camera.IsProtected);
        Assert.False(finalState.CameraAdvancedProtected);
        Assert.False(service.IsAdvancedProtectionEnabled);
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        private DesiredState _state = new();

        public DesiredState Load() => new()
        {
            CameraStandard = _state.CameraStandard,
            CameraSecure = _state.CameraSecure,
            MicrophoneStandard = _state.MicrophoneStandard,
            MicrophoneSecure = _state.MicrophoneSecure,
            AdvancedProtectionEnabled = _state.AdvancedProtectionEnabled,
            Language = _state.Language,
            Autostart = _state.Autostart
        };

        public void Save(DesiredState state) => _state = new()
        {
            CameraStandard = state.CameraStandard,
            CameraSecure = state.CameraSecure,
            MicrophoneStandard = state.MicrophoneStandard,
            MicrophoneSecure = state.MicrophoneSecure,
            AdvancedProtectionEnabled = state.AdvancedProtectionEnabled,
            Language = state.Language,
            Autostart = state.Autostart
        };
    }
}
