using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Infrastructure.Common.Storage;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.System;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

/// <summary>
/// End-to-end Windows integration lifecycle tests executing the full real lifecycle:
///
/// Scenario A: Protect camera → reboot Windows → PrivGvard autostarts → camera becomes protected.
/// Scenario B: Protect microphone → reboot → microphone becomes protected.
/// Scenario C: Protect both → reboot → both become protected.
/// Scenario D: Protect camera → explicitly disable → reboot → camera remains unprotected.
/// Scenario E: Protect camera → close application normally → start application again → camera becomes protected.
/// Scenario F: Protect camera → terminate process unexpectedly → restart → recovery occurs safely → desired protection is reconciled.
/// Scenario G: Change external OS state while PrivGvard is stopped → start PrivGvard → verify external changes are not blindly overwritten.
///
/// Uses real FileStateStore (state.json), real FilePrivacySessionStore (session.json WAL with .bak shadow),
/// and real WindowsAutostartProvider (Registry Run & StartupApproved).
/// </summary>
public sealed class WindowsRealLifecycleIntegrationTests : IDisposable
{
    private readonly string _testRootDirectory;
    private readonly string _stateDirectory;
    private readonly string _recoveryDirectory;
    private readonly string _testRegistryRoot;
    private readonly string _testRunKeyPath;
    private readonly string _testStartupApprovedKeyPath;
    private readonly string _dummyExePath;

