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
    private readonly ILegacyArtifactDetector _legacyDetector;
    private readonly bool _allowUntrackedMutations;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _shutdownStarted;

    public event Action<FullProtectionState>? StateChanged;

    public PlatformCapabilities Capabilities => _capabilityProvider.Capabilities;
    public PlatformInfo PlatformInfo => _capabilityProvider.PlatformInfo;
    public bool IsShutdownStarted => Volatile.Read(ref _shutdownStarted) != 0;
    internal bool HasPendingDesiredStateCleanup => false;
    public bool IsAdvancedProtectionEnabled => _stateStore.Load().AdvancedProtectionEnabled;

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
            legacyDetector: null,
            allowUntrackedMutations: false)
    {
    }

    public ProtectionService(
        IDeviceProtectionProvider protectionProvider,
        IDeviceDetector deviceDetector,
        IPlatformCapabilityProvider capabilityProvider,
        IStateStore stateStore,
        PrivacySessionService privacySessions,
        ILegacyArtifactDetector? legacyDetector)
        : this(
            protectionProvider,
            deviceDetector,
            capabilityProvider,
            stateStore,
            privacySessions,
            legacyDetector,
            allowUntrackedMutations: false)
    {
    }

    private ProtectionService(
        IDeviceProtectionProvider protectionProvider,
        IDeviceDetector deviceDetector,
        IPlatformCapabilityProvider capabilityProvider,
        IStateStore stateStore,
        PrivacySessionService privacySessions,
        ILegacyArtifactDetector? legacyDetector,
        bool allowUntrackedMutations)
    {
        _protectionProvider = protectionProvider;
        _deviceDetector = deviceDetector;
        _capabilityProvider = capabilityProvider;
        _stateStore = stateStore;
        _privacySessions = privacySessions;
        _legacyDetector = legacyDetector ?? new PrivLock.Infrastructure.Common.Storage.DefaultLegacyArtifactDetector();
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
            legacyDetector: null,
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
    /// Blocks the specified target. Applies standard protection, and if Advanced Protection is enabled globally,
    /// immediately reinforces the target with secure/hardware-level protection.
    /// </summary>
    public async Task<OperationResult> BlockDeviceAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        var stdResult = await EnableStandardProtectionAsync(target, cancellationToken);
        if (!stdResult.Success)
            return stdResult;

        if (IsAdvancedProtectionEnabled)
        {
            var advResult = await EnableSecureProtectionAsync(target, cancellationToken);
            if (!advResult.Success)
                return advResult;
        }

        return stdResult;
    }

    /// <summary>
    /// Unblocks the specified target, tearing down both secure and standard layers for that target,
    /// while leaving global Advanced Protection and other targets unaffected.
    /// </summary>
    public Task<OperationResult> UnblockDeviceAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default) =>
        DisableStandardProtectionAsync(target, cancellationToken);

    /// <summary>
    /// Toggles global Advanced Protection without altering whether devices are blocked or unblocked.
    /// If enabled: reinforces currently blocked devices with the advanced layer.
    /// If disabled: strips the advanced layer from protected devices without unblocking them.
    /// Idempotent: repeated calls produce consistent state without redundant mutations.
    /// </summary>
    public async Task<OperationResult> SetAdvancedProtectionAsync(
        bool enable,
        CancellationToken cancellationToken = default)
    {
        var desired = _stateStore.Load();
        desired.AdvancedProtectionEnabled = enable;
        _stateStore.Save(desired);

        var currentState = await _protectionProvider.GetProtectionStateAsync(cancellationToken);

        if (enable)
        {
            var cameraNeedsSecure = currentState.Camera.IsProtected &&
                                    currentState.Camera.SecureState != SecureProtectionState.Active;
            var micNeedsSecure = currentState.Microphone.IsProtected &&
                                 currentState.Microphone.SecureState != SecureProtectionState.Active;

            BlockTarget? targetToSecure = (cameraNeedsSecure, micNeedsSecure) switch
            {
                (true, true) => BlockTarget.Both,
                (true, false) => BlockTarget.Camera,
                (false, true) => BlockTarget.Microphone,
                (false, false) => null
            };

            if (targetToSecure.HasValue)
            {
                var result = await EnableSecureProtectionAsync(targetToSecure.Value, cancellationToken);
                if (!result.Success)
                {
                    var reverted = _stateStore.Load();
                    reverted.AdvancedProtectionEnabled = false;
                    _stateStore.Save(reverted);
                    await PublishVerifiedStateAsync(cancellationToken);
                    return result;
                }
            }
        }
        else
        {
            var cameraHasSecure = currentState.Camera.SecureState == SecureProtectionState.Active;
            var micHasSecure = currentState.Microphone.SecureState == SecureProtectionState.Active;

            BlockTarget? targetToDisable = (cameraHasSecure, micHasSecure) switch
            {
                (true, true) => BlockTarget.Both,
                (true, false) => BlockTarget.Camera,
                (false, true) => BlockTarget.Microphone,
                (false, false) => null
            };

            if (targetToDisable.HasValue)
            {
                var result = await DisableSecureProtectionAsync(targetToDisable.Value, cancellationToken);
                if (!result.Success)
                {
                    await PublishVerifiedStateAsync(cancellationToken);
                    return result;
                }
            }
        }

        await PublishVerifiedStateAsync(cancellationToken);
        return OperationResult.Ok();
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
            var result = await _privacySessions.RecoverUnfinishedSessionAsync(cancellationToken);
            if (!_allowUntrackedMutations &&
                result.SafeToExit &&
                !result.HadPersistedSession &&
                _legacyDetector.HasLegacyEvidence())
            {
                return LegacyUntrackedRecoveryResult();
            }
            // DesiredState represents durable user intent across restarts and shutdowns;
            // startup recovery restores any unfinished runtime mutations to a safe baseline
            // without clearing what the user chose to protect.
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Reconciles the persistent DesiredState with the actual operating-system protection state.
    /// Reapplies only the protections that DesiredState requires, without treating previous
    /// PrivacySessions as desired state, never touching inactive scopes, independently reconciling
    /// Camera and Microphone, and establishing a new PrivacySession for newly applied runtime mutations.
    /// Aborts safely without mutating platform state if startup recovery was unsafe or incomplete.
    /// </summary>
    public async Task<OperationResult> ReconcileDesiredProtectionAsync(
        PrivacyRecoveryResult? recoveryResult = null,
        CancellationToken cancellationToken = default)
    {
        if (IsShutdownStarted)
            return OperationResult.Fail("PrivGvard is shutting down; startup reconciliation was not performed.");

        if (recoveryResult is { SafeToExit: false } or { ConflictCount: > 0 })
        {
            var reason = recoveryResult.ErrorMessage ??
                "A previous privacy session recovery was incomplete or conflicted; startup reconciliation was aborted for safety.";
            Log.Warning("Startup reconciliation aborted because recovery was incomplete: {Reason}", reason);
            return OperationResult.Fail(reason);
        }

        try
        {
            var desired = _stateStore.Load();
            var actual = await _protectionProvider.GetProtectionStateAsync(cancellationToken);

            var cameraNeedsStandard = desired.CameraStandard == StandardProtectionState.Active &&
                                      actual.Camera.StandardState != StandardProtectionState.Active;

            var micNeedsStandard = desired.MicrophoneStandard == StandardProtectionState.Active &&
                                   actual.Microphone.StandardState != StandardProtectionState.Active;

            var cameraNeedsSecure = desired.CameraStandard == StandardProtectionState.Active &&
                                    (desired.AdvancedProtectionEnabled || desired.CameraSecure == SecureProtectionState.Active) &&
                                    actual.Camera.SecureState != SecureProtectionState.Active;

            var micNeedsSecure = desired.MicrophoneStandard == StandardProtectionState.Active &&
                                 (desired.AdvancedProtectionEnabled || desired.MicrophoneSecure == SecureProtectionState.Active) &&
                                 actual.Microphone.SecureState != SecureProtectionState.Active;

            if (!cameraNeedsStandard && !micNeedsStandard && !cameraNeedsSecure && !micNeedsSecure)
            {
                Log.Information("Startup reconciliation: OS protection already matches desired state; no mutations required");
                await PublishVerifiedStateAsync(cancellationToken);
                return OperationResult.Ok();
            }

            var failures = new List<string>();
            var details = new List<DeviceOperationDetail>();

            if (cameraNeedsStandard)
            {
                Log.Information("Startup reconciliation: Applying Camera standard protection");
                var camResult = await EnableStandardProtectionAsync(BlockTarget.Camera, cancellationToken);
                if (!camResult.Success)
                {
                    failures.Add($"Camera standard protection failed: {camResult.ErrorMessage}");
                }
                else if (camResult.Details != null)
                {
                    details.AddRange(camResult.Details);
                }
            }

            var cameraStandardActive = cameraNeedsStandard
                ? !failures.Any(f => f.Contains("Camera standard protection"))
                : actual.Camera.StandardState == StandardProtectionState.Active;

            if (cameraNeedsSecure && cameraStandardActive)
            {
                Log.Information("Startup reconciliation: Applying Camera secure protection");
                var camSecResult = await EnableSecureProtectionAsync(BlockTarget.Camera, cancellationToken);
                if (!camSecResult.Success)
                {
                    failures.Add($"Camera secure protection failed: {camSecResult.ErrorMessage}");
                }
                else if (camSecResult.Details != null)
                {
                    details.AddRange(camSecResult.Details);
                }
            }

            if (micNeedsStandard)
            {
                Log.Information("Startup reconciliation: Applying Microphone standard protection");
                var micResult = await EnableStandardProtectionAsync(BlockTarget.Microphone, cancellationToken);
                if (!micResult.Success)
                {
                    failures.Add($"Microphone standard protection failed: {micResult.ErrorMessage}");
                }
                else if (micResult.Details != null)
                {
                    details.AddRange(micResult.Details);
                }
            }

            var micStandardActive = micNeedsStandard
                ? !failures.Any(f => f.Contains("Microphone standard protection"))
                : actual.Microphone.StandardState == StandardProtectionState.Active;

            if (micNeedsSecure && micStandardActive)
            {
                Log.Information("Startup reconciliation: Applying Microphone secure protection");
                var micSecResult = await EnableSecureProtectionAsync(BlockTarget.Microphone, cancellationToken);
                if (!micSecResult.Success)
                {
                    failures.Add($"Microphone secure protection failed: {micSecResult.ErrorMessage}");
                }
                else if (micSecResult.Details != null)
                {
                    details.AddRange(micSecResult.Details);
                }
            }

            await PublishVerifiedStateAsync(cancellationToken);

            if (failures.Count > 0)
            {
                return OperationResult.Fail(string.Join("; ", failures), details);
            }

            return OperationResult.Ok(details);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during startup reconciliation");
            return OperationResult.Fail($"Startup reconciliation encountered an error: {ex.Message}");
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

            if (!_allowUntrackedMutations &&
                result.SafeToExit &&
                !result.HadPersistedSession &&
                _legacyDetector.HasLegacyEvidence())
                result = LegacyUntrackedRecoveryResult();

            // Invariant: Restoring the runtime OS state during shutdown MUST NOT implicitly mean
            // that the user disabled protection. DesiredState survives normal application shutdown
            // and Windows shutdown. Only explicit user disable operations modify DesiredState.
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal void SignalShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1);

    public void AbortShutdown() => Interlocked.Exchange(ref _shutdownStarted, 0);

    public async Task<FullProtectionState> GetCurrentStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await _protectionProvider.GetProtectionStateAsync(cancellationToken);
        return state with { AdvancedProtectionEnabled = IsAdvancedProtectionEnabled };
    }

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
                _legacyDetector.HasLegacyEvidence())
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
            return _legacyDetector.HasLegacyEvidence()
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
            return OperationResult.Fail("PrivGvard is shutting down; new protection changes are not accepted.");

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsShutdownStarted)
                return OperationResult.Fail("PrivGvard is shutting down; new protection changes are not accepted.");
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
        var state = await GetCurrentStateAsync(cancellationToken);
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
