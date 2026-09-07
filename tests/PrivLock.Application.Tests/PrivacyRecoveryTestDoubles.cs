using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Application.Tests;

/// <summary>
/// In-memory durable-store fake. Every load/save crosses a clone boundary so tests cannot pass
/// merely because the service and the fake accidentally share the same mutable object graph.
/// </summary>
internal sealed class RecordingPrivacySessionStore : IPrivacySessionStore
{
    private readonly object _sync = new();
    private PrivacySession? _session;
    private readonly List<PrivacySession> _savedSnapshots = [];
    private int _saveAttempts;

    public RecordingPrivacySessionStore(PrivacySession? initialSession = null)
    {
        _session = initialSession is null ? null : PrivacyRecoveryTestData.Clone(initialSession);
    }

    public Action<PrivacySession>? AfterSave { get; set; }
    public Exception? SaveException { get; set; }
    public Func<int, Exception?>? SaveFailureFactory { get; set; }
    public int SaveAttempts => Volatile.Read(ref _saveAttempts);

    public IReadOnlyList<PrivacySession> SavedSnapshots
    {
        get
        {
            lock (_sync)
            {
                return _savedSnapshots
                    .Select(PrivacyRecoveryTestData.Clone)
                    .ToList();
            }
        }
    }

    public PrivacySession? Current => Load();

    public PrivacySession? Load()
    {
        lock (_sync)
        {
            return _session is null ? null : PrivacyRecoveryTestData.Clone(_session);
        }
    }

    public void Save(PrivacySession session)
    {
        var attempt = Interlocked.Increment(ref _saveAttempts);
        var saveFailure = SaveFailureFactory?.Invoke(attempt) ?? SaveException;
        if (saveFailure is not null)
            throw saveFailure;

        PrivacySession committed;
        lock (_sync)
        {
            committed = PrivacyRecoveryTestData.Clone(session);
            _session = committed;
            _savedSnapshots.Add(PrivacyRecoveryTestData.Clone(committed));
        }

        AfterSave?.Invoke(PrivacyRecoveryTestData.Clone(committed));
    }
}

/// <summary>
/// State-machine fake for the exact-state platform adapter. Successful restore calls transition
/// a resource to MatchesOriginal; scripted failures leave the protected state in place.
/// </summary>
internal sealed class ScriptedPrivacySessionPlatformAdapter : IPrivacySessionPlatformAdapter
{
    private int _completedRecoveryPasses;
    private readonly object _sync = new();
    private readonly Dictionary<string, PrivacyResourceObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<OperationResult>> _restoreResults =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PrivacyOwnershipAttestation> _ownershipAttestations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _observeCalls = [];
    private readonly List<string> _restoreCalls = [];
    private readonly List<PrivacyResourceState> _restoredResources = [];

    public bool SupportsPersistentRecovery { get; set; } = true;
    public OperationResult QuiescenceResult { get; set; } = OperationResult.Ok();
    public int QuiescenceCalls { get; private set; }
    public IReadOnlyList<PrivacyResourceState> CapturedResources { get; set; } = [];
    public Action? OnCapture { get; set; }
    public Func<PrivacyResourceState, int, CancellationToken, Task>? BeforeRestoreAsync { get; set; }
    public Action<PrivacyResourceState, OperationResult>? AfterRestore { get; set; }
    public int CompletedRecoveryPasses => Volatile.Read(ref _completedRecoveryPasses);

    public Task<OperationResult> EnsureMutationQuiescenceAsync(
        CancellationToken cancellationToken = default)
    {
        QuiescenceCalls++;
        return Task.FromResult(QuiescenceResult);
    }

    public IReadOnlyList<string> ObserveCalls
    {
        get
        {
            lock (_sync)
                return _observeCalls.ToList();
        }
    }

    public IReadOnlyList<string> RestoreCalls
    {
        get
        {
            lock (_sync)
                return _restoreCalls.ToList();
        }
    }

    public IReadOnlyList<PrivacyResourceState> RestoredResources
    {
        get
        {
            lock (_sync)
                return _restoredResources.Select(PrivacyRecoveryTestData.Clone).ToList();
        }
    }

    public void SetObservation(
        string resourceId,
        PrivacyResourceObservationKind kind,
        string? errorMessage = null)
    {
        lock (_sync)
            _observations[resourceId] = new PrivacyResourceObservation(kind, errorMessage);
    }

