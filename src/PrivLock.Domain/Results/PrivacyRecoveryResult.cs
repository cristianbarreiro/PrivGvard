namespace PrivLock.Domain.Results;

/// <summary>
/// Aggregate result for an idempotent compare-and-restore pass.
/// Conflicts are terminal and intentionally never overwritten; missing/failed resources remain retryable.
/// </summary>
public sealed class PrivacyRecoveryResult
{
    public bool TrackingSupported { get; init; } = true;
    public bool HadPersistedSession { get; init; }
    /// <summary>
    /// True only when this pass found non-terminal journal work in the requested scope. Merely
    /// having an old journal file does not grant ownership over later external state.
    /// </summary>
    public bool HadRecoveryWork { get; init; }
    public bool IsComplete { get; init; }
    public int RestoredCount { get; init; }
    public int AlreadyRestoredCount { get; init; }
    public int ConflictCount { get; init; }
    /// <summary>
    /// Number of user-scoped resources restored from a durable write-ahead intent after a crash
    /// gap. In that gap, an external write of the exact same protected value is fundamentally
    /// indistinguishable; privileged machine resources still require protected attestation.
    /// </summary>
    public int IrreducibleAmbiguityCount { get; init; }
    public int MissingCount { get; init; }
    public int FailedCount { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// It is safe to exit when there are no retryable failures. Conflicts are preserved external state,
    /// so they do not require PrivLock to remain alive.
    /// </summary>
    public bool SafeToExit => MissingCount == 0 && FailedCount == 0;

    public static PrivacyRecoveryResult Unsupported() => new()
    {
        TrackingSupported = false,
        IsComplete = true
    };

    public static PrivacyRecoveryResult NothingToRestore(bool hadPersistedSession = false) => new()
    {
        HadPersistedSession = hadPersistedSession,
        IsComplete = true
    };
}
