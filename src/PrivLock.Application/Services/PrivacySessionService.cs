using System.Security.Cryptography;
using System.Text;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;
using Serilog.Context;

namespace PrivLock.Application.Services;

public sealed record PrivacyBlockPreparation(
    Guid SessionId,
    string OperationId,
    bool TrackingEnabled,
    IReadOnlyList<string> ResourceIds,
    IReadOnlyList<string> BaselineOwnedResourceIds,
    IReadOnlyList<PrivacyResourceState> PreparedResources)
{
    public static PrivacyBlockPreparation Unsupported(string operationId) =>
        new(Guid.Empty, operationId, false, [], [], []);
}

/// <summary>
/// Owns the persistent write-ahead journal and the exact compare-and-restore algorithm.
/// ProtectionService serializes the complete prepare/mutate/record transaction around this service.
/// </summary>
public sealed class PrivacySessionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PrivacySessionService>();

    private readonly IPrivacySessionStore _sessionStore;
    private readonly IPrivacySessionPlatformAdapter _platform;
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private int _processMutationQuiescenceRequired;

    public bool SupportsPersistentRecovery => _platform.SupportsPersistentRecovery;

    public PrivacySessionService(
        IPrivacySessionStore sessionStore,
        IPrivacySessionPlatformAdapter platform)
    {
        _sessionStore = sessionStore;
        _platform = platform;
    }

    public async Task<PrivacyBlockPreparation> PrepareBlockAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_platform.SupportsPersistentRecovery)
        {
            return PrivacyBlockPreparation.Unsupported(operationId);
        }

        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var session = _sessionStore.Load();
            if (session is { IsActive: true } &&
                (Volatile.Read(ref _processMutationQuiescenceRequired) != 0 ||
                 session.Resources.Any(resource => resource.ExecutionMayStillBeInFlight)))
            {
                var quiescence = await EnsureMutationQuiescenceAsync(cancellationToken);
                if (!quiescence.Success)
                {
                    throw new InvalidOperationException(
                        quiescence.ErrorMessage ??
                        "A previous native privacy operation may still be running.");
                }

                foreach (var resource in session.Resources.Where(resource => resource.ExecutionMayStillBeInFlight))
                    resource.ExecutionMayStillBeInFlight = false;
                session.UpdatedAtUtc = MaxTimestamp(now, session.CreatedAtUtc, session.UpdatedAtUtc);
                _sessionStore.Save(session);
                throw new InvalidOperationException(
                    "A previous uncertain privacy operation was quiesced; restore it before applying new protection changes.");
            }
            if (session is { IsActive: true } &&
                session.Resources.Any(RequiresRecoveryBeforeNewProtection))
            {
                throw new InvalidOperationException(
                    "An unfinished privacy recovery must be reconciled before applying new protection changes.");
            }
            if (session is null || !session.IsActive)
            {
                session = new PrivacySession
                {
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    OwnerProcessId = Environment.ProcessId,
                    IsActive = true,
                    Status = PrivacySessionStatus.Active
                };
            }

            // A resource that was fully restored earlier in a still-active mixed-target session must
            // be re-captured if the user blocks that scope again.
            session.Resources.RemoveAll(resource =>
                resource.Layer == layer &&
                IncludesTarget(target, resource.Target) &&
                IsTerminal(resource.JournalState));

            var captured = await _platform.CaptureAsync(layer, target, operationId, cancellationToken);
            var operationResourceIds = new List<string>();
            var baselineOwnedResourceIds = new List<string>();

            foreach (var resource in captured)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var existing = session.Resources.FirstOrDefault(candidate =>
                    string.Equals(candidate.ResourceId, resource.ResourceId, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    resource.OperationId = operationId;
                    resource.JournalState = resource.RequiresModification
                        ? PrivacyResourceJournalState.ApplyPending
                        : PrivacyResourceJournalState.Unchanged;
                    resource.ModifiedByPrivLock = false;
                    // A persisted write-ahead intent is not proof that PrivLock performed the
                    // future native write. A crash before/after dispatch must reconcile safely.
                    resource.OwnershipUncertain = resource.RequiresModification;
                    resource.ExecutionMayStillBeInFlight = false;
                    resource.LastUpdatedAtUtc = MaxTimestamp(now, resource.CapturedAtUtc, resource.LastUpdatedAtUtc);
                    session.Resources.Add(resource);
                    operationResourceIds.Add(resource.ResourceId);
                }
                else
                {
                    // Preserve the first original value for an outstanding owned change.
                    if (HasConfirmedOwnership(existing, existing.JournalState))
                        baselineOwnedResourceIds.Add(existing.ResourceId);
                    existing.OperationId = operationId;
                    existing.LastUpdatedAtUtc = MaxTimestamp(now, existing.CapturedAtUtc, existing.LastUpdatedAtUtc);
                    if (existing.RequiresModification && existing.JournalState == PrivacyResourceJournalState.Captured)
                    {
                        existing.JournalState = PrivacyResourceJournalState.ApplyPending;
                    }
                    operationResourceIds.Add(existing.ResourceId);
                }
            }

            session.LastOperationId = operationId;
            session.IsActive = true;
            session.WasRestored = false;
            session.Status = PrivacySessionStatus.Active;
            session.UpdatedAtUtc = MaxTimestamp(
                now,
                session.CreatedAtUtc,
                session.UpdatedAtUtc,
                session.Resources.Select(resource => resource.LastUpdatedAtUtc).DefaultIfEmpty().Max());

            // This commit is the safety boundary: the caller must not mutate the OS if it fails.
            _sessionStore.Save(session);

            Log.Information(
                "Privacy snapshot committed: SessionId={SessionId}, Layer={Layer}, Target={Target}, ResourceCount={ResourceCount}",
                session.SessionId,
                layer,
                target,
                operationResourceIds.Count);

            return new PrivacyBlockPreparation(
                session.SessionId,
                operationId,
                TrackingEnabled: true,
                operationResourceIds,
                baselineOwnedResourceIds,
                SelectResources(session, operationResourceIds).ToList());
        }
        finally
        {
            _journalGate.Release();
        }
    }

    /// <summary>
    /// Ensures no relevant resource changed between capture and dispatch. Native APIs do not offer
    /// a cross-resource transaction, so the platform repeats this comparison again during restore.
    /// </summary>
    public async Task<OperationResult> ValidatePreparedResourcesAsync(
        PrivacyBlockPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        if (!preparation.TrackingEnabled)
        {
            return OperationResult.Ok();
        }

        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            var session = LoadMatchingSession(preparation);
            var preparedResources = SelectResources(session, preparation.ResourceIds).ToList();
            if (!preparedResources.Any(resource => resource.RequiresModification) &&
                preparation.BaselineOwnedResourceIds.Count == 0)
            {
                const string noOwnershipError =
                    "The requested resources were already protected before this PrivLock operation; external state was not adopted as PrivLock-owned.";
                AbandonUndispatchedResources(session, preparation, noOwnershipError);
                return OperationResult.Fail(noOwnershipError);
            }

            foreach (var resource in preparedResources)
            {
                if (!resource.RequiresModification)
                {
                    continue;
                }

                var observation = await _platform.ObserveAsync(resource, cancellationToken);
                var isValidOriginal = observation.Kind == PrivacyResourceObservationKind.MatchesOriginal;
                var isValidPreviouslyApplied =
                    resource.JournalState == PrivacyResourceJournalState.Applied &&
                    observation.Kind == PrivacyResourceObservationKind.MatchesProtected;
                if (!isValidOriginal && !isValidPreviouslyApplied)
                {
                    var safeId = ToSafeLogId(resource.ResourceId);
                    Log.Warning(
                        "Prepared resource changed before block dispatch: Resource={Resource}, Observation={Observation}",
                        safeId,
                        observation.Kind);
                    var error =
                        $"A protected resource changed after its snapshot was captured ({observation.Kind}); no blocking changes were applied.";
                    AbandonUndispatchedResources(session, preparation, error);
                    return OperationResult.Fail(error);
                }
            }

            return OperationResult.Ok();
        }
        finally
        {
            _journalGate.Release();
        }
    }

    /// <summary>
    /// Reconciles each captured resource with actual platform state and persists the result after
    /// every entry, so partial operations and crashes remain recoverable.
    /// </summary>
    public async Task<OperationResult> RecordBlockOutcomeAsync(
        PrivacyBlockPreparation preparation,
        OperationResult platformResult,
        CancellationToken cancellationToken = default)
    {
        if (!preparation.TrackingEnabled)
        {
            return platformResult;
        }

        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            var session = LoadMatchingSession(preparation);
            var failures = new List<string>();

            foreach (var resource in SelectResources(session, preparation.ResourceIds))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!resource.RequiresModification)
                {
                    resource.JournalState = PrivacyResourceJournalState.Unchanged;
                    resource.ModifiedByPrivLock = false;
                    PersistResourceProgress(session, resource);
                    continue;
                }

                var resourceDetail = GetResourceDetail(platformResult, resource);
                var wasPreviouslyOwned = resource.JournalState == PrivacyResourceJournalState.Applied &&
                                         resource.ModifiedByPrivLock &&
                                         !resource.OwnershipUncertain;
                var outcomeUncertain = resourceDetail?.OutcomeUncertain == true ||
                                       resourceDetail == null && platformResult.OutcomeUncertain;
                var executionStillInFlight = resourceDetail?.ExecutionStillInFlight == true ||
                                             resourceDetail == null && platformResult.ExecutionStillInFlight;
                var operationConfirmed = resourceDetail?.Success ??
                                         (platformResult.Success && !platformResult.OutcomeUncertain);

                if (executionStillInFlight)
                {
                    RequireMutationQuiescence();
                    resource.ExecutionMayStillBeInFlight = true;
                    if (wasPreviouslyOwned)
                    {
                        // An idempotent re-apply cannot invalidate ownership that was already
                        // durably established. Preserve it while still enforcing the actor barrier.
                        resource.ModifiedByPrivLock = true;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.Applied;
                    }
                    else
                    {
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.JournalState = PrivacyResourceJournalState.ApplyPending;
                    }
                    resource.LastError = resourceDetail?.ErrorMessage ?? platformResult.ErrorMessage ??
                        "The native actor could not be proven stopped; reconciliation is deferred until startup quiescence.";
                    failures.Add(resource.LastError);
                    PersistResourceProgress(session, resource);
                    continue;
                }

                var observation = await _platform.ObserveAsync(resource, cancellationToken);

                // A secure worker may have created a machine-protected claim even when its final
                // response says failure (for example, claim cleanup failed or the IPC response was
                // lost). If the resource is already original or externally changed, retire/prove
                // absence of that claim before terminalizing the user-writable WAL row.
                var secureClaimMayExist =
                    resource.Layer == ProtectionLayer.Secure &&
                    observation.Kind is (PrivacyResourceObservationKind.MatchesOriginal or
                        PrivacyResourceObservationKind.Conflict) &&
                    (wasPreviouslyOwned || operationConfirmed || outcomeUncertain);
                if (secureClaimMayExist)
                {
                    var claimRetirement = await EnsureSecureClaimRetiredForTerminalObservationAsync(
                        resource,
                        cancellationToken);
                    if (!claimRetirement.Success)
                    {
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                        resource.LastError = claimRetirement.ErrorMessage ??
                            "Machine ownership could not be safely retired after block reconciliation.";
                        failures.Add(resource.LastError);
                        PersistResourceProgress(session, resource);
                        continue;
                    }
                }

                switch (observation.Kind)
                {
                    case PrivacyResourceObservationKind.MatchesProtected when wasPreviouslyOwned || operationConfirmed:
                        resource.ModifiedByPrivLock = true;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.Applied;
                        resource.LastError = null;
                        break;

                    case PrivacyResourceObservationKind.MatchesProtected when outcomeUncertain:
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.JournalState = PrivacyResourceJournalState.ApplyPending;
                        resource.LastError = resourceDetail?.ErrorMessage ?? platformResult.ErrorMessage ??
                            "The protected state was observed, but ownership is uncertain because the native completion response was lost.";
                        failures.Add(resource.LastError);
                        break;

                    case PrivacyResourceObservationKind.MatchesProtected:
                        // A failed compare-before-apply accompanied by a protected value means an
                        // external actor won the race. Never adopt that value as PrivLock-owned.
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.Conflict;
                        resource.LastError = resourceDetail?.ErrorMessage ?? platformResult.ErrorMessage ??
                            "The resource reached the protected state without a confirmed PrivLock mutation; it was preserved as externally owned.";
                        failures.Add(resource.LastError);
                        break;

                    case PrivacyResourceObservationKind.MatchesOriginal:
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.ApplyFailed;
                        resource.LastError = resourceDetail?.ErrorMessage ?? platformResult.ErrorMessage ?? "The protected value was not applied.";
                        failures.Add(resource.LastError);
                        break;

                    case PrivacyResourceObservationKind.Missing when wasPreviouslyOwned || operationConfirmed:
                        // The OS reported success before the device disappeared. Keep ownership so a
                        // later startup/hotplug can compare and restore the same stable Instance ID.
                        resource.ModifiedByPrivLock = true;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.Applied;
                        resource.LastError = "Resource disappeared after the block operation was confirmed.";
                        break;

                    case PrivacyResourceObservationKind.Missing when outcomeUncertain:
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.JournalState = PrivacyResourceJournalState.Missing;
                        resource.LastError = "Resource disappeared and the native mutation outcome is uncertain.";
                        failures.Add(resource.LastError);
                        break;

                    case PrivacyResourceObservationKind.Missing:
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        // The write-ahead entry proves that dispatch was attempted, but a missing
                        // resource makes the outcome ambiguous. Keep it retryable so a future
                        // startup/hotplug pass can compare the same stable resource identity.
                        resource.JournalState = PrivacyResourceJournalState.Missing;
                        resource.LastError = "Resource disappeared before its block operation could be reconciled.";
                        failures.Add(resource.LastError);
                        break;

                    case PrivacyResourceObservationKind.Conflict:
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = false;
                        resource.JournalState = PrivacyResourceJournalState.Conflict;
                        resource.LastError = observation.ErrorMessage ??
                            "State changed externally while the block operation was being reconciled; it was preserved.";
                        failures.Add(resource.LastError);
                        break;

                    default:
                        resource.ModifiedByPrivLock = wasPreviouslyOwned || operationConfirmed;
                        resource.OwnershipUncertain = outcomeUncertain;
                        // A confirmed native response establishes ownership even if this extra
                        // observation failed. Otherwise retain an ambiguous WAL intent.
                        resource.JournalState = wasPreviouslyOwned || operationConfirmed
                            ? PrivacyResourceJournalState.Applied
                            : PrivacyResourceJournalState.ApplyPending;
                        if (!wasPreviouslyOwned && !operationConfirmed)
                        {
                            resource.ModifiedByPrivLock = false;
                            resource.OwnershipUncertain = true;
                        }
                        resource.LastError = observation.ErrorMessage ??
                            $"Could not reconcile the block result: {observation.Kind}.";
                        failures.Add(resource.LastError);
                        break;
                }

                PersistResourceProgress(session, resource);

                Log.Information(
                    "Block journal updated: SessionId={SessionId}, Resource={Resource}, State={State}, ModifiedByPrivLock={Modified}",
                    session.SessionId,
                    ToSafeLogId(resource.ResourceId),
                    resource.JournalState,
                    resource.ModifiedByPrivLock);
            }

            if (!session.Resources.Any(IsOutstandingOwnedChange))
            {
                RecalculateSessionStatus(session);
                _sessionStore.Save(session);
            }

            if (!platformResult.Success)
            {
                return platformResult;
            }

            return failures.Count == 0
                ? OperationResult.Ok(platformResult.Details)
                : OperationResult.Fail(string.Join("; ", failures.Distinct()), platformResult.Details);
        }
        finally
        {
            _journalGate.Release();
        }
    }

    /// <summary>
    /// Emergency in-process compensation for the narrow case where the native apply returned a
    /// confirmed result but its ownership checkpoint could not be persisted. The durable WAL still
    /// contains the original snapshot, while the live result is the only trustworthy proof that
    /// this process performed the write. Compensate immediately rather than letting a later
    /// ApplyPending reconciliation strand a PrivLock-owned protected value as an external conflict.
    /// </summary>
    public async Task<PrivacyRecoveryResult> RollbackConfirmedApplyAfterCheckpointFailureAsync(
        PrivacyBlockPreparation preparation,
        OperationResult platformResult,
        CancellationToken cancellationToken = default)
    {
        if (!preparation.TrackingEnabled)
            return PrivacyRecoveryResult.Unsupported();

        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            var baseline = new HashSet<string>(
                preparation.BaselineOwnedResourceIds,
                StringComparer.OrdinalIgnoreCase);
            var preparedCandidates = preparation.PreparedResources
                .Where(resource => preparation.ResourceIds.Contains(
                    resource.ResourceId,
                    StringComparer.OrdinalIgnoreCase))
                .Where(resource => !baseline.Contains(resource.ResourceId))
                .Where(resource => resource.RequiresModification)
                .ToList();

            PrivacySession durableSession;
            try
            {
                durableSession = LoadMatchingSession(preparation);
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Emergency rollback could not reload the durable journal; no native compensation was attempted");
                return new PrivacyRecoveryResult
                {
                    HadPersistedSession = true,
                    HadRecoveryWork = preparedCandidates.Count > 0,
                    IsComplete = false,
                    FailedCount = Math.Max(1, preparedCandidates.Count),
                    ErrorMessage =
                        $"The durable journal could not be reloaded before emergency rollback: {ex.Message}"
                };
            }

            var durableById = durableSession.Resources.ToDictionary(
                resource => resource.ResourceId,
                StringComparer.OrdinalIgnoreCase);
            var candidates = new List<PrivacyResourceState>();

            var restoredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoredCount = 0;
            var alreadyRestoredCount = 0;
            var conflictCount = 0;
            var missingCount = 0;
            var failedCount = 0;
            var errors = new List<string>();

            foreach (var prepared in preparedCandidates)
            {
                if (!durableById.TryGetValue(prepared.ResourceId, out var durable))
                {
                    failedCount++;
                    errors.Add(
                        $"The durable journal no longer contains resource {ToSafeLogId(prepared.ResourceId)}; it was not compensated.");
                    continue;
                }

                if (HasConfirmedOwnership(durable, durable.JournalState))
                {
                    // This resource already crossed the ownership checkpoint before another
                    // resource's save failed. Restoring it while storage is unhealthy could leave
                    // a durable Applied row pointing at an original value and later overwrite an
                    // external same-value protection. Keep it protected for normal WAL recovery.
                    failedCount++;
                    errors.Add(
                        $"Resource {ToSafeLogId(durable.ResourceId)} is durably owned and remains pending normal recovery.");
                    continue;
                }

                if (durable.JournalState == PrivacyResourceJournalState.ApplyPending &&
                    !durable.ModifiedByPrivLock &&
                    durable.OwnershipUncertain)
                {
                    candidates.Add(durable);
                    continue;
                }

                if (durable.JournalState is PrivacyResourceJournalState.ApplyFailed or
                    PrivacyResourceJournalState.Conflict or
                    PrivacyResourceJournalState.Unchanged or
                    PrivacyResourceJournalState.Restored)
                {
                    continue;
                }

                failedCount++;
                errors.Add(
                    $"Resource {ToSafeLogId(durable.ResourceId)} has journal state {durable.JournalState} and was not safe to compensate.");
            }

            foreach (var resource in candidates)
            {
                var detail = GetResourceDetail(platformResult, resource);
                var confirmed = detail is not null
                    ? detail.Success && !detail.OutcomeUncertain && !detail.ExecutionStillInFlight
                    : platformResult.Success &&
                      !platformResult.OutcomeUncertain &&
                      !platformResult.ExecutionStillInFlight;

                if (!confirmed)
                {
                    conflictCount++;
                    errors.Add(
                        $"No confirmed live ownership proof remained for resource {ToSafeLogId(resource.ResourceId)}.");
                    continue;
                }

                PrivacyResourceObservation observation;
                try
                {
                    observation = await _platform.ObserveAsync(resource, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Emergency rollback could not observe resource {Resource}",
                        ToSafeLogId(resource.ResourceId));
                    failedCount++;
                    errors.Add(ex.Message);
                    continue;
                }

                if (observation.Kind == PrivacyResourceObservationKind.MatchesOriginal)
                {
                    var claimRetirement = await EnsureSecureClaimRetiredForTerminalObservationAsync(
                        resource,
                        CancellationToken.None);
                    if (!claimRetirement.Success)
                    {
                        failedCount++;
                        errors.Add(claimRetirement.ErrorMessage ??
                            "Machine ownership could not be retired before emergency rollback completion.");
                        continue;
                    }
                    alreadyRestoredCount++;
                    restoredIds.Add(resource.ResourceId);
                    continue;
                }

                if (observation.Kind == PrivacyResourceObservationKind.Missing)
                {
                    missingCount++;
                    errors.Add("A confirmed changed resource disappeared before emergency rollback.");
                    continue;
                }

                if (observation.Kind == PrivacyResourceObservationKind.Conflict)
                {
                    var claimRetirement = await EnsureSecureClaimRetiredForTerminalObservationAsync(
                        resource,
                        CancellationToken.None);
                    if (!claimRetirement.Success)
                    {
                        failedCount++;
                        errors.Add(claimRetirement.ErrorMessage ??
                            "Machine ownership could not be retired after an emergency rollback conflict.");
                        continue;
                    }
                    conflictCount++;
                    errors.Add(observation.ErrorMessage ?? "External state replaced a confirmed change before emergency rollback.");
                    continue;
                }

                if (observation.Kind != PrivacyResourceObservationKind.MatchesProtected)
                {
                    failedCount++;
                    errors.Add(observation.ErrorMessage ?? "A confirmed changed resource could not be inspected for emergency rollback.");
                    continue;
                }

                OperationResult restore;
                try
                {
                    restore = await _platform.RestoreOriginalAsync(resource, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Emergency rollback threw for resource {Resource}",
                        ToSafeLogId(resource.ResourceId));
                    restore = OperationResult.Fail(ex.Message, outcomeUncertain: true);
                }

                if (restore.ExecutionStillInFlight)
                {
                    RequireMutationQuiescence();
                    var quiescence = await EnsureMutationQuiescenceAsync(CancellationToken.None);
                    if (!quiescence.Success)
                    {
                        failedCount++;
                        errors.Add(quiescence.ErrorMessage ?? "Emergency rollback actor could not be quiesced.");
                        continue;
                    }
                }

                PrivacyResourceObservation verification;
                try
                {
                    verification = await _platform.ObserveAsync(resource, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Emergency rollback verification failed for resource {Resource}",
                        ToSafeLogId(resource.ResourceId));
                    failedCount++;
                    errors.Add(ex.Message);
                    continue;
                }

                if (verification.Kind == PrivacyResourceObservationKind.MatchesOriginal)
                {
                    var claimRetirement = await EnsureSecureClaimRetiredForTerminalObservationAsync(
                        resource,
                        CancellationToken.None);
                    if (!claimRetirement.Success)
                    {
                        failedCount++;
                        errors.Add(claimRetirement.ErrorMessage ??
                            "Machine ownership could not be retired after emergency restoration.");
                        continue;
                    }
                    restoredCount++;
                    restoredIds.Add(resource.ResourceId);
                }
                else if (verification.Kind == PrivacyResourceObservationKind.Missing)
                {
                    missingCount++;
                    errors.Add("A resource disappeared while emergency rollback was being verified.");
                }
                else if (!restore.Success &&
                         !restore.OutcomeUncertain &&
                         verification.Kind == PrivacyResourceObservationKind.MatchesProtected)
                {
                    failedCount++;
                    errors.Add(restore.ErrorMessage ?? "Emergency rollback failed and the owned protected value remains.");
                }
                else if (verification.Kind == PrivacyResourceObservationKind.Conflict)
                {
                    var claimRetirement = await EnsureSecureClaimRetiredForTerminalObservationAsync(
                        resource,
                        CancellationToken.None);
                    if (!claimRetirement.Success)
                    {
                        failedCount++;
                        errors.Add(claimRetirement.ErrorMessage ??
                            "Machine ownership could not be retired after an emergency restoration conflict.");
                        continue;
                    }
                    conflictCount++;
                    errors.Add(verification.ErrorMessage ?? "External state changed during emergency rollback verification.");
                }
                else
                {
                    failedCount++;
                    errors.Add(verification.ErrorMessage ?? restore.ErrorMessage ??
                        "Emergency rollback outcome could not be verified.");
                }
            }

            // The same storage failure may still be present. This checkpoint is best-effort only:
            // successfully restored resources are already safe, and an old ApplyPending entry will
            // later observe MatchesOriginal without authorizing another native write.
            if (restoredIds.Count > 0)
            {
                try
                {
                    var session = LoadMatchingSession(preparation);
                    foreach (var resource in SelectResources(session, restoredIds.ToList()))
                    {
                        resource.JournalState = PrivacyResourceJournalState.Restored;
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = false;
                        resource.ExecutionMayStillBeInFlight = false;
                        resource.LastError = null;
                        SetMonotonicProgressTimestamp(session, resource);
                    }
                    RecalculateSessionStatus(session);
                    _sessionStore.Save(session);
                }
                catch (Exception ex)
                {
                    Log.Error(ex,
                        "Emergency rollback restored native state but could not checkpoint the terminal journal state");
                }
            }

            return new PrivacyRecoveryResult
            {
                HadPersistedSession = true,
                HadRecoveryWork = preparedCandidates.Count > 0,
                IsComplete = missingCount == 0 && failedCount == 0,
                RestoredCount = restoredCount,
                AlreadyRestoredCount = alreadyRestoredCount,
                ConflictCount = conflictCount,
                MissingCount = missingCount,
                FailedCount = failedCount,
                ErrorMessage = errors.Count == 0 ? null : string.Join("; ", errors.Distinct())
            };
        }
        finally
        {
            _journalGate.Release();
        }
    }

    public Task<PrivacyRecoveryResult> RecoverUnfinishedSessionAsync(
        CancellationToken cancellationToken = default) =>
        RestoreAsync(layer: null, target: null, "StartupRecovery", cancellationToken);

    public Task<PrivacyRecoveryResult> RestoreAsync(
        ProtectionLayer? layer,
        BlockTarget? target,
        string reason,
        CancellationToken cancellationToken = default) =>
        RestoreCoreAsync(layer, target, reason, onlyResourceIds: null, cancellationToken);

    public Task<PrivacyRecoveryResult> RestorePreparedDeltaAsync(
        PrivacyBlockPreparation preparation,
        ProtectionLayer layer,
        BlockTarget target,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var baseline = new HashSet<string>(
            preparation.BaselineOwnedResourceIds,
            StringComparer.OrdinalIgnoreCase);
        var delta = preparation.ResourceIds
            .Where(resourceId => !baseline.Contains(resourceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RestoreCoreAsync(layer, target, reason, delta, cancellationToken);
    }

    private async Task<PrivacyRecoveryResult> RestoreCoreAsync(
        ProtectionLayer? layer,
        BlockTarget? target,
        string reason,
        IReadOnlySet<string>? onlyResourceIds,
        CancellationToken cancellationToken)
    {
        if (!_platform.SupportsPersistentRecovery)
        {
            return PrivacyRecoveryResult.Unsupported();
        }

        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            var session = _sessionStore.Load();
            if (session is null)
            {
                return PrivacyRecoveryResult.NothingToRestore();
            }

            if (!session.IsActive)
            {
                // Completed conflicts are historical diagnostics, not continuing authority over
                // whatever state exists now. Scope callers re-query actual platform state.
                return PrivacyRecoveryResult.NothingToRestore(hadPersistedSession: true);
            }

            using var operationContext = LogContext.PushProperty("SessionId", session.SessionId);
            Log.Information("Restoration started: Reason={Reason}, Layer={Layer}, Target={Target}", reason, layer, target);

            var processBarrierRequired = Volatile.Read(ref _processMutationQuiescenceRequired) != 0;
            var inFlightResources = session.Resources
                .Where(resource => MatchesSelection(resource, layer, target, onlyResourceIds))
                .Where(resource => resource.ExecutionMayStillBeInFlight)
                .ToList();
            if (processBarrierRequired || inFlightResources.Count > 0)
            {
                var quiescence = await EnsureMutationQuiescenceAsync(cancellationToken);
                if (!quiescence.Success)
                {
                    return new PrivacyRecoveryResult
                    {
                        HadPersistedSession = true,
                        HadRecoveryWork = inFlightResources.Count > 0,
                        IsComplete = false,
                        FailedCount = Math.Max(1, inFlightResources.Count),
                        ErrorMessage = quiescence.ErrorMessage ??
                            "A native privacy actor may still be running; recovery was deferred without observing state."
                    };
                }

                foreach (var resource in inFlightResources)
                    resource.ExecutionMayStillBeInFlight = false;
                session.UpdatedAtUtc = MaxTimestamp(
                    DateTimeOffset.UtcNow,
                    session.CreatedAtUtc,
                    session.UpdatedAtUtc);
                _sessionStore.Save(session);
            }

            var restoredCount = 0;
            var alreadyRestoredCount = 0;
            var conflictCount = session.Resources.Count(resource =>
                MatchesSelection(resource, layer, target, onlyResourceIds) &&
                resource.JournalState == PrivacyResourceJournalState.Conflict);
            var missingCount = 0;
            var failedCount = 0;
            var irreducibleAmbiguityCount = 0;

            var selected = session.Resources
                .Where(resource => MatchesSelection(resource, layer, target, onlyResourceIds))
                .Where(resource => resource.RequiresModification)
                .Where(resource => resource.JournalState is not
                    (PrivacyResourceJournalState.Unchanged or
                     PrivacyResourceJournalState.Restored or
                     PrivacyResourceJournalState.Conflict or
                     PrivacyResourceJournalState.ApplyFailed))
                // Reverse the protection dependency order: re-enable secure PnP nodes while
                // policies still deny access, then restore machine policy, endpoint mute, and
                // finally user policy. This keeps devices protected throughout restoration and
                // makes Core Audio endpoints observable again before their mute state is restored.
                .OrderBy(GetRestoreOrder)
                .ToList();
            var hadRecoveryWork = inFlightResources.Count > 0 || selected.Count > 0;

            foreach (var resource in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var priorState = resource.JournalState;
                var hadConfirmedOwnership = HasConfirmedOwnership(resource, priorState);
                var ownershipIsAmbiguous = resource.OwnershipUncertain ||
                                           priorState == PrivacyResourceJournalState.ApplyPending ||
                                           priorState == PrivacyResourceJournalState.RestorePending &&
                                           !hadConfirmedOwnership;
                resource.RestoreAttempts++;
                resource.LastError = null;
                PersistResourceProgress(session, resource);

                if (!hadConfirmedOwnership && !ownershipIsAmbiguous)
                {
                    resource.JournalState = PrivacyResourceJournalState.Conflict;
                    resource.ModifiedByPrivLock = false;
                    resource.OwnershipUncertain = false;
                    resource.LastError =
                        "The journal does not contain confirmed ownership for this resource; current state was preserved.";
                    conflictCount++;
                    PersistResourceProgress(session, resource);
                    continue;
                }

                var observation = await _platform.ObserveAsync(resource, cancellationToken);
                var requiresMachineAttestationCheck =
                    resource.Layer == ProtectionLayer.Secure && observation.Kind is
                        (PrivacyResourceObservationKind.MatchesOriginal or
                         PrivacyResourceObservationKind.Conflict) ||
                    ownershipIsAmbiguous && observation.Kind == PrivacyResourceObservationKind.MatchesProtected;
                if (requiresMachineAttestationCheck)
                {
                    var attestation = await _platform.VerifyOwnershipAttestationAsync(resource, cancellationToken);
                    if (attestation.Kind == PrivacyOwnershipAttestationKind.Confirmed &&
                        observation.Kind == PrivacyResourceObservationKind.MatchesProtected)
                    {
                        // Only a platform-protected proof may upgrade ambiguous user-writable WAL
                        // state into restore authority.
                        hadConfirmedOwnership = true;
                        ownershipIsAmbiguous = false;
                        resource.ModifiedByPrivLock = true;
                        resource.OwnershipUncertain = false;
                    }
                    else if (attestation.Kind == PrivacyOwnershipAttestationKind.Confirmed &&
                             observation.Kind != PrivacyResourceObservationKind.MatchesProtected)
                    {
                        resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.LastError =
                            "Machine ownership remained active while the resource appeared original; recovery remains retryable.";
                        failedCount++;
                        PersistResourceProgress(session, resource);
                        continue;
                    }
                    else if (attestation.Kind == PrivacyOwnershipAttestationKind.Error)
                    {
                        resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.LastError = attestation.ErrorMessage ??
                            "Machine-protected ownership could not be verified; recovery remains retryable.";
                        failedCount++;
                        PersistResourceProgress(session, resource);
                        continue;
                    }
                    else if (CanRecoverFromDurableUserScopedIntent(resource, priorState))
                    {
                        // HKCU policy and Core Audio mute are user-scoped mutations. Their durable
                        // pre-dispatch intent is sufficient authority to run the adapter's atomic
                        // compare-and-restore; unlike HKLM/PnP, this cannot elevate privilege.
                        // An external actor writing the exact protected value inside the crash gap
                        // is inherently indistinguishable, so count and log that narrow ambiguity.
                        hadConfirmedOwnership = true;
                        ownershipIsAmbiguous = false;
                        resource.ModifiedByPrivLock = true;
                        resource.OwnershipUncertain = false;
                        irreducibleAmbiguityCount++;
                        Log.Warning(
                            "Crash-gap recovery authorized by durable user-scoped intent: Resource={Resource}, PriorState={PriorState}, Classification=IrreducibleSameValueAmbiguity",
                            ToSafeLogId(resource.ResourceId),
                            priorState);
                    }
                }
                if (ownershipIsAmbiguous)
                {
                    resource.ModifiedByPrivLock = false;
                    resource.OwnershipUncertain = true;
                    switch (observation.Kind)
                    {
                        case PrivacyResourceObservationKind.MatchesOriginal:
                            resource.JournalState = PrivacyResourceJournalState.Restored;
                            resource.ModifiedByPrivLock = false;
                            resource.OwnershipUncertain = false;
                            resource.ExecutionMayStillBeInFlight = false;
                            resource.LastError = null;
                            alreadyRestoredCount++;
                            break;

                        case PrivacyResourceObservationKind.Missing:
                            resource.JournalState = PrivacyResourceJournalState.Missing;
                            resource.LastError = "Resource is missing and ownership of the prior mutation remains uncertain.";
                            missingCount++;
                            break;

                        case PrivacyResourceObservationKind.Error:
                            resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                            resource.LastError = observation.ErrorMessage ??
                                "Could not inspect a resource whose mutation ownership is uncertain.";
                            failedCount++;
                            break;

                        default:
                            resource.JournalState = PrivacyResourceJournalState.Conflict;
                            resource.ModifiedByPrivLock = false;
                            resource.OwnershipUncertain = false;
                            resource.LastError =
                                "Mutation ownership is uncertain; the current non-original value was preserved instead of being overwritten.";
                            conflictCount++;
                            break;
                    }

                    PersistResourceProgress(session, resource);
                    continue;
                }

                switch (observation.Kind)
                {
                    case PrivacyResourceObservationKind.MatchesOriginal:
                        resource.JournalState = PrivacyResourceJournalState.Restored;
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = false;
                        resource.LastError = null;
                        alreadyRestoredCount++;
                        break;

                    case PrivacyResourceObservationKind.MatchesProtected:
                    {
                        // Persist ambiguity before native dispatch. A crash can then never turn a
                        // merely protected value into authority for a blind second restore.
                        resource.JournalState = PrivacyResourceJournalState.RestorePending;
                        resource.ModifiedByPrivLock = false;
                        resource.OwnershipUncertain = true;
                        resource.ExecutionMayStillBeInFlight = false;
                        PersistResourceProgress(session, resource);

                        OperationResult restoreResult;
                        try
                        {
                            // Once RestorePending is durable, caller cancellation must not create a
                            // deterministic pre-dispatch ambiguity. Platform implementations are
                            // internally bounded and return explicit outcome/quiescence metadata.
                            restoreResult = await _platform.RestoreOriginalAsync(
                                resource,
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Platform restore threw after RestorePending checkpoint: Resource={Resource}",
                                ToSafeLogId(resource.ResourceId));
                            restoreResult = OperationResult.Fail(ex.Message, outcomeUncertain: true);
                        }
                        if (!restoreResult.Success)
                        {
                            if (restoreResult.ExecutionStillInFlight)
                            {
                                RequireMutationQuiescence();
                                resource.ExecutionMayStillBeInFlight = true;
                                resource.ModifiedByPrivLock = false;
                                resource.OwnershipUncertain = true;
                                resource.JournalState = PrivacyResourceJournalState.RestorePending;
                            }
                            else if (restoreResult.OutcomeUncertain)
                            {
                                // The restore write may have committed. Relinquish definite
                                // ownership so a later pass compares but never blindly rewrites.
                                resource.ModifiedByPrivLock = false;
                                resource.OwnershipUncertain = true;
                                resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                            }
                            else
                            {
                                // A non-uncertain failure is a contractual guarantee that the
                                // platform performed no mutation; reclaim the prior ownership.
                                resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                                resource.ModifiedByPrivLock = true;
                                resource.OwnershipUncertain = false;
                            }
                            resource.LastError = restoreResult.ErrorMessage;
                            failedCount++;
                            break;
                        }

                        var verification = await _platform.ObserveAsync(resource, CancellationToken.None);
                        if (verification.Kind == PrivacyResourceObservationKind.MatchesOriginal)
                        {
                            resource.JournalState = PrivacyResourceJournalState.Restored;
                            resource.ModifiedByPrivLock = false;
                            resource.OwnershipUncertain = false;
                            resource.LastError = null;
                            restoredCount++;
                        }
                        else
                        {
                            // Every platform restore contract verifies the original value before
                            // returning success. A different second observation is therefore a
                            // post-restore external change (or disappearance), never permission to
                            // overwrite the protected value again.
                            resource.JournalState = PrivacyResourceJournalState.Conflict;
                            resource.ModifiedByPrivLock = false;
                            resource.OwnershipUncertain = false;
                            resource.LastError = verification.ErrorMessage ??
                                $"State changed or became unavailable after a verified restoration ({verification.Kind}); PrivLock ownership was relinquished.";
                            conflictCount++;
                        }
                        break;
                    }

                    case PrivacyResourceObservationKind.Missing:
                        resource.JournalState = PrivacyResourceJournalState.Missing;
                        resource.LastError = "Resource is not currently present; restoration remains pending.";
                        missingCount++;
                        break;

                    case PrivacyResourceObservationKind.Conflict:
                        resource.JournalState = PrivacyResourceJournalState.Conflict;
                        resource.ModifiedByPrivLock = false;
                        resource.LastError = observation.ErrorMessage ??
                            "Current state differs from both original and PrivLock-applied state; external state was preserved.";
                        conflictCount++;
                        break;

                    default:
                        resource.JournalState = PrivacyResourceJournalState.RestoreFailed;
                        resource.LastError = observation.ErrorMessage ?? "Could not inspect current resource state.";
                        failedCount++;
                        break;
                }

                PersistResourceProgress(session, resource);
                Log.Information(
                    "Restore journal updated: Resource={Resource}, State={State}, Attempts={Attempts}",
                    ToSafeLogId(resource.ResourceId),
                    resource.JournalState,
                    resource.RestoreAttempts);
            }

            RecalculateSessionStatus(session);
            _sessionStore.Save(session);

            var completeForScope = missingCount == 0 && failedCount == 0;
            Log.Information(
                "Restoration finished: Complete={Complete}, Restored={Restored}, AlreadyOriginal={Already}, Conflicts={Conflicts}, IrreducibleAmbiguities={IrreducibleAmbiguities}, Missing={Missing}, Failed={Failed}",
                completeForScope,
                restoredCount,
                alreadyRestoredCount,
                conflictCount,
                irreducibleAmbiguityCount,
                missingCount,
                failedCount);

            return new PrivacyRecoveryResult
            {
                HadPersistedSession = true,
                HadRecoveryWork = hadRecoveryWork,
                IsComplete = completeForScope,
                RestoredCount = restoredCount,
                AlreadyRestoredCount = alreadyRestoredCount,
                ConflictCount = conflictCount,
                IrreducibleAmbiguityCount = irreducibleAmbiguityCount,
                MissingCount = missingCount,
                FailedCount = failedCount,
                ErrorMessage = BuildRecoveryMessage(
                    conflictCount,
                    irreducibleAmbiguityCount,
                    missingCount,
                    failedCount)
            };
        }
        finally
        {
            CompletePlatformPass();
            _journalGate.Release();
        }
    }

    public object GetDiagnosticSummary()
    {
        try
        {
            var session = _sessionStore.Load();
            return session is null
                ? new { HasSession = false }
                : new
                {
                    HasSession = true,
                    session.SessionId,
                    session.SchemaVersion,
                    session.Status,
                    session.IsActive,
                    session.WasRestored,
                    ResourceCount = session.Resources.Count,
                    OutstandingCount = session.Resources.Count(IsOutstandingOwnedChange),
                    InFlightCount = session.Resources.Count(resource => resource.ExecutionMayStillBeInFlight),
                    ConflictCount = session.Resources.Count(resource => resource.JournalState == PrivacyResourceJournalState.Conflict)
                };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build privacy session diagnostic summary");
            return new { HasSession = true, JournalReadable = false, ErrorType = ex.GetType().Name };
        }
    }

    /// <summary>
    /// Records an in-memory barrier before attempting any journal checkpoint. This closes the
    /// double-failure window where both the privileged response and the subsequent disk save fail.
    /// </summary>
    public void RequireMutationQuiescence() =>
        Interlocked.Exchange(ref _processMutationQuiescenceRequired, 1);

    public async Task<OperationResult> EnsureMutationQuiescenceAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _platform.EnsureMutationQuiescenceAsync(cancellationToken);
        if (result.Success)
            Interlocked.Exchange(ref _processMutationQuiescenceRequired, 0);
        return result;
    }

    public void CompletePlatformPass()
    {
        try
        {
            _platform.CompleteRecoveryPass();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to release platform privilege after privacy operation");
        }
    }

    private async Task<OperationResult> EnsureSecureClaimRetiredForTerminalObservationAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken)
    {
        if (resource.Layer != ProtectionLayer.Secure)
            return OperationResult.Ok();

        var attestation = await _platform.VerifyOwnershipAttestationAsync(resource, cancellationToken);
        return attestation.Kind == PrivacyOwnershipAttestationKind.NotConfirmed
            ? OperationResult.Ok()
            : OperationResult.Fail(
                attestation.ErrorMessage ??
                "Machine ownership could not be safely retired before terminalizing recovery state.");
    }

    private PrivacySession LoadMatchingSession(PrivacyBlockPreparation preparation)
    {
        var session = _sessionStore.Load()
            ?? throw new InvalidOperationException("The committed privacy session disappeared before the operation completed.");

        if (session.SessionId != preparation.SessionId || !session.IsActive)
        {
            throw new InvalidOperationException("The active privacy session no longer matches the prepared operation.");
        }

        return session;
    }

    private void PersistResourceProgress(PrivacySession session, PrivacyResourceState resource)
    {
        SetMonotonicProgressTimestamp(session, resource);
        _sessionStore.Save(session);
    }

    private static void SetMonotonicProgressTimestamp(
        PrivacySession session,
        PrivacyResourceState resource)
    {
        var now = DateTimeOffset.UtcNow;
        resource.LastUpdatedAtUtc = MaxTimestamp(
            now,
            resource.CapturedAtUtc,
            resource.LastUpdatedAtUtc);
        session.UpdatedAtUtc = MaxTimestamp(
            now,
            session.CreatedAtUtc,
            session.UpdatedAtUtc,
            resource.LastUpdatedAtUtc);
    }

    private void AbandonUndispatchedResources(
        PrivacySession session,
        PrivacyBlockPreparation preparation,
        string error)
    {
        foreach (var resource in SelectResources(session, preparation.ResourceIds))
        {
            if (resource.JournalState != PrivacyResourceJournalState.ApplyPending ||
                resource.ModifiedByPrivLock)
            {
                continue;
            }

            resource.JournalState = PrivacyResourceJournalState.ApplyFailed;
            resource.ModifiedByPrivLock = false;
            resource.OwnershipUncertain = false;
            resource.ExecutionMayStillBeInFlight = false;
            resource.LastError = error;
            resource.LastUpdatedAtUtc = MaxTimestamp(
                DateTimeOffset.UtcNow,
                resource.CapturedAtUtc,
                resource.LastUpdatedAtUtc);
        }

        RecalculateSessionStatus(session);
        session.UpdatedAtUtc = MaxTimestamp(
            DateTimeOffset.UtcNow,
            session.CreatedAtUtc,
            session.UpdatedAtUtc,
            session.Resources.Select(resource => resource.LastUpdatedAtUtc).DefaultIfEmpty().Max());
        _sessionStore.Save(session);
    }

    private static IEnumerable<PrivacyResourceState> SelectResources(
        PrivacySession session,
        IReadOnlyList<string> resourceIds)
    {
        var idSet = new HashSet<string>(resourceIds, StringComparer.OrdinalIgnoreCase);
        return session.Resources.Where(resource => idSet.Contains(resource.ResourceId)).ToList();
    }

    private static DeviceOperationDetail? GetResourceDetail(OperationResult result, PrivacyResourceState resource)
    {
        var nativeId = resource switch
        {
            OriginalDeviceState device => device.InstanceId,
            OriginalAudioEndpointState endpoint => endpoint.EndpointId,
            _ => null
        };

        return result.Details.FirstOrDefault(detail =>
            string.Equals(detail.DeviceId, resource.ResourceId, StringComparison.OrdinalIgnoreCase) ||
            nativeId != null && string.Equals(detail.DeviceId, nativeId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesScope(
        PrivacyResourceState resource,
        ProtectionLayer? layer,
        BlockTarget? target) =>
        (!layer.HasValue || resource.Layer == layer.Value) &&
        (!target.HasValue || IncludesTarget(target.Value, resource.Target));

    private static bool MatchesSelection(
        PrivacyResourceState resource,
        ProtectionLayer? layer,
        BlockTarget? target,
        IReadOnlySet<string>? onlyResourceIds) =>
        MatchesScope(resource, layer, target) &&
        (onlyResourceIds == null || onlyResourceIds.Contains(resource.ResourceId));

    private static bool IncludesTarget(BlockTarget requested, BlockTarget resourceTarget) =>
        requested == BlockTarget.Both || requested == resourceTarget;

    private static bool IsOutstandingOwnedChange(PrivacyResourceState resource) =>
        resource.RequiresModification && resource.JournalState is
            PrivacyResourceJournalState.ApplyPending or
            PrivacyResourceJournalState.Applied or
            PrivacyResourceJournalState.RestorePending or
            PrivacyResourceJournalState.Missing or
            PrivacyResourceJournalState.RestoreFailed;

    private static bool RequiresRecoveryBeforeNewProtection(PrivacyResourceState resource) =>
        resource.RequiresModification && resource.JournalState is
            PrivacyResourceJournalState.ApplyPending or
            PrivacyResourceJournalState.RestorePending or
            PrivacyResourceJournalState.Missing or
            PrivacyResourceJournalState.RestoreFailed;

    private static bool HasConfirmedOwnership(
        PrivacyResourceState resource,
        PrivacyResourceJournalState state) =>
        resource.ModifiedByPrivLock &&
        !resource.OwnershipUncertain &&
        state is PrivacyResourceJournalState.Applied or
            PrivacyResourceJournalState.RestorePending or
            PrivacyResourceJournalState.Missing or
            PrivacyResourceJournalState.RestoreFailed;

    private static bool CanRecoverFromDurableUserScopedIntent(
        PrivacyResourceState resource,
        PrivacyResourceJournalState priorState) =>
        (priorState is PrivacyResourceJournalState.ApplyPending or
            PrivacyResourceJournalState.RestorePending or
            PrivacyResourceJournalState.Missing or
            PrivacyResourceJournalState.RestoreFailed) &&
        resource.Layer == ProtectionLayer.Standard &&
        (resource is OriginalAudioEndpointState or
            OriginalPolicyState { RegistryHive: PrivacyRegistryHive.CurrentUser });

    private static bool IsTerminal(PrivacyResourceJournalState state) => state is
        PrivacyResourceJournalState.Unchanged or
        PrivacyResourceJournalState.Restored or
        PrivacyResourceJournalState.Conflict or
        PrivacyResourceJournalState.ApplyFailed;

    private static int GetRestoreOrder(PrivacyResourceState resource) => resource switch
    {
        OriginalDeviceState { Layer: ProtectionLayer.Secure } => 0,
        OriginalPolicyState { Layer: ProtectionLayer.Secure } => 1,
        OriginalAudioEndpointState { Layer: ProtectionLayer.Standard } => 2,
        OriginalPolicyState { Layer: ProtectionLayer.Standard } => 3,
        _ => 4
    };

    private static void RecalculateSessionStatus(PrivacySession session)
    {
        var hasRetryable = session.Resources.Any(IsOutstandingOwnedChange);
        var hasConflicts = session.Resources.Any(resource =>
            resource.JournalState == PrivacyResourceJournalState.Conflict);

        if (hasRetryable)
        {
            session.IsActive = true;
            session.WasRestored = false;
            session.Status = PrivacySessionStatus.RecoveryIncomplete;
            session.CompletedAtUtc = null;
            return;
        }

        CompleteSession(session, hasConflicts);
    }

    private static void CompleteSession(PrivacySession session, bool hasConflicts)
    {
        session.IsActive = false;
        session.WasRestored = !hasConflicts;
        session.Status = hasConflicts
            ? PrivacySessionStatus.CompletedWithConflicts
            : PrivacySessionStatus.Restored;
        session.CompletedAtUtc = MaxTimestamp(
            DateTimeOffset.UtcNow,
            session.CreatedAtUtc,
            session.UpdatedAtUtc,
            session.Resources.Select(resource => resource.LastUpdatedAtUtc).DefaultIfEmpty().Max());
        session.UpdatedAtUtc = session.CompletedAtUtc.Value;
    }

    private static DateTimeOffset MaxTimestamp(params DateTimeOffset[] timestamps) =>
        timestamps.Max();

    private static string? BuildRecoveryMessage(
        int conflictCount,
        int irreducibleAmbiguityCount,
        int missingCount,
        int failedCount)
    {
        var messages = new List<string>();
        if (conflictCount > 0)
            messages.Add($"{conflictCount} external state conflict(s) were preserved");
        if (irreducibleAmbiguityCount > 0)
            messages.Add(
                $"{irreducibleAmbiguityCount} user-scoped resource(s) were restored from durable intent across an irreducible same-value crash gap");
        if (missingCount > 0)
            messages.Add($"{missingCount} missing resource(s) remain pending");
        if (failedCount > 0)
            messages.Add($"{failedCount} restoration operation(s) failed");
        return messages.Count == 0 ? null : string.Join("; ", messages) + ".";
    }

    private static string ToSafeLogId(string resourceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(resourceId));
        return Convert.ToHexString(digest.AsSpan(0, 6));
    }
}

/// <summary>
/// Explicit fallback for platforms whose current controllers cannot yet capture exact native state.
/// It prevents the application layer from pretending that durable recovery is available.
/// </summary>
public sealed class UnsupportedPrivacySessionPlatformAdapter : IPrivacySessionPlatformAdapter
{
    public bool SupportsPersistentRecovery => false;

    public Task<OperationResult> EnsureMutationQuiescenceAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Ok());

    public Task<IReadOnlyList<PrivacyResourceState>> CaptureAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrivacyResourceState>>([]);

    public Task<PrivacyResourceObservation> ObserveAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PrivacyResourceObservation(PrivacyResourceObservationKind.Error, "Platform recovery is not supported."));

    public Task<OperationResult> RestoreOriginalAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Fail("Platform recovery is not supported."));

    public void CompleteRecoveryPass()
    {
    }
}
