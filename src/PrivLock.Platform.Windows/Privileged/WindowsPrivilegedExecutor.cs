using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Infrastructure.Common.Logging;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Policies;
using Serilog;

namespace PrivLock.Platform.Windows.Privileged;

internal enum PrivilegedOwnershipVerificationAction
{
    Confirm,
    NotConfirmed,
    RetireAndNotConfirm,
    Error
}

/// <summary>
/// Handles execution of strictly validated privileged commands in the transient elevated worker.
/// </summary>
public static class WindowsPrivilegedExecutor
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(WindowsPrivilegedExecutor));
    private const string PrivilegedOperationMutexName = @"Global\PrivLock_PrivilegedOperation";
    internal const string OwnershipNotConfirmedPrefix = "OWNERSHIP_NOT_CONFIRMED:";

    internal static OperationResult ExecuteWorkerCommand(
        string command,
        string argument,
        string ownerUserSid) =>
        ExecutePrivilegedCommandCore(command, argument, ownerUserSid);

    private static OperationResult ExecutePrivilegedCommandCore(
        string command,
        string argument,
        string ownerUserSid)
    {
        Log.Information("Executing internal privileged command: Command={Command}", command);

        try
        {
            var deviceController = new WindowsDeviceController();

            return command.ToLowerInvariant() switch
            {
                "apply-policy" => ValidateAndApplyPolicy(argument, ownerUserSid),
                "apply-device" => ValidateAndApplyDevice(deviceController, argument, ownerUserSid),
                "restore-policy" => ValidateAndRestorePolicy(argument, ownerUserSid),
                "restore-device" => ValidateAndRestoreDevice(deviceController, argument, ownerUserSid),
                "verify-policy-ownership" => ValidatePolicyOwnership(argument, ownerUserSid),
                "verify-device-ownership" => ValidateDeviceOwnership(argument, ownerUserSid),
                "ping" => OperationResult.Ok(),
                _ => OperationResult.Fail($"Unknown privileged command: '{command}'")
            };
        }
        catch (Exception ex)
        {
            CrashReporter.GenerateCrashReport(ex, "WindowsPrivilegedExecutor.ExecuteWorkerCommand");
            Log.Error(ex, "Failed to execute internal privileged command: {Command}", command);
            // The exception can follow a native mutation or an ownership checkpoint write.
            // Only the resource-specific paths can prove that neither remains outstanding.
            return OperationResult.Fail($"Privileged execution error: {ex.Message}", outcomeUncertain: true);
        }
    }

    /// <summary>
    /// Requests execution via an operation-scoped elevated session.
    /// </summary>
    internal static Task<OperationResult> InvokeOnDemandElevationAsync(string command, string argument)
    {
        return WindowsPrivilegedSession.Instance.ExecuteCommandAsync(command, argument);
    }

    /// <summary>
    /// Startup barrier for a helper orphaned by abrupt main-process termination. The helper holds
    /// this mutex for the full native command, so recovery cannot snapshot an intermediate batch.
    /// </summary>
    public static OperationResult WaitForPreviousPrivilegedOperation(TimeSpan timeout)
    {
        try
        {
            using var operationLease = TryAcquirePrivilegedOperationLease(timeout);
            return operationLease != null
                ? OperationResult.Ok()
                : OperationResult.Fail("A previous PrivLock privileged operation did not finish before the startup timeout.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not establish the privileged-operation startup barrier");
            return OperationResult.Fail($"Privileged-operation startup barrier failed: {ex.Message}");
        }
    }

    internal static PrivilegedOperationLease? TryAcquirePrivilegedOperationLease(TimeSpan timeout)
    {
        var operationMutex = CreatePrivilegedOperationMutex();
        try
        {
            var acquired = false;
            try
            {
                acquired = operationMutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                Log.Warning("Recovered abandoned privileged-operation mutex");
            }

            if (!acquired)
            {
                operationMutex.Dispose();
                return null;
            }

            return new PrivilegedOperationLease(operationMutex);
        }
        catch
        {
            operationMutex.Dispose();
            throw;
        }
    }

    private static Mutex CreatePrivilegedOperationMutex()
    {
        // This mutex serializes operations; authorization is enforced separately by the pipe ACL,
        // PID handshake, command whitelist, and payload validation. Authenticated users need
        // synchronize/modify so an OTS-elevated helper cannot strand recovery behind an ACL it owns.
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            authenticatedUsers,
            MutexRights.Synchronize | MutexRights.Modify,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            administrators,
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            system,
            MutexRights.FullControl,
            AccessControlType.Allow));
        return MutexAcl.Create(
            initiallyOwned: false,
            PrivilegedOperationMutexName,
            out _,
            security);
    }

    private static OperationResult ValidateAndApplyPolicy(string payload, string ownerUserSid)
    {
        var policy = DecodeAndValidatePolicy(payload);
        if (policy == null || !policy.RequiresModification ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, ownerUserSid, out var identity))
            return OperationResult.Fail("Invalid privileged policy application request.");

        var observation = WindowsRegistryValueCodec.Observe(policy);
        if (observation.Kind == PrivacyResourceObservationKind.MatchesProtected)
        {
            var ownership = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
            if (ownership == PrivilegedOwnershipLookupKind.MatchingApplied)
                return OperationResult.Ok();
            if (ownership is PrivilegedOwnershipLookupKind.MatchingApplyPending or
                PrivilegedOwnershipLookupKind.MatchingRestorePending)
            {
                return WindowsPrivilegedOwnershipStore.TryTransition(
                    identity!, ownership, PrivilegedOwnershipStage.Applied, out var recoveryError)
                    ? OperationResult.Ok()
                    : OperationResult.Fail(
                        recoveryError ?? "Could not finalize recovered Registry ownership.",
                        outcomeUncertain: true);
            }
            return OperationResult.Fail(lookupError ??
                "Registry apply conflict: protected state has no matching machine ownership attestation.");
        }
        if (observation.Kind != PrivacyResourceObservationKind.MatchesOriginal)
            return OperationResult.Fail(observation.ErrorMessage ?? "Registry apply conflict: current state changed after snapshot.");

        var existing = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var existingError);
        if (existing == PrivilegedOwnershipLookupKind.Missing)
        {
            if (!WindowsPrivilegedOwnershipStore.TryCreateApplyPrepared(identity!, out var claimError))
                return AbortUndispatchedApply(identity!, claimError ?? "Could not create privileged ownership attestation before Registry mutation.");
        }
        else if (existing is PrivilegedOwnershipLookupKind.MatchingApplied or
                 PrivilegedOwnershipLookupKind.MatchingRestorePending or
                 PrivilegedOwnershipLookupKind.MatchingApplyPending)
        {
            if (!WindowsPrivilegedOwnershipStore.TryTransition(
                    identity!,
                    existing,
                    PrivilegedOwnershipStage.ApplyPrepared,
                    out var reclaimError))
            {
                return AbortUndispatchedApply(identity!, reclaimError ??
                    "Could not re-arm the matching Registry ownership attestation.");
            }
        }
        else if (existing != PrivilegedOwnershipLookupKind.MatchingApplyPrepared)
        {
            return OperationResult.Fail(existingError ??
                "Registry apply rejected because incompatible machine ownership state exists.");
        }

        // ApplyPrepared is not restore authority. Promote it to ApplyPending only after the final
        // pre-dispatch comparison; this minimizes the sole irreducible crash window to the few
        // instructions between this durable transition and the native Registry call.
        var finalObservation = WindowsRegistryValueCodec.Observe(policy);
        if (finalObservation.Kind != PrivacyResourceObservationKind.MatchesOriginal)
            return AbortUndispatchedApply(identity!, finalObservation.ErrorMessage ??
                "Registry state changed before native dispatch.");
        if (!WindowsPrivilegedOwnershipStore.TryTransition(
                identity!,
                PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
                PrivilegedOwnershipStage.ApplyPending,
                out var dispatchError))
        {
            return AbortUndispatchedApply(identity!, dispatchError ?? "Could not checkpoint Registry native dispatch.");
        }

        // Never let user-writable WAL flags make a protected value look like an idempotent apply.
        MarkFreshApplyIntent(policy);
        var result = WindowsRegistryValueCodec.CompareAndApplyProtected(policy);
        if (!result.Success)
            return CleanupFailedApply(identity!, result);

        if (WindowsPrivilegedOwnershipStore.TryTransition(
            identity!,
            PrivilegedOwnershipLookupKind.MatchingApplyPending,
            PrivilegedOwnershipStage.Applied,
            out var finalizeError))
        {
            return result;
        }

        // The live worker has direct evidence that this exact native apply succeeded. Compensate
        // immediately while that evidence is still available; a later process must not infer the
        // same fact from an ambiguous ApplyPending record.
        var rollback = WindowsRegistryValueCodec.CompareAndRestore(policy);
        string? cleanupError = null;
        if (rollback.Success && WindowsPrivilegedOwnershipStore.TryDelete(
                identity!,
                PrivilegedOwnershipLookupKind.MatchingApplyPending,
                out cleanupError))
        {
            return OperationResult.Fail(
                finalizeError ?? "Registry ownership attestation finalization failed; the mutation was rolled back.");
        }

        return OperationResult.Fail(
            finalizeError ?? cleanupError ?? rollback.ErrorMessage ??
            "Registry protection succeeded but ownership finalization and compensating restoration were incomplete.",
            outcomeUncertain: true);
    }

    private static OperationResult ValidateAndApplyDevice(
        WindowsDeviceController controller,
        string payload,
        string ownerUserSid)
    {
        var device = DecodeAndValidateDevice(payload);
        if (device == null || !device.RequiresModification || !device.OriginalEnabledState || device.OriginalProblemCode != 0 ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(device, ownerUserSid, out var identity))
            return OperationResult.Fail("Invalid privileged device application request.");

        if (!TryResolveCapturedDevice(device, out var validationError))
            return OperationResult.Fail(validationError);

        var current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded)
            return OperationResult.Fail(current.ErrorMessage ?? "Could not query PnP device state.");
        if (!current.IsPresent)
            return OperationResult.Fail("PnP device disappeared after its snapshot was committed.");
        if (current.ProblemCode == CfgMgrInterop.CM_PROB_DISABLED)
        {
            var ownership = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
            if (ownership == PrivilegedOwnershipLookupKind.MatchingApplied)
                return OperationResult.Ok();
            if (ownership is PrivilegedOwnershipLookupKind.MatchingApplyPending or
                PrivilegedOwnershipLookupKind.MatchingRestorePending)
            {
                return WindowsPrivilegedOwnershipStore.TryTransition(
                    identity!, ownership, PrivilegedOwnershipStage.Applied, out var recoveryError)
                    ? OperationResult.Ok()
                    : OperationResult.Fail(
                        recoveryError ?? "Could not finalize recovered PnP ownership.",
                        outcomeUncertain: true);
            }
            return OperationResult.Fail(lookupError ??
                "PnP apply conflict: disabled state has no matching machine ownership attestation.");
        }
        if (current.ProblemCode != device.OriginalProblemCode)
            return OperationResult.Fail("PnP apply conflict: device state changed after snapshot.");

        var existing = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var existingError);
        if (existing == PrivilegedOwnershipLookupKind.Missing)
        {
            if (!WindowsPrivilegedOwnershipStore.TryCreateApplyPrepared(identity!, out var claimError))
                return AbortUndispatchedApply(identity!, claimError ?? "Could not create privileged ownership attestation before PnP mutation.");
        }
        else if (existing is PrivilegedOwnershipLookupKind.MatchingApplied or
                 PrivilegedOwnershipLookupKind.MatchingRestorePending or
                 PrivilegedOwnershipLookupKind.MatchingApplyPending)
        {
            if (!WindowsPrivilegedOwnershipStore.TryTransition(
                    identity!,
                    existing,
                    PrivilegedOwnershipStage.ApplyPrepared,
                    out var reclaimError))
            {
                return AbortUndispatchedApply(identity!, reclaimError ??
                    "Could not re-arm the matching PnP ownership attestation.");
            }
        }
        else if (existing != PrivilegedOwnershipLookupKind.MatchingApplyPrepared)
        {
            return OperationResult.Fail(existingError ??
                "PnP apply rejected because incompatible machine ownership state exists.");
        }

        current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded || !current.IsPresent || current.ProblemCode != device.OriginalProblemCode)
        {
            return AbortUndispatchedApply(
                identity!,
                current.ErrorMessage ?? "PnP apply conflict: device changed before native dispatch.");
        }
        if (!WindowsPrivilegedOwnershipStore.TryTransition(
                identity!,
                PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
                PrivilegedOwnershipStage.ApplyPending,
                out var dispatchError))
        {
            return AbortUndispatchedApply(identity!, dispatchError ?? "Could not checkpoint PnP native dispatch.");
        }

        MarkFreshApplyIntent(device);
        current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded || !current.IsPresent || current.ProblemCode != device.OriginalProblemCode)
        {
            var raceResult = OperationResult.Fail(
                current.ErrorMessage ?? "PnP apply conflict: device changed at native dispatch.");
            return CleanupFailedApply(identity!, raceResult);
        }
        var info = CreateDeviceInfo(device, isEnabled: true, current.ProblemCode);
        var result = controller.DisableDevicesAsync([info]).GetAwaiter().GetResult();
        if (!result.Success)
            return CleanupFailedApply(identity!, result);

        if (WindowsPrivilegedOwnershipStore.TryTransition(
            identity!,
            PrivilegedOwnershipLookupKind.MatchingApplyPending,
            PrivilegedOwnershipStage.Applied,
            out var finalizeError))
        {
            return result;
        }

        var rollbackCurrent = controller.GetDeviceNodeState(device.InstanceId);
        var rollback = rollbackCurrent.QuerySucceeded && rollbackCurrent.IsPresent &&
                       rollbackCurrent.ProblemCode == CfgMgrInterop.CM_PROB_DISABLED
            ? controller.EnableDevicesAsync([
                CreateDeviceInfo(device, isEnabled: false, CfgMgrInterop.CM_PROB_DISABLED)
            ]).GetAwaiter().GetResult()
            : OperationResult.Fail(
                rollbackCurrent.ErrorMessage ??
                "PnP compensating restoration was skipped because device state changed externally.");
        string? cleanupError = null;
        if (rollback.Success && WindowsPrivilegedOwnershipStore.TryDelete(
                identity!,
                PrivilegedOwnershipLookupKind.MatchingApplyPending,
                out cleanupError))
        {
            return OperationResult.Fail(
                finalizeError ?? "PnP ownership attestation finalization failed; the mutation was rolled back.");
        }

        return OperationResult.Fail(
            finalizeError ?? cleanupError ?? rollback.ErrorMessage ??
            "PnP protection succeeded but ownership finalization and compensating restoration were incomplete.",
            outcomeUncertain: true);
    }

    private static OperationResult ValidateAndRestorePolicy(string payload, string ownerUserSid)
    {
        var policy = DecodeAndValidatePolicy(payload);
        if (policy == null || !policy.RequiresModification ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, ownerUserSid, out var identity))
            return OperationResult.Fail("Invalid privileged policy restoration request.");

        var ownership = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
        if (!WindowsPrivilegedOwnershipStore.IsRestoreAuthority(ownership))
        {
            return OperationResult.Fail(lookupError ??
                "Registry restore rejected: no matching machine ownership attestation exists.");
        }

        var observation = WindowsRegistryValueCodec.Observe(policy);
        if (observation.Kind == PrivacyResourceObservationKind.MatchesOriginal)
        {
            return WindowsPrivilegedOwnershipStore.TryDelete(identity!, ownership, out var deleteError)
                ? OperationResult.Ok()
                : OperationResult.Fail(deleteError ?? "Could not retire the completed Registry ownership attestation.");
        }
        if (observation.Kind != PrivacyResourceObservationKind.MatchesProtected)
            return OperationResult.Fail(observation.ErrorMessage ?? "Registry restore conflict: external state was preserved.");

        if (ownership is PrivilegedOwnershipLookupKind.MatchingApplyPending or
            PrivilegedOwnershipLookupKind.MatchingApplied &&
            !WindowsPrivilegedOwnershipStore.TryTransition(
                identity!,
                ownership,
                PrivilegedOwnershipStage.RestorePending,
                out var transitionError))
        {
            return OperationResult.Fail(transitionError ?? "Could not checkpoint privileged Registry restoration.");
        }

        var result = WindowsRegistryValueCodec.CompareAndRestore(policy);
        if (!result.Success)
            return result;
        return WindowsPrivilegedOwnershipStore.TryDelete(
            identity!,
            PrivilegedOwnershipLookupKind.MatchingRestorePending,
            out var finalizeError)
            ? result
            : OperationResult.Fail(
                finalizeError ?? "Registry restoration succeeded but ownership attestation cleanup failed.",
                outcomeUncertain: true);
    }

    private static OperationResult ValidateAndRestoreDevice(
        WindowsDeviceController controller,
        string payload,
        string ownerUserSid)
    {
        var device = DecodeAndValidateDevice(payload);
        if (device == null || !device.RequiresModification || !device.OriginalEnabledState || device.OriginalProblemCode != 0 ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(device, ownerUserSid, out var identity))
            return OperationResult.Fail("Invalid privileged device restoration request.");

        // The payload crosses an unprivileged boundary. Re-resolve the InstanceId and class in
        // Windows rather than trusting the serialized DeviceClass/Target supplied by the caller.
        if (!TryResolveCapturedDevice(device, out var validationError))
            return OperationResult.Fail(validationError);

        var ownership = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
        if (!WindowsPrivilegedOwnershipStore.IsRestoreAuthority(ownership))
        {
            return OperationResult.Fail(lookupError ??
                "PnP restore rejected: no matching machine ownership attestation exists.");
        }

        var current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded)
            return OperationResult.Fail(current.ErrorMessage ?? "Could not query PnP device state.");
        if (!current.IsPresent)
            return OperationResult.Fail("PnP device is currently missing; restoration remains pending.");
        if (current.ProblemCode == device.OriginalProblemCode)
        {
            return WindowsPrivilegedOwnershipStore.TryDelete(identity!, ownership, out var deleteError)
                ? OperationResult.Ok()
                : OperationResult.Fail(deleteError ?? "Could not retire the completed PnP ownership attestation.");
        }
        if (current.ProblemCode != CfgMgrInterop.CM_PROB_DISABLED)
            return OperationResult.Fail("PnP restore conflict: external device state was preserved.");

        if (ownership is PrivilegedOwnershipLookupKind.MatchingApplyPending or
            PrivilegedOwnershipLookupKind.MatchingApplied &&
            !WindowsPrivilegedOwnershipStore.TryTransition(
                identity!,
                ownership,
                PrivilegedOwnershipStage.RestorePending,
                out var transitionError))
        {
            return OperationResult.Fail(transitionError ?? "Could not checkpoint privileged PnP restoration.");
        }

        current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded || !current.IsPresent || current.ProblemCode != CfgMgrInterop.CM_PROB_DISABLED)
        {
            return OperationResult.Fail(
                current.ErrorMessage ?? "PnP restore conflict: device changed after restoration checkpoint.");
        }
        var info = CreateDeviceInfo(device, isEnabled: false, current.ProblemCode);
        var result = device.OriginalEnabledState
            ? controller.EnableDevicesAsync([info]).GetAwaiter().GetResult()
            : controller.DisableDevicesAsync([info]).GetAwaiter().GetResult();
        if (!result.Success)
            return result;
        return WindowsPrivilegedOwnershipStore.TryDelete(
            identity!,
            PrivilegedOwnershipLookupKind.MatchingRestorePending,
            out var finalizeError)
            ? result
            : OperationResult.Fail(
                finalizeError ?? "PnP restoration succeeded but ownership attestation cleanup failed.",
                outcomeUncertain: true);
    }

    private static void MarkFreshApplyIntent(PrivacyResourceState resource)
    {
        resource.JournalState = PrivacyResourceJournalState.ApplyPending;
        resource.ModifiedByPrivLock = false;
        resource.OwnershipUncertain = true;
        resource.ExecutionMayStillBeInFlight = false;
    }

    private static OperationResult CleanupFailedApply(
        PrivilegedResourceIdentity identity,
        OperationResult result)
    {
        if (result.OutcomeUncertain || result.ExecutionStillInFlight)
            return result;
        if (!WindowsPrivilegedOwnershipStore.TryDelete(
                identity,
                PrivilegedOwnershipLookupKind.MatchingApplyPending,
                out var cleanupError))
        {
            return OperationResult.Fail(
                $"{result.ErrorMessage} Ownership-attestation cleanup failed: {cleanupError}",
                result.Details,
                outcomeUncertain: true);
        }
        return result;
    }

    private static OperationResult ValidatePolicyOwnership(string payload, string ownerUserSid)
    {
        var policy = DecodeAndValidatePolicy(payload);
        if (policy == null || !policy.RequiresModification ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, ownerUserSid, out var identity))
        {
            return OperationResult.Fail("Invalid privileged policy ownership verification request.");
        }

        var lookup = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
        if (!IsMatchingOwnershipLookup(lookup))
            return OwnershipVerificationFailure(lookup, lookupError, "Registry");

        var observation = WindowsRegistryValueCodec.Observe(policy);
        return CompleteOwnershipVerification(
            identity!,
            lookup,
            observation,
            lookupError,
            "Registry");
    }

    private static OperationResult ValidateDeviceOwnership(string payload, string ownerUserSid)
    {
        var device = DecodeAndValidateDevice(payload);
        if (device == null || !device.RequiresModification ||
            !WindowsPrivilegedOwnershipFingerprint.TryCreate(device, ownerUserSid, out var identity))
        {
            return OperationResult.Fail("Invalid privileged device ownership verification request.");
        }
        var lookup = WindowsPrivilegedOwnershipStore.Lookup(identity!, out var lookupError);
        if (!IsMatchingOwnershipLookup(lookup))
            return OwnershipVerificationFailure(lookup, lookupError, "PnP");

        var controller = new WindowsDeviceController();
        var current = controller.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded)
        {
            return CompleteOwnershipVerification(
                identity!,
                lookup,
                new PrivacyResourceObservation(
                    PrivacyResourceObservationKind.Error,
                    current.ErrorMessage ?? "Could not query PnP device state."),
                lookupError,
                "PnP");
        }
        if (!current.IsPresent)
        {
            return CompleteOwnershipVerification(
                identity!,
                lookup,
                new PrivacyResourceObservation(PrivacyResourceObservationKind.Missing),
                lookupError,
                "PnP");
        }
        if (!TryResolveCapturedDevice(device, out var validationError))
        {
            return CompleteOwnershipVerification(
                identity!,
                lookup,
                new PrivacyResourceObservation(PrivacyResourceObservationKind.Error, validationError),
                lookupError,
                "PnP");
        }

        var observation = current.ProblemCode == device.OriginalProblemCode
            ? new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal)
            : current.ProblemCode == CfgMgrInterop.CM_PROB_DISABLED && !device.ProtectedEnabledState
                ? new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesProtected)
                : new PrivacyResourceObservation(
                    PrivacyResourceObservationKind.Conflict,
                    "PnP state differs from both the original and PrivLock-applied states.");
        return CompleteOwnershipVerification(
            identity!,
            lookup,
            observation,
            lookupError,
            "PnP");
    }

    private static OperationResult CompleteOwnershipVerification(
        PrivilegedResourceIdentity identity,
        PrivilegedOwnershipLookupKind lookup,
        PrivacyResourceObservation observation,
        string? lookupError,
        string resourceKind)
    {
        var action = ClassifyOwnershipVerificationForTest(lookup, observation.Kind);
        switch (action)
        {
            case PrivilegedOwnershipVerificationAction.Confirm:
                return OperationResult.Ok();

            case PrivilegedOwnershipVerificationAction.NotConfirmed:
                return OwnershipVerificationFailure(lookup, lookupError, resourceKind);

            case PrivilegedOwnershipVerificationAction.RetireAndNotConfirm:
                if (!WindowsPrivilegedOwnershipStore.TryDelete(identity, lookup, out var cleanupError))
                {
                    return OperationResult.Fail(
                        cleanupError ?? $"Could not retire the stale {resourceKind} ownership attestation.",
                        outcomeUncertain: true);
                }

                return OperationResult.Fail(
                    OwnershipNotConfirmedPrefix +
                    $"The {resourceKind} ownership attestation was retired after observing {observation.Kind}.");

            default:
                return OperationResult.Fail(
                    observation.ErrorMessage ?? lookupError ??
                    $"The {resourceKind} ownership attestation could not be verified against current state.");
        }
    }

    internal static PrivilegedOwnershipVerificationAction ClassifyOwnershipVerificationForTest(
        PrivilegedOwnershipLookupKind lookup,
        PrivacyResourceObservationKind observation)
    {
        if (lookup is PrivilegedOwnershipLookupKind.Missing or PrivilegedOwnershipLookupKind.Mismatch)
            return PrivilegedOwnershipVerificationAction.NotConfirmed;
        if (!IsMatchingOwnershipLookup(lookup))
            return PrivilegedOwnershipVerificationAction.Error;

        if (observation == PrivacyResourceObservationKind.Error)
            return PrivilegedOwnershipVerificationAction.Error;
        if (observation is PrivacyResourceObservationKind.MatchesOriginal or
            PrivacyResourceObservationKind.Conflict)
        {
            return PrivilegedOwnershipVerificationAction.RetireAndNotConfirm;
        }
        if (observation == PrivacyResourceObservationKind.MatchesProtected &&
            lookup == PrivilegedOwnershipLookupKind.MatchingApplyPrepared)
        {
            return PrivilegedOwnershipVerificationAction.RetireAndNotConfirm;
        }
        if (observation == PrivacyResourceObservationKind.Missing &&
            lookup == PrivilegedOwnershipLookupKind.MatchingApplyPrepared)
        {
            return PrivilegedOwnershipVerificationAction.NotConfirmed;
        }
        if (WindowsPrivilegedOwnershipStore.IsRestoreAuthority(lookup) &&
            observation is (PrivacyResourceObservationKind.MatchesProtected or
                PrivacyResourceObservationKind.Missing))
        {
            return PrivilegedOwnershipVerificationAction.Confirm;
        }

        return PrivilegedOwnershipVerificationAction.Error;
    }

    private static bool IsMatchingOwnershipLookup(PrivilegedOwnershipLookupKind lookup) =>
        lookup is PrivilegedOwnershipLookupKind.MatchingApplyPrepared or
            PrivilegedOwnershipLookupKind.MatchingApplyPending or
            PrivilegedOwnershipLookupKind.MatchingApplied or
            PrivilegedOwnershipLookupKind.MatchingRestorePending;

    private static OperationResult OwnershipVerificationFailure(
        PrivilegedOwnershipLookupKind lookup,
        string? error,
        string resourceKind) =>
        lookup is PrivilegedOwnershipLookupKind.Missing or
            PrivilegedOwnershipLookupKind.MatchingApplyPrepared or
            PrivilegedOwnershipLookupKind.Mismatch
            ? OperationResult.Fail(
                OwnershipNotConfirmedPrefix +
                (error ?? $"No confirmed machine ownership attestation exists for the {resourceKind} resource."))
            : OperationResult.Fail(error ?? $"The {resourceKind} ownership attestation could not be verified.");

    internal delegate PrivilegedOwnershipLookupKind OwnershipLookup(
        PrivilegedResourceIdentity identity,
        out string? error);

    internal delegate bool OwnershipDeletion(
        PrivilegedResourceIdentity identity,
        PrivilegedOwnershipLookupKind expected,
        out string? error);

    internal static OperationResult AbortUndispatchedApply(
        PrivilegedResourceIdentity identity,
        string error,
        OwnershipLookup? lookupOwnership = null,
        OwnershipDeletion? deleteOwnership = null)
    {
        lookupOwnership ??= WindowsPrivilegedOwnershipStore.Lookup;
        deleteOwnership ??= WindowsPrivilegedOwnershipStore.TryDelete;

        // SetValue can succeed even when Flush or read-back verification fails. Re-read the
        // actual stage instead of assuming a failed checkpoint left its old state intact.
        // This cleanup is valid only in the live worker before any native apply is dispatched.
        var actual = lookupOwnership(identity, out var lookupError);
        if (actual == PrivilegedOwnershipLookupKind.Missing)
            return OperationResult.Fail(error);
        if (!IsMatchingOwnershipLookup(actual))
        {
            return OperationResult.Fail(
                $"{error} Undispatched-attestation cleanup could not establish ownership: {lookupError}",
                outcomeUncertain: true);
        }

        return deleteOwnership(identity, actual, out var cleanupError)
            ? OperationResult.Fail(error)
            : OperationResult.Fail(
                $"{error} Undispatched-attestation cleanup failed: {cleanupError}",
                outcomeUncertain: true);
    }

    private static OriginalPolicyState? DecodeAndValidatePolicy(string payload)
    {
        var policy = WindowsPrivacySessionPlatformAdapter.DecodePayload<OriginalPolicyState>(payload);
        if (policy == null ||
            policy.Layer != ProtectionLayer.Secure ||
            policy.RegistryHive != PrivacyRegistryHive.LocalMachine ||
            policy.RegistryView != PrivacyRegistryView.Default ||
            !string.Equals(policy.RegistryPath, WindowsPolicyManager.PolicyRegistryPath, StringComparison.OrdinalIgnoreCase) ||
            !policy.ProtectedValueExists ||
            policy.ProtectedValueKind != PrivacyRegistryValueKind.DWord ||
            policy.ProtectedValue != WindowsPolicyManager.PolicyDeny.ToString(global::System.Globalization.CultureInfo.InvariantCulture) ||
            policy.ValueName is not (WindowsPolicyManager.CameraValueName or WindowsPolicyManager.MicrophoneValueName) ||
            policy.Target != TargetForPolicy(policy.ValueName) ||
            !string.Equals(
                policy.ResourceId,
                $"registry:{policy.RegistryHive}:{policy.RegistryView}:{policy.RegistryPath}:{policy.ValueName}",
                StringComparison.OrdinalIgnoreCase) ||
            !WindowsRegistryValueCodec.IsSupportedValue(policy))
        {
            return null;
        }

        return policy;
    }

    private static OriginalDeviceState? DecodeAndValidateDevice(string payload)
    {
        var device = WindowsPrivacySessionPlatformAdapter.DecodePayload<OriginalDeviceState>(payload);
        if (device == null ||
            device.Layer != ProtectionLayer.Secure ||
            device.Target is not (BlockTarget.Camera or BlockTarget.Microphone) ||
            !device.IsSafelyRestorable ||
            device.ProtectedEnabledState ||
            !IsValidInstanceId(device.InstanceId) ||
            !string.Equals(device.ResourceId, $"pnp:{device.InstanceId}", StringComparison.OrdinalIgnoreCase) ||
            device.OriginalProblemCode is not (0 or CfgMgrInterop.CM_PROB_DISABLED) ||
            !IsAllowedPrivacyClass(device.DeviceClass, device.Target))
        {
            return null;
        }

        return device;
    }

    private static bool TryResolveCapturedDevice(OriginalDeviceState device, out string error)
    {
        var detected = new WindowsDeviceDetector().DetectAllAsync().GetAwaiter().GetResult()
            .SingleOrDefault(candidate =>
                string.Equals(candidate.Id, device.InstanceId, StringComparison.OrdinalIgnoreCase));
        var expectedType = device.Target == BlockTarget.Camera
            ? DeviceType.Camera
            : DeviceType.Microphone;
        if (detected == null ||
            detected.DeviceType != expectedType ||
            detected.PlatformIdentifier == null ||
            !IsAllowedPrivacyClass(detected.PlatformIdentifier, device.Target) ||
            !string.Equals(detected.PlatformIdentifier, device.DeviceClass, StringComparison.OrdinalIgnoreCase))
        {
            error = "The requested PnP node is not currently verified as the captured camera or microphone endpoint.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static DeviceInfo CreateDeviceInfo(
        OriginalDeviceState device,
        bool isEnabled,
        uint problemCode) =>
        new()
        {
            Id = device.InstanceId,
            FriendlyName = "Privacy device",
            DeviceType = device.Target == BlockTarget.Camera ? DeviceType.Camera : DeviceType.Microphone,
            IsPresent = true,
            IsEnabled = isEnabled,
            ConfigurationProblemCode = problemCode,
            IsStateKnown = true
        };

    private static BlockTarget TargetForPolicy(string valueName) =>
        valueName == WindowsPolicyManager.CameraValueName
            ? BlockTarget.Camera
            : BlockTarget.Microphone;

    private static bool IsValidInstanceId(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        instanceId.Length <= 1024 &&
        instanceId.IndexOfAny(['|', '\t', '\r', '\n']) < 0;

    private static bool IsAllowedPrivacyClass(string deviceClass, BlockTarget target)
    {
        const string cameraClass = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";
        const string audioEndpointClass = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";
        return target == BlockTarget.Camera
            ? string.Equals(deviceClass, cameraClass, StringComparison.OrdinalIgnoreCase)
            : string.Equals(deviceClass, audioEndpointClass, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class PrivilegedOperationLease : IDisposable
    {
        private Mutex? _mutex;

        internal PrivilegedOperationLease(Mutex mutex)
        {
            _mutex = mutex;
        }

        public void Dispose()
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex == null)
                return;

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                Log.Error(ex, "Privileged-operation mutex ownership was lost");
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