    public void EnqueueRestoreResults(string resourceId, params OperationResult[] results)
    {
        lock (_sync)
            _restoreResults[resourceId] = new Queue<OperationResult>(results);
    }

    public void SetOwnershipAttestation(
        string resourceId,
        PrivacyOwnershipAttestationKind kind,
        string? errorMessage = null)
    {
        lock (_sync)
            _ownershipAttestations[resourceId] = new PrivacyOwnershipAttestation(kind, errorMessage);
    }

    public Task<IReadOnlyList<PrivacyResourceState>> CaptureAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnCapture?.Invoke();
        return Task.FromResult<IReadOnlyList<PrivacyResourceState>>(
            CapturedResources.Select(PrivacyRecoveryTestData.Clone).ToList());
    }

    public Task<PrivacyResourceObservation> ObserveAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _observeCalls.Add(resource.ResourceId);
            return Task.FromResult(_observations.TryGetValue(resource.ResourceId, out var observation)
                ? observation
                : new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal));
        }
    }

    public Task<PrivacyOwnershipAttestation> VerifyOwnershipAttestationAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_ownershipAttestations.TryGetValue(resource.ResourceId, out var attestation)
                ? attestation
                : new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Unsupported));
        }
    }

    public async Task<OperationResult> RestoreOriginalAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        int attempt;
        lock (_sync)
        {
            _restoreCalls.Add(resource.ResourceId);
            _restoredResources.Add(PrivacyRecoveryTestData.Clone(resource));
            attempt = _restoreCalls.Count(id =>
                string.Equals(id, resource.ResourceId, StringComparison.OrdinalIgnoreCase));
        }

        if (BeforeRestoreAsync is not null)
            await BeforeRestoreAsync(resource, attempt, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        OperationResult result;
        lock (_sync)
        {
            result = _restoreResults.TryGetValue(resource.ResourceId, out var results) && results.Count > 0
                ? results.Dequeue()
                : OperationResult.Ok();

            if (result.Success)
            {
                _observations[resource.ResourceId] =
                    new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
            }
        }

        AfterRestore?.Invoke(PrivacyRecoveryTestData.Clone(resource), result);

        return result;
    }

    public void CompleteRecoveryPass() => Interlocked.Increment(ref _completedRecoveryPasses);
}

