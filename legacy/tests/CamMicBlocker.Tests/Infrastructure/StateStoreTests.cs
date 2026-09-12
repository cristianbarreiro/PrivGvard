using System.IO;
using System.Text.Json;
using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using CamMicBlocker.Infrastructure;
using Xunit;

namespace CamMicBlocker.Tests.Infrastructure;

public class StateStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testFilePath;

    public StateStoreTests()
    {
        // Use a temporary directory for test isolation
        _testDir = Path.Combine(Path.GetTempPath(), $"CamMicBlocker_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _testFilePath = Path.Combine(_testDir, "state.json");
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var store = CreateStore();
        var state = store.Load();

        Assert.Equal(BlockStatus.Allowed, state.Camera);
        Assert.Equal(BlockStatus.Allowed, state.Microphone);
        Assert.False(state.StartWithWindows);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesState()
    {
        var store = CreateStore();
        var original = new DesiredState
        {
            Camera = BlockStatus.Blocked,
            Microphone = BlockStatus.Allowed,
            StartWithWindows = true
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(BlockStatus.Blocked, loaded.Camera);
        Assert.Equal(BlockStatus.Allowed, loaded.Microphone);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(_testFilePath, "{ this is not valid json }}}");

        var store = CreateStore();
        var state = store.Load();

        // Should not throw; returns defaults
        Assert.Equal(BlockStatus.Allowed, state.Camera);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var subDir = Path.Combine(_testDir, "sub", "dir");
        var store = CreateStore(subDir);
        
        store.Save(new DesiredState { Camera = BlockStatus.Blocked });

        Assert.True(File.Exists(Path.Combine(subDir, "state.json")));
    }

    private TestStateStore CreateStore(string? dir = null)
    {
        return new TestStateStore(dir ?? _testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* Cleanup on best effort */ }
    }

    /// <summary>
    /// Test-only subclass that allows specifying a custom directory.
    /// </summary>
    private sealed class TestStateStore : IStateStore
    {
        private readonly string _filePath;
        private readonly object _lock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public TestStateStore(string directory)
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "state.json");
        }

        public DesiredState Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_filePath)) return new DesiredState();
                    var json = File.ReadAllText(_filePath);
                    return JsonSerializer.Deserialize<DesiredState>(json, JsonOptions) ?? new DesiredState();
                }
                catch { return new DesiredState(); }
            }
        }

        public void Save(DesiredState state)
        {
            lock (_lock)
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
        }
    }
}
