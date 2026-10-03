using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public class ProtectionLifecycleRestartTests
{
    [Fact]
    public async Task Lifecycle_Camera_Enable_Shutdown_StartupReconcile_Cycle()
    {
        // Steps 1 to 9:
        // 1. Start PrivGvard.
        // 2. Enable camera protection.
        // 3. Verify camera is protected.
        // 4. Close PrivGvard normally.
        // 5. Verify runtime state was restored according to the existing shutdown contract.
        // 6. Verify DesiredState still says camera protection is enabled.
        // 7. Start PrivGvard again.
        // 8. Verify startup reconciliation reapplies camera protection.
        // 9. Verify DesiredState remains enabled.

        var env = new LifecycleEnvironment();

        // 1. Start PrivGvard (Instance 1)
        var app1 = env.CreateApplicationInstance();

        // 2. Enable camera protection
        var enableResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enableResult.Success);

        // 3. Verify camera is protected
        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state1.Camera.StandardState);
        Assert.False(state1.Microphone.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        var initialSessionId = env.SessionStore.Current!.SessionId;

        // 4. Close PrivGvard normally
        var shutdownResult = await app1.ShutdownCoordinator.RestoreAsync("UserExit");
        Assert.True(shutdownResult.SafeToExit);

        // 5. Verify runtime state was restored according to the existing shutdown contract
        var osStateAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osStateAfterShutdown.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Inactive, osStateAfterShutdown.Camera.StandardState);
        Assert.False(osStateAfterShutdown.Microphone.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.False(env.SessionStore.Current!.IsActive);
        Assert.Equal(PrivacySessionStatus.Restored, env.SessionStore.Current!.Status);

        // 6. Verify DesiredState still says camera protection is enabled
        var desiredAfterShutdown = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterShutdown.MicrophoneStandard);

        // 7. Start PrivGvard again (Instance 2 simulating process restart)
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);
        Assert.False(recovery2.HadRecoveryWork);

        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        // 8. Verify startup reconciliation reapplies camera protection
        var state2 = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.True(state2.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state2.Camera.StandardState);
        Assert.False(state2.Microphone.IsProtected);
        var osStateAfterReconcile = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osStateAfterReconcile.Camera.IsProtected);
        // A fresh session was established in WAL for newly applied runtime mutations:
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        Assert.NotEqual(initialSessionId, env.SessionStore.Current!.SessionId);

        // 9. Verify DesiredState remains enabled
        var desiredAfterReconcile = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterReconcile.CameraStandard);
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterReconcile.MicrophoneStandard);
    }

    [Fact]
    public async Task Lifecycle_Microphone_Enable_Shutdown_StartupReconcile_Cycle()
    {
        var env = new LifecycleEnvironment();

        // 1. Start PrivGvard (Instance 1)
        var app1 = env.CreateApplicationInstance();

        // 2. Enable microphone protection
        var enableResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(enableResult.Success);

        // 3. Verify microphone is protected
        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.False(state1.Camera.IsProtected);
        Assert.True(state1.Microphone.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state1.Microphone.StandardState);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        var initialSessionId = env.SessionStore.Current!.SessionId;

        // 4. Close PrivGvard normally
        var shutdownResult = await app1.ShutdownCoordinator.RestoreAsync("UserExit");
        Assert.True(shutdownResult.SafeToExit);

        // 5. Verify runtime state was restored according to shutdown contract
        var osStateAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osStateAfterShutdown.Microphone.IsProtected);
        Assert.Equal(StandardProtectionState.Inactive, osStateAfterShutdown.Microphone.StandardState);
        Assert.False(env.SessionStore.Current!.IsActive);
        Assert.Equal(PrivacySessionStatus.Restored, env.SessionStore.Current!.Status);

        // 6. Verify DesiredState still says microphone protection is enabled
        var desiredAfterShutdown = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterShutdown.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.MicrophoneStandard);

        // 7. Start PrivGvard again (Instance 2)
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);

        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        // 8. Verify startup reconciliation reapplies microphone protection
        var state2 = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.False(state2.Camera.IsProtected);
        Assert.True(state2.Microphone.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state2.Microphone.StandardState);
        var osStateAfterReconcile = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osStateAfterReconcile.Microphone.IsProtected);
        // New session created for the new runtime execution:
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        Assert.NotEqual(initialSessionId, env.SessionStore.Current!.SessionId);

        // 9. Verify DesiredState remains enabled
        var desiredAfterReconcile = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterReconcile.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredAfterReconcile.MicrophoneStandard);
    }

    [Fact]
    public async Task Lifecycle_Both_Enable_Shutdown_StartupReconcile_Cycle()
    {
        var env = new LifecycleEnvironment();

        // 1. Start PrivGvard (Instance 1)
        var app1 = env.CreateApplicationInstance();

        // 2. Enable both Camera and Microphone protection
        var camResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        var micResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(camResult.Success);
        Assert.True(micResult.Success);

        // 3. Verify both are protected
        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);
        Assert.True(state1.Microphone.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state1.Camera.StandardState);
        Assert.Equal(StandardProtectionState.Active, state1.Microphone.StandardState);
        var initialSessionId = env.SessionStore.Current!.SessionId;

        // 4. Close PrivGvard normally
        var shutdownResult = await app1.ShutdownCoordinator.RestoreAsync("UserExit");
        Assert.True(shutdownResult.SafeToExit);

        // 5. Verify runtime state was restored according to shutdown contract
        var osStateAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osStateAfterShutdown.Camera.IsProtected);
        Assert.False(osStateAfterShutdown.Microphone.IsProtected);
        Assert.False(env.SessionStore.Current!.IsActive);
        Assert.Equal(PrivacySessionStatus.Restored, env.SessionStore.Current!.Status);

        // 6. Verify DesiredState still says both protections are enabled
        var desiredAfterShutdown = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.MicrophoneStandard);

        // 7. Start PrivGvard again (Instance 2)
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);

        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        // 8. Verify startup reconciliation reapplies both protections
        var state2 = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.True(state2.Camera.IsProtected);
        Assert.True(state2.Microphone.IsProtected);
        Assert.Equal(StandardProtectionState.Active, state2.Camera.StandardState);
        Assert.Equal(StandardProtectionState.Active, state2.Microphone.StandardState);
        var osStateAfterReconcile = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osStateAfterReconcile.Camera.IsProtected);
        Assert.True(osStateAfterReconcile.Microphone.IsProtected);
        // Both resources exist in a fresh session:
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        Assert.NotEqual(initialSessionId, env.SessionStore.Current!.SessionId);
        Assert.Equal(2, env.SessionStore.Current!.Resources.Count);

        // 9. Verify DesiredState remains enabled
        var desiredAfterReconcile = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterReconcile.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredAfterReconcile.MicrophoneStandard);
    }

    [Fact]
    public async Task Lifecycle_ExplicitUserDisable_DoesNotReapplyAtStartup()
    {
        var env = new LifecycleEnvironment();

        // 1. Start PrivGvard (Instance 1)
        var app1 = env.CreateApplicationInstance();

        // 2. Enable camera protection
        await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);

        // Explicit user disable
        var disableResult = await app1.ProtectionService.DisableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(disableResult.Success);

        // Verify runtime state was immediately unblocked
        var stateAfterDisable = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.False(stateAfterDisable.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Inactive, stateAfterDisable.Camera.StandardState);

        // Verify DesiredState was cleared by explicit disable
        var desiredAfterDisable = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterDisable.CameraStandard);

        // Close PrivGvard normally
        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("UserExit");
        Assert.True(shutdown.SafeToExit);

        // Start PrivGvard again (Instance 2)
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);

        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        // Verify camera remains unprotected and DesiredState remains inactive:
        var state2 = await app2.ProtectionService.GetCurrentStateAsync();
        Assert.False(state2.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Inactive, state2.Camera.StandardState);
        Assert.Equal(StandardProtectionState.Inactive, env.StateStore.Load().CameraStandard);
    }

    [Fact]
    public async Task Lifecycle_MultiCycle_Enable_Shutdown_Startup_Shutdown_Startup()
    {
        // Test: Enable → shutdown → startup → shutdown → startup
        var env = new LifecycleEnvironment();

        // === Cycle 1: Enable & Shutdown ===
        var app1 = env.CreateApplicationInstance();
        var enable1 = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable1.Success);
        Assert.True((await app1.ProtectionService.GetCurrentStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);

        var shutdown1 = await app1.ShutdownCoordinator.RestoreAsync("UserExit");
        Assert.True(shutdown1.SafeToExit);
        Assert.False((await env.ProtectionProvider.GetProtectionStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);

        // === Cycle 2: Startup & Shutdown ===
        var app2 = env.CreateApplicationInstance();
        var recovery2 = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery2.SafeToExit);
        var reconcile2 = await app2.RecoveryService.ReconcileAtStartupAsync(recovery2);
        Assert.True(reconcile2.Success);

        Assert.True((await app2.ProtectionService.GetCurrentStateAsync()).Camera.IsProtected);
        Assert.True((await env.ProtectionProvider.GetProtectionStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);

        var shutdown2 = await app2.ShutdownCoordinator.RestoreAsync("OperatingSystemShutdownOrLogout");
        Assert.True(shutdown2.SafeToExit);
        Assert.False((await env.ProtectionProvider.GetProtectionStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);

        // === Cycle 3: Startup ===
        var app3 = env.CreateApplicationInstance();
        var recovery3 = await app3.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery3.SafeToExit);
        var reconcile3 = await app3.RecoveryService.ReconcileAtStartupAsync(recovery3);
        Assert.True(reconcile3.Success);

        Assert.True((await app3.ProtectionService.GetCurrentStateAsync()).Camera.IsProtected);
        Assert.True((await env.ProtectionProvider.GetProtectionStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);
    }

    [Fact]
    public async Task Lifecycle_StartupReconciliation_IsIdempotent_DoesNotDuplicateSessionsOrCorruptState()
    {
        var env = new LifecycleEnvironment();

        // 1. Initial run: Enable Camera and shutdown normally
        var app1 = env.CreateApplicationInstance();
        await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        await app1.ShutdownCoordinator.RestoreAsync("UserExit");

        // 2. Restart app
        var app2 = env.CreateApplicationInstance();
        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        // First reconciliation call:
        var firstReconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(firstReconcile.Success);
        Assert.True((await app2.ProtectionService.GetCurrentStateAsync()).Camera.IsProtected);
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);
        var expectedSessionId = env.SessionStore.Current!.SessionId;
        var saveAttemptsBeforeSecondCall = env.SessionStore.SaveAttempts;

        // Second reconciliation call (idempotency verification):
        var secondReconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(secondReconcile.Success);

        // State remains protected, DesiredState intact, and no duplicate session created:
        Assert.True((await app2.ProtectionService.GetCurrentStateAsync()).Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Active, env.StateStore.Load().CameraStandard);
        Assert.Equal(expectedSessionId, env.SessionStore.Current!.SessionId);
        Assert.Equal(saveAttemptsBeforeSecondCall, env.SessionStore.SaveAttempts);
    }

    [Fact]
    public async Task Lifecycle_RecoveryFailure_AbortsReconciliationSafely_PreservingDesiredState()
    {
        var env = new LifecycleEnvironment();

        // Arrange: DesiredState says Camera is Active, but startup recovery encounters a failure
        env.StateStore.Save(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available,
            MicrophoneStandard = StandardProtectionState.Inactive
        });

        var failedRecovery = new PrivacyRecoveryResult
        {
            IsComplete = false,
            FailedCount = 1,
            ErrorMessage = "CoreAudio endpoint mute restoration timed out"
        };

        var app = env.CreateApplicationInstance();
        var reconcileResult = await app.RecoveryService.ReconcileAtStartupAsync(failedRecovery);

        // Assert: reconciliation safely aborted
        Assert.False(reconcileResult.Success);
        Assert.Contains("CoreAudio endpoint mute restoration timed out", reconcileResult.ErrorMessage);

        // OS state was not mutated blindly:
        var osState = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osState.Camera.IsProtected);
        Assert.Equal(StandardProtectionState.Inactive, osState.Camera.StandardState);

        // DesiredState was preserved (not cleared):
        var desired = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desired.CameraStandard);
    }

    [Fact]
    public async Task Lifecycle_WindowsAutostart_Boot_StartupRecovery_Reconcile_ProtectsAndPreservesAutostartState()
    {
        // Validates:
        // Windows boot
        //     ↓
        // PrivGvard starts automatically (Autostart only starts the process)
        //     ↓
        // startup recovery
        //     ↓
        // desired protection reconciliation
        //     ↓
        // privacy protection becomes active
        //     ↓
        // application remains available without blocking normal Windows operation

        var env = new LifecycleEnvironment();

        // 1. Initial configuration: User has autostart enabled and protects both camera and microphone
        var app1 = env.CreateApplicationInstance();
        var enableBoth = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Both);
        Assert.True(enableBoth.Success);

        var stateStore = env.StateStore;
        var desiredBeforeShutdown = stateStore.Load();
        desiredBeforeShutdown.Autostart = true;
        stateStore.Save(desiredBeforeShutdown);

        // 2. Windows shutdown: coordinated restore restores OS runtime state
        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("WindowsShutdown");
        Assert.True(shutdown.SafeToExit);

        var osAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterShutdown.Camera.IsProtected);
        Assert.False(osAfterShutdown.Microphone.IsProtected);

        // DesiredState must survive shutdown intact:
        var desiredAfterShutdown = stateStore.Load();
        Assert.True(desiredAfterShutdown.Autostart);
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredAfterShutdown.MicrophoneStandard);

        // 3. Windows boot: PrivGvard is launched via Autostart registry Run entry (--minimized)
        // Autostart provider only launched the process. The application lifecycle performs recovery and reconciliation.
        var appBoot = env.CreateApplicationInstance();

        // 4. Startup recovery runs first
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.False(recovery.HadRecoveryWork);

        // 5. Desired protection reconciliation runs next
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // 6. Privacy protection is now active on OS devices
        var osAfterReconcile = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osAfterReconcile.Camera.IsProtected);
        Assert.True(osAfterReconcile.Microphone.IsProtected);

        // A new active privacy session journal is established in WAL
        Assert.NotNull(env.SessionStore.Current);
        Assert.True(env.SessionStore.Current!.IsActive);

        // 7. Desired state remains intact and preserved
        var desiredFinal = stateStore.Load();
        Assert.True(desiredFinal.Autostart);
        Assert.Equal(StandardProtectionState.Active, desiredFinal.CameraStandard);
        Assert.Equal(StandardProtectionState.Active, desiredFinal.MicrophoneStandard);
    }

    // --- Test Harness & Doubles ---

    private sealed record ApplicationInstance(
        ProtectionService ProtectionService,
        ShutdownCoordinator ShutdownCoordinator,
        PrivacyRecoveryService RecoveryService);

    private sealed class LifecycleEnvironment
    {
        public RecordingPrivacySessionStore SessionStore { get; } = new();
        public MemoryStateStore StateStore { get; } = new();
        public LifecyclePlatformAdapter PlatformAdapter { get; }
        public LifecycleProtectionProvider ProtectionProvider { get; }

        public LifecycleEnvironment()
        {
            PlatformAdapter = new LifecyclePlatformAdapter();
            ProtectionProvider = new LifecycleProtectionProvider(PlatformAdapter);
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
            var capabilityProvider = new LifecycleCapabilityProvider();
            var detector = new LifecycleDetector();
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

    private sealed class LifecyclePlatformAdapter : IPrivacySessionPlatformAdapter
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, PrivacyResourceObservationKind> _observations = new(StringComparer.OrdinalIgnoreCase);

        public bool SupportsPersistentRecovery => true;
        public Action<PrivacyResourceState>? OnRestored { get; set; }

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
                var kind = _observations.TryGetValue(resource.ResourceId, out var obs)
                    ? obs
                    : PrivacyResourceObservationKind.MatchesOriginal;
                return Task.FromResult(new PrivacyResourceObservation(kind));
            }
        }

        public Task<PrivacyOwnershipAttestation> VerifyOwnershipAttestationAsync(
            PrivacyResourceState resource,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Confirmed));

        public Task<OperationResult> RestoreOriginalAsync(
            PrivacyResourceState resource,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _observations[resource.ResourceId] = PrivacyResourceObservationKind.MatchesOriginal;
            }
            OnRestored?.Invoke(resource);
            return Task.FromResult(OperationResult.Ok());
        }

        public void SetProtected(string resourceId)
        {
            lock (_sync)
            {
                _observations[resourceId] = PrivacyResourceObservationKind.MatchesProtected;
            }
        }

        public void CompleteRecoveryPass() { }
    }

    private sealed class LifecycleProtectionProvider : IDeviceProtectionProvider
    {
        private readonly LifecyclePlatformAdapter _adapter;
        private StandardProtectionState _cameraState = StandardProtectionState.Inactive;
        private StandardProtectionState _micState = StandardProtectionState.Inactive;
        private SecureProtectionState _cameraSecure = SecureProtectionState.Unavailable;
        private SecureProtectionState _micSecure = SecureProtectionState.Unavailable;

        public LifecycleProtectionProvider(LifecyclePlatformAdapter adapter)
        {
            _adapter = adapter;
        }

        public void SetCameraStandardState(StandardProtectionState state)
        {
            _cameraState = state;
            _cameraSecure = state == StandardProtectionState.Active
                ? SecureProtectionState.Available
                : SecureProtectionState.Unavailable;
        }

        public void SetMicrophoneStandardState(StandardProtectionState state)
        {
            _micState = state;
            _micSecure = state == StandardProtectionState.Active
                ? SecureProtectionState.Available
                : SecureProtectionState.Unavailable;
        }

        public Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
            {
                SetCameraStandardState(StandardProtectionState.Active);
                _adapter.SetProtected("device:camera-node-1");
            }
            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                SetMicrophoneStandardState(StandardProtectionState.Active);
                _adapter.SetProtected("audio-mute:mic-endpoint-1");
            }
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
            {
                SetCameraStandardState(StandardProtectionState.Inactive);
            }
            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                SetMicrophoneStandardState(StandardProtectionState.Inactive);
            }
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
                _cameraSecure = SecureProtectionState.Active;
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                _micSecure = SecureProtectionState.Active;
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
        {
            if (target is BlockTarget.Camera or BlockTarget.Both)
                _cameraSecure = _cameraState == StandardProtectionState.Active ? SecureProtectionState.Available : SecureProtectionState.Unavailable;
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                _micSecure = _micState == StandardProtectionState.Active ? SecureProtectionState.Available : SecureProtectionState.Unavailable;
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FullProtectionState
            {
                Camera = new TargetProtectionStatus
                {
                    Target = BlockTarget.Camera,
                    StandardState = _cameraState,
                    SecureState = _cameraSecure
                },
                Microphone = new TargetProtectionStatus
                {
                    Target = BlockTarget.Microphone,
                    StandardState = _micState,
                    SecureState = _micSecure
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

    private sealed class LifecycleCapabilityProvider : IPlatformCapabilityProvider
    {
        public PlatformCapabilities Capabilities { get; } = new()
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        };

        public PlatformInfo PlatformInfo { get; } = new()
        {
            OperatingSystemName = "LifecycleTestOS",
            OsVersion = "1.0",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        };
    }

    private sealed class LifecycleDetector : IDeviceDetector
    {
        public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    }
}
