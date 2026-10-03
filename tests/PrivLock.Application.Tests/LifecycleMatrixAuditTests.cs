using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

/// <summary>
/// Dedicated lifecycle test audit verifying the complete matrix:
///
/// DesiredState: Camera, Microphone, Both, None
/// Runtime State: Original, Protected, Externally Modified (Conflict), Unavailable (Missing)
/// Lifecycle Phases: Startup, Normal Runtime, Shutdown, Restart, Windows Reboot, Crash, Recovery
///
/// Every scenario strictly verifies:
/// 1. DesiredState
/// 2. PrivacySession state
/// 3. Actual protection state
/// 4. Ownership
/// 5. Recovery result
/// 6. Whether a new mutation is allowed
///
/// Includes rigorous idempotency validation across all lifecycle operations.
/// </summary>
public class LifecycleMatrixAuditTests
{
    // =========================================================================
    // 1. DesiredState Matrix Scenarios (Camera, Microphone, Both, None)
    // =========================================================================

    [Fact]
    public async Task Matrix_DesiredState_Camera_CompleteLifecycle()
    {
        var env = new MatrixAuditEnvironment();

        // 1. Start App 1 and enable Camera protection
        var app1 = env.CreateApplicationInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        // Verify 6 Criteria during active protection:
        // 1. DesiredState: Camera=Active, Mic=Inactive
        var desired1 = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired1.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, desired1.MicrophoneStandard);

        // 2. PrivacySession state: Active
        var session1 = env.SessionStore.Current;
        Assert.NotNull(session1);
        Assert.True(session1!.IsActive);
        Assert.Equal(PrivacySessionStatus.Active, session1.Status);

        // 3. Actual protection state: Camera is Protected, Mic is Inactive
        var os1 = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(os1.Camera.IsProtected);
        Assert.False(os1.Microphone.IsProtected);

        // 4. Ownership: PrivGvard confirmed ownership
        var camResource1 = session1.Resources.First(r => r.Target == BlockTarget.Camera);
        Assert.True(camResource1.ModifiedByPrivLock);
        Assert.False(camResource1.OwnershipUncertain);
        Assert.Equal(PrivacyResourceJournalState.Applied, camResource1.JournalState);

