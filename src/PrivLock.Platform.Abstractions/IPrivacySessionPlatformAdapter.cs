using PrivLock.Domain.Models;
using PrivLock.Domain.Results;

namespace PrivLock.Platform.Abstractions;

public enum PrivacyResourceObservationKind
{
    MatchesOriginal,
    MatchesProtected,
    Missing,
    Conflict,
    Error
}

public sealed record PrivacyResourceObservation(
    PrivacyResourceObservationKind Kind,
    string? ErrorMessage = null);

public enum PrivacyOwnershipAttestationKind
{
    Unsupported,
    Confirmed,
    NotConfirmed,
    Error
}

public sealed record PrivacyOwnershipAttestation(
    PrivacyOwnershipAttestationKind Kind,
    string? ErrorMessage = null);

/// <summary>
/// Platform-specific exact-state capture and compare-and-restore operations.
/// Broad block operations remain on IDeviceProtectionProvider; this adapter owns reversibility.
/// </summary>
public interface IPrivacySessionPlatformAdapter
{
    bool SupportsPersistentRecovery { get; }

    /// <summary>
    /// Proves that no previously dispatched native actor can still mutate state. Recovery must not
    /// observe or terminalize an in-flight WAL entry until this succeeds.
    /// </summary>
    Task<OperationResult> EnsureMutationQuiescenceAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PrivacyResourceState>> CaptureAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<PrivacyResourceObservation> ObserveAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Independently verifies durable platform-protected ownership when a user-writable WAL entry
    /// is ambiguous. Unsupported implementations retain the conservative no-ownership behavior.
    /// </summary>
    Task<PrivacyOwnershipAttestation> VerifyOwnershipAttestationAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Unsupported));

    /// <summary>
    /// Restores the exact original value. Implementations must repeat compare-and-restore as close
    /// to the mutation as the native API permits to narrow time-of-check/time-of-use races.
    /// </summary>
    Task<OperationResult> RestoreOriginalAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases any privilege scoped to the current restoration pass. Implementations must not
    /// retain an elevated worker after this callback.
    /// </summary>
    void CompleteRecoveryPass();
}
