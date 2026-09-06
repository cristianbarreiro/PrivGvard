using System.Diagnostics;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;
using Serilog.Context;

namespace PrivLock.Application.Services;

/// <summary>
/// Single serialization point for block, unblock, recovery, and shutdown restoration.
/// Every supported Windows mutation is prepared in a durable journal before platform dispatch.
/// </summary>
public sealed class ProtectionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ProtectionService>();

    private readonly IDeviceProtectionProvider _protectionProvider;
    private readonly IDeviceDetector _deviceDetector;
    private readonly IPlatformCapabilityProvider _capabilityProvider;
    private readonly IStateStore _stateStore;
    private readonly PrivacySessionService _privacySessions;
    private readonly bool _allowUntrackedMutations;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _shutdownStarted;
    private int _desiredStateCleanupPending;

    public event Action<FullProtectionState>? StateChanged;

    public PlatformCapabilities Capabilities => _capabilityProvider.Capabilities;
    public PlatformInfo PlatformInfo => _capabilityProvider.PlatformInfo;
    public bool IsShutdownStarted => Volatile.Read(ref _shutdownStarted) != 0;
    internal bool HasPendingDesiredStateCleanup => Volatile.Read(ref _desiredStateCleanupPending) != 0;

    public ProtectionService(
        IDeviceProtectionProvider protectionProvider,
        IDeviceDetector deviceDetector,
        IPlatformCapabilityProvider capabilityProvider,
        IStateStore stateStore,
        PrivacySessionService privacySessions)
        : this(
            protectionProvider,
            deviceDetector,
            capabilityProvider,
            stateStore,
            privacySessions,
            allowUntrackedMutations: false)
    {
    }

    private ProtectionService(
        IDeviceProtectionProvider protectionProvider,
        IDeviceDetector deviceDetector,
        IPlatformCapabilityProvider capabilityProvider,
        IStateStore stateStore,
        PrivacySessionService privacySessions,
        bool allowUntrackedMutations)
    {
        _protectionProvider = protectionProvider;
        _deviceDetector = deviceDetector;
        _capabilityProvider = capabilityProvider;
        _stateStore = stateStore;
        _privacySessions = privacySessions;
        _allowUntrackedMutations = allowUntrackedMutations;
    }

    /// <summary>
    /// Compatibility constructor for platform-agnostic callers/tests that do not opt into an exact
    /// platform recovery adapter. Production DI uses the five-argument constructor.
    /// </summary>
    public ProtectionService(
        IDeviceProtectionProvider protectionProvider,
        IDeviceDetector deviceDetector,
        IPlatformCapabilityProvider capabilityProvider,
        IStateStore stateStore)
        : this(
            protectionProvider,
            deviceDetector,
            capabilityProvider,
            stateStore,
            new PrivacySessionService(
                new VolatilePrivacySessionStore(),
                new UnsupportedPrivacySessionPlatformAdapter()),
            allowUntrackedMutations: true)
    {
    }

    public async Task<OperationResult> EnableStandardProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        var operationId = CreateOperationId("StdEn");
        using var context = LogContext.PushProperty("OperationId", operationId);
        return await RunSerializedUserOperationAsync(async () =>
        {
            PrivacyBlockPreparation? preparation = null;
            var result = await PrepareApplyAndRecordAsync(
                ProtectionLayer.Standard,
                target,
                operationId,
                ct => _protectionProvider.EnableStandardProtectionAsync(target, ct),
                prepared => preparation = prepared,
                cancellationToken);
            if (!result.Success)
                return result;

            try
            {
                var desired = _stateStore.Load();
                if (target is BlockTarget.Camera or BlockTarget.Both)
                {
                    desired.CameraStandard = StandardProtectionState.Active;
                    if (desired.CameraSecure == SecureProtectionState.Unavailable)
                        desired.CameraSecure = SecureProtectionState.Available;
                }
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                {
                    desired.MicrophoneStandard = StandardProtectionState.Active;
                    if (desired.MicrophoneSecure == SecureProtectionState.Unavailable)
                        desired.MicrophoneSecure = SecureProtectionState.Available;
                }
                _stateStore.Save(desired);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Desired-state persistence failed after Standard protection; rolling back owned changes");
                var rollback = await RestorePreparedDeltaOrScopeAsync(
                    preparation,
                    ProtectionLayer.Standard,
                    target,
                    "DesiredStateSaveFailure",
                    CancellationToken.None);
                return OperationResult.Fail(
                    rollback.SafeToExit
                        ? $"Protection was rolled back because application state could not be saved: {ex.Message}"
                        : $"Application state could not be saved and rollback remains incomplete: {rollback.ErrorMessage}");
            }
            await PublishVerifiedStateAsync(cancellationToken);
            return OperationResult.Ok(result.Details);
        }, operationId, cancellationToken);
    }

    public async Task<OperationResult> DisableStandardProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        var operationId = CreateOperationId("StdDis");
        using var context = LogContext.PushProperty("OperationId", operationId);
        return await RunSerializedUserOperationAsync(async () =>
        {
            // Standard teardown also owns Secure changes. Legacy/unsupported providers are only
            // called when verified state says Secure is active, preserving prior API behavior.
            var secureResult = PrivacyRecoveryResult.NothingToRestore();
            if (_privacySessions.SupportsPersistentRecovery ||
                await IsSecureActiveAsync(target, cancellationToken))
            {
                secureResult = await RestoreScopeAsync(
                    ProtectionLayer.Secure,
                    target,
                    "UserDisableStandard-SecureFirst",
                    cancellationToken);
            }
            var standardResult = await RestoreScopeAsync(
                ProtectionLayer.Standard,
                target,
                "UserDisableStandard",
                cancellationToken);

            var combined = CombineRecoveryResults(secureResult, standardResult);
            if (combined.SafeToExit &&
                (combined.HadRecoveryWork || combined.ConflictCount == 0) &&
                (combined.TrackingSupported || _allowUntrackedMutations))
            {
                UpdateDesiredAfterStandardDisable(target);
                await PublishVerifiedStateAsync(cancellationToken);
            }

            return ToOperationResult(combined);
        }, operationId, cancellationToken);
    }

    public async Task<OperationResult> EnableSecureProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        var operationId = CreateOperationId("SecEn");
        using var context = LogContext.PushProperty("OperationId", operationId);
        return await RunSerializedUserOperationAsync(async () =>
        {
            var current = await _protectionProvider.GetProtectionStateAsync(cancellationToken);
            if (target is BlockTarget.Camera or BlockTarget.Both &&
                current.Camera.StandardState != StandardProtectionState.Active)
            {
                return OperationResult.Fail(
                    "You must enable Standard Protection before enabling Secure Protection for Camera.");
            }
            if (target is BlockTarget.Microphone or BlockTarget.Both &&
                current.Microphone.StandardState != StandardProtectionState.Active)
            {
                return OperationResult.Fail(
                    "You must enable Standard Protection before enabling Secure Protection for Microphone.");
            }

            PrivacyBlockPreparation? preparation = null;
            var result = await PrepareApplyAndRecordAsync(
                ProtectionLayer.Secure,
                target,
                operationId,
                ct => _protectionProvider.EnableSecureProtectionAsync(target, ct),
                prepared => preparation = prepared,
                cancellationToken);
            if (!result.Success)
                return result;

            try
            {
                var desired = _stateStore.Load();
                if (target is BlockTarget.Camera or BlockTarget.Both)
                    desired.CameraSecure = SecureProtectionState.Active;
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                    desired.MicrophoneSecure = SecureProtectionState.Active;
                _stateStore.Save(desired);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Desired-state persistence failed after Secure protection; rolling back owned changes");
                var rollback = await RestorePreparedDeltaOrScopeAsync(
                    preparation,
                    ProtectionLayer.Secure,
                    target,
                    "DesiredStateSaveFailure",
                    CancellationToken.None);
                return OperationResult.Fail(
                    rollback.SafeToExit
                        ? $"Secure protection was rolled back because application state could not be saved: {ex.Message}"
                        : $"Application state could not be saved and rollback remains incomplete: {rollback.ErrorMessage}");
            }
            await PublishVerifiedStateAsync(cancellationToken);
            return OperationResult.Ok(result.Details);
        }, operationId, cancellationToken);
    }

    public async Task<OperationResult> DisableSecureProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        var operationId = CreateOperationId("SecDis");
        using var context = LogContext.PushProperty("OperationId", operationId);
        return await RunSerializedUserOperationAsync(async () =>
        {
            var recovery = await RestoreScopeAsync(
                ProtectionLayer.Secure,
                target,
                "UserDisableSecure",
                cancellationToken);
            if (recovery.SafeToExit &&
                (recovery.HadRecoveryWork || recovery.ConflictCount == 0) &&
                (recovery.TrackingSupported || _allowUntrackedMutations))
            {
                var desired = _stateStore.Load();
                if (target is BlockTarget.Camera or BlockTarget.Both)
                    desired.CameraSecure = desired.CameraStandard == StandardProtectionState.Active
                        ? SecureProtectionState.Available
                        : SecureProtectionState.Unavailable;
                if (target is BlockTarget.Microphone or BlockTarget.Both)
                    desired.MicrophoneSecure = desired.MicrophoneStandard == StandardProtectionState.Active
                        ? SecureProtectionState.Available
                        : SecureProtectionState.Unavailable;
                _stateStore.Save(desired);
                await PublishVerifiedStateAsync(cancellationToken);
            }
            return ToOperationResult(recovery);
        }, operationId, cancellationToken);
    }

    /// <summary>
    /// Startup recovery runs before the ViewModel/UI exists and before any new mutation is allowed.
    /// </summary>
    public async Task<PrivacyRecoveryResult> RecoverPreviousSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var desiredBeforeRecovery = _stateStore.Load();
            var result = await _privacySessions.RecoverUnfinishedSessionAsync(cancellationToken);
            if (!_allowUntrackedMutations &&
                result.SafeToExit &&
                !result.HadPersistedSession &&
                HasAnyDesiredProtection(desiredBeforeRecovery))
            {
                return LegacyUntrackedRecoveryResult();
            }
            if (result.TrackingSupported &&
                result.SafeToExit &&
                result.HadPersistedSession &&
                HasAnyDesiredProtection(desiredBeforeRecovery))
                ResetDesiredProtectionStateBestEffort("startup recovery");
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Marks the service as stopping before waiting on the operation gate. This ordering ensures a
    /// queued toggle cannot run after shutdown restoration has completed.
    /// </summary>
    public async Task<PrivacyRecoveryResult> BeginShutdownAndRestoreAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        SignalShutdown();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            PrivacyRecoveryResult result;
            if (_privacySessions.SupportsPersistentRecovery)
            {
                result = await _privacySessions.RestoreAsync(null, null, reason, cancellationToken);
            }
            else
            {
                if (!_allowUntrackedMutations)
                {
                    // Production must never perform a broad "allow/unmute" without an exact
                    // snapshot proving ownership. Unsupported platforms reject mutation earlier.
                    result = PrivacyRecoveryResult.Unsupported();
                }
                else
                {
                    var secure = await _protectionProvider.DisableSecureProtectionAsync(BlockTarget.Both, cancellationToken);
                    var standard = await _protectionProvider.DisableStandardProtectionAsync(BlockTarget.Both, cancellationToken);
                    result = new PrivacyRecoveryResult
                    {
                        TrackingSupported = false,
                        IsComplete = secure.Success && standard.Success,
                        FailedCount = (secure.Success ? 0 : 1) + (standard.Success ? 0 : 1),
                        ErrorMessage = secure.ErrorMessage ?? standard.ErrorMessage
                    };
                }
            }

            var desiredAfterRecovery = _stateStore.Load();
            if (!_allowUntrackedMutations &&
                result.SafeToExit &&
                !result.HadPersistedSession &&
                HasAnyDesiredProtection(desiredAfterRecovery))
                result = LegacyUntrackedRecoveryResult();

            if (result.SafeToExit &&
                result.HadPersistedSession &&
                HasAnyDesiredProtection(desiredAfterRecovery) &&
                (result.TrackingSupported || _allowUntrackedMutations))
                ResetDesiredProtectionStateBestEffort("shutdown recovery");
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal void SignalShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1);

    public void AbortShutdown() => Interlocked.Exchange(ref _shutdownStarted, 0);

    public Task<FullProtectionState> GetCurrentStateAsync(CancellationToken cancellationToken = default) =>
        _protectionProvider.GetProtectionStateAsync(cancellationToken);

    public Task<IReadOnlyList<DeviceInfo>> GetDetectedDevicesAsync(CancellationToken cancellationToken = default) =>
        _deviceDetector.DetectAllAsync(cancellationToken);

    public object GetRecoveryDiagnosticSummary() => _privacySessions.GetDiagnosticSummary();

    private async Task<OperationResult> PrepareApplyAndRecordAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        Func<CancellationToken, Task<OperationResult>> apply,
        Action<PrivacyBlockPreparation> capturePreparation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PrepareApplyAndRecordCoreAsync(
                layer,
                target,
                operationId,
                apply,
                capturePreparation,
                cancellationToken);
        }
        finally
        {
            _privacySessions.CompletePlatformPass();
        }
    }

    private async Task<OperationResult> PrepareApplyAndRecordCoreAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        Func<CancellationToken, Task<OperationResult>> apply,
        Action<PrivacyBlockPreparation> capturePreparation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        PrivacyBlockPreparation preparation;
        try
        {
            preparation = await _privacySessions.PrepareBlockAsync(layer, target, operationId, cancellationToken);
            capturePreparation(preparation);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Snapshot persistence failed; platform mutation was not dispatched");
            return OperationResult.Fail($"Could not persist a recovery snapshot: {ex.Message}");
        }

        if (!preparation.TrackingEnabled && !_allowUntrackedMutations)
        {
            return OperationResult.Fail(
                "This platform cannot yet capture and restore exact per-resource privacy state; the protection change was not applied.");
        }

        var validation = await _privacySessions.ValidatePreparedResourcesAsync(preparation, cancellationToken);
        if (!validation.Success)
        {
            return validation;
        }

        OperationResult platformResult;
        try
        {
            platformResult = await apply(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Platform block operation threw after snapshot commit");
            platformResult = OperationResult.Fail(ex.Message, outcomeUncertain: true);
        }

        OperationResult journalResult;
        try
        {
            // Reconciliation and rollback must survive caller cancellation once native dispatch began.
            journalResult = await _privacySessions.RecordBlockOutcomeAsync(
                preparation,
                platformResult,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to record platform outcome; durable ApplyPending entries remain for startup recovery");
            var executionStillInFlight = platformResult.ExecutionStillInFlight;
            if (executionStillInFlight)
            {
                _privacySessions.RequireMutationQuiescence();
                var quiescence = await _privacySessions.EnsureMutationQuiescenceAsync(CancellationToken.None);
                executionStillInFlight = !quiescence.Success;
            }

            if (executionStillInFlight)
            {
                return OperationResult.Fail(
                    $"The platform outcome checkpoint failed and its native actor is not quiescent: {ex.Message}",
                    platformResult.Details,
                    outcomeUncertain: true,
                    executionStillInFlight: true);
            }

            var compensation = await _privacySessions.RollbackConfirmedApplyAfterCheckpointFailureAsync(
                preparation,
                platformResult,
                CancellationToken.None);
            var compensationMessage = compensation.SafeToExit
                ? " Confirmed changes were immediately restored."
                : $" Immediate restoration remains incomplete: {compensation.ErrorMessage}";
            return OperationResult.Fail(
                $"The platform outcome checkpoint failed: {ex.Message}.{compensationMessage}",
                platformResult.Details,
                outcomeUncertain: platformResult.OutcomeUncertain,
                executionStillInFlight: false);
        }

        var result = platformResult.Success ? journalResult : platformResult;
        if (!result.Success)
        {
            if (result.ExecutionStillInFlight)
            {
                return OperationResult.Fail(
                    (result.ErrorMessage ?? "Protection outcome is unknown.") +
                    " Recovery remains deferred until the privileged actor is proven stopped.",
                    result.Details,
                    outcomeUncertain: true,
                    executionStillInFlight: true);
            }

            var rollback = await RestorePreparedDeltaOrScopeAsync(
                preparation,
                layer,
                target,
                "BlockFailureRollback",
                CancellationToken.None);
            var rollbackSuffix = !rollback.SafeToExit
                ? $" Rollback remains incomplete: {rollback.ErrorMessage}"
                : rollback.ConflictCount > 0
                    ? $" External conflicts were preserved: {rollback.ErrorMessage}"
                    : " Partial changes were rolled back.";
            return OperationResult.Fail((result.ErrorMessage ?? "Protection operation failed.") + rollbackSuffix, result.Details);
        }

        stopwatch.Stop();
        Log.Information(
            "Protection applied and journaled: Layer={Layer}, Target={Target}, DurationMs={DurationMs}",
            layer,
            target,
            stopwatch.ElapsedMilliseconds);
        return result;
    }

    private Task<PrivacyRecoveryResult> RestorePreparedDeltaOrScopeAsync(
        PrivacyBlockPreparation? preparation,
        ProtectionLayer layer,
        BlockTarget target,
        string reason,
        CancellationToken cancellationToken) =>
        preparation is { TrackingEnabled: true }
            ? _privacySessions.RestorePreparedDeltaAsync(
                preparation,
                layer,
                target,
                reason,
                cancellationToken)
            : _privacySessions.RestoreAsync(layer, target, reason, cancellationToken);

    private async Task<PrivacyRecoveryResult> RestoreScopeAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_privacySessions.SupportsPersistentRecovery)
        {
            var recovery = await _privacySessions.RestoreAsync(layer, target, reason, cancellationToken);
            if (!recovery.SafeToExit)
                return recovery;
            if (!recovery.HadRecoveryWork &&
                !recovery.HadPersistedSession &&
                IsDesiredScopeActive(_stateStore.Load(), layer, target))
                return LegacyUntrackedRecoveryResult();
            if (!recovery.HadRecoveryWork)
            {
                var actual = await _protectionProvider.GetProtectionStateAsync(cancellationToken);
                var active = IsActualScopeActive(actual, layer, target);
                if (active == true)
                {
                    return new PrivacyRecoveryResult
                    {
                        IsComplete = true,
                        ConflictCount = 1,
                        ErrorMessage =
                            "The requested privacy scope is protected, but no PrivLock ownership snapshot exists. " +
                            "The external or legacy state was preserved."
                    };
                }
                if (active == null)
                {
                    return new PrivacyRecoveryResult
                    {
                        IsComplete = false,
                        FailedCount = 1,
                        ErrorMessage =
                            "The requested privacy scope has no ownership snapshot and its current state could not be verified."
                    };
                }
            }
            return recovery;
        }

        if (!_allowUntrackedMutations)
        {
            return IsDesiredScopeActive(_stateStore.Load(), layer, target)
                ? LegacyUntrackedRecoveryResult()
                : PrivacyRecoveryResult.Unsupported();
        }

        var result = layer == ProtectionLayer.Standard
            ? await _protectionProvider.DisableStandardProtectionAsync(target, cancellationToken)
            : await _protectionProvider.DisableSecureProtectionAsync(target, cancellationToken);
        return new PrivacyRecoveryResult
        {
            TrackingSupported = false,
            IsComplete = result.Success,
            FailedCount = result.Success ? 0 : 1,
            ErrorMessage = result.ErrorMessage
        };
    }

    private async Task<OperationResult> RunSerializedUserOperationAsync(
        Func<Task<OperationResult>> operation,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (IsShutdownStarted)
            return OperationResult.Fail("PrivLock is shutting down; new protection changes are not accepted.");

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsShutdownStarted)
                return OperationResult.Fail("PrivLock is shutting down; new protection changes are not accepted.");
            // The whole serialized operation must be independent of a UI synchronization context.
            // Shutdown/logout is then free to return control to the dispatcher while this operation
            // reaches its durable checkpoint and releases the gate.
            return await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Protection operation {OperationId} was cancelled", operationId);
            return OperationResult.Fail("The operation was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected protection operation failure");
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task PublishVerifiedStateAsync(CancellationToken cancellationToken)
    {
        var state = await _protectionProvider.GetProtectionStateAsync(cancellationToken);
        StateChanged?.Invoke(state);
    }

    private async Task<bool> IsSecureActiveAsync(BlockTarget target, CancellationToken cancellationToken)
    {
        var state = await _protectionProvider.GetProtectionStateAsync(cancellationToken);
        return target switch
        {
            BlockTarget.Camera => state.Camera.SecureState == SecureProtectionState.Active,
            BlockTarget.Microphone => state.Microphone.SecureState == SecureProtectionState.Active,
            BlockTarget.Both =>
                state.Camera.SecureState == SecureProtectionState.Active ||
                state.Microphone.SecureState == SecureProtectionState.Active,
            _ => false
        };
    }

    private void UpdateDesiredAfterStandardDisable(BlockTarget target)
    {
        var desired = _stateStore.Load();
        if (target is BlockTarget.Camera or BlockTarget.Both)
        {
            desired.CameraStandard = StandardProtectionState.Inactive;
            desired.CameraSecure = SecureProtectionState.Unavailable;
        }
        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            desired.MicrophoneStandard = StandardProtectionState.Inactive;
            desired.MicrophoneSecure = SecureProtectionState.Unavailable;
        }
        _stateStore.Save(desired);
    }

    private void ResetDesiredProtectionState()
    {
        var desired = _stateStore.Load();
        desired.CameraStandard = StandardProtectionState.Inactive;
        desired.CameraSecure = SecureProtectionState.Unavailable;
        desired.MicrophoneStandard = StandardProtectionState.Inactive;
        desired.MicrophoneSecure = SecureProtectionState.Unavailable;
        _stateStore.Save(desired);
    }

    private void ResetDesiredProtectionStateBestEffort(string context)
    {
        try
        {
            ResetDesiredProtectionState();
            Interlocked.Exchange(ref _desiredStateCleanupPending, 0);
        }
        catch (Exception ex)
        {
            // The recovery journal and verified native state remain authoritative. A preferences
            // file failure must not turn a completed hardware restore into an unsafe shutdown.
            Interlocked.Exchange(ref _desiredStateCleanupPending, 1);
            Log.Error(ex, "Failed to reset desired protection state after {Context}", context);
        }
    }

    private OperationResult ToOperationResult(PrivacyRecoveryResult result)
    {
        if (!result.TrackingSupported && !_allowUntrackedMutations)
        {
            return OperationResult.Fail(
                "Exact reversible privacy-state tracking is not supported on this platform; no broad unblock was attempted.");
        }
        if (!result.SafeToExit)
            return OperationResult.Fail(result.ErrorMessage ?? "Restoration remains incomplete.");
        if (result.ConflictCount > 0)
            return OperationResult.Fail(result.ErrorMessage ?? "External state conflicts were preserved.");
        return OperationResult.Ok();
    }

    private static PrivacyRecoveryResult CombineRecoveryResults(params PrivacyRecoveryResult[] results) => new()
    {
        TrackingSupported = results.All(result => result.TrackingSupported),
        HadPersistedSession = results.Any(result => result.HadPersistedSession),
        HadRecoveryWork = results.Any(result => result.HadRecoveryWork),
        IsComplete = results.All(result => result.IsComplete),
        RestoredCount = results.Sum(result => result.RestoredCount),
        AlreadyRestoredCount = results.Sum(result => result.AlreadyRestoredCount),
        ConflictCount = results.Sum(result => result.ConflictCount),
        IrreducibleAmbiguityCount = results.Sum(result => result.IrreducibleAmbiguityCount),
        MissingCount = results.Sum(result => result.MissingCount),
        FailedCount = results.Sum(result => result.FailedCount),
        ErrorMessage = string.Join(" ", results.Select(result => result.ErrorMessage).Where(message => !string.IsNullOrWhiteSpace(message)))
    };

    private static PrivacyRecoveryResult LegacyUntrackedRecoveryResult() => new()
    {
        IsComplete = false,
        FailedCount = 1,
        ErrorMessage =
            "A protection state from an older PrivLock version was detected without an exact recovery snapshot. " +
            "It was preserved; automatic broad unblocking is unsafe. Review the affected operating-system privacy settings manually."
    };

    private static bool HasAnyDesiredProtection(DesiredState desired) =>
        desired.CameraStandard == StandardProtectionState.Active ||
        desired.MicrophoneStandard == StandardProtectionState.Active ||
        desired.CameraSecure == SecureProtectionState.Active ||
        desired.MicrophoneSecure == SecureProtectionState.Active;

    private static bool IsDesiredScopeActive(
        DesiredState desired,
        ProtectionLayer layer,
        BlockTarget target)
    {
        var camera = layer == ProtectionLayer.Standard
            ? desired.CameraStandard == StandardProtectionState.Active
            : desired.CameraSecure == SecureProtectionState.Active;
        var microphone = layer == ProtectionLayer.Standard
            ? desired.MicrophoneStandard == StandardProtectionState.Active
            : desired.MicrophoneSecure == SecureProtectionState.Active;
        return target switch
        {
            BlockTarget.Camera => camera,
            BlockTarget.Microphone => microphone,
            BlockTarget.Both => camera || microphone,
            _ => false
        };
    }

    private static bool? IsActualScopeActive(
        FullProtectionState state,
        ProtectionLayer layer,
        BlockTarget target)
    {
        static bool? ForTarget(TargetProtectionStatus status, ProtectionLayer requestedLayer) =>
            requestedLayer == ProtectionLayer.Standard
                ? status.StandardState switch
                {
                    StandardProtectionState.Active => true,
                    StandardProtectionState.Inactive => false,
                    _ => null
                }
                : status.SecureState switch
                {
                    SecureProtectionState.Active => true,
                    SecureProtectionState.Unknown => null,
                    _ => false
                };

        var camera = ForTarget(state.Camera, layer);
        var microphone = ForTarget(state.Microphone, layer);
        return target switch
        {
            BlockTarget.Camera => camera,
            BlockTarget.Microphone => microphone,
            BlockTarget.Both when camera == true || microphone == true => true,
            BlockTarget.Both when camera == null || microphone == null => null,
            BlockTarget.Both => false,
            _ => null
        };
    }

    private static string CreateOperationId(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"Op-{prefix}-{suffix}";
    }

    private sealed class VolatilePrivacySessionStore : IPrivacySessionStore
    {
        private PrivacySession? _session;
        public PrivacySession? Load() => _session;
        public void Save(PrivacySession session) => _session = session;
    }
}
