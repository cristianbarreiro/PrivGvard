using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

/// <summary>
/// Hardened crash recovery lifecycle tests validating all crash boundaries:
/// 1. Process terminated while protection is active.
/// 2. Process terminated immediately after journal creation.
/// 3. Process terminated during native protection mutation (mutation completed, did not complete, or external conflict).
/// 4. Process terminated during shutdown restoration (restore completed or did not complete).
/// 5. Windows reboot while protection is active.
/// 6. Windows power loss while protection is active.
/// </summary>
public class CrashRecoveryLifecycleTests
{
    // =========================================================================
    // Boundary 1: Process terminated while protection is active
    // =========================================================================

    [Fact]
    public async Task Boundary1_TerminatedWhileProtectionActive_RestoresPreviousSession_ThenReconcilesDesiredProtection()
    {
        var env = new CrashLifecycleEnvironment();

        // 1. Process 1 starts and enables Camera protection
        var app1 = env.CreateApplicationInstance();
        var enableResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enableResult.Success);

        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        var initialSessionId = env.SessionStore.Current!.SessionId;

        // 2. Process 1 is killed abruptly (crash/kill: no shutdown or restoration called)
        // Devices remain protected in OS, WAL remains Active with Applied resources.

        // 3. Process 2 starts (next startup)
        var app2 = env.CreateApplicationInstance();

        // 4. Recover unfinished session
        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.True(recovery.HadRecoveryWork);

