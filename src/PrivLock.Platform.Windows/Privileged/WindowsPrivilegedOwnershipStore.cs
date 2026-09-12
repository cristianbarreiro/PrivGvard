using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PrivLock.Domain.Models;
using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Policies;

namespace PrivLock.Platform.Windows.Privileged;

internal enum PrivilegedOwnershipStage
{
    ApplyPrepared = 1,
    ApplyPending = 2,
    Applied = 3,
    RestorePending = 4
}

internal enum PrivilegedOwnershipLookupKind
{
    Missing,
    MatchingApplyPrepared,
    MatchingApplyPending,
    MatchingApplied,
    MatchingRestorePending,
    Mismatch,
    InvalidOrUnavailable
}

internal sealed record PrivilegedResourceIdentity(
    string ResourceKey,
    string Fingerprint,
    string OwnerUserSid);

/// <summary>
/// Produces a deterministic digest over the exact privileged resource transition. The digest is
/// intentionally independent of mutable WAL bookkeeping (timestamps, operation id, journal state)
/// so an already-owned resource can be re-applied without replacing its original snapshot.
/// </summary>
internal static class WindowsPrivilegedOwnershipFingerprint
{
    private const int FingerprintVersion = 1;

    internal static bool TryCreate(
        PrivacyResourceState resource,
        string ownerUserSid,
        out PrivilegedResourceIdentity? identity)
    {
        identity = null;
        if (!TryNormalizeSid(ownerUserSid, out var normalizedSid) ||
            resource.Layer != ProtectionLayer.Secure ||
            resource.Target is not (BlockTarget.Camera or BlockTarget.Microphone))
        {
            return false;
        }

        using var canonical = new MemoryStream(capacity: 1024);
        using var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true);
        writer.Write(FingerprintVersion);
        WriteString(writer, normalizedSid);

        string canonicalResourceId;
        switch (resource)
        {
            case OriginalPolicyState policy when IsValidPolicy(policy):
                writer.Write((byte)1);
                canonicalResourceId = policy.ResourceId.ToUpperInvariant();
                WriteString(writer, canonicalResourceId);
                writer.Write((int)policy.RegistryHive);
                writer.Write((int)policy.RegistryView);
                WriteString(writer, policy.RegistryPath.ToUpperInvariant());
                WriteString(writer, policy.ValueName.ToUpperInvariant());
                writer.Write(policy.ValueExisted);
                writer.Write((int)policy.OriginalValueKind);
                WriteString(writer, policy.OriginalValue);
                writer.Write(policy.ProtectedValueExists);
                writer.Write((int)policy.ProtectedValueKind);
                WriteString(writer, policy.ProtectedValue);
                break;

            case OriginalDeviceState device when IsValidDevice(device):
                writer.Write((byte)2);
                canonicalResourceId = device.ResourceId.ToUpperInvariant();
                WriteString(writer, canonicalResourceId);
                WriteString(writer, device.InstanceId.ToUpperInvariant());
                WriteString(writer, device.DeviceClass.ToUpperInvariant());
                writer.Write(device.OriginalEnabledState);
                writer.Write(device.OriginalProblemCode!.Value);
                writer.Write(device.ProtectedEnabledState);
                writer.Write(device.IsSafelyRestorable);
                break;

            default:
                return false;
        }