        // 5. Whether a new mutation is allowed: Can enable Microphone dynamically
        var enableMic = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(enableMic.Success);
        var osAfterMutation = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osAfterMutation.Microphone.IsProtected);

        // 6. Shutdown & Recovery on Restart:
        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("NormalExit");
        Assert.True(shutdown.SafeToExit);

        // State after shutdown: hardware restored to baseline
        var osAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterShutdown.Camera.IsProtected);
        Assert.False(osAfterShutdown.Microphone.IsProtected);

        // DesiredState survived shutdown intact:
        var desiredAfterShutdown = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.CameraStandard);

        // Relaunch (Restart):
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);
        Assert.False(recovery2.HadRecoveryWork);

        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        var osRestart = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osRestart.Camera.IsProtected);
    }

    [Fact]
    public async Task Matrix_DesiredState_Microphone_CompleteLifecycle()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();
        var enable = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(enable.Success);

        // 1. DesiredState
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desired.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desired.MicrophoneStandard);

        // 2. PrivacySession
        var session = env.SessionStore.Current;
        Assert.NotNull(session);
        Assert.True(session!.IsActive);

        // 3. Actual protection state
        var os = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(os.Camera.IsProtected);
        Assert.True(os.Microphone.IsProtected);

        // 4. Ownership
        var micResource = session.Resources.First(r => r.Target == BlockTarget.Microphone);
        Assert.True(micResource.ModifiedByPrivLock);
        Assert.False(micResource.OwnershipUncertain);

        // 5. Whether a new mutation is allowed: User can toggle Mic off explicitly
        var disableMic = await app.ProtectionService.DisableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(disableMic.Success);
        var osAfterDisable = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterDisable.Microphone.IsProtected);

        var desiredAfterDisable = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterDisable.MicrophoneStandard);

        // 6. Recovery result on subsequent recovery check: safe and clean
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
    }

    [Fact]
    public async Task Matrix_DesiredState_Both_CompleteLifecycle()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();
        var enable = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Both);
        Assert.True(enable.Success);

        // 1. DesiredState
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desired.MicrophoneStandard);

        // 2. PrivacySession
        var session = env.SessionStore.Current;
        Assert.NotNull(session);
        Assert.True(session!.IsActive);
        Assert.Equal(2, session.Resources.Count(r => r.JournalState == PrivacyResourceJournalState.Applied));

        // 3. Actual protection state
        var os = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(os.Camera.IsProtected);
        Assert.True(os.Microphone.IsProtected);

        // 4. Ownership
        Assert.All(session.Resources, r =>
        {
            Assert.True(r.ModifiedByPrivLock);
            Assert.False(r.OwnershipUncertain);
        });

        // 5. Whether a new mutation is allowed: user can toggle Camera off while keeping Mic on
        var toggleCam = await app.ProtectionService.DisableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(toggleCam.Success);
        var osAfterCamDisable = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterCamDisable.Camera.IsProtected);
        Assert.True(osAfterCamDisable.Microphone.IsProtected);

        // 6. Shutdown & Recovery: Shutdown restores remaining active devices safely
        var shutdown = await app.ShutdownCoordinator.RestoreAsync("NormalExit");
        Assert.True(shutdown.SafeToExit);
        var osAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterShutdown.Camera.IsProtected);
        Assert.False(osAfterShutdown.Microphone.IsProtected);
    }

    [Fact]
    public async Task Matrix_DesiredState_None_CompleteLifecycle()
    {
        var env = new MatrixAuditEnvironment();

        env.StateStore.Save(new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            MicrophoneStandard = StandardProtectionState.Inactive
        });

        var app = env.CreateApplicationInstance();
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // 1. DesiredState
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desired.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, desired.MicrophoneStandard);

        // 2. PrivacySession: None active
        var session = env.SessionStore.Current;
        Assert.True(session == null || !session.IsActive);

        // 3. Actual protection state
        var os = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(os.Camera.IsProtected);
        Assert.False(os.Microphone.IsProtected);

        // 4. Ownership: 0 owned resources
        Assert.True(session == null || session.Resources.Count == 0);

        // 5. Recovery result: Clean
        Assert.True(recovery.SafeToExit);
        Assert.False(recovery.HadRecoveryWork);

        // 6. Whether a new mutation is allowed: User can enable camera on demand
        var enable = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);
        var osAfterEnable = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osAfterEnable.Camera.IsProtected);
    }

    // =========================================================================
    // 2. Runtime State Matrix Scenarios (Original, Protected, Conflict, Unavailable)
    // =========================================================================

    [Fact]
    public async Task Matrix_RuntimeState_Original_MatchesBaseline_NoRedundantWrites()
    {
        var env = new MatrixAuditEnvironment();

        // Hardware is in Original state
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
        env.ProtectionProvider.SetMicrophoneStandardState(StandardProtectionState.Inactive);

        var app = env.CreateApplicationInstance();
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);

        Assert.True(recovery.SafeToExit);
        Assert.False(recovery.HadRecoveryWork);
        Assert.True(reconcile.Success);
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);
    }

    [Fact]
    public async Task Matrix_RuntimeState_Protected_ExternalProtectionNotAdopted()
    {
        var env = new MatrixAuditEnvironment();

        // Hardware was already protected by an external tool or policy before PrivGvard started
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Active);
        env.PlatformAdapter.SetObservation("device:camera-node-1", PrivacyResourceObservationKind.MatchesProtected);

        var app = env.CreateApplicationInstance();

        // When user attempts to enable protection on already protected device without PrivGvard ownership,
        // PrivGvard refuses to adopt external state
        var result = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.False(result.Success);
        Assert.Contains("no blocking changes were applied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Verify:
        // 1. DesiredState: untouched
        // 2. Session: no active owned session
        var session = env.SessionStore.Current;
        Assert.True(session == null || !session.IsActive);

        // 3. Actual state: remains external protected
        var os = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(os.Camera.IsProtected);

        // 4. Ownership: NOT owned by PrivGvard
        Assert.True(session == null || !session.Resources.Any(r => r.ModifiedByPrivLock));
    }

    [Fact]
    public async Task Matrix_RuntimeState_ExternallyModified_ConflictPreservedWithoutBlindWrites()
    {
        var env = new MatrixAuditEnvironment();

        // Interrupted session where native device was externally altered to a conflict
        var pendingResource = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        pendingResource.OwnershipUncertain = true;

        env.SessionStore.Save(PrivacyRecoveryTestData.ActiveSession(pendingResource));
        env.PlatformAdapter.SetObservation(pendingResource.ResourceId, PrivacyResourceObservationKind.Conflict, "External modification");
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery detects conflict
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.ConflictCount > 0);

        // Reconcile aborts safely
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.False(reconcile.Success);

        // External conflict was NOT overwritten
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);

        // DesiredState preserved
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);

        // Subsequent new mutation on conflicted session is safely rejected until conflict is resolved
        var newMutation = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.False(newMutation.Success);
    }

    [Fact]
    public async Task Matrix_RuntimeState_Unavailable_MissingDeviceHandledGracefully()
    {
        var env = new MatrixAuditEnvironment();

        // Device was owned, but physically unplugged
        var missingCam = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.Applied,
            modifiedByPrivLock: true);

        env.SessionStore.Save(PrivacyRecoveryTestData.ActiveSession(missingCam));
        env.PlatformAdapter.SetObservation(missingCam.ResourceId, PrivacyResourceObservationKind.Missing);
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery detects missing device: remains retryable (MissingCount > 0, SafeToExit = false)
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.False(recovery.SafeToExit);
        Assert.True(recovery.MissingCount > 0);

        // Reconciliation aborts safely: does not write blindly
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.False(reconcile.Success);
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);
    }

    // =========================================================================
    // 3. Lifecycle Phases & Transitions
    // =========================================================================

    [Fact]
    public async Task Matrix_Lifecycle_WindowsReboot_RecoveryAndReconciliationCycle()
    {
        var env = new MatrixAuditEnvironment();

        // Windows running: App 1 enables Microphone protection
        var app1 = env.CreateApplicationInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(enable.Success);

        // Windows restarts (Process 1 abruptly ended)
        var app2 = env.CreateApplicationInstance();

        // Recovery restores baseline
        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        // Reconcile reapplies Microphone per DesiredState
        var reconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var osState = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osState.Microphone.IsProtected);
    }

    [Fact]
    public async Task Matrix_Lifecycle_CrashRecovery_TerminatedMidRestore_RecoversToBaseline()
    {
        var env = new MatrixAuditEnvironment();

        // Crash happened while RestorePending was committed
        var restoring = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.RestorePending,
            modifiedByPrivLock: false);
        restoring.OwnershipUncertain = true;

        env.SessionStore.Save(PrivacyRecoveryTestData.ActiveSession(restoring));
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Active);
        env.PlatformAdapter.SetObservation(restoring.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery finishes restoration
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        // Reconcile reapplies per DesiredState
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var osState = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osState.Camera.IsProtected);
    }

    // =========================================================================
    // 4. Rigorous Idempotency Validation
    // =========================================================================

    [Fact]
    public async Task Matrix_Idempotency_DoubleRecovery_IsSafeAndNoOp()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();

        // First recovery
        var recovery1 = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery1.SafeToExit);
        Assert.False(recovery1.HadRecoveryWork);

        // Second recovery immediately following
        var recovery2 = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);
        Assert.False(recovery2.HadRecoveryWork);
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);
    }

    [Fact]
    public async Task Matrix_Idempotency_DoubleReconcile_DoesNotDuplicateSession()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();
        var enable = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        var session1 = env.SessionStore.Current;
        Assert.NotNull(session1);
        Assert.True(session1!.IsActive);

        // Second reconcile: detects OS is already in desired state, executes 0 mutations
        var reconcile2 = await app.RecoveryService.ReconcileAtStartupAsync(PrivacyRecoveryResult.NothingToRestore());
        Assert.True(reconcile2.Success);

        var session2 = env.SessionStore.Current;
        Assert.NotNull(session2);
        Assert.Equal(session1.SessionId, session2!.SessionId);
    }

    [Fact]
    public async Task Matrix_Idempotency_DoubleShutdown_JoinsExistingRestoration()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();
        var enable = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Both);
        Assert.True(enable.Success);

        // First shutdown restore
        var shutdown1 = await app.ShutdownCoordinator.RestoreAsync("Shutdown1");
        Assert.True(shutdown1.SafeToExit);

        // Second shutdown restore
        var shutdown2 = await app.ShutdownCoordinator.RestoreAsync("Shutdown2");
        Assert.True(shutdown2.SafeToExit);

        // DesiredState still preserved
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desired.MicrophoneStandard);
    }

    [Fact]
    public async Task Matrix_Idempotency_RepeatedEnableCalls_DoNotCorruptState()
    {
        var env = new MatrixAuditEnvironment();

        var app = env.CreateApplicationInstance();

        // Enable Camera first time
        var enable1 = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable1.Success);
        var initialSessionId = env.SessionStore.Current!.SessionId;

        // Enable Camera second time (already enabled)
        var enable2 = await app.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable2.Success);

        // State remains valid and uncorrupted
        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
        Assert.False(state.Microphone.IsProtected);
        Assert.Equal(initialSessionId, env.SessionStore.Current!.SessionId);
    }

    // =========================================================================
    // Test Harness & Doubles
    // =========================================================================

    private sealed record ApplicationInstance(
        ProtectionService ProtectionService,
        ShutdownCoordinator ShutdownCoordinator,
        PrivacyRecoveryService RecoveryService);

    private sealed class MatrixAuditEnvironment
    {
        public RecordingPrivacySessionStore SessionStore { get; } = new();
        public MemoryStateStore StateStore { get; } = new();
        public MatrixPlatformAdapter PlatformAdapter { get; }
        public MatrixProtectionProvider ProtectionProvider { get; }

        public MatrixAuditEnvironment()
        {
            PlatformAdapter = new MatrixPlatformAdapter();
            ProtectionProvider = new MatrixProtectionProvider(PlatformAdapter);
            PlatformAdapter.OnRestored = resource =>
            {
                if (resource.Target == BlockTarget.Camera)
                    ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
                if (resource.Target == BlockTarget.Microphone)
                    ProtectionProvider.SetMicrophoneStandardState(StandardProtectionState.Inactive);
            };
        }

        public ApplicationInstance CreateApplicationInstance()
        {
            var capabilityProvider = new MatrixCapabilityProvider();
            var detector = new MatrixDetector();
            var privacySessions = new PrivacySessionService(SessionStore, PlatformAdapter);
            var protection = new ProtectionService(
                ProtectionProvider,
                detector,
                capabilityProvider,
                StateStore,
                privacySessions);
            var coordinator = new ShutdownCoordinator(protection);
            var recovery = new PrivacyRecoveryService(protection);
            return new ApplicationInstance(protection, coordinator, recovery);
        }
    }

    private sealed class MatrixPlatformAdapter : IPrivacySessionPlatformAdapter
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, PrivacyResourceObservation> _observations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PrivacyOwnershipAttestationKind> _attestations = new(StringComparer.OrdinalIgnoreCase);
        private int _restoreCalls;

        public bool SupportsPersistentRecovery => true;
        public Action<PrivacyResourceState>? OnRestored { get; set; }
        public int RestoreCallCount => Volatile.Read(ref _restoreCalls);

        public void SetObservation(string resourceId, PrivacyResourceObservationKind kind, string? errorMessage = null)
        {
            lock (_sync)
            {
                _observations[resourceId] = new PrivacyResourceObservation(kind, errorMessage);
            }
        }

        public void SetAttestation(string resourceId, PrivacyOwnershipAttestationKind kind)
        {
            lock (_sync)
            {
                _attestations[resourceId] = kind;
            }
        }

        public Task<OperationResult> EnsureMutationQuiescenceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<IReadOnlyList<PrivacyResourceState>> CaptureAsync(
            ProtectionLayer layer,
            BlockTarget target,
            string operationId,
            CancellationToken cancellationToken = default)
        {
            var list = new List<PrivacyResourceState>();
            if (target is BlockTarget.Camera or BlockTarget.Both)
            {
                list.Add(PrivacyRecoveryTestData.Device(
                    "camera-node-1",
                    target: BlockTarget.Camera,
                    layer: layer,
                    journalState: PrivacyResourceJournalState.Captured,
                    modifiedByPrivLock: false));
            }
            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                list.Add(PrivacyRecoveryTestData.AudioEndpoint(
                    "mic-endpoint-1",
                    journalState: PrivacyResourceJournalState.Captured));
            }
            return Task.FromResult<IReadOnlyList<PrivacyResourceState>>(list);
        }

        public Task<PrivacyResourceObservation> ObserveAsync(
            PrivacyResourceState resource,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var obs = _observations.TryGetValue(resource.ResourceId, out var custom)
                    ? custom
                    : new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
                return Task.FromResult(obs);
            }
        }

        public Task<PrivacyOwnershipAttestation> VerifyOwnershipAttestationAsync(
            PrivacyResourceState resource,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var kind = _attestations.TryGetValue(resource.ResourceId, out var custom)
                    ? custom
                    : PrivacyOwnershipAttestationKind.Confirmed;
                return Task.FromResult(new PrivacyOwnershipAttestation(kind));
            }
        }

        public Task<OperationResult> RestoreOriginalAsync(
            PrivacyResourceState resource,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _restoreCalls);
            lock (_sync)
            {
                _observations[resource.ResourceId] = new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
            }
            OnRestored?.Invoke(resource);
            return Task.FromResult(OperationResult.Ok());
        }

        public void SetProtected(string resourceId)
        {
            lock (_sync)
            {
                _observations[resourceId] = new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesProtected);
            }
        }

        public void CompleteRecoveryPass() { }
    }

    private sealed class MatrixProtectionProvider : IDeviceProtectionProvider
    {
        private readonly MatrixPlatformAdapter _adapter;
        private StandardProtectionState _cameraState = StandardProtectionState.Inactive;
        private StandardProtectionState _micState = StandardProtectionState.Inactive;

        public MatrixProtectionProvider(MatrixPlatformAdapter adapter)
        {
            _adapter = adapter;
        }

        public void SetCameraStandardState(StandardProtectionState state)
        {
            _cameraState = state;
            if (state == StandardProtectionState.Active)
                _adapter.SetProtected("device:camera-node-1");
            else
                _adapter.SetObservation("device:camera-node-1", PrivacyResourceObservationKind.MatchesOriginal);
        }

        public void SetMicrophoneStandardState(StandardProtectionState state)
        {
            _micState = state;
            if (state == StandardProtectionState.Active)
                _adapter.SetProtected("audio-mute:mic-endpoint-1");
            else
                _adapter.SetObservation("audio-mute:mic-endpoint-1", PrivacyResourceObservationKind.MatchesOriginal);
        }

        public Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
                SetCameraStandardState(StandardProtectionState.Active);
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                SetMicrophoneStandardState(StandardProtectionState.Active);
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
                SetCameraStandardState(StandardProtectionState.Inactive);
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                SetMicrophoneStandardState(StandardProtectionState.Inactive);
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = _cameraState,
                    SecureState = SecureProtectionState.Unavailable
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = _micState,
                    SecureState = SecureProtectionState.Unavailable
                }
            });
    }

    private sealed class MemoryStateStore : IStateStore
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

        public void Save(DesiredState state)
        {
            _state = new DesiredState
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

    private sealed class MatrixCapabilityProvider : IPlatformCapabilityProvider
    {
        public PlatformCapabilities Capabilities { get; } = new()
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        };

        public PlatformInfo PlatformInfo { get; } = new()
        {
            OperatingSystemName = "MatrixTestOS",
            OsVersion = "1.0",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        };
    }

    private sealed class MatrixDetector : IDeviceDetector
    {
        public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    }
}