internal static class PrivacyRecoveryTestData
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public static OriginalDeviceState Device(
        string id,
        bool originalEnabled = true,
        bool protectedEnabled = false,
        PrivacyResourceJournalState journalState = PrivacyResourceJournalState.Applied,
        bool modifiedByPrivLock = true,
        ProtectionLayer layer = ProtectionLayer.Standard,
        BlockTarget target = BlockTarget.Camera,
        bool safelyRestorable = true) => new()
    {
        ResourceId = $"device:{id}",
        OperationId = "Op-Test",
        Layer = layer,
        Target = target,
        CapturedAtUtc = CapturedAt,
        LastUpdatedAtUtc = CapturedAt,
        JournalState = journalState,
        ModifiedByPrivLock = modifiedByPrivLock,
        InstanceId = id,
        FriendlyName = id,
        DeviceClass = target == BlockTarget.Camera ? "Camera" : "AudioEndpoint",
        IsPresentAtCapture = true,
        OriginalEnabledState = originalEnabled,
        OriginalProblemCode = originalEnabled ? 0u : 22u,
        ProtectedEnabledState = protectedEnabled,
        IsSafelyRestorable = safelyRestorable
    };

    public static OriginalPolicyState Policy(
        string id,
        bool valueExisted,
        string? originalValue,
        PrivacyRegistryValueKind originalKind = PrivacyRegistryValueKind.DWord,
        PrivacyResourceJournalState journalState = PrivacyResourceJournalState.Applied,
        ProtectionLayer layer = ProtectionLayer.Secure,
        BlockTarget target = BlockTarget.Camera) => new()
    {
        ResourceId = $"registry:{id}",
        OperationId = "Op-Test",
        Layer = layer,
        Target = target,
        CapturedAtUtc = CapturedAt,
        LastUpdatedAtUtc = CapturedAt,
        JournalState = journalState,
        ModifiedByPrivLock = true,
        RegistryHive = layer == ProtectionLayer.Secure
            ? PrivacyRegistryHive.LocalMachine
            : PrivacyRegistryHive.CurrentUser,
        RegistryView = PrivacyRegistryView.Default,
        RegistryPath = $@"Software\PrivLock.Tests\{id}",
        ValueName = "PrivacyValue",
        ValueExisted = valueExisted,
        OriginalValueKind = valueExisted ? originalKind : PrivacyRegistryValueKind.None,
        OriginalValue = valueExisted ? originalValue : null,
        ProtectedValueExists = true,
        ProtectedValueKind = PrivacyRegistryValueKind.DWord,
        ProtectedValue = "2"
    };

    public static OriginalAudioEndpointState AudioEndpoint(
        string id,
        PrivacyResourceJournalState journalState = PrivacyResourceJournalState.Applied) => new()
    {
        ResourceId = $"audio-mute:{id}",
        OperationId = "Op-Test",
        Layer = ProtectionLayer.Standard,
        Target = BlockTarget.Microphone,
        CapturedAtUtc = CapturedAt,
        LastUpdatedAtUtc = CapturedAt,
        JournalState = journalState,
        ModifiedByPrivLock = true,
        EndpointId = id,
        OriginalMutedState = false,
        ProtectedMutedState = true
    };

    public static PrivacySession ActiveSession(params PrivacyResourceState[] resources) => new()
    {
        SessionId = Guid.NewGuid(),
        CreatedAtUtc = CapturedAt,
        UpdatedAtUtc = CapturedAt,
        OwnerProcessId = 1234,
        IsActive = true,
        WasRestored = false,
        Status = PrivacySessionStatus.Active,
        LastOperationId = "Op-Test",
        Resources = resources.Select(Clone).ToList()
    };

    public static PrivacySession Clone(PrivacySession source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        SessionId = source.SessionId,
        CreatedAtUtc = source.CreatedAtUtc,
        UpdatedAtUtc = source.UpdatedAtUtc,
        CompletedAtUtc = source.CompletedAtUtc,
        OwnerProcessId = source.OwnerProcessId,
        IsActive = source.IsActive,
        WasRestored = source.WasRestored,
        Status = source.Status,
        LastOperationId = source.LastOperationId,
        Resources = source.Resources.Select(Clone).ToList()
    };

    public static PrivacyResourceState Clone(PrivacyResourceState source) => source switch
    {
        OriginalDeviceState device => new OriginalDeviceState
        {
            ResourceId = device.ResourceId,
            OperationId = device.OperationId,
            Layer = device.Layer,
            Target = device.Target,
            CapturedAtUtc = device.CapturedAtUtc,
            LastUpdatedAtUtc = device.LastUpdatedAtUtc,
            JournalState = device.JournalState,
            ModifiedByPrivLock = device.ModifiedByPrivLock,
            OwnershipUncertain = device.OwnershipUncertain,
            ExecutionMayStillBeInFlight = device.ExecutionMayStillBeInFlight,
            RestoreAttempts = device.RestoreAttempts,
            LastError = device.LastError,
            InstanceId = device.InstanceId,
            FriendlyName = device.FriendlyName,
            DeviceClass = device.DeviceClass,
            IsPresentAtCapture = device.IsPresentAtCapture,
            OriginalEnabledState = device.OriginalEnabledState,
            OriginalProblemCode = device.OriginalProblemCode,
            ProtectedEnabledState = device.ProtectedEnabledState,
            IsSafelyRestorable = device.IsSafelyRestorable
        },
        OriginalPolicyState policy => new OriginalPolicyState
        {
            ResourceId = policy.ResourceId,
            OperationId = policy.OperationId,
            Layer = policy.Layer,
            Target = policy.Target,
            CapturedAtUtc = policy.CapturedAtUtc,
            LastUpdatedAtUtc = policy.LastUpdatedAtUtc,
            JournalState = policy.JournalState,
            ModifiedByPrivLock = policy.ModifiedByPrivLock,
            OwnershipUncertain = policy.OwnershipUncertain,
            ExecutionMayStillBeInFlight = policy.ExecutionMayStillBeInFlight,
            RestoreAttempts = policy.RestoreAttempts,
            LastError = policy.LastError,
            RegistryHive = policy.RegistryHive,
            RegistryView = policy.RegistryView,
            RegistryPath = policy.RegistryPath,
            ValueName = policy.ValueName,
            ValueExisted = policy.ValueExisted,
            OriginalValueKind = policy.OriginalValueKind,
            OriginalValue = policy.OriginalValue,
            ProtectedValueExists = policy.ProtectedValueExists,
            ProtectedValueKind = policy.ProtectedValueKind,
            ProtectedValue = policy.ProtectedValue
        },
        OriginalAudioEndpointState endpoint => new OriginalAudioEndpointState
        {
            ResourceId = endpoint.ResourceId,
            OperationId = endpoint.OperationId,
            Layer = endpoint.Layer,
            Target = endpoint.Target,
            CapturedAtUtc = endpoint.CapturedAtUtc,
            LastUpdatedAtUtc = endpoint.LastUpdatedAtUtc,
            JournalState = endpoint.JournalState,
            ModifiedByPrivLock = endpoint.ModifiedByPrivLock,
            OwnershipUncertain = endpoint.OwnershipUncertain,
            ExecutionMayStillBeInFlight = endpoint.ExecutionMayStillBeInFlight,
            RestoreAttempts = endpoint.RestoreAttempts,
            LastError = endpoint.LastError,
            EndpointId = endpoint.EndpointId,
            OriginalMutedState = endpoint.OriginalMutedState,
            ProtectedMutedState = endpoint.ProtectedMutedState
        },
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.GetType().Name, null)
    };
}

