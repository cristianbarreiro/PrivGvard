using System.Text.Json.Serialization;

namespace PrivLock.Domain.Models;

/// <summary>
/// Identifies the protection tier that owns a reversible operating-system change.
/// </summary>
public enum ProtectionLayer
{
    Standard,
    Secure
}

public enum PrivacySessionStatus
{
    Active,
    RecoveryIncomplete,
    Restored,
    CompletedWithConflicts
}

/// <summary>
/// Durable write-ahead journal state for one operating-system resource.
/// </summary>
public enum PrivacyResourceJournalState
{
    Captured,
    ApplyPending,
    Applied,
    Unchanged,
    ApplyFailed,
    RestorePending,
    Restored,
    Missing,
    Conflict,
    RestoreFailed
}

public enum PrivacyRegistryHive
{
    CurrentUser,
    LocalMachine
}

public enum PrivacyRegistryView
{
    Default,
    Registry32,
    Registry64
}

/// <summary>
/// Platform-neutral representation of the Registry value kinds that PrivLock can restore exactly.
/// </summary>
public enum PrivacyRegistryValueKind
{
    None,
    String,
    ExpandString,
    Binary,
    DWord,
    MultiString,
    QWord
}

/// <summary>
/// Base journal entry. Derived types contain the exact original and PrivLock-applied values.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$resourceType")]
[JsonDerivedType(typeof(OriginalDeviceState), "device")]
[JsonDerivedType(typeof(OriginalPolicyState), "registry")]
[JsonDerivedType(typeof(OriginalAudioEndpointState), "audioMute")]
public abstract class PrivacyResourceState
{
    public required string ResourceId { get; set; }
    public required string OperationId { get; set; }
    public ProtectionLayer Layer { get; set; }
    public BlockTarget Target { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public PrivacyResourceJournalState JournalState { get; set; } = PrivacyResourceJournalState.Captured;
    public bool ModifiedByPrivLock { get; set; }
    public bool OwnershipUncertain { get; set; }
    public bool ExecutionMayStillBeInFlight { get; set; }
    public int RestoreAttempts { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore]
    public abstract bool RequiresModification { get; }
}

public sealed class OriginalDeviceState : PrivacyResourceState
{
    public required string InstanceId { get; set; }
    public required string FriendlyName { get; set; }
    public required string DeviceClass { get; set; }
    public bool IsPresentAtCapture { get; set; }
    public bool OriginalEnabledState { get; set; }
    public uint? OriginalProblemCode { get; set; }
    public bool ProtectedEnabledState { get; set; }

    /// <summary>
    /// Devices in an unknown/pre-existing problem state are deliberately not modified.
    /// </summary>
    public bool IsSafelyRestorable { get; set; }

    public override bool RequiresModification =>
        IsSafelyRestorable && OriginalEnabledState != ProtectedEnabledState;
}

public sealed class OriginalPolicyState : PrivacyResourceState
{
    public PrivacyRegistryHive RegistryHive { get; set; }
    public PrivacyRegistryView RegistryView { get; set; }
    public required string RegistryPath { get; set; }
    public required string ValueName { get; set; }

    public bool ValueExisted { get; set; }
    public PrivacyRegistryValueKind OriginalValueKind { get; set; }

    /// <summary>
    /// Canonical JSON/base64/string representation interpreted according to OriginalValueKind.
    /// Null is valid only when ValueExisted is false.
    /// </summary>
    public string? OriginalValue { get; set; }

    public bool ProtectedValueExists { get; set; } = true;
    public PrivacyRegistryValueKind ProtectedValueKind { get; set; }
    public string? ProtectedValue { get; set; }

    public override bool RequiresModification =>
        ValueExisted != ProtectedValueExists ||
        OriginalValueKind != ProtectedValueKind ||
        !string.Equals(OriginalValue, ProtectedValue, StringComparison.Ordinal);
}

public sealed class OriginalAudioEndpointState : PrivacyResourceState
{
    public required string EndpointId { get; set; }
    public bool OriginalMutedState { get; set; }
    public bool ProtectedMutedState { get; set; } = true;

    public override bool RequiresModification => OriginalMutedState != ProtectedMutedState;
}

/// <summary>
/// Durable, per-user reversible protection session. IsActive remains true until every retryable
/// PrivLock-owned change has either been restored or classified as a non-overwritable conflict.
/// </summary>
public sealed class PrivacySession
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int OwnerProcessId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool WasRestored { get; set; }
    public PrivacySessionStatus Status { get; set; } = PrivacySessionStatus.Active;
    public string? LastOperationId { get; set; }
    public List<PrivacyResourceState> Resources { get; set; } = [];
}
