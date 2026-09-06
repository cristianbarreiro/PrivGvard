using PrivLock.Platform.Windows.Privileged;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class UndispatchedOwnershipCleanupTests
{
    [Theory]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPrepared, true)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPending, true)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplied, true)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingRestorePending, true)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPrepared, false)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPending, false)]
    public void FailedCheckpoint_ReconcilesObservedClaimBeforeReportingDefiniteFailure(
        int observedValue, bool deletionSucceeds)
    {
        var observed = (PrivilegedOwnershipLookupKind)observedValue;
        var identity = new PrivilegedResourceIdentity("resource", "fingerprint", "owner");
        PrivilegedOwnershipLookupKind? deletedStage = null;
        var result = WindowsPrivilegedExecutor.AbortUndispatchedApply(
            identity,
            "checkpoint failed",
            (PrivilegedResourceIdentity candidate, out string? error) =>
            {
                Assert.Same(identity, candidate);
                error = null;
                return observed;
            },
            (PrivilegedResourceIdentity candidate, PrivilegedOwnershipLookupKind stage, out string? error) =>
            {
                Assert.Same(identity, candidate);
                deletedStage = stage;
                error = deletionSucceeds ? null : "cleanup failed";
                return deletionSucceeds;
            });

        Assert.False(result.Success);
        Assert.Equal(observed, deletedStage);
        Assert.Equal(!deletionSucceeds, result.OutcomeUncertain);
    }

    [Theory]
    [InlineData((int)PrivilegedOwnershipLookupKind.Missing, false)]
    [InlineData((int)PrivilegedOwnershipLookupKind.Mismatch, true)]
    [InlineData((int)PrivilegedOwnershipLookupKind.InvalidOrUnavailable, true)]
    public void UnownedOrUnreadableClaim_IsNeverDeleted(
        int observedValue, bool uncertain)
    {
        var observed = (PrivilegedOwnershipLookupKind)observedValue;
        var result = WindowsPrivilegedExecutor.AbortUndispatchedApply(
            new PrivilegedResourceIdentity("resource", "fingerprint", "owner"),
            "checkpoint failed",
            (PrivilegedResourceIdentity _, out string? error) =>
            {
                error = "lookup result";
                return observed;
            },
            (PrivilegedResourceIdentity _, PrivilegedOwnershipLookupKind stage, out string? error) =>
                throw new InvalidOperationException("Unowned claims must never be deleted."));

        Assert.False(result.Success);
        Assert.Equal(uncertain, result.OutcomeUncertain);
    }
}
