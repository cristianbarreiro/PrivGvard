using PrivLock.Domain.Models;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.Policies;
using PrivLock.Platform.Windows.Privileged;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class PrivilegedOwnershipFingerprintTests
{
    private const string UserA = "S-1-5-21-100-200-300-1001";
    private const string UserB = "S-1-5-21-100-200-300-1002";

    [Fact]
    public void PolicyFingerprint_BindsExactOriginalAndOwnerButNotWalBookkeeping()
    {
        var first = CreatePolicy(originalValue: "1");
        var sameTransition = CreatePolicy(originalValue: "1");
        sameTransition.OperationId = "different-operation";
        sameTransition.JournalState = PrivacyResourceJournalState.Applied;
        sameTransition.ModifiedByPrivLock = true;
        sameTransition.OwnershipUncertain = false;
        var changedOriginal = CreatePolicy(originalValue: "0");

        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(first, UserA, out var firstIdentity));
        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(sameTransition, UserA, out var sameIdentity));
        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(changedOriginal, UserA, out var changedIdentity));
        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(first, UserB, out var otherUserIdentity));

        Assert.Equal(firstIdentity, sameIdentity);
        Assert.Equal(firstIdentity!.ResourceKey, changedIdentity!.ResourceKey);
        Assert.NotEqual(firstIdentity.Fingerprint, changedIdentity.Fingerprint);
        Assert.Equal(firstIdentity.ResourceKey, otherUserIdentity!.ResourceKey);
        Assert.NotEqual(firstIdentity.Fingerprint, otherUserIdentity.Fingerprint);
        Assert.NotEqual(firstIdentity.OwnerUserSid, otherUserIdentity.OwnerUserSid);
    }

    [Fact]
    public void DeviceFingerprint_BindsStableNodeClassAndOriginalProblemState()
    {
        var camera = CreateCamera();

        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(camera, UserA, out var identity));
        Assert.NotNull(identity);

        camera.DeviceClass = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";
        Assert.False(WindowsPrivilegedOwnershipFingerprint.TryCreate(camera, UserA, out _));
        camera.DeviceClass = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";
        camera.OriginalProblemCode = 22;
        camera.OriginalEnabledState = false;
        Assert.False(WindowsPrivilegedOwnershipFingerprint.TryCreate(camera, UserA, out _));
    }

    [Fact]
    public void Fingerprint_RejectsNonSecureOrNonCanonicalResourcesAndInvalidSid()
    {
        var policy = CreatePolicy(originalValue: null);
        policy.Layer = ProtectionLayer.Standard;
        Assert.False(WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, UserA, out _));

        policy.Layer = ProtectionLayer.Secure;
        policy.ResourceId = "registry:forged";
        Assert.False(WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, UserA, out _));

        policy.ResourceId = $"registry:{policy.RegistryHive}:{policy.RegistryView}:{policy.RegistryPath}:{policy.ValueName}";
        Assert.False(WindowsPrivilegedOwnershipFingerprint.TryCreate(policy, "not-a-sid", out _));
    }

    [Fact]
    public void AttestationMatch_RequiresResourceFingerprintAndOwnerTogether()
    {
        Assert.True(WindowsPrivilegedOwnershipFingerprint.TryCreate(CreatePolicy("1"), UserA, out var identity));

        Assert.True(WindowsPrivilegedOwnershipStore.RecordMatchesIdentityForTest(
            identity!.ResourceKey,
            identity.Fingerprint,
            identity.OwnerUserSid,
            identity));
        Assert.False(WindowsPrivilegedOwnershipStore.RecordMatchesIdentityForTest(
            identity.ResourceKey,
            new string('0', 64),
            identity.OwnerUserSid,
            identity));
        Assert.False(WindowsPrivilegedOwnershipStore.RecordMatchesIdentityForTest(
            identity.ResourceKey,
            identity.Fingerprint,
            UserB,
            identity));
    }

    [Fact]
    public void RestoreAuthority_BeginsOnlyAtDurableNativeDispatchCheckpoint()
    {
        Assert.False(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.Missing));
        Assert.False(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.MatchingApplyPrepared));
        Assert.True(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.MatchingApplyPending));
        Assert.True(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.MatchingApplied));
        Assert.True(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.MatchingRestorePending));
        Assert.False(WindowsPrivilegedOwnershipStore.IsRestoreAuthority(
            PrivilegedOwnershipLookupKind.Mismatch));
    }

    [Theory]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPrepared)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplyPending)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingApplied)]
    [InlineData((int)PrivilegedOwnershipLookupKind.MatchingRestorePending)]
    public void OwnershipVerification_RetiresMatchingClaimsAfterOriginalOrConflict(
        int lookupValue)
    {
        var lookup = (PrivilegedOwnershipLookupKind)lookupValue;
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.RetireAndNotConfirm,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                lookup,
                PrivacyResourceObservationKind.MatchesOriginal));
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.RetireAndNotConfirm,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                lookup,
                PrivacyResourceObservationKind.Conflict));
    }

    [Fact]
    public void OwnershipVerification_RejectsPreparedProtectedClaimButConfirmsDispatchAuthority()
    {
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.RetireAndNotConfirm,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
                PrivacyResourceObservationKind.MatchesProtected));

        foreach (var lookup in new[]
                 {
                     PrivilegedOwnershipLookupKind.MatchingApplyPending,
                     PrivilegedOwnershipLookupKind.MatchingApplied,
                     PrivilegedOwnershipLookupKind.MatchingRestorePending
                 })
        {
            Assert.Equal(
                PrivilegedOwnershipVerificationAction.Confirm,
                WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                    lookup,
                    PrivacyResourceObservationKind.MatchesProtected));
        }
    }

    [Fact]
    public void OwnershipVerification_KeepsClaimsWhenDeviceIsMissingOrObservationFails()
    {
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.NotConfirmed,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
                PrivacyResourceObservationKind.Missing));
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.Confirm,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                PrivilegedOwnershipLookupKind.MatchingApplied,
                PrivacyResourceObservationKind.Missing));
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.Error,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                PrivilegedOwnershipLookupKind.MatchingApplied,
                PrivacyResourceObservationKind.Error));
    }

    [Theory]
    [InlineData((int)PrivilegedOwnershipLookupKind.Missing)]
    [InlineData((int)PrivilegedOwnershipLookupKind.Mismatch)]
    public void OwnershipVerification_NeverRetiresAbsentOrForeignClaims(
        int lookupValue)
    {
        var lookup = (PrivilegedOwnershipLookupKind)lookupValue;
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.NotConfirmed,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                lookup,
                PrivacyResourceObservationKind.MatchesOriginal));
        Assert.Equal(
            PrivilegedOwnershipVerificationAction.NotConfirmed,
            WindowsPrivilegedExecutor.ClassifyOwnershipVerificationForTest(
                lookup,
                PrivacyResourceObservationKind.MatchesProtected));
    }

    private static OriginalPolicyState CreatePolicy(string? originalValue)
    {
        var path = WindowsPolicyManager.PolicyRegistryPath;
        var valueName = WindowsPolicyManager.CameraValueName;
        return new OriginalPolicyState
        {
            ResourceId = $"registry:{PrivacyRegistryHive.LocalMachine}:{PrivacyRegistryView.Default}:{path}:{valueName}",
            OperationId = "operation-a",
            Layer = ProtectionLayer.Secure,
            Target = BlockTarget.Camera,
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            LastUpdatedAtUtc = DateTimeOffset.UnixEpoch,
            RegistryHive = PrivacyRegistryHive.LocalMachine,
            RegistryView = PrivacyRegistryView.Default,
            RegistryPath = path,
            ValueName = valueName,
            ValueExisted = originalValue != null,
            OriginalValueKind = originalValue == null ? PrivacyRegistryValueKind.None : PrivacyRegistryValueKind.DWord,
            OriginalValue = originalValue,
            ProtectedValueExists = true,
            ProtectedValueKind = PrivacyRegistryValueKind.DWord,
            ProtectedValue = WindowsPolicyManager.PolicyDeny.ToString()
        };
    }

    private static OriginalDeviceState CreateCamera() => new()
    {
        ResourceId = @"pnp:USB\VID_1234&PID_5678\CAMERA",
        OperationId = "operation-a",
        Layer = ProtectionLayer.Secure,
        Target = BlockTarget.Camera,
        CapturedAtUtc = DateTimeOffset.UnixEpoch,
        LastUpdatedAtUtc = DateTimeOffset.UnixEpoch,
        InstanceId = @"USB\VID_1234&PID_5678\CAMERA",
        FriendlyName = "Camera",
        DeviceClass = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}",
        IsPresentAtCapture = true,
        OriginalEnabledState = true,
        OriginalProblemCode = 0,
        ProtectedEnabledState = false,
        IsSafelyRestorable = true
    };
}
