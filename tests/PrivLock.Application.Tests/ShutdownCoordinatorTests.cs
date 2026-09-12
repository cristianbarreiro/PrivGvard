using PrivLock.Application.Services;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public async Task RestoreAsync_ConcurrentShutdownSignalsShareOneActiveRestore()
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
        var (protection, coordinator) = CreateCoordinator(store, platform);

        var windowClosing = coordinator.RestoreAsync("WindowClosing");
        await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var trayExit = coordinator.RestoreAsync("TrayExit");

        Assert.Same(windowClosing, trayExit);
        Assert.True(protection.IsShutdownStarted);
        allowRestore.TrySetResult();
        var result = await windowClosing;
        var processExitFallback = coordinator.RestoreAsync("ProcessExit");

        Assert.True(result.SafeToExit);
        Assert.Same(windowClosing, processExitFallback);
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
        Assert.Equal(1, platform.CompletedRecoveryPasses);
    }

    [Fact]
    public async Task AbortShutdownAfterFailedUserExit_AllowsRetryOfPendingRecovery()
    {
        var device = PrivacyRecoveryTestData.Device("temporarily-missing-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.Missing);
        var (protection, coordinator) = CreateCoordinator(store, platform);

        var failedExit = await coordinator.RestoreAsync("UserExit");

        Assert.False(failedExit.SafeToExit);
        Assert.True(protection.IsShutdownStarted);
        coordinator.AbortShutdownAfterFailedUserExit();
        Assert.False(protection.IsShutdownStarted);

        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var retry = await coordinator.RestoreAsync("UserExitRetry");

        Assert.True(retry.SafeToExit);
        Assert.Equal(1, retry.RestoredCount);
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
        Assert.Equal(2, platform.CompletedRecoveryPasses);
        Assert.Equal(PrivacySessionStatus.Restored, store.Current!.Status);
    }

    [Fact]
    public void TryRestoreWithin_CompletedRestore_ReturnsSafeResult()
    {
        var device = PrivacyRecoveryTestData.Device("camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var (_, coordinator) = CreateCoordinator(store, platform);

        var safe = coordinator.TryRestoreWithin(
            "ProcessExit",
            TimeSpan.FromSeconds(5),
            out var result);

        Assert.True(safe);
        Assert.NotNull(result);
        Assert.True(result.SafeToExit);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
    }

    [Fact]
    public async Task RestoreWithinAsync_TimeoutDoesNotCancelUnderlyingRecovery()
    {
        var device = PrivacyRecoveryTestData.Device("slow-shutdown-camera");
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
        var (protection, coordinator) = CreateCoordinator(store, platform);

        var bounded = coordinator.RestoreWithinAsync(
            "OperatingSystemShutdownOrLogout",
            TimeSpan.FromMilliseconds(200));
        await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await bounded);
        Assert.True(protection.IsShutdownStarted);

        var stillActive = coordinator.RestoreAsync("ApplicationLifetimeExited");
        allowRestore.TrySetResult();
        var completed = await stillActive.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed.SafeToExit);
        Assert.Equal([device.ResourceId], platform.RestoreCalls);
    }

    [Fact]
    public async Task CompletedRestore_DesiredStateSaveFailure_IsRetriedByNextExitSignal()
    {
        var restoredDevice = PrivacyRecoveryTestData.Device(
            "restored-camera",
            journalState: PrivacyResourceJournalState.Restored,
            modifiedByPrivLock: false);
        var terminalSession = PrivacyRecoveryTestData.ActiveSession(restoredDevice);
        terminalSession.IsActive = false;
        terminalSession.WasRestored = true;
        terminalSession.Status = PrivacySessionStatus.Restored;
        terminalSession.CompletedAtUtc = terminalSession.UpdatedAtUtc;
        var store = new RecordingPrivacySessionStore(terminalSession);
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies
        {
            SaveFailureFactory = attempt => attempt == 1
                ? new IOException("preferences temporarily unavailable")
                : null
        };
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available
        });
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);
        var coordinator = new ShutdownCoordinator(protection);

        var firstTask = coordinator.RestoreAsync("UserExit");
        var first = await firstTask;

        Assert.True(first.SafeToExit);
        Assert.True(first.HadPersistedSession);
        Assert.Equal(1, dependencies.SaveAttempts);
        Assert.Equal(StandardProtectionState.Active, dependencies.Load().CameraStandard);

        var retryTask = coordinator.RestoreAsync("ApplicationLifetimeExited");
        Assert.NotSame(firstTask, retryTask);
        var retry = await retryTask;

        Assert.True(retry.SafeToExit);
        Assert.True(retry.HadPersistedSession);
        Assert.Equal(2, dependencies.SaveAttempts);
        Assert.Equal(1, dependencies.SaveCount);
        Assert.Equal(StandardProtectionState.Inactive, dependencies.Load().CameraStandard);
    }

    [Fact]
    public async Task TerminalJournalWithStaleDesiredState_IsNotClassifiedAsLegacyUntracked()
    {
        var restoredDevice = PrivacyRecoveryTestData.Device(
            "terminal-camera",
            journalState: PrivacyResourceJournalState.Restored,
            modifiedByPrivLock: false);
        var terminalSession = PrivacyRecoveryTestData.ActiveSession(restoredDevice);
        terminalSession.IsActive = false;
        terminalSession.WasRestored = true;
        terminalSession.Status = PrivacySessionStatus.Restored;
        terminalSession.CompletedAtUtc = terminalSession.UpdatedAtUtc;
        var store = new RecordingPrivacySessionStore(terminalSession);
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies
        {
            SaveFailureFactory = attempt => attempt == 1
                ? new IOException("first desired-state reset failed")
                : null
        };
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Available
        });
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var startup = await protection.RecoverPreviousSessionAsync();
        var disable = await protection.DisableStandardProtectionAsync(BlockTarget.Camera);

        Assert.True(startup.SafeToExit);
        Assert.True(disable.Success);
        Assert.DoesNotContain("older PrivLock version", disable.ErrorMessage ?? string.Empty);
        Assert.Equal(2, dependencies.SaveAttempts);
        Assert.Equal(StandardProtectionState.Inactive, dependencies.Load().CameraStandard);
    }

    [Fact]
    public async Task IncompleteStartupAndShutdownRecovery_DoNotClearDesiredProtectionState()
    {
        var device = PrivacyRecoveryTestData.Device("missing-camera");
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(device));
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        platform.SetObservation(device.ResourceId, PrivacyResourceObservationKind.Missing);
        var dependencies = new RecoveryHostDependencies();
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);
        var coordinator = new ShutdownCoordinator(protection);

        var startup = await protection.RecoverPreviousSessionAsync();
        var shutdown = await coordinator.RestoreAsync("UserExit");

        Assert.False(startup.SafeToExit);
        Assert.False(shutdown.SafeToExit);
        Assert.Equal(0, dependencies.SaveCount);
    }

    [Fact]
    public async Task LegacyDesiredBlockWithoutJournal_IsPreservedAndReportedUnsafe()
    {
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies();
        dependencies.SetDesiredState(new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active
        });
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var startup = await protection.RecoverPreviousSessionAsync();
        var shutdown = await protection.BeginShutdownAndRestoreAsync("LegacyExit");

        Assert.False(startup.SafeToExit);
        Assert.False(shutdown.SafeToExit);
        Assert.Contains("older PrivLock version", startup.ErrorMessage);
        Assert.Equal(0, dependencies.SaveCount);
    }

    [Fact]
    public async Task UnquiescedBlockResult_DoesNotRunImmediateRollback()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "worker-running-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [camera] };
        var dependencies = new RecoveryHostDependencies
        {
            StandardEnableResult = OperationResult.Fail(
                "worker still running",
                outcomeUncertain: true,
                executionStillInFlight: true)
        };
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var result = await protection.EnableStandardProtectionAsync(BlockTarget.Camera);

        Assert.False(result.Success);
        Assert.True(result.ExecutionStillInFlight);
        Assert.Single(platform.ObserveCalls); // pre-dispatch validation only
        Assert.Empty(platform.RestoreCalls);
        var pending = Assert.Single(store.Current!.Resources);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, pending.JournalState);
        Assert.True(store.Current.IsActive);
    }

    [Fact]
    public async Task InFlightResultAndJournalCheckpointFailure_KeepProcessBarrierBeforeObservation()
    {
        var camera = PrivacyRecoveryTestData.Device(
            "double-failure-camera",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore
        {
            SaveFailureFactory = attempt => attempt >= 2 ? new IOException("checkpoint failed") : null
        };
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera],
            QuiescenceResult = OperationResult.Fail("helper lease remains held")
        };
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var dependencies = new RecoveryHostDependencies
        {
            StandardEnableResult = OperationResult.Fail(
                "worker still running",
                outcomeUncertain: true,
                executionStillInFlight: true)
        };
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var enable = await protection.EnableStandardProtectionAsync(BlockTarget.Camera);
        var observationsAfterValidation = platform.ObserveCalls.Count;
        var shutdown = await sessions.RestoreAsync(null, null, "SameProcessShutdown");

        Assert.False(enable.Success);
        Assert.True(enable.ExecutionStillInFlight);
        Assert.False(shutdown.SafeToExit);
        Assert.Equal(observationsAfterValidation, platform.ObserveCalls.Count);
        Assert.Empty(platform.RestoreCalls);
        Assert.True(platform.QuiescenceCalls >= 2);
        Assert.Equal(PrivacyResourceJournalState.ApplyPending, Assert.Single(store.Current!.Resources).JournalState);
    }

    [Fact]
    public async Task DisableWithoutJournal_WhenActualScopeIsProtected_PreservesUnownedState()
    {
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter();
        var dependencies = new RecoveryHostDependencies
        {
            CurrentProtectionState = new FullProtectionState
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
            }
        };
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var result = await protection.DisableStandardProtectionAsync(BlockTarget.Camera);

        Assert.False(result.Success);
        Assert.Contains("ownership snapshot", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, dependencies.StandardDisableCalls);
        Assert.Equal(0, dependencies.SaveCount);
    }

    [Fact]
    public async Task PartialApplyFailure_RollsBackOnlyResourcesNewlyOwnedByThatOperation()
    {
        var camera = PrivacyRecoveryTestData.Device("baseline-camera");
        var microphone = PrivacyRecoveryTestData.Device(
            "new-microphone",
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false,
            target: BlockTarget.Microphone);
        var store = new RecordingPrivacySessionStore(PrivacyRecoveryTestData.ActiveSession(camera));
        var platform = new ScriptedPrivacySessionPlatformAdapter
        {
            CapturedResources = [camera, microphone]
        };
        platform.SetObservation(camera.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        platform.SetObservation(microphone.ResourceId, PrivacyResourceObservationKind.MatchesOriginal);
        var dependencies = new RecoveryHostDependencies
        {
            StandardEnableResult = OperationResult.Fail(
                "later resource failed",
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
            platform.SetObservation(microphone.ResourceId, PrivacyResourceObservationKind.MatchesProtected);
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var result = await protection.EnableStandardProtectionAsync(BlockTarget.Both);

        Assert.False(result.Success);
        Assert.Equal([microphone.ResourceId], platform.RestoreCalls);
        var resources = store.Current!.Resources;
        Assert.Equal(
            PrivacyResourceJournalState.Applied,
            resources.Single(resource => resource.ResourceId == camera.ResourceId).JournalState);
        Assert.Equal(
            PrivacyResourceJournalState.Restored,
            resources.Single(resource => resource.ResourceId == microphone.ResourceId).JournalState);
    }

    [Fact]
    public async Task EnableWhenEverythingWasAlreadyProtected_DoesNotAdoptOwnershipOrDesiredState()
    {
        var preProtected = PrivacyRecoveryTestData.Device(
            "externally-protected-camera",
            originalEnabled: false,
            protectedEnabled: false,
            journalState: PrivacyResourceJournalState.Captured,
            modifiedByPrivLock: false);
        var store = new RecordingPrivacySessionStore();
        var platform = new ScriptedPrivacySessionPlatformAdapter { CapturedResources = [preProtected] };
        var dependencies = new RecoveryHostDependencies();
        var sessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            sessions);

        var result = await protection.EnableStandardProtectionAsync(BlockTarget.Camera);

        Assert.False(result.Success);
        Assert.Contains("already protected", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, dependencies.StandardEnableCalls);
        Assert.Equal(0, dependencies.SaveCount);
        Assert.False(store.Current!.IsActive);
        Assert.Equal(PrivacyResourceJournalState.Unchanged, Assert.Single(store.Current.Resources).JournalState);
    }

    private static (ProtectionService Protection, ShutdownCoordinator Coordinator) CreateCoordinator(
        IPrivacySessionStore store,
        IPrivacySessionPlatformAdapter platform)
    {
        var dependencies = new RecoveryHostDependencies();
        var privacySessions = new PrivacySessionService(store, platform);
        var protection = new ProtectionService(
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            privacySessions);
        return (protection, new ShutdownCoordinator(protection));
    }
}