        writer.Flush();
        var fingerprint = Convert.ToHexString(SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))));
        var resourceKeyMaterial = Encoding.UTF8.GetBytes($"{FingerprintVersion}\0{resource.GetType().Name}\0{canonicalResourceId}");
        var resourceKey = Convert.ToHexString(SHA256.HashData(resourceKeyMaterial));
        identity = new PrivilegedResourceIdentity(resourceKey, fingerprint, normalizedSid);
        return true;
    }

    private static bool IsValidPolicy(OriginalPolicyState policy)
    {
        var targetMatches = policy.ValueName switch
        {
            WindowsPolicyManager.CameraValueName => policy.Target == BlockTarget.Camera,
            WindowsPolicyManager.MicrophoneValueName => policy.Target == BlockTarget.Microphone,
            _ => false
        };
        return policy.RegistryHive == PrivacyRegistryHive.LocalMachine &&
               policy.RegistryView == PrivacyRegistryView.Default &&
               string.Equals(policy.RegistryPath, WindowsPolicyManager.PolicyRegistryPath, StringComparison.OrdinalIgnoreCase) &&
               targetMatches &&
               string.Equals(
                   policy.ResourceId,
                   $"registry:{policy.RegistryHive}:{policy.RegistryView}:{policy.RegistryPath}:{policy.ValueName}",
                   StringComparison.OrdinalIgnoreCase) &&
               policy.ProtectedValueExists &&
               policy.ProtectedValueKind == PrivacyRegistryValueKind.DWord &&
               string.Equals(
                   policy.ProtectedValue,
                   WindowsPolicyManager.PolicyDeny.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal) &&
               WindowsRegistryValueCodec.IsSupportedValue(policy);
    }

    private static bool IsValidDevice(OriginalDeviceState device)
    {
        const string cameraClass = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";
        const string audioEndpointClass = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";
        var allowedClass = device.Target == BlockTarget.Camera
            ? string.Equals(device.DeviceClass, cameraClass, StringComparison.OrdinalIgnoreCase)
            : string.Equals(device.DeviceClass, audioEndpointClass, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(device.InstanceId) &&
               device.InstanceId.Length <= 1024 &&
               device.InstanceId.IndexOfAny(['|', '\t', '\r', '\n']) < 0 &&
               string.Equals(device.ResourceId, $"pnp:{device.InstanceId}", StringComparison.OrdinalIgnoreCase) &&
               allowedClass &&
               device.IsSafelyRestorable &&
               device.OriginalEnabledState &&
               device.OriginalProblemCode == 0 &&
               !device.ProtectedEnabledState;
    }

    private static bool TryNormalizeSid(string candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 256)
            return false;
        try
        {
            normalized = new SecurityIdentifier(candidate).Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value == null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

/// <summary>
/// Machine-protected proof that an elevated PrivLock worker observed an exact original state and
/// initiated its corresponding protected transition. LocalAppData WAL data alone never grants a
/// restore or an idempotent protected-state adoption.
/// </summary>
internal static class WindowsPrivilegedOwnershipStore
{
    private const int RecordVersion = 2;
    private const string RootPath = @"SOFTWARE\PrivLock\PrivilegedOwnership\v2";
    private const string ValuePrefix = "R_";
    private const int MaxRecordCharacters = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsRestoreAuthority(PrivilegedOwnershipLookupKind lookup) =>
        lookup is PrivilegedOwnershipLookupKind.MatchingApplyPending or
            PrivilegedOwnershipLookupKind.MatchingApplied or
            PrivilegedOwnershipLookupKind.MatchingRestorePending;

    internal static PrivilegedOwnershipLookupKind Lookup(
        PrivilegedResourceIdentity identity,
        out string? error)
    {
        error = null;
        try
        {
            using var baseKey = OpenMachineBaseKey();
            using var root = baseKey.OpenSubKey(RootPath, writable: false);
            if (root == null)
                return PrivilegedOwnershipLookupKind.Missing;

            var raw = root.GetValue(ValueName(identity), null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw == null)
                return PrivilegedOwnershipLookupKind.Missing;
            if (raw is not string json || json.Length is <= 0 or > MaxRecordCharacters)
            {
                error = "The privileged ownership record has an invalid representation.";
                return PrivilegedOwnershipLookupKind.InvalidOrUnavailable;
            }

            var record = JsonSerializer.Deserialize<StoredOwnershipRecord>(json, JsonOptions);
            if (!IsStructurallyValid(record))
            {
                error = "The privileged ownership record failed structural validation.";
                return PrivilegedOwnershipLookupKind.InvalidOrUnavailable;
            }
            if (!FixedEquals(record!.ResourceKey, identity.ResourceKey) ||
                !FixedEquals(record.Fingerprint, identity.Fingerprint) ||
                !string.Equals(record.OwnerUserSid, identity.OwnerUserSid, StringComparison.OrdinalIgnoreCase))
            {
                error = "A different privileged ownership record already exists for this resource.";
                return PrivilegedOwnershipLookupKind.Mismatch;
            }

            return record.Stage switch
            {
                PrivilegedOwnershipStage.ApplyPrepared => PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
                PrivilegedOwnershipStage.ApplyPending => PrivilegedOwnershipLookupKind.MatchingApplyPending,
                PrivilegedOwnershipStage.Applied => PrivilegedOwnershipLookupKind.MatchingApplied,
                PrivilegedOwnershipStage.RestorePending => PrivilegedOwnershipLookupKind.MatchingRestorePending,
                _ => PrivilegedOwnershipLookupKind.InvalidOrUnavailable
            };
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            error = $"Privileged ownership lookup failed: {ex.Message}";
            return PrivilegedOwnershipLookupKind.InvalidOrUnavailable;
        }
    }

    internal static bool TryCreateApplyPrepared(
        PrivilegedResourceIdentity identity,
        out string? error)
    {
        error = null;
        var lookup = Lookup(identity, out error);
        if (lookup == PrivilegedOwnershipLookupKind.MatchingApplyPrepared)
            return true;
        if (lookup != PrivilegedOwnershipLookupKind.Missing)
        {
            error ??= "The resource already has incompatible privileged ownership state.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var record = new StoredOwnershipRecord
        {
            Version = RecordVersion,
            ResourceKey = identity.ResourceKey,
            Fingerprint = identity.Fingerprint,
            OwnerUserSid = identity.OwnerUserSid,
            Stage = PrivilegedOwnershipStage.ApplyPrepared,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        return TryWriteAndVerify(identity, record, PrivilegedOwnershipLookupKind.MatchingApplyPrepared, out error);
    }

    internal static bool TryTransition(
        PrivilegedResourceIdentity identity,
        PrivilegedOwnershipLookupKind expected,
        PrivilegedOwnershipStage nextStage,
        out string? error)
    {
        var actual = Lookup(identity, out error);
        if (actual != expected)
        {
            error ??= $"Privileged ownership transition rejected: expected {expected}, actual {actual}.";
            return false;
        }

        try
        {
            using var baseKey = OpenMachineBaseKey();
            using var root = baseKey.OpenSubKey(RootPath, writable: true);
            if (root == null)
            {
                error = "The privileged ownership key disappeared during transition.";
                return false;
            }

            var json = root.GetValue(ValueName(identity), null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var record = json == null ? null : JsonSerializer.Deserialize<StoredOwnershipRecord>(json, JsonOptions);
            if (!Matches(record, identity, expected))
            {
                error = "The privileged ownership record changed during transition.";
                return false;
            }

            record!.Stage = nextStage;
            var now = DateTimeOffset.UtcNow;
            record.UpdatedAtUtc = now < record.CreatedAtUtc ? record.CreatedAtUtc : now;
            return TryWriteAndVerify(
                identity,
                record,
                LookupForStage(nextStage),
                out error,
                alreadyOpenRoot: root);
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            error = $"Privileged ownership transition failed: {ex.Message}";
            return false;
        }
    }

    internal static bool TryDelete(
        PrivilegedResourceIdentity identity,
        PrivilegedOwnershipLookupKind expected,
        out string? error)
    {
        var actual = Lookup(identity, out error);
        if (actual != expected)
        {
            error ??= $"Privileged ownership deletion rejected: expected {expected}, actual {actual}.";
            return false;
        }

        try
        {
            using var baseKey = OpenMachineBaseKey();
            using var root = baseKey.OpenSubKey(RootPath, writable: true);
            if (root == null)
            {
                error = "The privileged ownership key disappeared during deletion.";
                return false;
            }

            var json = root.GetValue(ValueName(identity), null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var record = json == null ? null : JsonSerializer.Deserialize<StoredOwnershipRecord>(json, JsonOptions);
            if (!Matches(record, identity, expected))
            {
                error = "The privileged ownership record changed during deletion.";
                return false;
            }

            root.DeleteValue(ValueName(identity), throwOnMissingValue: true);
            root.Flush();
            if (root.GetValue(ValueName(identity), null, RegistryValueOptions.DoNotExpandEnvironmentNames) != null)
            {
                error = "The privileged ownership record deletion could not be verified.";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            error = $"Privileged ownership deletion failed: {ex.Message}";
            return false;
        }
    }

    internal static bool RecordMatchesIdentityForTest(
        string resourceKey,
        string fingerprint,
        string ownerUserSid,
        PrivilegedResourceIdentity identity) =>
        FixedEquals(resourceKey, identity.ResourceKey) &&
        FixedEquals(fingerprint, identity.Fingerprint) &&
        string.Equals(ownerUserSid, identity.OwnerUserSid, StringComparison.OrdinalIgnoreCase);

    private static bool TryWriteAndVerify(
        PrivilegedResourceIdentity identity,
        StoredOwnershipRecord record,
        PrivilegedOwnershipLookupKind expected,
        out string? error,
        RegistryKey? alreadyOpenRoot = null)
    {
        var ownsRoot = alreadyOpenRoot == null;
        RegistryKey? root = alreadyOpenRoot;
        try
        {
            root ??= CreateProtectedRoot();
            var json = JsonSerializer.Serialize(record, JsonOptions);
            if (json.Length > MaxRecordCharacters)
            {
                error = "The privileged ownership record exceeds the storage limit.";
                return false;
            }

            root.SetValue(ValueName(identity), json, RegistryValueKind.String);
            root.Flush();
            var actual = Lookup(identity, out error);
            if (actual != expected)
            {
                error ??= "The privileged ownership record could not be verified after writing.";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            error = $"Privileged ownership write failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (ownsRoot)
                root?.Dispose();
        }
    }

    private static RegistryKey CreateProtectedRoot()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new RegistryAccessRule(
            administrators,
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            system,
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        using var baseKey = OpenMachineBaseKey();
        var root = baseKey.CreateSubKey(
            RootPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryOptions.None,
            security) ?? throw new IOException("Could not create the privileged ownership key.");
        root.SetAccessControl(security);
        return root;
    }

    private static RegistryKey OpenMachineBaseKey() =>
        RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);

    private static string ValueName(PrivilegedResourceIdentity identity) => ValuePrefix + identity.ResourceKey;

    private static bool IsStructurallyValid(StoredOwnershipRecord? record) =>
        record != null &&
        record.Version == RecordVersion &&
        IsUpperHex(record.ResourceKey, 64) &&
        IsUpperHex(record.Fingerprint, 64) &&
        !string.IsNullOrWhiteSpace(record.OwnerUserSid) &&
        record.OwnerUserSid.Length <= 256 &&
        Enum.IsDefined(record.Stage) &&
        record.CreatedAtUtc != default &&
        record.UpdatedAtUtc != default;

    private static bool Matches(
        StoredOwnershipRecord? record,
        PrivilegedResourceIdentity identity,
        PrivilegedOwnershipLookupKind expected) =>
        IsStructurallyValid(record) &&
        FixedEquals(record!.ResourceKey, identity.ResourceKey) &&
        FixedEquals(record.Fingerprint, identity.Fingerprint) &&
        string.Equals(record.OwnerUserSid, identity.OwnerUserSid, StringComparison.OrdinalIgnoreCase) &&
        LookupForStage(record.Stage) == expected;

    private static PrivilegedOwnershipLookupKind LookupForStage(PrivilegedOwnershipStage stage) => stage switch
    {
        PrivilegedOwnershipStage.ApplyPrepared => PrivilegedOwnershipLookupKind.MatchingApplyPrepared,
        PrivilegedOwnershipStage.ApplyPending => PrivilegedOwnershipLookupKind.MatchingApplyPending,
        PrivilegedOwnershipStage.Applied => PrivilegedOwnershipLookupKind.MatchingApplied,
        PrivilegedOwnershipStage.RestorePending => PrivilegedOwnershipLookupKind.MatchingRestorePending,
        _ => PrivilegedOwnershipLookupKind.InvalidOrUnavailable
    };

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static bool IsUpperHex(string value, int expectedLength) =>
        value.Length == expectedLength &&
        value.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") < 0;

    private static bool IsStoreException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        global::System.Security.SecurityException or
        ArgumentException or
        InvalidOperationException or
        JsonException;

    private sealed class StoredOwnershipRecord
    {
        public int Version { get; set; }
        public required string ResourceKey { get; set; }
        public required string Fingerprint { get; set; }
        public required string OwnerUserSid { get; set; }
        public PrivilegedOwnershipStage Stage { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