    public WindowsRealLifecycleIntegrationTests()
    {
        var testId = Guid.NewGuid().ToString("N");
        _testRootDirectory = Path.Combine(Path.GetTempPath(), $"PrivGvardWindowsIntegration_{testId}");
        _stateDirectory = Path.Combine(_testRootDirectory, "Data");
        _recoveryDirectory = Path.Combine(_testRootDirectory, "Recovery");

        Directory.CreateDirectory(_stateDirectory);
        Directory.CreateDirectory(_recoveryDirectory);

        _testRegistryRoot = $@"Software\PrivGvardTests\Integration_{testId}";
        _testRunKeyPath = $@"{_testRegistryRoot}\Run";
        _testStartupApprovedKeyPath = $@"{_testRegistryRoot}\StartupApproved\Run";

        using (var rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_testRunKeyPath)) { }
        using (var sk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_testStartupApprovedKeyPath)) { }

        _dummyExePath = Path.Combine(_testRootDirectory, "PrivGvard.exe");
        File.WriteAllBytes(_dummyExePath, [0x4D, 0x5A]); // PE header
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_testRegistryRoot, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            if (Directory.Exists(_testRootDirectory))
                Directory.Delete(_testRootDirectory, recursive: true);
        }
        catch { }
    }

    // =========================================================================
    // Scenario A: Protect camera → reboot Windows → PrivGvard autostarts → camera becomes protected
    // =========================================================================

    [Fact]
    public async Task ScenarioA_ProtectCamera_RebootWindows_Autostarts_CameraBecomesProtected()
    {
        var env = CreateEnvironment();

        // 1. Initial execution: User configures Autostart and protects Camera
        var app1 = env.CreateInstance();
        var autostartResult = env.AutostartProvider.EnableAutostart();
        Assert.True(autostartResult.Success);

        var enableResult = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enableResult.Success);

        var state1 = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.True(state1.Camera.IsProtected);

        // 2. Windows shuts down for reboot: coordinated restore restores OS state, marks session restored
        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("WindowsShutdown");
        Assert.True(shutdown.SafeToExit);

        var osAfterShutdown = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterShutdown.Camera.IsProtected);

        // DesiredState and Autostart survived shutdown in real disk files
        var desiredOnDisk = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredOnDisk.CameraStandard);
        Assert.True(env.AutostartProvider.IsAutostartEnabled());

        // 3. Windows boots: Autostart launches PrivGvard (App 2) with --minimized
        var appBoot = env.CreateInstance();

        // 4. Startup recovery runs first (no unfinished work remaining)
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.False(recovery.HadRecoveryWork);

        // 5. Startup reconciliation reconciles DesiredState
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // 6. Camera protection is active again
        var stateBoot = await appBoot.ProtectionService.GetCurrentStateAsync();
        Assert.True(stateBoot.Camera.IsProtected);
        Assert.NotNull(env.SessionStore.Load());
        Assert.True(env.SessionStore.Load()!.IsActive);
    }

    // =========================================================================
    // Scenario B: Protect microphone → reboot → microphone becomes protected
    // =========================================================================

    [Fact]
    public async Task ScenarioB_ProtectMicrophone_Reboot_MicrophoneBecomesProtected()
    {
        var env = CreateEnvironment();

        var app1 = env.CreateInstance();
        env.AutostartProvider.EnableAutostart();

        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Microphone);
        Assert.True(enable.Success);

        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("WindowsShutdown");
        Assert.True(shutdown.SafeToExit);

        var appBoot = env.CreateInstance();
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var stateBoot = await appBoot.ProtectionService.GetCurrentStateAsync();
        Assert.True(stateBoot.Microphone.IsProtected);
        Assert.False(stateBoot.Camera.IsProtected);
    }

    // =========================================================================
    // Scenario C: Protect both → reboot → both become protected
    // =========================================================================

    [Fact]
    public async Task ScenarioC_ProtectBoth_Reboot_BothBecomeProtected()
    {
        var env = CreateEnvironment();

        var app1 = env.CreateInstance();
        env.AutostartProvider.EnableAutostart();

        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Both);
        Assert.True(enable.Success);

        var shutdown = await app1.ShutdownCoordinator.RestoreAsync("WindowsShutdown");
        Assert.True(shutdown.SafeToExit);

        var appBoot = env.CreateInstance();
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var stateBoot = await appBoot.ProtectionService.GetCurrentStateAsync();
        Assert.True(stateBoot.Camera.IsProtected);
        Assert.True(stateBoot.Microphone.IsProtected);
    }

    // =========================================================================
    // Scenario D: Protect camera → explicitly disable → reboot → camera remains unprotected
    // =========================================================================

    [Fact]
    public async Task ScenarioD_ProtectCamera_ExplicitlyDisable_Reboot_CameraRemainsUnprotected()
    {
        var env = CreateEnvironment();

        var app1 = env.CreateInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        // User explicitly disables Camera protection
        var disable = await app1.ProtectionService.DisableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(disable.Success);

        var stateAfterDisable = await app1.ProtectionService.GetCurrentStateAsync();
        Assert.False(stateAfterDisable.Camera.IsProtected);

        // DesiredState was updated to Inactive
        var desiredAfterDisable = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Inactive, desiredAfterDisable.CameraStandard);

        // Windows reboots
        var appBoot = env.CreateInstance();
        var recovery = await appBoot.RecoveryService.RecoverAtStartupAsync();
        var reconcile = await appBoot.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // Camera remains unprotected
        var stateBoot = await appBoot.ProtectionService.GetCurrentStateAsync();
        Assert.False(stateBoot.Camera.IsProtected);
        Assert.False(stateBoot.Microphone.IsProtected);
    }

    // =========================================================================
    // Scenario E: Protect camera → close application normally → start again → camera becomes protected
    // =========================================================================

    [Fact]
    public async Task ScenarioE_ProtectCamera_CloseNormally_StartAgain_CameraBecomesProtected()
    {
        var env = CreateEnvironment();

        // 1. App 1 protects Camera
        var app1 = env.CreateInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        // 2. User exits application normally via System Tray menu ("Salir")
        var exitResult = await app1.ShutdownCoordinator.RestoreAsync("TrayExit");
        Assert.True(exitResult.SafeToExit);

        // OS hardware is temporarily restored to original baseline for safety
        var osAfterExit = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterExit.Camera.IsProtected);

        // DesiredState remains Active
        var desiredAfterExit = env.StateStore.Load();
        Assert.Equal(StandardProtectionState.Active, desiredAfterExit.CameraStandard);

        // 3. User launches PrivGvard again
        var app2 = env.CreateInstance();
        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var osAfterRelaunch = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osAfterRelaunch.Camera.IsProtected);
    }

    // =========================================================================
    // Scenario F: Protect camera → terminate process unexpectedly → restart → recovery occurs safely → desired protection is reconciled
    // =========================================================================

    [Fact]
    public async Task ScenarioF_ProtectCamera_TerminateUnexpectedly_Restart_RecoveryOccursSafely_DesiredProtectionReconciled()
    {
        var env = CreateEnvironment();

        // 1. App 1 protects Camera
        var app1 = env.CreateInstance();
        var enable = await app1.ProtectionService.EnableStandardProtectionAsync(BlockTarget.Camera);
        Assert.True(enable.Success);

        // 2. Process is killed abruptly (crash/terminate: ShutdownCoordinator is NOT called)
        // WAL remains Active on disk with Applied resources. Devices remain protected.
        var sessionBeforeCrash = env.SessionStore.Load();
        Assert.NotNull(sessionBeforeCrash);
        Assert.True(sessionBeforeCrash!.IsActive);

        // 3. Next startup (App 2)
        var app2 = env.CreateInstance();

        // Recovery detects unfinished session, restores devices to clean baseline
        var recovery = await app2.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);
        Assert.True(recovery.HadRecoveryWork);

        var osAfterRecovery = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.False(osAfterRecovery.Camera.IsProtected);

        // Reconciliation reapplies Camera per DesiredState
        var reconcile = await app2.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        var osFinal = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osFinal.Camera.IsProtected);

        var sessionFinal = env.SessionStore.Load();
        Assert.NotNull(sessionFinal);
        Assert.True(sessionFinal!.IsActive);
        Assert.NotEqual(sessionBeforeCrash.SessionId, sessionFinal.SessionId);
    }

    // =========================================================================
    // Scenario G: Change external OS state while PrivGvard is stopped → start PrivGvard → external changes NOT blindly overwritten
    // =========================================================================

    [Fact]
    public async Task ScenarioG_ExternalOSChangeWhileStopped_StartupDoesNotBlindlyOverwrite()
    {
        var env = CreateEnvironment();

        // 1. User had Camera disabled (DesiredState = None)
        env.StateStore.Save(new DesiredState
        {
            CameraStandard = StandardProtectionState.Inactive,
            MicrophoneStandard = StandardProtectionState.Inactive
        });

        // 2. While PrivGvard is stopped, an external administrator / group policy sets the camera to Blocked
        env.ProtectionProvider.SetCameraStandardState(StandardProtectionState.Active);
        env.PlatformAdapter.SetObservation(
            IntegrationPlatformAdapter.CameraConsentResourceId,
            PrivacyResourceObservationKind.MatchesProtected);

        // 3. PrivGvard starts
        var app = env.CreateInstance();
        var recovery = await app.RecoveryService.RecoverAtStartupAsync();
        Assert.True(recovery.SafeToExit);

        var reconcile = await app.RecoveryService.ReconcileAtStartupAsync(recovery);
        Assert.True(reconcile.Success);

        // Verify: PrivGvard did NOT blindly unblock the administrator's camera block!
        var osState = await env.ProtectionProvider.GetProtectionStateAsync();
        Assert.True(osState.Camera.IsProtected);

        // Zero restore calls made
        Assert.Equal(0, env.PlatformAdapter.RestoreCallCount);
    }

    // =========================================================================
    // Integration Test Environment
    // =========================================================================

    private TestEnvironment CreateEnvironment()
    {
        var stateStore = new FileStateStore(_stateDirectory);
        var sessionStore = new FilePrivacySessionStore(_recoveryDirectory);
        var autostart = new WindowsAutostartProvider(_testRunKeyPath, _testStartupApprovedKeyPath, () => _dummyExePath);
        var adapter = new IntegrationPlatformAdapter();
        var provider = new IntegrationProtectionProvider(adapter);

        return new TestEnvironment(stateStore, sessionStore, autostart, adapter, provider);
    }

    private sealed record ApplicationInstance(
        ProtectionService ProtectionService,
        ShutdownCoordinator ShutdownCoordinator,
        PrivacyRecoveryService RecoveryService);

    private sealed class TestEnvironment
    {
        public FileStateStore StateStore { get; }
        public FilePrivacySessionStore SessionStore { get; }
        public WindowsAutostartProvider AutostartProvider { get; }
        public IntegrationPlatformAdapter PlatformAdapter { get; }
        public IntegrationProtectionProvider ProtectionProvider { get; }

        public TestEnvironment(
            FileStateStore stateStore,
            FilePrivacySessionStore sessionStore,
            WindowsAutostartProvider autostartProvider,
            IntegrationPlatformAdapter platformAdapter,
            IntegrationProtectionProvider protectionProvider)
        {
            StateStore = stateStore;
            SessionStore = sessionStore;
            AutostartProvider = autostartProvider;
            PlatformAdapter = platformAdapter;
            ProtectionProvider = protectionProvider;

            PlatformAdapter.OnRestored = resource =>
            {
                if (resource.Target == BlockTarget.Camera)
                    ProtectionProvider.SetCameraStandardState(StandardProtectionState.Inactive);
                if (resource.Target == BlockTarget.Microphone)
                    ProtectionProvider.SetMicrophoneStandardState(StandardProtectionState.Inactive);
            };
        }

        public ApplicationInstance CreateInstance()
        {
            var capabilityProvider = new IntegrationCapabilityProvider();
            var detector = new IntegrationDetector();
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

    private sealed class IntegrationPlatformAdapter : IPrivacySessionPlatformAdapter
    {
        public const string CameraConsentResourceId =
            @"registry:CurrentUser:Default:Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam:Value";
        public const string MicMuteResourceId = "audio-mute:mic-endpoint-1";

        private readonly object _sync = new();
        private readonly Dictionary<string, PrivacyResourceObservation> _observations = new(StringComparer.OrdinalIgnoreCase);
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
                list.Add(new OriginalPolicyState
                {
                    ResourceId = CameraConsentResourceId,
                    OperationId = operationId,
                    Layer = ProtectionLayer.Standard,
                    Target = BlockTarget.Camera,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    JournalState = PrivacyResourceJournalState.Captured,
                    ModifiedByPrivLock = false,
                    OwnershipUncertain = false,
                    RegistryHive = PrivacyRegistryHive.CurrentUser,
                    RegistryView = PrivacyRegistryView.Default,
                    RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam",
                    ValueName = "Value",
                    ValueExisted = true,
                    OriginalValueKind = PrivacyRegistryValueKind.String,
                    OriginalValue = "Allow",
                    ProtectedValueExists = true,
                    ProtectedValueKind = PrivacyRegistryValueKind.String,
                    ProtectedValue = "Deny"
                });
            }
            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                list.Add(new OriginalAudioEndpointState
                {
                    ResourceId = MicMuteResourceId,
                    OperationId = operationId,
                    Layer = ProtectionLayer.Standard,
                    Target = BlockTarget.Microphone,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    JournalState = PrivacyResourceJournalState.Captured,
                    ModifiedByPrivLock = false,
                    EndpointId = "mic-endpoint-1",
                    OriginalMutedState = false,
                    ProtectedMutedState = true
                });
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Confirmed));

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

    private sealed class IntegrationProtectionProvider : IDeviceProtectionProvider
    {
        private readonly IntegrationPlatformAdapter _adapter;
        private StandardProtectionState _cameraState = StandardProtectionState.Inactive;
        private StandardProtectionState _micState = StandardProtectionState.Inactive;

        public IntegrationProtectionProvider(IntegrationPlatformAdapter adapter)
        {
            _adapter = adapter;
        }

        public void SetCameraStandardState(StandardProtectionState state)
        {
            _cameraState = state;
            if (state == StandardProtectionState.Active)
                _adapter.SetProtected(IntegrationPlatformAdapter.CameraConsentResourceId);
            else
                _adapter.SetObservation(IntegrationPlatformAdapter.CameraConsentResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        }

        public void SetMicrophoneStandardState(StandardProtectionState state)
        {
            _micState = state;
            if (state == StandardProtectionState.Active)
                _adapter.SetProtected(IntegrationPlatformAdapter.MicMuteResourceId);
            else
                _adapter.SetObservation(IntegrationPlatformAdapter.MicMuteResourceId, PrivacyResourceObservationKind.MatchesOriginal);
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

    private sealed class IntegrationCapabilityProvider : IPlatformCapabilityProvider
    {
        public PlatformCapabilities Capabilities { get; } = new()
        {
            CameraProtectionLevel = CapabilityLevel.DualLayer,
            MicrophoneProtectionLevel = CapabilityLevel.DualLayer
        };

        public PlatformInfo PlatformInfo { get; } = new()
        {
            OperatingSystemName = "Windows",
            OsVersion = "10.0",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        };
    }

    private sealed class IntegrationDetector : IDeviceDetector
    {
        public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);
    }
}
