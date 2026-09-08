using PrivLock.Infrastructure.Common.Storage;
using Xunit;

namespace PrivLock.Infrastructure.Tests;

public sealed class StorageMigrationHelperTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _legacyDir;
    private readonly string _targetDir;

    public StorageMigrationHelperTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"PrivGvard_MigrationTests_{Guid.NewGuid():N}");
        _legacyDir = Path.Combine(_testRoot, "PrivLock");
        _targetDir = Path.Combine(_testRoot, "PrivGvard");
    }

    [Fact]
    public void MigrateDirectory_LegacyDoesNotExist_DoesNothing()
    {
        StorageMigrationHelper.MigrateDirectory(_targetDir, _legacyDir);
        Assert.False(Directory.Exists(_targetDir));
    }

    [Fact]
    public void MigrateDirectory_MigratesStateAndRecovery()
    {
        Directory.CreateDirectory(_legacyDir);
        var legacyState = Path.Combine(_legacyDir, "state.json");
        File.WriteAllText(legacyState, "{\"language\":\"es\"}");

        var legacyRecovery = Path.Combine(_legacyDir, "Recovery");
        Directory.CreateDirectory(legacyRecovery);
        var sessionFile = Path.Combine(legacyRecovery, "privacy-session-v1.json");
        File.WriteAllText(sessionFile, "{\"sessionId\":\"test-123\"}");

        StorageMigrationHelper.MigrateDirectory(_targetDir, _legacyDir);

        Assert.True(Directory.Exists(_targetDir));
        var targetState = Path.Combine(_targetDir, "state.json");
        Assert.True(File.Exists(targetState));
        Assert.Equal("{\"language\":\"es\"}", File.ReadAllText(targetState));

        var targetSession = Path.Combine(_targetDir, "Recovery", "privacy-session-v1.json");
        Assert.True(File.Exists(targetSession));
        Assert.Equal("{\"sessionId\":\"test-123\"}", File.ReadAllText(targetSession));

        // Legacy files remain intact
        Assert.True(File.Exists(legacyState));
        Assert.True(File.Exists(sessionFile));
    }

    [Fact]
    public void MigrateDirectory_TargetAlreadyHasState_DoesNotOverwriteTarget()
    {
        Directory.CreateDirectory(_legacyDir);
        var legacyState = Path.Combine(_legacyDir, "state.json");
        File.WriteAllText(legacyState, "{\"language\":\"es\"}");

        Directory.CreateDirectory(_targetDir);
        var targetState = Path.Combine(_targetDir, "state.json");
        File.WriteAllText(targetState, "{\"language\":\"en\"}");

        StorageMigrationHelper.MigrateDirectory(_targetDir, _legacyDir);

        Assert.Equal("{\"language\":\"en\"}", File.ReadAllText(targetState));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup in test temp
            }
        }
    }
}
