using System.Text.Json;
using PrivLock.Domain.Models;
using PrivLock.Infrastructure.Common.Storage;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Infrastructure.Tests;

public sealed class FilePrivacySessionStoreTests : IDisposable
{
    private const string JournalFileName = "privacy-session-v1.json";

    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"PrivLock_PrivacySessionStoreTests_{Guid.NewGuid():N}");

    private string JournalPath => Path.Combine(_testDirectory, JournalFileName);
    private string BackupPath => JournalPath + ".bak";
    private string MarkerPath => Path.Combine(
        _testDirectory,
        FileActivePrivacySessionMarker.FileName);

    [Fact]
    public void Load_WhenJournalDoesNotExist_ReturnsNull()
    {
        var store = new FilePrivacySessionStore(_testDirectory);

        var loaded = store.Load();

        Assert.Null(loaded);
    }

    [Fact]
    public void Save_ActiveSession_CreatesMarkerAndTerminalCommitRemovesIt()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        var store = new FilePrivacySessionStore(_testDirectory, marker);
        var session = CreateSession(Guid.NewGuid(), "marker-lifecycle");

        store.Save(session);

        Assert.Equal(session.SessionId.ToString("D"), File.ReadAllText(MarkerPath));

        session.IsActive = false;
        session.WasRestored = true;
        session.Status = PrivacySessionStatus.Restored;
        session.CompletedAtUtc = session.UpdatedAtUtc;
        store.Save(session);

        Assert.False(File.Exists(MarkerPath));
        Assert.True(File.Exists(JournalPath));
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public void Save_WhenMarkerCannotBeCreated_DoesNotCommitJournal()
    {
        var marker = new ThrowingActivePrivacySessionMarker();
        var store = new FilePrivacySessionStore(_testDirectory, marker);

        var exception = Assert.Throws<PrivacySessionStoreException>(
            () => store.Save(CreateSession(Guid.NewGuid(), "marker-failure")));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.False(File.Exists(JournalPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void Load_ValidatedJournalRepairsMarkerForItsActiveSession()
    {
        var writerMarker = new FileActivePrivacySessionMarker(_testDirectory);
        var store = new FilePrivacySessionStore(_testDirectory, writerMarker);
        var session = CreateSession(Guid.NewGuid(), "marker-reconcile");
        store.Save(session);
        File.WriteAllText(MarkerPath, Guid.NewGuid().ToString("D"));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
        Assert.Equal(session.SessionId.ToString("D"), File.ReadAllText(MarkerPath));
    }

    [Fact]
    public void Load_WithoutJournalRemovesStaleMarker()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        marker.EnsureActive(Guid.NewGuid());
        var store = new FilePrivacySessionStore(_testDirectory, marker);

        Assert.Null(store.Load());

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllPolymorphicResourceTypes()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var original = CreateSession(Guid.NewGuid(), "roundtrip-operation");
        original.Status = PrivacySessionStatus.RecoveryIncomplete;
        original.Resources =
        [
            new OriginalDeviceState
            {
                ResourceId = @"pnp:USB\VID_1234&PID_5678\CAMERA",
                OperationId = "disable-camera-1",
                Layer = ProtectionLayer.Secure,
                Target = BlockTarget.Camera,
                CapturedAtUtc = Utc(2026, 8, 25, 12, 1),
                LastUpdatedAtUtc = Utc(2026, 8, 25, 12, 2),
                JournalState = PrivacyResourceJournalState.Applied,
                ModifiedByPrivLock = true,
                RestoreAttempts = 2,
                LastError = "previous transient failure",
                InstanceId = @"USB\VID_1234&PID_5678\CAMERA",
                FriendlyName = "Integrated Camera",
                DeviceClass = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}",
                IsPresentAtCapture = true,
                OriginalEnabledState = true,
                OriginalProblemCode = 0,
                ProtectedEnabledState = false,
                IsSafelyRestorable = true
            },
            new OriginalPolicyState
            {
                ResourceId = @"registry:LocalMachine:Default:SOFTWARE\Policies\Microsoft\Windows\AppPrivacy:LetAppsAccessCamera",
                OperationId = "set-camera-policy",
                Layer = ProtectionLayer.Secure,
                Target = BlockTarget.Camera,
                CapturedAtUtc = Utc(2026, 8, 25, 12, 3),
                LastUpdatedAtUtc = Utc(2026, 8, 25, 12, 4),
                JournalState = PrivacyResourceJournalState.ApplyPending,
                ModifiedByPrivLock = false,
                OwnershipUncertain = true,
                RegistryHive = PrivacyRegistryHive.LocalMachine,
                RegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessCamera",
                ValueExisted = true,
                OriginalValueKind = PrivacyRegistryValueKind.DWord,
                OriginalValue = "1",
                ProtectedValueExists = true,
                ProtectedValueKind = PrivacyRegistryValueKind.DWord,
                ProtectedValue = "2"
            },
            new OriginalAudioEndpointState
            {
                ResourceId = "audio-mute:endpoint-1",
                OperationId = "mute-microphone-1",
                Layer = ProtectionLayer.Standard,
                Target = BlockTarget.Microphone,
                CapturedAtUtc = Utc(2026, 8, 25, 12, 5),
                LastUpdatedAtUtc = Utc(2026, 8, 25, 12, 6),
                JournalState = PrivacyResourceJournalState.RestorePending,
                ModifiedByPrivLock = true,
                EndpointId = "endpoint-1",
                OriginalMutedState = false,
                ProtectedMutedState = true
            }
        ];

        store.Save(original);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.SessionId, loaded.SessionId);
        Assert.Equal(original.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, loaded.UpdatedAtUtc);
        Assert.Equal(original.OwnerProcessId, loaded.OwnerProcessId);
        Assert.Equal(original.IsActive, loaded.IsActive);
        Assert.Equal(original.Status, loaded.Status);
        Assert.Equal(original.LastOperationId, loaded.LastOperationId);

        var device = Assert.IsType<OriginalDeviceState>(loaded.Resources[0]);
        Assert.Equal(@"USB\VID_1234&PID_5678\CAMERA", device.InstanceId);
        Assert.Equal((uint)0, device.OriginalProblemCode);
        Assert.Equal(2, device.RestoreAttempts);
        Assert.Equal("previous transient failure", device.LastError);
        Assert.True(device.RequiresModification);

        var policy = Assert.IsType<OriginalPolicyState>(loaded.Resources[1]);
        Assert.Equal(PrivacyRegistryHive.LocalMachine, policy.RegistryHive);
        Assert.Equal(PrivacyRegistryValueKind.DWord, policy.OriginalValueKind);
        Assert.Equal("1", policy.OriginalValue);
        Assert.Equal("2", policy.ProtectedValue);
        Assert.True(policy.RequiresModification);

        var audio = Assert.IsType<OriginalAudioEndpointState>(loaded.Resources[2]);
        Assert.Equal("endpoint-1", audio.EndpointId);
        Assert.False(audio.OriginalMutedState);
        Assert.True(audio.ProtectedMutedState);
        Assert.True(audio.RequiresModification);
    }

    [Fact]
    public void Save_WhenStartingNewSession_CommitsBackupForTheSameNewSession()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var first = CreateSession(Guid.NewGuid(), "first-operation");
        var second = CreateSession(Guid.NewGuid(), "second-operation");

        store.Save(first);
        Assert.True(File.Exists(BackupPath));

        store.Save(second);

        var current = store.Load();
        Assert.NotNull(current);
        Assert.Equal(second.SessionId, current.SessionId);
        Assert.True(File.Exists(BackupPath));

        using var backupDocument = JsonDocument.Parse(File.ReadAllText(BackupPath));
        Assert.Equal(
            second.SessionId,
            backupDocument.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.tmp"));
    }

    [Fact]
    public void Load_WhenPrimaryIsCorrupt_RecoversValidatedBackup()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var recoverable = CreateSession(Guid.NewGuid(), "recoverable-operation");
        var replaced = CreateSession(Guid.NewGuid(), "replacement-operation");
        store.Save(recoverable);
        store.Save(replaced);
        File.WriteAllText(JournalPath, "{ definitely-not-valid-json");

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(replaced.SessionId, loaded.SessionId);
        Assert.Equal(replaced.LastOperationId, loaded.LastOperationId);

        loaded.LastOperationId = "healed-checkpoint";
        loaded.UpdatedAtUtc = loaded.UpdatedAtUtc.AddMinutes(1);
        store.Save(loaded);

        var healed = store.Load();
        Assert.NotNull(healed);
        Assert.Equal("healed-checkpoint", healed.LastOperationId);
    }

    [Fact]
    public void Load_WhenPrimaryIsMissing_RecoversValidatedBackup()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "backup-only");
        store.Save(session);
        File.Delete(JournalPath);

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
    }

    [Fact]
    public void Save_WhenUpdatingSameSession_BackupMatchesLatestWalCommit()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "first-revision");
        store.Save(session);
        session.LastOperationId = "second-revision";
        session.UpdatedAtUtc = session.UpdatedAtUtc.AddMinutes(1);

        store.Save(session);

        using var backupDocument = JsonDocument.Parse(File.ReadAllText(BackupPath));
        Assert.Equal(
            "second-revision",
            backupDocument.RootElement.GetProperty("lastOperationId").GetString());
    }

    [Fact]
    public void Load_WhenSchemaIsUnsupportedAndNoBackupExists_ThrowsStoreException()
    {
        Directory.CreateDirectory(_testDirectory);
        var invalid = CreateSession(Guid.NewGuid(), "future-schema");
        invalid.SchemaVersion = PrivacySession.CurrentSchemaVersion + 1;
        File.WriteAllText(
            JournalPath,
            JsonSerializer.Serialize(invalid, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        var store = new FilePrivacySessionStore(_testDirectory);

        var exception = Assert.Throws<PrivacySessionStoreException>(() => store.Load());

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("no backup exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_WhenJournalCannotBeReplaced_PropagatesStoreExceptionAndCleansTempFile()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        Directory.CreateDirectory(JournalPath);

        var exception = Assert.Throws<PrivacySessionStoreException>(
            () => store.Save(CreateSession(Guid.NewGuid(), "cannot-save")));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Contains("could not persist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.tmp"));
    }

    [Fact]
    public void Save_WhenResourceSemanticsAreInvalid_FailsClosedBeforeWriting()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "invalid-resource");
        session.Resources =
        [
            new OriginalPolicyState
            {
                ResourceId = "registry:invalid",
                OperationId = "invalid-resource",
                Layer = ProtectionLayer.Secure,
                Target = BlockTarget.Camera,
                CapturedAtUtc = session.CreatedAtUtc,
                LastUpdatedAtUtc = session.UpdatedAtUtc,
                RegistryHive = PrivacyRegistryHive.LocalMachine,
                RegistryView = PrivacyRegistryView.Default,
                RegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessCamera",
                ValueExisted = false,
                OriginalValueKind = PrivacyRegistryValueKind.DWord,
                OriginalValue = "1",
                ProtectedValueExists = true,
                ProtectedValueKind = PrivacyRegistryValueKind.DWord,
                ProtectedValue = "2"
            }
        ];

        Assert.Throws<InvalidDataException>(() => store.Save(session));
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void SaveAndLoad_AllowsOnlySupportedInFlightOwnershipShapes()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "in-flight-shapes");
        session.Resources =
        [
            new OriginalAudioEndpointState
            {
                ResourceId = "audio-mute:owned-reapply",
                OperationId = "in-flight-shapes",
                Layer = ProtectionLayer.Standard,
                Target = BlockTarget.Microphone,
                CapturedAtUtc = session.CreatedAtUtc,
                LastUpdatedAtUtc = session.UpdatedAtUtc,
                JournalState = PrivacyResourceJournalState.Applied,
                ModifiedByPrivLock = true,
                OwnershipUncertain = false,
                ExecutionMayStillBeInFlight = true,
                EndpointId = "owned-reapply",
                OriginalMutedState = false,
                ProtectedMutedState = true
            },
            new OriginalAudioEndpointState
            {
                ResourceId = "audio-mute:uncertain-restore",
                OperationId = "in-flight-shapes",
                Layer = ProtectionLayer.Standard,
                Target = BlockTarget.Microphone,
                CapturedAtUtc = session.CreatedAtUtc,
                LastUpdatedAtUtc = session.UpdatedAtUtc,
                JournalState = PrivacyResourceJournalState.RestorePending,
                ModifiedByPrivLock = false,
                OwnershipUncertain = true,
                ExecutionMayStillBeInFlight = true,
                EndpointId = "uncertain-restore",
                OriginalMutedState = false,
                ProtectedMutedState = true
            }
        ];

        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.All(loaded.Resources, resource => Assert.True(resource.ExecutionMayStillBeInFlight));

        loaded.Resources[0].JournalState = PrivacyResourceJournalState.ApplyPending;
        Assert.Throws<InvalidDataException>(() => store.Save(loaded));
    }

    [Fact]
    public void Save_ManipulatedMachineRegistryTarget_IsRejectedBeforeRecoveryCanUseIt()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "tampered-hklm-target");
        session.Resources =
        [
            new OriginalPolicyState
            {
                ResourceId = @"registry:LocalMachine:Default:SOFTWARE\Unrelated:LetAppsAccessCamera",
                OperationId = "tampered-hklm-target",
                Layer = ProtectionLayer.Secure,
                Target = BlockTarget.Camera,
                CapturedAtUtc = session.CreatedAtUtc,
                LastUpdatedAtUtc = session.UpdatedAtUtc,
                JournalState = PrivacyResourceJournalState.Applied,
                ModifiedByPrivLock = true,
                RegistryHive = PrivacyRegistryHive.LocalMachine,
                RegistryView = PrivacyRegistryView.Default,
                RegistryPath = @"SOFTWARE\Unrelated",
                ValueName = "LetAppsAccessCamera",
                ValueExisted = false,
                OriginalValueKind = PrivacyRegistryValueKind.None,
                OriginalValue = null,
                ProtectedValueExists = true,
                ProtectedValueKind = PrivacyRegistryValueKind.DWord,
                ProtectedValue = "2"
            }
        ];

        Assert.Throws<InvalidDataException>(() => store.Save(session));
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Save_WallClockMovedBackward_DoesNotInvalidateOtherwiseValidWal()
    {
        var store = new FilePrivacySessionStore(_testDirectory);
        var session = CreateSession(Guid.NewGuid(), "clock-rollback");
        session.Resources =
        [
            new OriginalAudioEndpointState
            {
                ResourceId = "audio-mute:clock-rollback-endpoint",
                OperationId = "clock-rollback",
                Layer = ProtectionLayer.Standard,
                Target = BlockTarget.Microphone,
                CapturedAtUtc = Utc(2026, 8, 25, 12, 10),
                LastUpdatedAtUtc = Utc(2026, 8, 25, 11, 55),
                JournalState = PrivacyResourceJournalState.Applied,
                ModifiedByPrivLock = true,
                EndpointId = "clock-rollback-endpoint",
                OriginalMutedState = false,
                ProtectedMutedState = true
            }
        ];

        store.Save(session);

        Assert.NotNull(store.Load());
    }

    [Fact]
    public void Load_ActiveMarkerFailure_DoesNotFallBackToTerminalBackup()
    {
        var writer = new FilePrivacySessionStore(_testDirectory);
        var terminal = CreateSession(Guid.NewGuid(), "terminal");
        terminal.IsActive = false;
        terminal.WasRestored = true;
        terminal.Status = PrivacySessionStatus.Restored;
        writer.Save(terminal);
        var terminalJson = File.ReadAllText(JournalPath);
        writer.Save(CreateSession(Guid.NewGuid(), "active"));
        File.WriteAllText(JournalPath + ".bak", terminalJson);
        var marker = new FailingReconciliationMarker();
        var reader = new FilePrivacySessionStore(_testDirectory, marker);

        Assert.Throws<PrivacySessionStoreException>(() => reader.Load());
        Assert.Equal(0, marker.RetireCalls);
    }

    [Fact]
    public void Load_TerminalPrimary_RepairsActiveBackupBeforeRetiringMarker()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        var store = new FilePrivacySessionStore(_testDirectory, marker);
        var session = CreateSession(Guid.NewGuid(), "terminal-checkpoint");
        store.Save(session);
        var activeJson = File.ReadAllText(JournalPath);
        session.IsActive = false;
        session.WasRestored = true;
        session.Status = PrivacySessionStatus.Restored;
        store.Save(session);
        File.WriteAllText(JournalPath + ".bak", activeJson);
        marker.EnsureActive(session.SessionId);

        Assert.False(store.Load()!.IsActive);
        Assert.Equal(File.ReadAllText(JournalPath), File.ReadAllText(JournalPath + ".bak"));
        Assert.False(File.Exists(Path.Combine(_testDirectory, FileActivePrivacySessionMarker.FileName)));
    }

    private sealed class FailingReconciliationMarker : IActivePrivacySessionMarker
    {
        public int RetireCalls { get; private set; }
        public bool EnsureActive(Guid sessionId) => throw new IOException("marker unavailable");
        public void Delete(Guid sessionId) => RetireCalls++;
        public void Reconcile(Guid? activeSessionId)
        {
            if (activeSessionId.HasValue)
                throw new IOException("marker unavailable");
            RetireCalls++;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static PrivacySession CreateSession(Guid sessionId, string operationId) => new()
    {
        SessionId = sessionId,
        CreatedAtUtc = Utc(2026, 8, 25, 12, 0),
        UpdatedAtUtc = Utc(2026, 8, 25, 12, 10),
        OwnerProcessId = 4242,
        IsActive = true,
        WasRestored = false,
        Status = PrivacySessionStatus.Active,
        LastOperationId = operationId
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class ThrowingActivePrivacySessionMarker : IActivePrivacySessionMarker
    {
        public bool EnsureActive(Guid sessionId) =>
            throw new IOException("simulated marker failure");

        public void Delete(Guid sessionId)
        {
        }

        public void Reconcile(Guid? activeSessionId)
        {
        }
    }
}