        // Native device was safely restored to baseline according to recovery contract
        var osAfterRecovery = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterRecovery.Camera.IsProtected);

        // 5. Reconcile persistent DesiredState
        var reconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // Camera protection is active again
        var state2 = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.True(state2.Camera.IsProtected);

        // A fresh runtime session was established in WAL with a new SessionId
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        Assert.NotEqual(initialSessionId, env.SessionStore.Current!.SessionId);

        // DesiredState was never cleared
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);
    }

    // =========================================================================
    // Boundary 2: Process terminated immediately after journal creation
    // =========================================================================

    [Fact]
    public async Task Boundary2_TerminatedImmediatelyAfterJournalCreation_ResolvesApplyPendingWithoutBlindWrites_ThenReconciles()
    {
        var env = new CrashLifecycleEnvironment();

        // Simulate crash right after PrepareBlockAsync committed ApplyPending to WAL,
        // but BEFORE any native mutation call was dispatched.
        var pendingResource = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        pendingResource.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(pendingResource);
        env.SessionStore.Save(session);

        // Hardware state was never modified (still original / inactive)
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
        env.PlatformAdapter.SetObservation(pendingResource.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);

        // User DesiredState has Camera = Active
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        // Next startup starts
        var app = env.CreateApplicationInstance();

        // 1. Recover unfinished session
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        // Because resource was MatchesOriginal, no unnecessary native restore write was dispatched
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);

        // 2. Reconcile DesiredState
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // Protection is now applied cleanly
        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        Assert.NotEqual(session.SessionId, env.SessionStore.Current!.SessionId);
    }

    // =========================================================================
    // Boundary 3: Process terminated during native protection mutation
    // =========================================================================

    [Fact]
    public async Task Boundary3a_TerminatedDuringNativeMutation_WhenNativeMutationDidNotTakeEffect_RestoresSafely_ThenReconciles()
    {
        var env = new CrashLifecycleEnvironment();

        // Native call was interrupted before modifying hardware
        var pendingResource = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        pendingResource.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(pendingResource);
        env.SessionStore.Save(session);

        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
        env.PlatformAdapter.SetObservation(pendingResource.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
    }

    [Fact]
    public async Task Boundary3b_TerminatedDuringNativeMutation_WhenNativeMutationDidTakeEffect_RestoresAndReconciles()
    {
        var env = new CrashLifecycleEnvironment();

        // Native call succeeded (audio endpoint was muted), but process crashed before
        // RecordBlockOutcomeAsync could persist Applied to the WAL.
        var pendingMic = PrivacyRecoveryTestData.AudioEndpoint(
            "mic-endpoint-1",
            journalState: PrivacyResourceJournalState.ApplyPending);
        pendingMic.ModifiedByPrivLock = false;
        pendingMic.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(pendingMic);
        env.SessionStore.Save(session);

        // Native state was modified to protected
        env.ProtectionProvider.SetMicrophoneStandardState(StandardProtectionState.Active);
        env.PlatformAdapter.SetObservation(pendingMic.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        env.StateStore.Save(new DesiredState { MicrophoneStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery detects MatchesProtected with durable user-scoped intent, cleanly restores it to baseline
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.True(recovery.HadRecoveryWork);

        var osAfterRecovery = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterRecovery.Microphone.IsProtected);

        // Reconcile reapplies protection per DesiredState
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Microphone.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
    }

    [Fact]
    public async Task Boundary3c_TerminatedDuringNativeMutation_WhenExternalConflictObserved_PreservesConflictAndAbortsReconcile()
    {
        var env = new CrashLifecycleEnvironment();

        // Native mutation was in-flight, but an external actor wrote an unconfirmed value
        var pendingResource = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.ApplyPending,
            modifiedByPrivLock: false);
        pendingResource.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(pendingResource);
        env.SessionStore.Save(session);

        // External conflict observed
        env.PlatformAdapter.SetObservation(pendingResource.ResourceId, PrivacyResourceObservationKind.Conflict, "External write detected");
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery marks conflict and refuses to blindly overwrite external state
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.ConflictCount > 0);

        // Startup reconciliation safely aborts because recovery had conflicts
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.False(reconcile.Success);
        Assert.Contains("conflict", reconcile.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // DesiredState was preserved (not cleared)
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);

        // External conflict was NOT overwritten with a blind restore
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);
    }

    // =========================================================================
    // Boundary 4: Process terminated during shutdown restoration
    // =========================================================================

    [Fact]
    public async Task Boundary4a_TerminatedDuringShutdownRestoration_WhenNativeRestoreDidNotComplete_RestoresAtStartup_ThenReconciles()
    {
        var env = new CrashLifecycleEnvironment();

        // Process was killed while RestorePending was checkpointed, before native restore executed
        var restoringDevice = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.RestorePending,
            modifiedByPrivLock: false);
        restoringDevice.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(restoringDevice);
        env.SessionStore.Save(session);

        // Hardware still protected
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Active);
        env.PlatformAdapter.SetObservation(restoringDevice.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery completes the restoration to baseline
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.True(recovery.HadRecoveryWork);

        var osAfterRecovery = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterRecovery.Camera.IsProtected);

        // Reconcile reapplies protection per DesiredState
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
    }

    [Fact]
    public async Task Boundary4b_TerminatedDuringShutdownRestoration_WhenNativeRestoreCompleted_MarksRestored_ThenReconciles()
    {
        var env = new CrashLifecycleEnvironment();

        // Process was killed after native restore finished, but before WAL was committed as Restored
        var restoringDevice = PrivacyRecoveryTestData.Device(
            "camera-node-1",
            journalState: PrivacyResourceJournalState.RestorePending,
            modifiedByPrivLock: false);
        restoringDevice.OwnershipUncertain = true;

        var session = PrivacyRecoveryTestData.ActiveSession(restoringDevice);
        env.SessionStore.Save(session);

        // Hardware was already restored to baseline
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
        env.PlatformAdapter.SetObservation(restoringDevice.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        env.StateStore.Save(new DesiredState { CameraStandard = StandardProtectionState.Active });

        var app = env.CreateApplicationInstance();

        // Recovery recognizes MatchesOriginal and marks Restored without redundant write
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);

        // Reconcile reapplies protection per DesiredState
        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var state = await app.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
    }

    // =========================================================================
    // Boundary 5: Windows reboot while protection is active
    // =========================================================================

    [Fact]
    public async Task Boundary5_WindowsRebootWhileProtectionActive_RecoversAndReconcilesIdempotently()
    {
        var env = new CrashLifecycleEnvironment();

        // App 1 enables protection
        var app1 = env.CreateApplicationInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        // Windows reboots while active (App 1 terminated)
        var app2 = env.CreateApplicationInstance();

        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile1 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile1.Success);

        var state = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.True(state.Camera.IsProtected);
        var activeSession = env.SessionStore.Current;
        Assert.NotNull(activeSession);
        Assert.True(activeSession!.IsActive);

        // Idempotency: Running reconcile again does not duplicate or corrupt session
        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(PrivacyRecoveryResult.NothingToRestore());
        Assert.True(reconcile2.Success);

        var sessionAfterSecondReconcile = env.SessionStore.Current;
        Assert.NotNull(sessionAfterSecondReconcile);
        Assert.Equal(activeSession.SessionId, sessionAfterSecondReconcile!.SessionId);
    }

    // =========================================================================
    // Boundary 6: Windows power loss while protection is active
    // =========================================================================

    [Fact]
    public async Task Boundary6_WindowsPowerLoss_BothProtected_PreservesDurableIntent_RestoresAndReconcilesBoth()
    {
        var env = new CrashLifecycleEnvironment();

        // App 1 protects both camera and microphone
        var app1 = env.CreateApplicationInstance();
        var enableBoth = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Both);
        Assert.True(enableBoth.Success);

        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);
        Assert.True(state1.Microphone.IsProtected);

        // Sudden power loss cut: devices remain protected in hardware, WAL has Applied for both
        // Power restored: Windows boots, PrivGvard starts
        var appBoot = env.CreateApplicationInstance();

        // 1. Recover unfinished session
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.True(recovery.HadRecoveryWork);

        // Both devices cleanly reverted to baseline
        var osAfterRecovery = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterRecovery.Camera.IsProtected);
        Assert.False(osAfterRecovery.Microphone.IsProtected);

        // 2. Reconcile DesiredState (both camera and microphone)
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // Both are protected again
        var state2 = await appBoot.ProtectionService.GetCurrentStateAsync();
        Assert.True(state2.Camera.IsProtected);
        Assert.True(state2.Microphone.IsProtected);

        // Fresh session established
        var finalSession = env.SessionStore.Current;
        Assert.NotNull(finalSession);
        Assert.True(finalSession!.IsActive);

        // DesiredState was never interpreted as disabled by the power loss
        var finalDesired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, finalDesired.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, finalDesired.MicrophoneStandard);
    }

    // =========================================================================
    // Test Harness & Doubles
    // =========================================================================

    private sealed record ApplicationInstance(
        ProtectionService ProtectionService,
        ShutdownCoordinator ShutdownCoordinator,
        PrivacyRecoveryService RecoveryService);

    private sealed class CrashLifecycleEnvironment
    {
        public RecordingPrivacySessionStore SessionStore { get; } = new();
        public MemoryStateStore StateStore { get; } = new();
        public CrashPlatformAdapter PlatformAdapter { get; }
        public CrashProtectionProvider ProtectionProvider { get; }

        public CrashLifecycleEnvironment()
        {
            PlatformAdapter = new CrashPlatformAdapter();
            ProtectionProvider = new CrashProtectionProvider(PlatformAdapter);
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
            var capabilityProvider = new CrashCapabilityProvider();
            var detector = new CrashDetector();
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

    private sealed class CrashPlatformAdapter : IPrivacySessionPlatformAdapter
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

    private sealed class CrashProtectionProvider : IDeviceProtectionProvider
    {
        private readonly CrashPlatformAdapter _adapter;
        private StandardProtectionState _cameraState = StandardProtectionState.Inactive;
        private StandardProtectionState _micState = StandardProtectionState.Inactive;

        public CrashProtectionProvider(CrashPlatformAdapter adapter)
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

    private sealed class CrashCapabilityProvider : IPlatformCapabilityProvider
    {
        public PlatformCapabilities Capabilities { get; } = new()
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        };

        public PlatformInfo PlatformInfo { get; } = new()
        {
            OperatingSystemName = "CrashTestOS",
            OsVersion = "1.0",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        };
    }

    private sealed class CrashDetector : IDeviceDetector
    {
        public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    }
}
