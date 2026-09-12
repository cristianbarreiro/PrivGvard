using System.IO;
using System.Text.Json;
using CamMicBlocker.Domain.Interfaces;
using Serilog;

namespace CamMicBlocker.Infrastructure;

/// <summary>
/// Persists the user's desired blocking state as a JSON file in AppData.
/// Thread-safe via file locking. Handles missing/corrupt files gracefully.
/// 
/// File location: %LOCALAPPDATA%\CamMicBlocker\state.json
/// </summary>
public sealed class StateStore : IStateStore
{
    private static readonly ILogger Log = Serilog.Log.ForContext<StateStore>();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public StateStore()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CamMicBlocker");

        Directory.CreateDirectory(appDataDir);
        _filePath = Path.Combine(appDataDir, "state.json");
    }

    public DesiredState Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Log.Information("No state file found at {Path}, using defaults", _filePath);
                    return new DesiredState();
                }

                var json = File.ReadAllText(_filePath);
                var state = JsonSerializer.Deserialize<DesiredState>(json, JsonOptions);

                if (state == null)
                {
                    Log.Warning("State file deserialized to null, using defaults");
                    return new DesiredState();
                }

                Log.Debug("Loaded state: Camera={Camera}, Microphone={Microphone}, StartWithWindows={Startup}",
                    state.Camera, state.Microphone, state.StartWithWindows);
                return state;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load state from {Path}, using defaults", _filePath);
                return new DesiredState();
            }
        }
    }

    public void Save(DesiredState state)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                File.WriteAllText(_filePath, json);
                Log.Debug("Saved state to {Path}", _filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save state to {Path}", _filePath);
            }
        }
    }
}
