using Moq;
using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public sealed class WalFailureRecoveryTests
{
    [Fact]
    public async Task EmergencyRollback_SecureOriginalState_RemainsRetryableUntilClaimIsRetired()
    {
        var policy = PrivacyRecoveryTestData.Policy(
            "emergency-secure-claim",
            valueExisted: true,
            originalValue: "1",
            journalState: PrivacyResourceJournalState.Captured,
            layer: ProtectionLayer.Secure);
        policy.ModifiedByPrivLock = false;
        policy.OwnershipUncertain = false;
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [policy] };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Secure,
            BlockTarget.Camera,
            "Op-EmergencySecureClaim");
        platform.SetObservation(policy.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        platform.SetOwnershipAttestation(
            policy.ResourceId,
            PrivacyOwnershipAttestationKind.Error,
            "claim store unavailable");

        var rollback = await service.RollbackConfirmedApplyAfterCheckpointFailureAsync(
            preparation,
            OperationResult.Ok([
                new DeviceOperationDetail
                {
                    DeviceId = policy.ResourceId,
                    FriendlyName = "machine policy",
                    Success = true
                }
            ]));

        Assert.False(rollback.SafeToExit);
        Assert.Equal(1, rollback.FailedCount);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, Assert.Single(store.Current!.Resources).JournalState);

        platform.SetOwnershipAttestation(policy.ResourceId, PrivacyOwnershipAttestationKind.NotConfirmed);
        var recovery = await service.RecoverUnfinishedSessionAsync();

        Assert.True(recovery.SafeToExit);
        Assert.Equal(PrivacyResourceJournalState.Restored, Assert.Single(store.Current.Resources).JournalState);
        Assert.Empty(platform.RestoreCalls);
    }

    [Fact]
    public async Task ConfirmedApply_WhenOutcomeCheckpointFails_IsImmediatelyCompensated()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "checkpoint-failure-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var journal = new RecordingPrivacySessionStore
        {
            // Prepare succeeds; every post-mutation checkpoint fails.
            SaveFailureFactory = attempt => attempt >= 2
                ? new IOException("recovery volume unavailable")
                : null
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera]
        };
        var sessions = new PrivacySessionService(journal, platform);

        var native = new Mock<IDeviceProtectionProvider>();
        native.Setup(provider => provider.EnableStandardProtectionAsync(
                BlockTarget.Camera,
                It.IsAny<CancellationToken>()))
            .Callback(() => platform.SetObservation(
                camera.ResourceId,
                PrivacyResourceObservationKind.MatchesProtected))
            .ReturnsAsync(OperationResult.Ok());
        native.Setup(provider => provider.GetProtectionStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(InactiveState());

        var detector = new Mock<IDeviceDetector>();
        detector.Setup(value => value.DetectAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var capabilities = new Mock<IPlatformCapabilityProvider>();
        capabilities.SetupGet(value => value.Capabilities).Returns(new PlatformCapabilities());
        capabilities.SetupGet(value => value.PlatformInfo).Returns(new PlatformInfo
        {
            OperatingSystemName = "Test",
            OsVersion = "1",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        });
        var desired = new Mock<IStateStore>();
        desired.Setup(value => value.Load()).Returns(new DesiredState());
        var service = new ProtectionService(
            native.Object,
            detector.Object,
            capabilities.Object,
            desired.Object,
            sessions);

        var result = await service.EnableStandardProtectionAsync(BlockTarget.Camera);

        Assert.False(result.Success);
        Assert.Contains(camera.ResourceId, platform.RestoreCalls);
        Assert.Equal(PrivacyResourceObservationKind.MatchesOriginal,
            (await platform.ObserveAsync(camera)).Kind);
        desired.Verify(value => value.Save(It.IsAny<DesiredState>()), Times.Never);
    }

    [Fact]
    public async Task PartialOutcomeCheckpointFailure_CompensatesOnlyApplyPendingResources()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "durably-applied-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var microphone = PrivacyRecoveryTestData.Device(
            "uncheckpointed-microphone",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false,
            target: BlockTarget.Microphone);
        var journal = new RecordingPrivacySessionStore
        {
            SaveFailureFactory = attempt => attempt >= 3
                ? new IOException("recovery volume unavailable")
                : null
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera, microphone]
        };
        var dependencies = new RecoveryHostDependencies
        {
            StandardEnableResult = OperationResult.Ok(
            [
                new DeviceOperationDetail
                {
                    DeviceId = camera.ResourceId,
                    FriendlyName = camera.ResourceId,
                    Success = true
                },
                new DeviceOperationDetail
                {
                    DeviceId = microphone.ResourceId,
                    FriendlyName = microphone.ResourceId,
                    Success = true
                }
            ])
        };
        dependencies.BeforeStandardEnableReturn = () =>
        {
            platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
            platform.SetObservation(microphone.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        };
        var sessions = new PrivacySessionService(journal, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var result = await protection.EnableStandardProtectionAsync(BlockTarget.Both);

        Assert.False(result.Success);
        Assert.Contains("remains incomplete", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([microphone.ResourceId], platform.RestoreCalls);
        var durableAfterFailure = journal.Current!.Resources;
        var durableCamera = durableAfterFailure.Single(resource => resource.ResourceId == camera.ResourceId);
        var durableMicrophone = durableAfterFailure.Single(resource => resource.ResourceId == microphone.ResourceId);
        Assert.Equal(PrivacyResourceJournalState.Applied, durableCamera.JournalState);
        Assert.True(durableCamera.ModifiedByPrivLock);
        Assert.Equal(PrivacyResourceObservationKind.MatchesProtected,
            (await platform.ObserveAsync(durableCamera)).Kind);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, durableMicrophone.JournalState);
        Assert.False(durableMicrophone.ModifiedByPrivLock);
        Assert.True(durableMicrophone.OwnershipUncertain);
        Assert.Equal(PrivacyResourceObservationKind.MatchesOriginal,
            (await platform.ObserveAsync(durableMicrophone)).Kind);

        journal.SaveFailureFactory = null;
        var recovery = await sessions.RestoreAsync(null, null, "StorageRecovered");

        Assert.True(recovery.SafeToExit);
        Assert.Equal([microphone.ResourceId, camera.ResourceId], platform.RestoreCalls);
        Assert.All(journal.Current!.Resources,
            resource => Assert.Equal(PrivacyResourceJournalState.Restored, resource.JournalState));
    }

    [Fact]
    public async Task Restore_WhenCallerCancelsAfterRestorePendingCheckpoint_CompletesNativeRestore()
    {
        var camera = PrivacyRecoveryTestData.Device("cancel-after-restore-pending");
        using var cancellation = new CancellationTokenSource();
        var journal = new RecordingPrivacySessionStore(
            PrivacyRecoveryTestData.ActiveSession(camera))
        {
            AfterSave = snapshot =>
            {
                if (snapshot.Resources.Single().JournalState == PrivacyResourceJournalState.RestorePending)
                    cancellation.Cancel();
            }
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(journal, platform);

        var result = await service.RestoreAsync(
            null,
            null,
            "CancellationRace",
            cancellation.Token);

        Assert.True(result.SafeToExit);
        Assert.Equal([camera.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacyResourceJournalState.Restored,
            Assert.Single(journal.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task PrepareBlock_WithFutureCapturedTimestamp_ClampsProgressMonotonically()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "clock-rollback-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        camera.CapturedAtUtc = DateTimeOffset.UtcNow.AddDays(2);
        camera.LastUpdatedAtUtc = camera.CapturedAtUtc;
        var journal = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        var service = new PrivacySessionService(journal, platform);

        await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "ClockRollback");

        var persisted = Assert.Single(journal.Current!.Resources);
        Assert.True(persisted.LastUpdatedAtUtc >= persisted.CapturedAtUtc);
        Assert.True(journal.Current.UpdatedAtUtc >= persisted.LastUpdatedAtUtc);
    }

    private static FullProtectionState InactiveState() => new()
    {
        Camera = new TargetProtectionStatus
        {
            Target = BlockTarget.Camera,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable,
            IsVerified = true
        },
        Microphone = new TargetProtectionStatus
        {
            Target = BlockTarget.Microphone,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable,
            IsVerified = true
        }
    };
}