internal sealed class RecoveryHostDependencies :
    IDeviceProtectionProvider,
    IDeviceDetector,
    IPlatformCapabilityProvider,
    IStateStore
{
    private DesiredState _desiredState = new();
    private int _saveAttempts;
    public int SaveCount { get; private set; }
    public int SaveAttempts => Volatile.Read(ref _saveAttempts);
    public Func<int, Exception?>? SaveFailureFactory { get; set; }
    public OperationResult StandardEnableResult { get; set; } = OperationResult.Ok();
    public Action? BeforeStandardEnableReturn { get; set; }
    public int StandardEnableCalls { get; private set; }
    public int StandardDisableCalls { get; private set; }

    public FullProtectionState CurrentProtectionState { get; set; } = new()
    {
        Camera = new TargetProtectionStatus
        {
            Target = BlockTarget.Camera,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable
        },
        Microphone = new TargetProtectionStatus
        {
            Target = BlockTarget.Microphone,
            StandardState = StandardProtectionState.Inactive,
            SecureState = SecureProtectionState.Unavailable
        }
    };

    public PlatformCapabilities Capabilities { get; } = new();

    public PlatformInfo PlatformInfo { get; } = new()
    {
        OperatingSystemName = "TestOS",
        OsVersion = "1",
        Architecture = "x64",
        Is64Bit = true,
        IsElevated = false
    };

    public Task<OperationResult> EnableStandardProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        StandardEnableCalls++;
        BeforeStandardEnableReturn?.Invoke();
        return Task.FromResult(StandardEnableResult);
    }

    public Task<OperationResult> DisableStandardProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default)
    {
        StandardDisableCalls++;
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<OperationResult> EnableSecureProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Ok());

    public Task<OperationResult> DisableSecureProtectionAsync(
        BlockTarget target,
        CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Ok());

    public Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentProtectionState);

    public Task<IReadOnlyList<DeviceInfo>> DetectCamerasAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

    public Task<IReadOnlyList<DeviceInfo>> DetectMicrophonesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

    public Task<IReadOnlyList<DeviceInfo>> DetectAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

    public DesiredState Load() => new()
    {
        CameraStandard = _desiredState.CameraStandard,
        CameraSecure = _desiredState.CameraSecure,
        MicrophoneStandard = _desiredState.MicrophoneStandard,
        MicrophoneSecure = _desiredState.MicrophoneSecure,
        AdvancedProtectionEnabled = _desiredState.AdvancedProtectionEnabled,
        Language = _desiredState.Language,
        Autostart = _desiredState.Autostart
    };

    public void Save(DesiredState state)
    {
        var attempt = Interlocked.Increment(ref _saveAttempts);
        var failure = SaveFailureFactory?.Invoke(attempt);
        if (failure is not null)
            throw failure;

        SaveCount++;
        _desiredState = state;
    }

    public void SetDesiredState(DesiredState state) => _desiredState = state;
}
