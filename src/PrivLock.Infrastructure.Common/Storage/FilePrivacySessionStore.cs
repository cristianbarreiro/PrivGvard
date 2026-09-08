using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using PrivLock.Domain.Models;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Infrastructure.Common.Storage;

/// <summary>
/// Crash-resilient JSON journal stored in the current user's LocalAppData profile.
/// Writes use a same-directory temporary file, durable flush, and atomic replacement with backup.
/// </summary>
public sealed class FilePrivacySessionStore : IPrivacySessionStore
{
    private const long MaximumJournalBytes = 2 * 1024 * 1024;
    private const int MaximumResources = 1024;
    private const string AppPrivacyPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private const string CameraPolicyName = "LetAppsAccessCamera";
    private const string MicrophonePolicyName = "LetAppsAccessMicrophone";
    private const string CameraConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private const string CameraConsentNonPackagedPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged";
    private const string MicrophoneConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string MicrophoneConsentNonPackagedPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";
    private const string CameraClassGuid = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";
    private const string AudioEndpointClassGuid = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}";
    private static readonly ILogger Log = Serilog.Log.ForContext<FilePrivacySessionStore>();
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly object _pathLock;
    private readonly IActivePrivacySessionMarker _activeSessionMarker;

    public FilePrivacySessionStore(string? customDirectory = null)
        : this(customDirectory, new NoOpActivePrivacySessionMarker())
    {
    }

    public FilePrivacySessionStore(IActivePrivacySessionMarker activeSessionMarker)
        : this(customDirectory: null, activeSessionMarker)
    {
    }

    public FilePrivacySessionStore(
        string? customDirectory,
        IActivePrivacySessionMarker activeSessionMarker)
    {
        ArgumentNullException.ThrowIfNull(activeSessionMarker);
        var recoveryDirectory = customDirectory ?? Path.Combine(
            StorageMigrationHelper.GetDefaultDataDirectory(),
            "Recovery");

        Directory.CreateDirectory(recoveryDirectory);
        _filePath = Path.GetFullPath(Path.Combine(recoveryDirectory, "privacy-session-v1.json"));
        _backupPath = _filePath + ".bak";
        _pathLock = PathLocks.GetOrAdd(_filePath, static _ => new object());
        _activeSessionMarker = activeSessionMarker;
    }

    public PrivacySession? Load()
    {
        lock (_pathLock)
        {
            PrivacySession session;
            Exception? primaryException = null;
            var primaryMissing = false;
            try
            {
                session = ReadAndValidate(_filePath);
            }
            catch (Exception ex) when (IsRecoverableStorageException(ex))
            {
                primaryException = ex;
                primaryMissing = ex is FileNotFoundException or DirectoryNotFoundException;
                session = null!;
            }

            if (primaryException != null)
            {
                try
                {
                    session = ReadAndValidate(_backupPath);
                }
                catch (Exception backupException) when (IsRecoverableStorageException(backupException))
                {
                    var backupMissing = backupException is FileNotFoundException or DirectoryNotFoundException;
                    if (primaryMissing && backupMissing)
                    {
                        ReconcileMarker(activeSessionId: null);
                        return null;
                    }

                    throw new PrivacySessionStoreException(
                        backupMissing
                            ? "The active privacy recovery journal is unreadable and no backup exists."
                            : "Both the active privacy recovery journal and its backup are unreadable.",
                        backupMissing ? primaryException :
                        new AggregateException(primaryException, backupException));
                }

                // A terminal backup cannot prove that an unreadable primary did not contain a
                // newer active commit. Keep the marker until that ambiguity is resolved.
                if (!primaryMissing && !session.IsActive)
                {
                    throw new PrivacySessionStoreException(
                        "The primary privacy recovery journal is unreadable; its terminal backup cannot authorize retiring the active-session marker.",
                        primaryException);
                }

                Log.Warning(primaryException,
                    "Recovered privacy session {SessionId} from the validated backup journal",
                    session.SessionId);
            }

            // Marker errors are not journal-read errors: never fall back to another revision
            // after selecting a validated journal merely because marker reconciliation failed.
            if (session.IsActive)
            {
                ReconcileMarker(session.SessionId);
            }
            else
            {
                try
                {
                    // A crash may leave a terminal primary and an older active backup. Retire
                    // the marker only after both copies durably contain the terminal state.
                    if (primaryMissing)
                        WriteDurableCopy(_backupPath, _filePath);
                    else
                        WriteDurableCopy(_filePath, _backupPath);
                }
                catch (Exception ex) when (IsRecoverableStorageException(ex))
                {
                    throw new PrivacySessionStoreException(
                        "PrivGvard could not complete both terminal recovery journal copies; the active-session marker was retained.",
                        ex);
                }

                ReconcileMarker(activeSessionId: null);
            }

            return session;
        }
    }

    public void Save(PrivacySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(session);

        lock (_pathLock)
        {
            var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";

            try
            {
                // The marker is the cross-user uninstall barrier. It must be durable before this
                // WAL commit can authorize any following native mutation.
                if (session.IsActive)
                    _activeSessionMarker.EnsureActive(session.SessionId);

                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, session, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                var serializedLength = new FileInfo(tempPath).Length;
                if (serializedLength <= 0 || serializedLength > MaximumJournalBytes)
                {
                    throw new InvalidDataException(
                        $"Serialized privacy recovery journal size {serializedLength} is invalid.");
                }

                if (File.Exists(_filePath))
                {
                    try
                    {
                        File.Replace(tempPath, _filePath, _backupPath, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException ex)
                    {
                        Log.Warning(ex, "Atomic File.Replace is unavailable; using same-volume overwrite rename");
                        WriteDurableCopy(_filePath, _backupPath);
                        File.Move(tempPath, _filePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }

                // Every successful commit is the authorization boundary for a possible native
                // mutation. Keep two durable copies of that exact latest WAL state; a previous
                // revision may omit a resource added to an already-active session.
                WriteDurableCopy(_filePath, _backupPath);

                // Delete only after both terminal WAL copies are durable. Failure intentionally
                // leaves a stale fail-closed marker that the next validated Load can reconcile.
                if (!session.IsActive)
                    _activeSessionMarker.Delete(session.SessionId);

                Log.Debug(
                    "Persisted privacy session {SessionId}: Status={Status}, Resources={ResourceCount}",
                    session.SessionId,
                    session.Status,
                    session.Resources.Count);
            }
            catch (Exception ex) when (IsRecoverableStorageException(ex))
            {
                Log.Error(ex, "Failed to durably persist privacy recovery journal at {Path}", _filePath);
                throw new PrivacySessionStoreException(
                    "PrivGvard could not persist its recovery journal; the operating-system state was not safe to change.",
                    ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception cleanupException) when (IsRecoverableStorageException(cleanupException))
                    {
                        Log.Warning(cleanupException, "Could not delete abandoned journal temp file {Path}", tempPath);
                    }
                }
            }
        }
    }

    private void ReconcileMarker(Guid? activeSessionId)
    {
        try
        {
            _activeSessionMarker.Reconcile(activeSessionId);
        }
        catch (Exception ex) when (IsRecoverableStorageException(ex))
        {
            throw new PrivacySessionStoreException(
                "PrivGvard could not reconcile its machine-visible active-session marker.",
                ex);
        }
    }

    private static PrivacySession ReadAndValidate(string path)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumJournalBytes)
            throw new InvalidDataException($"Privacy recovery journal size {length} is invalid.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var session = JsonSerializer.Deserialize<PrivacySession>(stream, JsonOptions)
            ?? throw new InvalidDataException("Privacy recovery journal deserialized to null.");
        Validate(session);
        return session;
    }

    private static void Validate(PrivacySession session)
    {
        if (session.SchemaVersion != PrivacySession.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported privacy session schema {session.SchemaVersion}; expected {PrivacySession.CurrentSchemaVersion}.");
        }

        if (session.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("Privacy session ID must not be empty.");
        }

        if (session.CreatedAtUtc == default || session.UpdatedAtUtc == default)
        {
            throw new InvalidDataException("Privacy session timestamps are missing.");
        }

        if (session.Resources == null)
            throw new InvalidDataException("Privacy session resource collection is null.");
        if (session.Resources.Count > MaximumResources)
            throw new InvalidDataException($"Privacy session contains more than {MaximumResources} resources.");
        if (!Enum.IsDefined(session.Status))
            throw new InvalidDataException("Privacy session status is invalid.");
        if (session.OwnerProcessId < 0 || session.LastOperationId?.Length > 128)
            throw new InvalidDataException("Privacy session process/operation metadata is invalid.");
        if (session.IsActive != (session.Status is PrivacySessionStatus.Active or PrivacySessionStatus.RecoveryIncomplete))
            throw new InvalidDataException("Privacy session active flag and status disagree.");
        if (session.WasRestored && (session.IsActive || session.Status != PrivacySessionStatus.Restored))
            throw new InvalidDataException("Privacy session restored flag is inconsistent.");

        if (!session.IsActive && session.Resources.Any(resource => resource?.JournalState is
                PrivacyResourceJournalState.Captured or
                PrivacyResourceJournalState.ApplyPending or
                PrivacyResourceJournalState.Applied or
                PrivacyResourceJournalState.RestorePending or
                PrivacyResourceJournalState.Missing or
                PrivacyResourceJournalState.RestoreFailed))
        {
            throw new InvalidDataException("An inactive privacy session contains retryable resources.");
        }

        var conflictCount = session.Resources.Count(resource =>
            resource?.JournalState == PrivacyResourceJournalState.Conflict);
        if (!session.IsActive &&
            (session.Status == PrivacySessionStatus.Restored && (!session.WasRestored || conflictCount != 0) ||
             session.Status == PrivacySessionStatus.CompletedWithConflicts &&
             (session.WasRestored || conflictCount == 0)))
        {
            throw new InvalidDataException("Privacy session completion status is inconsistent with its resources.");
        }

        var duplicate = session.Resources
            .Where(resource => resource != null)
            .GroupBy(resource => resource.ResourceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate != null)
        {
            throw new InvalidDataException($"Duplicate privacy resource ID '{duplicate.Key}'.");
        }

        foreach (var resource in session.Resources)
            ValidateResource(resource);
    }

    private static void ValidateResource(PrivacyResourceState? resource)
    {
        if (resource == null)
            throw new InvalidDataException("Privacy session contains a null resource.");
        if (string.IsNullOrWhiteSpace(resource.ResourceId) || resource.ResourceId.Length > 4096 ||
            string.IsNullOrWhiteSpace(resource.OperationId) || resource.OperationId.Length > 128 ||
            resource.LastError?.Length > 4096 ||
            resource.CapturedAtUtc == default || resource.LastUpdatedAtUtc == default ||
            resource.RestoreAttempts < 0 ||
            !IsJournalOwnershipStateValid(resource) ||
            !Enum.IsDefined(resource.Layer) ||
            !Enum.IsDefined(resource.Target) ||
            resource.Target == BlockTarget.Both ||
            !Enum.IsDefined(resource.JournalState))
        {
            throw new InvalidDataException("A privacy session resource has invalid common metadata.");
        }

        switch (resource)
        {
            case OriginalDeviceState device:
                if (device.Layer != ProtectionLayer.Secure ||
                    string.IsNullOrWhiteSpace(device.InstanceId) || device.InstanceId.Length > 2048 ||
                    device.InstanceId.IndexOfAny(['|', '\t', '\r', '\n']) >= 0 ||
                    string.IsNullOrWhiteSpace(device.DeviceClass) || device.DeviceClass.Length > 256 ||
                    string.IsNullOrWhiteSpace(device.FriendlyName) || device.FriendlyName.Length > 512 ||
                    !string.Equals(device.ResourceId, $"pnp:{device.InstanceId}", StringComparison.OrdinalIgnoreCase) ||
                    !IsAllowedPrivacyDeviceClass(device.DeviceClass, device.Target) ||
                    device.ProtectedEnabledState ||
                    device.IsSafelyRestorable &&
                    ((device.OriginalEnabledState && device.OriginalProblemCode != 0) ||
                     (!device.OriginalEnabledState && device.OriginalProblemCode != 22)))
                {
                    throw new InvalidDataException("A device recovery resource is semantically invalid.");
                }
                break;

            case OriginalPolicyState policy:
                if (!Enum.IsDefined(policy.RegistryHive) || !Enum.IsDefined(policy.RegistryView) ||
                    !Enum.IsDefined(policy.OriginalValueKind) || !Enum.IsDefined(policy.ProtectedValueKind) ||
                    string.IsNullOrWhiteSpace(policy.RegistryPath) || policy.RegistryPath.Length > 2048 ||
                    string.IsNullOrWhiteSpace(policy.ValueName) || policy.ValueName.Length > 256 ||
                    policy.Layer == ProtectionLayer.Standard && policy.RegistryHive != PrivacyRegistryHive.CurrentUser ||
                    policy.Layer == ProtectionLayer.Secure && policy.RegistryHive != PrivacyRegistryHive.LocalMachine ||
                    policy.ValueExisted != (policy.OriginalValueKind != PrivacyRegistryValueKind.None) ||
                    policy.ValueExisted != (policy.OriginalValue != null) ||
                    policy.ProtectedValueExists != (policy.ProtectedValueKind != PrivacyRegistryValueKind.None) ||
                    policy.ProtectedValueExists != (policy.ProtectedValue != null) ||
                    !string.Equals(
                        policy.ResourceId,
                        $"registry:{policy.RegistryHive}:{policy.RegistryView}:{policy.RegistryPath}:{policy.ValueName}",
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsAllowedPrivacyPolicy(policy) ||
                    policy.ValueExisted && !IsCanonicalRegistryValue(policy.OriginalValueKind, policy.OriginalValue) ||
                    policy.ProtectedValueExists && !IsCanonicalRegistryValue(policy.ProtectedValueKind, policy.ProtectedValue))
                {
                    throw new InvalidDataException("A Registry recovery resource is semantically invalid.");
                }
                break;

            case OriginalAudioEndpointState endpoint:
                if (endpoint.Layer != ProtectionLayer.Standard ||
                    endpoint.Target != BlockTarget.Microphone ||
                    string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.EndpointId.Length > 2048 ||
                    endpoint.EndpointId.IndexOfAny(['|', '\t', '\r', '\n']) >= 0 ||
                    !string.Equals(endpoint.ResourceId, $"audio-mute:{endpoint.EndpointId}", StringComparison.OrdinalIgnoreCase) ||
                    !endpoint.ProtectedMutedState)
                {
                    throw new InvalidDataException("An audio endpoint recovery resource is semantically invalid.");
                }
                break;

            default:
                throw new InvalidDataException($"Unsupported privacy resource type '{resource.GetType().Name}'.");
        }
    }

    private static bool IsJournalOwnershipStateValid(PrivacyResourceState resource)
    {
        if (resource.ModifiedByPrivLock && resource.OwnershipUncertain)
            return false;

        var stateShapeIsValid = resource.RequiresModification
            ? resource.JournalState switch
            {
                PrivacyResourceJournalState.ApplyPending =>
                    !resource.ModifiedByPrivLock && resource.OwnershipUncertain,
                PrivacyResourceJournalState.Applied =>
                    resource.ModifiedByPrivLock && !resource.OwnershipUncertain,
                PrivacyResourceJournalState.ApplyFailed or
                PrivacyResourceJournalState.Restored or
                PrivacyResourceJournalState.Conflict =>
                    !resource.ModifiedByPrivLock && !resource.OwnershipUncertain,
                PrivacyResourceJournalState.RestorePending or
                PrivacyResourceJournalState.Missing or
                PrivacyResourceJournalState.RestoreFailed =>
                    resource.ModifiedByPrivLock != resource.OwnershipUncertain,
                _ => false
            }
            : resource.JournalState == PrivacyResourceJournalState.Unchanged &&
              !resource.ModifiedByPrivLock &&
              !resource.OwnershipUncertain;

        if (!stateShapeIsValid)
            return false;
        if (!resource.ExecutionMayStillBeInFlight)
            return true;

        return resource is
            { JournalState: PrivacyResourceJournalState.ApplyPending,
              ModifiedByPrivLock: false,
              OwnershipUncertain: true } or
            { JournalState: PrivacyResourceJournalState.RestorePending,
              ModifiedByPrivLock: false,
              OwnershipUncertain: true } or
            { JournalState: PrivacyResourceJournalState.Applied,
              ModifiedByPrivLock: true,
              OwnershipUncertain: false };
    }

    private static bool IsAllowedPrivacyDeviceClass(string deviceClass, BlockTarget target) =>
        target == BlockTarget.Camera
            ? string.Equals(deviceClass, CameraClassGuid, StringComparison.OrdinalIgnoreCase)
            : target == BlockTarget.Microphone &&
              string.Equals(deviceClass, AudioEndpointClassGuid, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedPrivacyPolicy(OriginalPolicyState policy)
    {
        if (!policy.ProtectedValueExists)
            return false;

        if (policy.Layer == ProtectionLayer.Secure)
        {
            var targetMatches = policy.ValueName switch
            {
                CameraPolicyName => policy.Target == BlockTarget.Camera,
                MicrophonePolicyName => policy.Target == BlockTarget.Microphone,
                _ => false
            };
            return policy.RegistryHive == PrivacyRegistryHive.LocalMachine &&
                   policy.RegistryView == PrivacyRegistryView.Default &&
                   string.Equals(policy.RegistryPath, AppPrivacyPath, StringComparison.OrdinalIgnoreCase) &&
                   targetMatches &&
                   policy.ProtectedValueKind == PrivacyRegistryValueKind.DWord &&
                   string.Equals(policy.ProtectedValue, "2", StringComparison.Ordinal);
        }

        var allowedPath = policy.Target switch
        {
            BlockTarget.Camera =>
                string.Equals(policy.RegistryPath, CameraConsentPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(policy.RegistryPath, CameraConsentNonPackagedPath, StringComparison.OrdinalIgnoreCase),
            BlockTarget.Microphone =>
                string.Equals(policy.RegistryPath, MicrophoneConsentPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(policy.RegistryPath, MicrophoneConsentNonPackagedPath, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        return policy.RegistryHive == PrivacyRegistryHive.CurrentUser &&
               policy.RegistryView == PrivacyRegistryView.Default &&
               allowedPath &&
               string.Equals(policy.ValueName, "Value", StringComparison.OrdinalIgnoreCase) &&
               policy.ProtectedValueKind == PrivacyRegistryValueKind.String &&
               string.Equals(policy.ProtectedValue, "Deny", StringComparison.Ordinal);
    }

    private static bool IsCanonicalRegistryValue(PrivacyRegistryValueKind kind, string? value)
    {
        try
        {
            return kind switch
            {
                PrivacyRegistryValueKind.String or PrivacyRegistryValueKind.ExpandString => value != null,
                PrivacyRegistryValueKind.Binary => Convert.FromBase64String(value ?? string.Empty) is not null,
                PrivacyRegistryValueKind.DWord => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _),
                PrivacyRegistryValueKind.MultiString =>
                    JsonSerializer.Deserialize<string[]>(value ?? string.Empty) is { } items &&
                    items.All(item => item != null),
                PrivacyRegistryValueKind.QWord => long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _),
                _ => false
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static void WriteDurableCopy(string sourcePath, string destinationPath)
    {
        var tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (IsRecoverableStorageException(ex))
                {
                    Log.Warning(ex, "Could not delete abandoned backup temp file {Path}", tempPath);
                }
            }
        }
    }

    private static bool IsRecoverableStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            global::System.Security.SecurityException;
}

public sealed class PrivacySessionStoreException : IOException
{
    public PrivacySessionStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
