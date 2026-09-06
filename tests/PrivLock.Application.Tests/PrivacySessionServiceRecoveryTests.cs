using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public sealed class PrivacySessionServiceRecoveryTests
{
    [Fact]
    public async Task PrepareBlockAsync_CommitsApplyPendingSnapshotBeforeReturningToMutationCaller()
    {
        var trace = new List<string>();
        var resource = PrivacyRecoveryTestData.Device(
            "camera-a",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore
        {
            AfterSave = snapshot =>
            {
                var saved = Assert.Single(snapshot.Resources);
                Assert.Equal(PrivacyResourceJournalState.ApplyPending, saved.JournalState);
                Assert.False(saved.ModifiedByPrivLock);
                trace.Add("save");
            }
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [resource],
            OnCapture = () => trace.Add("capture")
        };
        var service = new PrivacySessionService(store, platform);

        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-Snapshot");

        // A platform mutation belongs to the caller and can only happen after Prepare returns.
        trace.Add("caller-mutation");

        Assert.True(preparation.TrackingEnabled);
        Assert.Equal(["capture", "save", "caller-mutation"], trace);
        Assert.Equal(resource.ResourceId, Assert.Single(preparation.ResourceIds));
        Assert.Equal("Op-Snapshot", store.Current!.LastOperationId);
    }

    [Fact]
    public async Task PrepareBlockAsync_WhenSnapshotCommitFails_DoesNotReturnMutationPreparation()
    {
        var store = new RecordingPrivacySessionStore
        {
            SaveException = new IOException("disk unavailable")
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources =
            [
                PrivacyRecoveryTestData.Device(
                    "camera-a",
                    journalState: PrivacyResourceJournalState.Captured,
                    modifiedByPrivLock: false)
            ]
        };
        var service = new PrivacySessionService(store, platform);

        var error = await Assert.ThrowsAsync<IOException>(() => service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-FailedCommit"));

        Assert.Equal("disk unavailable", error.Message);
        Assert.Empty(store.SavedSnapshots);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task ValidatePreparedResourcesAsync_ExternalPreDispatchChange_IsNeverRestored()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "predispatch-race-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-PreDispatchRace");
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);

        var validation = await service.ValidatePreparedResourcesAsync(preparation);
        var recovery = await service.RecoverUnfinishedSessionAsync();

        Assert.False(validation.Success);
        Assert.True(recovery.SafeToExit);
        Assert.Empty(platform.RestoreCalls);
        var persisted = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.ApplyFailed, persisted.JournalState);
        Assert.False(persisted.ModifiedByPrivLock);
    }

    [Fact]
    public async Task RestoreAsync_NormalRestore_MutatesOnlyOwnedProtectedResource()
    {
        var owned = PrivacyRecoveryTestData.Device("owned-camera");
        var failedBeforeMutation = PrivacyRecoveryTestData.Device(
            "not-owned-camera",
            journalState: PrivacyResourceJournalState.ApplyFailed,
            modifiedByPrivLock: false);
        var preDisabled = PrivacyRecoveryTestData.Device(
            "pre-disabled-camera",
            originalEnabled: false,
            protectedEnabled: false,
            journalState: PrivacyResourceJournalState.Unchanged,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore(
            PrivacyRecoveryTestData.ActiveSession(owned, failedBeforeMutation, preDisabled));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(owned.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(failedBeforeMutation.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.IsComplete);
        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(0, result.AlreadyRestoredCount);
        Assert.Equal([owned.ResourceId], platform.RestoreCalls);
        Assert.DoesNotContain(failedBeforeMutation.ResourceId, platform.ObserveCalls);
        Assert.DoesNotContain(preDisabled.ResourceId, platform.ObserveCalls);
        Assert.Equal(PrivacySessionStatus.Restored, store.Current!.Status);
        Assert.False(store.Current.IsActive);
    }

    [Fact]
    public async Task RestoreAsync_DeviceDisabledBeforePrivLock_RemainsUnchanged()
    {
        var preDisabled = PrivacyRecoveryTestData.Device(
            "manual-disabled-camera",
            originalEnabled: false,
            protectedEnabled: false,
            journalState: PrivacyResourceJournalState.Unchanged,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore(
            PrivacyRecoveryTestData.ActiveSession(preDisabled));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.IsComplete);
        Assert.Empty(platform.ObserveCalls);
        Assert.Empty(platform.RestoreCalls);
        var persisted = Assert.IsType<OriginalDeviceState>(Assert.Single(store.Current!.Resources));
        Assert.False(persisted.OriginalEnabledState);
        Assert.Equal(PrivacyResourceJournalState.Unchanged, persisted.JournalState);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_PartialBlock_CheckpointsEachResourceTruthfully()
    {
        var cameraA = PrivacyRecoveryTestData.Device(
            "camera-a",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var cameraB = PrivacyRecoveryTestData.Device(
            "camera-b",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [cameraA, cameraB]
        };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-PartialBlock");
        platform.SetObservation(cameraA.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(cameraB.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var platformResult = OperationResult.Fail(
            "camera-b failed",
            [
                Detail("camera-a", success: true),
                Detail("camera-b", success: false)
            ]);

        var result = await service.RecordBlockOutcomeAsync(preparation, platformResult);

        Assert.False(result.Success);
        var current = store.Current!;
        var persistedA = current.Resources.Single(resource => resource.ResourceId == cameraA.ResourceId);
        var persistedB = current.Resources.Single(resource => resource.ResourceId == cameraB.ResourceId);
        Assert.Equal(PrivacyResourceJournalState.Applied, persistedA.JournalState);
        Assert.True(persistedA.ModifiedByPrivLock);
        Assert.Equal(PrivacyResourceJournalState.ApplyFailed, persistedB.JournalState);
        Assert.False(persistedB.ModifiedByPrivLock);
        Assert.True(current.IsActive);

        Assert.Contains(store.SavedSnapshots, snapshot =>
            snapshot.Resources.Single(resource => resource.ResourceId == cameraA.ResourceId).JournalState ==
                PrivacyResourceJournalState.Applied &&
            snapshot.Resources.Single(resource => resource.ResourceId == cameraB.ResourceId).JournalState ==
                PrivacyResourceJournalState.ApplyPending);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_ObservationErrorAfterReportedSuccess_RemainsRecoverable()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "camera-ambiguous",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera]
        };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-Ambiguous");
        platform.SetObservation(
            camera.ResourceId,
            PrivacyResourceObservationKind.Error,
            "transient query failure");

        var record = await service.RecordBlockOutcomeAsync(preparation, OperationResult.Ok());

        Assert.False(record.Success);
        Assert.True(store.Current!.IsActive);
        Assert.Equal(
            PrivacyResourceJournalState.Applied,
            Assert.Single(store.Current.Resources).JournalState);

        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var recovery = await service.RecoverUnfinishedSessionAsync();
        Assert.True(recovery.SafeToExit);
        Assert.Equal([camera.ResourceId], platform.RestoreCalls);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_SecureOriginalState_DoesNotTerminalizeUntilClaimIsRetired()
    {
        var policy = PrivacyRecoveryTestData.Policy(
            "secure-claim-cleanup",
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
            "Op-SecureClaimCleanup");
        platform.SetObservation(policy.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        platform.SetOwnershipAttestation(
            policy.ResourceId,
            PrivacyOwnershipAttestationKind.Error,
            "claim cleanup unavailable");

        var record = await service.RecordBlockOutcomeAsync(
            preparation,
            OperationResult.Ok([Detail(policy.ResourceId, success: true)]));

        Assert.False(record.Success);
        var retryable = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.RestoreFailed, retryable.JournalState);
        Assert.True(retryable.OwnershipUncertain);
        Assert.True(store.Current.IsActive);

        platform.SetOwnershipAttestation(policy.ResourceId, PrivacyOwnershipAttestationKind.NotConfirmed);
        var recovery = await service.RecoverUnfinishedSessionAsync();

        Assert.True(recovery.SafeToExit);
        Assert.Equal(PrivacyResourceJournalState.Restored, Assert.Single(store.Current.Resources).JournalState);
        Assert.Empty(platform.RestoreCalls);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_FailedFinalCompare_DoesNotAdoptProtectedExternalState()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "external-race-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-ExternalRace");
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);

        var record = await service.RecordBlockOutcomeAsync(
            preparation,
            OperationResult.Fail("final compare conflict", [Detail(camera.ResourceId, success: false)]));

        Assert.False(record.Success);
        var persisted = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.Conflict, persisted.JournalState);
        Assert.False(persisted.ModifiedByPrivLock);
        Assert.False(persisted.OwnershipUncertain);

        var recovery = await service.RecoverUnfinishedSessionAsync();
        Assert.True(recovery.SafeToExit);
        Assert.Equal(0, recovery.ConflictCount);
        Assert.False(recovery.HadRecoveryWork);
        Assert.Empty(platform.RestoreCalls);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_LostCompletionResponse_NeverBlindlyRestoresProtectedState()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "uncertain-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-Uncertain");
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);

        var record = await service.RecordBlockOutcomeAsync(
            preparation,
            OperationResult.Fail(
                "IPC response lost",
                [Detail(camera.ResourceId, success: false, outcomeUncertain: true)],
                outcomeUncertain: true));

        Assert.False(record.Success);
        var pending = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, pending.JournalState);
        Assert.True(pending.OwnershipUncertain);

        var recovery = await service.RecoverUnfinishedSessionAsync();
        Assert.True(recovery.SafeToExit);
        Assert.Equal(1, recovery.ConflictCount);
        Assert.Empty(platform.RestoreCalls);
        var terminal = Assert.Single(store.Current.Resources);
        Assert.Equal(PrivacyResourceJournalState.Conflict, terminal.JournalState);
        Assert.False(terminal.OwnershipUncertain);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_UnquiescedNativeActor_DefersAllObservation()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "still-running-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-StillRunning");

        var record = await service.RecordBlockOutcomeAsync(
            preparation,
            OperationResult.Fail(
                "helper could not be stopped",
                [new DeviceOperationDetail
                {
                    DeviceId = camera.ResourceId,
                    FriendlyName = camera.ResourceId,
                    Success = false,
                    OutcomeUncertain = true,
                    ExecutionStillInFlight = true
                }],
                outcomeUncertain: true,
                executionStillInFlight: true));

        Assert.False(record.Success);
        Assert.Empty(platform.ObserveCalls);
        var pending = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, pending.JournalState);
        Assert.True(pending.OwnershipUncertain);
        Assert.True(store.Current.IsActive);
    }

    [Fact]
    public async Task RestoreAsync_InFlightActorMustQuiesceBeforeAnyObservation()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "quiescence-camera",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        camera.OwnershipUncertain = true;
        camera.ExecutionMayStillBeInFlight = true;
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(camera));
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            QuiescenceResult = OperationResult.Fail("helper lease remains held")
        };
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var service = new PrivacySessionService(store, platform);

        var deferred = await service.RestoreAsync(null, null, "SameProcessShutdown");

        Assert.False(deferred.SafeToExit);
        Assert.Empty(platform.ObserveCalls);
        Assert.Empty(platform.RestoreCalls);
        Assert.True(Assert.Single(store.Current!.Resources).ExecutionMayStillBeInFlight);

        platform.QuiescenceResult = OperationResult.Ok();
        var completed = await service.RestoreAsync(null, null, "QuiescedRetry");

        Assert.True(completed.SafeToExit);
        Assert.Equal(1, completed.AlreadyRestoredCount);
        Assert.Single(platform.ObserveCalls);
        Assert.Empty(platform.RestoreCalls);
        Assert.False(Assert.Single(store.Current!.Resources).ExecutionMayStillBeInFlight);
    }

    [Fact]
    public async Task RecordBlockOutcomeAsync_InFlightIdempotentReapply_PreservesPriorOwnership()
    {
        var owned = PrivacyRecoveryTestData.Device("owned-reapply-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(owned));
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [owned] };
        platform.SetObservation(owned.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);
        var preparation = await service.PrepareBlockAsync(
            ProtectionLayer.Standard,
            BlockTarget.Camera,
            "Op-Reapply");

        var record = await service.RecordBlockOutcomeAsync(
            preparation,
            OperationResult.Fail(
                "idempotent worker response timed out",
                [new DeviceOperationDetail
                {
                    DeviceId = owned.ResourceId,
                    FriendlyName = owned.ResourceId,
                    Success = false,
                    OutcomeUncertain = true,
                    ExecutionStillInFlight = true
                }],
                outcomeUncertain: true,
                executionStillInFlight: true));

        Assert.False(record.Success);
        var pending = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.Applied, pending.JournalState);
        Assert.True(pending.ModifiedByPrivLock);
        Assert.False(pending.OwnershipUncertain);
        Assert.True(pending.ExecutionMayStillBeInFlight);

        var recovery = await service.RestoreAsync(null, null, "QuiescedOwnedReapply");

        Assert.True(recovery.SafeToExit);
        Assert.Equal([owned.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacySessionStatus.Restored, store.Current!.Status);
    }

    [Fact]
    public async Task RestoreAsync_PostWriteFailure_RelinquishesOwnershipBeforeAnyRetry()
    {
        var camera = PrivacyRecoveryTestData.Device("post-write-failure-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(camera));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.EnqueueRestoreResults(
            camera.ResourceId,
            OperationResult.Fail("native post-write verification failed", outcomeUncertain: true));
        var service = new PrivacySessionService(store, platform);

        var first = await service.RestoreAsync(null, null, "PostWriteFailure");

        Assert.False(first.SafeToExit);
        var uncertain = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.RestoreFailed, uncertain.JournalState);
        Assert.False(uncertain.ModifiedByPrivLock);
        Assert.True(uncertain.OwnershipUncertain);

        var second = await service.RestoreAsync(null, null, "ConflictReconciliation");

        Assert.True(second.SafeToExit);
        Assert.Equal(1, second.ConflictCount);
        Assert.Equal([camera.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacyResourceJournalState.Conflict, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task RestoreAsync_ExternalChangeAfterVerifiedRestore_IsConflictWithoutSecondWrite()
    {
        var camera = PrivacyRecoveryTestData.Device("post-success-race-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(camera));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.AfterRestore = (_, result) =>
        {
            if (result.Success)
                platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        };
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "PostSuccessExternalRace");

        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal([camera.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacyResourceJournalState.Conflict, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task RestoreAsync_ApplyFailedResourceThatLaterLooksProtected_IsNeverTouched()
    {
        var owned = PrivacyRecoveryTestData.Device("owned-mixed-camera");
        var neverOwned = PrivacyRecoveryTestData.Device(
            "failed-mixed-camera",
            journalState: PrivacyResourceJournalState.ApplyFailed,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(owned, neverOwned));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(owned.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(neverOwned.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "MixedShutdown");

        Assert.True(result.SafeToExit);
        Assert.Equal([owned.ResourceId], platform.RestoreCalls);
        Assert.DoesNotContain(neverOwned.ResourceId, platform.ObserveCalls);
    }

    [Fact]
    public async Task RestoreAsync_MissingOwnedResource_RemainsRetryableWithoutBlindMutation()
    {
        var device = PrivacyRecoveryTestData.Device("unplugged-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.Missing);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "DeviceUnplugged");

        Assert.False(result.IsComplete);
        Assert.False(result.SafeToExit);
        Assert.Equal(1, result.MissingCount);
        Assert.Empty(platform.RestoreCalls);
        Assert.Contains("remain pending", result.ErrorMessage);
        var session = store.Current!;
        Assert.True(session.IsActive);
        Assert.Equal(PrivacySessionStatus.RecoveryIncomplete, session.Status);
        Assert.Equal(
            PrivacyResourceJournalState.Missing,
            Assert.Single(session.Resources).JournalState);
    }

    [Fact]
    public async Task RestoreAsync_ExternalConflict_IsPreservedAndClassifiedAsTerminal()
    {
        var device = PrivacyRecoveryTestData.Device("externally-changed-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(
            device.ResourceId,
            PrivacyResourceObservationKind.Conflict,
            "state belongs to another actor");
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.IsComplete);
        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.ConflictCount);
        Assert.Empty(platform.RestoreCalls);
        var session = store.Current!;
        Assert.False(session.IsActive);
        Assert.False(session.WasRestored);
        Assert.Equal(PrivacySessionStatus.CompletedWithConflicts, session.Status);
        Assert.Equal(
            PrivacyResourceJournalState.Conflict,
            Assert.Single(session.Resources).JournalState);

        var repeated = await service.RecoverUnfinishedSessionAsync();
        Assert.True(repeated.SafeToExit);
        Assert.Equal(0, repeated.ConflictCount);
        Assert.False(repeated.HadRecoveryWork);
    }

    [Fact]
    public async Task RestoreAsync_PartialFailure_RetriesOnlyOutstandingResourceAndCompletes()
    {
        var camera = PrivacyRecoveryTestData.Device("camera");
        var microphone = PrivacyRecoveryTestData.Device(
            "microphone",
            target: BlockTarget.Microphone);
        var store = new RecordingPrivacySessionStore(
            PrivacyRecoveryTestData.ActiveSession(camera, microphone));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(microphone.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.EnqueueRestoreResults(
            microphone.ResourceId,
            OperationResult.Fail("transient native error"),
            OperationResult.Ok());
        var service = new PrivacySessionService(store, platform);

        var first = await service.RestoreAsync(null, null, "FirstAttempt");

        Assert.False(first.IsComplete);
        Assert.Equal(1, first.RestoredCount);
        Assert.Equal(1, first.FailedCount);
        Assert.True(store.Current!.IsActive);

        var second = await service.RestoreAsync(null, null, "Retry");

        Assert.True(second.IsComplete);
        Assert.Equal(1, second.RestoredCount);
        Assert.Equal([camera.ResourceId, microphone.ResourceId, microphone.ResourceId], platform.RestoreCalls);
        var resources = store.Current!.Resources;
        Assert.Equal(1, resources.Single(resource => resource.ResourceId == camera.ResourceId).RestoreAttempts);
        Assert.Equal(2, resources.Single(resource => resource.ResourceId == microphone.ResourceId).RestoreAttempts);
        Assert.All(resources, resource => Assert.Equal(PrivacyResourceJournalState.Restored, resource.JournalState));
        Assert.Equal(PrivacySessionStatus.Restored, store.Current.Status);
    }

    [Fact]
    public async Task RestoreAsync_ReversesSecurePnpBeforeStandardAudioDependency()
    {
        var audio = PrivacyRecoveryTestData.AudioEndpoint("microphone-endpoint");
        var pnp = PrivacyRecoveryTestData.Device(
            "microphone-pnp",
            layer: ProtectionLayer.Secure,
            target: BlockTarget.Microphone);
        // Standard was applied first, so it appears first in the journal. Restore must explicitly
        // reverse that dependency rather than relying on insertion order.
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(audio, pnp));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(audio.ResourceId, PrivacyResourceObservationKind.Missing);
        platform.SetObservation(pnp.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.BeforeRestoreAsync = (resource, _, _) =>
        {
            if (resource.ResourceId == pnp.ResourceId)
                platform.SetObservation(audio.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
            return Task.CompletedTask;
        };
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.SafeToExit);
        Assert.Equal([pnp.ResourceId, audio.ResourceId], platform.RestoreCalls);
    }

    [Fact]
    public async Task RestoreAsync_PolicyThatOriginallyExisted_PreservesExactTypeAndValue()
    {
        var policy = PrivacyRecoveryTestData.Policy(
            "existing-policy",
            valueExisted: true,
            originalValue: "1");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(policy));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(policy.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.SafeToExit);
        var restored = Assert.IsType<OriginalPolicyState>(Assert.Single(platform.RestoredResources));
        Assert.True(restored.ValueExisted);
        Assert.Equal(PrivacyRegistryValueKind.DWord, restored.OriginalValueKind);
        Assert.Equal("1", restored.OriginalValue);
    }

    [Fact]
    public async Task RestoreAsync_PolicyThatWasOriginallyAbsent_PreservesAbsence()
    {
        var policy = PrivacyRecoveryTestData.Policy(
            "absent-policy",
            valueExisted: false,
            originalValue: null);
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(policy));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(policy.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "NormalExit");

        Assert.True(result.SafeToExit);
        var restored = Assert.IsType<OriginalPolicyState>(Assert.Single(platform.RestoredResources));
        Assert.False(restored.ValueExisted);
        Assert.Equal(PrivacyRegistryValueKind.None, restored.OriginalValueKind);
        Assert.Null(restored.OriginalValue);
    }

    [Fact]
    public async Task RestoreAsync_ConcurrentCalls_AreSerializedAndNativeRestoreRunsOnce()
    {
        var device = PrivacyRecoveryTestData.Device("camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform.BeforeRestoreAsync = async (_, _, cancellationToken) =>
        {
            restoreEntered.TrySetResult();
            await allowRestore.Task.WaitAsync(cancellationToken);
        };
        var service = new PrivacySessionService(store, platform);

        var firstTask = service.RestoreAsync(null, null, "WindowClosing");
        await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = service.RestoreAsync(null, null, "TrayExit");
        Assert.False(secondTask.IsCompleted);

        allowRestore.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, results.Sum(result => result.RestoredCount));
        Assert.All(results, result => Assert.True(result.SafeToExit));
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacySessionStatus.Restored, store.Current!.Status);
    }

    [Fact]
    public async Task RecoverUnfinishedSessionAsync_ApplyPendingAfterCrash_PreservesAmbiguousProtectedState()
    {
        var device = PrivacyRecoveryTestData.Device(
            "camera",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        device.OwnershipUncertain = true;
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RecoverUnfinishedSessionAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.ConflictCount);
        Assert.Empty(platform.RestoreCalls);
        Assert.Equal(PrivacySessionStatus.CompletedWithConflicts, store.Current!.Status);
        Assert.False(store.Current.WasRestored);
    }

    [Fact]
    public async Task RecoverUnfinishedSessionAsync_MachineAttestationUpgradesApplyPendingOwnership()
    {
        var device = PrivacyRecoveryTestData.Device(
            "camera-attested",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        device.OwnershipUncertain = true;
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetOwnershipAttestation(device.ResourceId, PrivacyOwnershipAttestationKind.Confirmed);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RecoverUnfinishedSessionAsync();

        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacyResourceJournalState.Restored, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task RecoverUnfinishedSessionAsync_ApplyPendingHkcuPolicy_UsesDurableIntentCompareAndRestore()
    {
        var policy = PrivacyRecoveryTestData.Policy(
            "hkcu-crash-gap",
            valueExisted: false,
            originalValue: null,
            journalState: PrivacyResourceJournalState.ApplyPending,
            layer: ProtectionLayer.Standard);
        policy.ModifiedByPrivLock = false;
        policy.OwnershipUncertain = true;
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(policy));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(policy.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RecoverUnfinishedSessionAsync();

        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(1, result.IrreducibleAmbiguityCount);
        Assert.Equal([policy.ResourceId], platform.RestoreCalls);
        Assert.Contains("irreducible", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PrivacyResourceJournalState.Restored, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task RecoverUnfinishedSessionAsync_RestorePendingAudio_UsesDurableIntentCompareAndRestore()
    {
        var endpoint = PrivacyRecoveryTestData.AudioEndpoint(
            "audio-crash-gap",
            PrivacyResourceJournalState.RestorePending);
        endpoint.ModifiedByPrivLock = false;
        endpoint.OwnershipUncertain = true;
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(endpoint));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(endpoint.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var service = new PrivacySessionService(store, platform);

        var result = await service.RecoverUnfinishedSessionAsync();

        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(1, result.IrreducibleAmbiguityCount);
        Assert.Equal([endpoint.ResourceId], platform.RestoreCalls);
        Assert.Equal(PrivacyResourceJournalState.Restored, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task RestoreAsync_AlreadyRestoredSession_IsANoOp()
    {
        var device = PrivacyRecoveryTestData.Device(
            "camera",
            journalState: PrivacyResourceJournalState.Restored);
        var restoredSession = PrivacyRecoveryTestData.ActiveSession(device);
        restoredSession.IsActive = false;
        restoredSession.WasRestored = true;
        restoredSession.Status = PrivacySessionStatus.Restored;
        restoredSession.CompletedAtUtc = restoredSession.UpdatedAtUtc;
        var store = new RecordingPrivacySessionStore(restoredSession);
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var service = new PrivacySessionService(store, platform);

        var result = await service.RestoreAsync(null, null, "DuplicateExitEvent");

        Assert.True(result.IsComplete);
        Assert.Equal(0, result.RestoredCount);
        Assert.Empty(platform.ObserveCalls);
        Assert.Empty(platform.RestoreCalls);
        Assert.Empty(store.SavedSnapshots);
    }

    private static DeviceOperationDetail Detail(
        string id,
        bool success,
        bool outcomeUncertain = false) => new()
    {
        DeviceId = id,
        FriendlyName = id,
        Success = success,
        OutcomeUncertain = outcomeUncertain,
        ErrorMessage = success ? null : "native failure"
    };
}
