using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

/// <summary>
/// Regression tests verifying the State Separation Invariant:
/// "DesiredState is user intent. PrivacySession is recovery authority. Actual OS State is runtime reality. Never use one as a substitute for another."
/// </summary>
public sealed class StateSeparationInvariantTests
{
    // =========================================================================
    // Requirement 2: Legacy Detection Regression Tests
    // =========================================================================

    [Fact]
    public async Task DesiredStateActive_WithoutPrivacySession_AndNoLegacyEvidence_DoesNotInferLegacyUntracked()
    {
        // Setup:
        // DesiredState.Active
        // +
        // No PrivacySession (store is empty)
        // +
        // No evidence of legacy runtime mutation (HasLegacyEvidence = false)
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var legacyDetector = new ScriptedLegacyArtifactDetector(hasLegacyEvidence: false);
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Active
        });

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions,
            legacyDetector);

        // Act: Run startup recovery
        var startupRecovery = await protection.RecoverPreviousSessionAsync();

        // Assert:
        // 1. Recovery succeeds safely because there is no unfinished session to clean up
        Assert.True(startupRecovery.SafeToExit);
        Assert.False(startupRecovery.HadPersistedSession);
        Assert.Equal(0, startupRecovery.FailedCount);

        // 2. Invariant: DesiredState alone was NOT inferred as legacy recovery state
        Assert.DoesNotContain("older PrivLock version", startupRecovery.ErrorMessage ?? string.Empty);

        // 3. Invariant: DesiredState alone does NOT grant recovery authority (no native restore calls made)
        Assert.Empty(platform.RestoreCalls);

        // 4. Invariant: Startup recovery did NOT erase or mutate DesiredState
        Assert.Equal(0, dependencies.SaveCount);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().CameraStandard);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().MicrophoneStandard);
    }

    [Fact]
    public async Task LegacyEvidencePresent_WithoutPrivacySession_PreservesStateAndReportsUnsafe()
    {
        // Setup:
        // No PrivacySession
        // +
        // Explicit evidence of legacy runtime mutation exists (HasLegacyEvidence = true)
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var legacyDetector = new ScriptedLegacyArtifactDetector(hasLegacyEvidence: true);
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active
        });

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions,
            legacyDetector);

        // Act: Run startup recovery
        var startupRecovery = await protection.RecoverPreviousSessionAsync();

        // Assert:
        // 1. Refuses broad unblocking and reports unsafe because legacy evidence exists without an exact WAL
        Assert.False(startupRecovery.SafeToExit);
        Assert.Equal(1, startupRecovery.FailedCount);
        Assert.Contains("older PrivLock version", startupRecovery.ErrorMessage);

        // 2. Invariant: Zero blind unblock / restore calls made
        Assert.Empty(platform.RestoreCalls);

        // 3. Invariant: DesiredState remains untouched
        Assert.Equal(0, dependencies.SaveCount);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().CameraStandard);
    }

    // =========================================================================
    // Requirement 3: Partial Secure Reconciliation Regression Test
    // =========================================================================

    [Fact]
    public async Task StartupReconciliation_WhenStandardSucceedsAndSecureFails_PreservesDesiredStateAndEnablesRetry()
    {
        // Setup: User desires both Standard and Secure camera protection
        var camera = PrivacyRecoveryTestData.Device("camera-1", target: BlockTarget.Camera);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera]
        };
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active
        });

        // Simulate platform: Standard succeeds, Secure fails (e.g. UAC cancelled / access denied)
        var shouldSecureFail = true;
        dependencies.BeforeStandardEnableReturn = () =>
        {
            platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        };
        dependencies.EnableSecureHandler = _ =>
        {
            if (shouldSecureFail)
                return OperationResult.Fail("Simulated elevation denial for PnP hardware node");

            platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
            return OperationResult.Ok();
        };

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var recoveryService = new PrivacyRecoveryService(protection);

        // Act 1: First startup recovery and reconciliation
        var startupRecovery = await recoveryService.RecoverAtStartupAsync();
        Assert.True(startupRecovery.SafeToExit);

        var reconcileResult = await recoveryService.ReconcileAtStartupAsync(startupRecovery);

        // Assert 1:
        // Reconciliation failed because Secure could not be applied
        Assert.False(reconcileResult.Success);
        Assert.Contains("Camera secure protection failed", reconcileResult.ErrorMessage);

        // INVARIANT CHECK: DesiredState was NOT cleared or downgraded despite the failure!
        var desiredAfterFailedSecure = dependencies.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterFailedSecure.CameraStandard);
        Assert.Equal(SecureProtectionState.Active, desiredAfterFailedSecure.CameraSecure);

        // Actual OS state: Standard is Active, Secure is Inactive
        var actualState1 = await dependencies.GetProtectionStateAsync();
        Assert.Equal(StandardProtectionState.Active, actualState1.Camera.StandardState);
        Assert.NotEqual(SecureProtectionState.Active, actualState1.Camera.SecureState);

        // Act 2: Next startup reconciliation where user authorizes elevation / secure succeeds
        shouldSecureFail = false;
        var retryRecovery = await recoveryService.RecoverAtStartupAsync();
        Assert.True(retryRecovery.SafeToExit);

        var retryReconcile = await recoveryService.ReconcileAtStartupAsync(retryRecovery);

        // Assert 2:
        // Reconciliation succeeds and applies the pending secure layer
        Assert.True(retryReconcile.Success, retryReconcile.ErrorMessage ?? "Reconciliation failed without error message");

        var desiredFinal = dependencies.Load();
        Assert.Equal(StandardProtectionState.Active, desiredFinal.CameraStandard);
        Assert.Equal(SecureProtectionState.Active, desiredFinal.CameraSecure);

        var actualStateFinal = await dependencies.GetProtectionStateAsync();
        Assert.Equal(StandardProtectionState.Active, actualStateFinal.Camera.StandardState);
        Assert.Equal(SecureProtectionState.Active, actualStateFinal.Camera.SecureState);
    }

    // =========================================================================
    // Requirement 4: External Conflict Regression Tests
    // =========================================================================

    [Fact]
    public async Task StartupReconciliation_WhenExternalCameraProtectedAndDesiredInactive_PreservesExternalStateAndDoesNotUnblock()
    {
        // Setup: User desires NO protection
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            MicrophoneStandard = StandardProtectionState.Inactive
        });

        // External state: IT administrator disabled Camera in Windows
        dependencies.SetCameraStandardState(StandardProtectionState.Active);

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var recoveryService = new PrivacyRecoveryService(protection);

        // Act: Startup recovery and reconciliation
        var recovery = await recoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile = await recoveryService.ReconcileAtStartupAsync(recovery);

        // Assert:
        // 1. Reconciliation finishes cleanly
        Assert.True(reconcile.Success);

        // 2. Invariant: External Camera protection was NOT unblocked or modified
        var actualState = await dependencies.GetProtectionStateAsync();
        Assert.Equal(StandardProtectionState.Active, actualState.Camera.StandardState);

        // 3. Invariant: Zero unblock/restore operations were dispatched
        Assert.Empty(platform.RestoreCalls);

        // 4. Invariant: DesiredState remains Inactive
        Assert.Equal(StandardProtectionState.Inactive, dependencies.Load().CameraStandard);
    }

    [Fact]
    public async Task StartupReconciliation_WhenExternalMicrophoneProtectedAndDesiredInactive_PreservesExternalStateAndDoesNotUnblock()
    {
        // Setup: User desires NO protection
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            MicrophoneStandard = StandardProtectionState.Inactive
        });

        // External state: Microphone is muted externally
        dependencies.SetMicrophoneStandardState(StandardProtectionState.Active);

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var recoveryService = new PrivacyRecoveryService(protection);

        // Act: Startup recovery and reconciliation
        var recovery = await recoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile = await recoveryService.ReconcileAtStartupAsync(recovery);

        // Assert:
        // 1. Reconciliation finishes cleanly
        Assert.True(reconcile.Success);

        // 2. Invariant: External Microphone protection was NOT unblocked or unmuted
        var actualState = await dependencies.GetProtectionStateAsync();
        Assert.Equal(StandardProtectionState.Active, actualState.Microphone.StandardState);

        // 3. Invariant: Zero unblock/restore operations were dispatched
        Assert.Empty(platform.RestoreCalls);

        // 4. Invariant: DesiredState remains Inactive
        Assert.Equal(StandardProtectionState.Inactive, dependencies.Load().MicrophoneStandard);
    }

    // =========================================================================
    // Requirement 5: Asymmetric Crash Recovery Test
    // =========================================================================

    [Fact]
    public async Task StartupRecovery_WithAsymmetricJournalState_QuiescesInFlightRestoresOwnedAndReconcilesToNewSession()
    {
        // Setup: Interrupted execution produced asymmetric journal state:
        // Camera was Applied
        // Microphone was Prepared / in-flight
        var cameraResource = PrivacyRecoveryTestData.Device(
            "camera-1",
            target: BlockTarget.Camera,
            journalState: PrivacyResourceJournalState.Applied,
            modifiedByPrivLock: true);

        var micResource = PrivacyRecoveryTestData.Device(
            "mic-1",
            target: BlockTarget.Microphone,
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        micResource.ExecutionMayStillBeInFlight = true;

        var previousSession = PrivacyRecoveryTestData.ActiveSession(cameraResource, micResource);
        var previousSessionId = previousSession.SessionId;

        var store = new RecordingPrivacySessionStore(previousSession);
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [cameraResource, micResource]
        };
        platform.SetObservation(cameraResource.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(micResource.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);

        var dependencies = new RecoveryHostDependencies();
        dependencies.BeforeStandardEnableReturn = () =>
        {
            platform.SetObservation(cameraResource.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
            platform.SetObservation(micResource.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        };
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active
        });

        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var recoveryService = new PrivacyRecoveryService(protection);

        // Act 1: Startup Recovery of the unfinished asymmetric session
        var recoveryResult = await recoveryService.RecoverAtStartupAsync();

        // Assert 1:
        // 1. Quiescence was ensured and owned changes were restored
        Assert.True(recoveryResult.SafeToExit);
        Assert.True(platform.QuiescenceCalls > 0);
        Assert.Contains(cameraResource.ResourceId, platform.RestoreCalls);

        // 2. Previous session is now completed/restored
        var sessionAfterRecovery = store.Current!;
        Assert.False(sessionAfterRecovery.IsActive);
        Assert.True(sessionAfterRecovery.WasRestored);

        // 3. Invariant: DesiredState was NOT cleared or mutated during recovery
        Assert.Equal(0, dependencies.SaveCount);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().CameraStandard);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().MicrophoneStandard);

        // Act 2: Startup Reconciliation to apply DesiredState
        var reconcileResult = await recoveryService.ReconcileAtStartupAsync(recoveryResult);

        // Assert 2:
        Assert.True(reconcileResult.Success);

        // A NEW PrivacySession was created for the new runtime mutations
        var newSession = store.Current!;
        Assert.NotNull(newSession);
        Assert.True(newSession.IsActive);

        // INVARIANT VERIFICATIONS:
        // - Previous PrivacySession != DesiredState
        // - Previous PrivacySession != New PrivacySession
        Assert.NotEqual(previousSessionId, newSession.SessionId);
        Assert.NotEqual(previousSession.CreatedAtUtc, newSession.CreatedAtUtc);

        // Both Camera and Microphone are now protected in actual OS state
        var finalOsState = await dependencies.GetProtectionStateAsync();
        Assert.True(finalOsState.Camera.IsProtected);
        Assert.True(finalOsState.Microphone.IsProtected);
    }
}
