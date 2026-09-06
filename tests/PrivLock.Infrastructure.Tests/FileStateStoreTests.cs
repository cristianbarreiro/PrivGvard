using PrivLock.Domain.Models;
using PrivLock.Infrastructure.Common.Storage;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Infrastructure.Tests;

public class FileStateStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testFilePath;

    public FileStateStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"PrivLock_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _testFilePath = Path.Combine(_testDir, "state.json");
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var store = new FileStateStore(_testDir);
        var state = store.Load();

        Assert.Equal(StandardProtectionState.Inactive, state.CameraStandard);
        Assert.Equal(SecureProtectionState.Unavailable, state.CameraSecure);
        Assert.Equal(StandardProtectionState.Inactive, state.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Unavailable, state.MicrophoneSecure);
        Assert.False(state.Autostart);
        Assert.Equal("es", state.Language);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesState()
    {
        var store = new FileStateStore(_testDir);
        var original = new DesiredState
        {
            CameraStandard = StandardProtectionState.Active,
            CameraSecure = SecureProtectionState.Active,
            MicrophoneStandard = StandardProtectionState.Active,
            MicrophoneSecure = SecureProtectionState.Available,
            Autostart = true,
            Language = "en"
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(StandardProtectionState.Active, loaded.CameraStandard);
        Assert.Equal(SecureProtectionState.Active, loaded.CameraSecure);
        Assert.Equal(StandardProtectionState.Active, loaded.MicrophoneStandard);
        Assert.Equal(SecureProtectionState.Available, loaded.MicrophoneSecure);
        Assert.True(loaded.Autostart);
        Assert.Equal("en", loaded.Language);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(_testFilePath, "{ corrupt json [[[]");

        var store = new FileStateStore(_testDir);
        var state = store.Load();

        Assert.Equal(StandardProtectionState.Inactive, state.CameraStandard);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }
}
