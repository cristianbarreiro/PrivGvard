using CamMicBlocker.Domain.Models;

namespace CamMicBlocker.Domain.Interfaces;

/// <summary>
/// Persists the desired blocking state across application restarts.
/// Stored in a JSON file in the user's AppData.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// Loads the last known desired state.
    /// Returns defaults (Allowed/Allowed) if no state file exists.
    /// </summary>
    DesiredState Load();

    /// <summary>
    /// Saves the current desired state.
    /// </summary>
    void Save(DesiredState state);
}

/// <summary>
/// The user's desired blocking state. Persisted to disk.
/// </summary>
public sealed class DesiredState
{
    public BlockStatus Camera { get; set; } = BlockStatus.Allowed;
    public BlockStatus Microphone { get; set; } = BlockStatus.Allowed;
    public bool StartWithWindows { get; set; } = false;
    public string Language { get; set; } = "es";
}
