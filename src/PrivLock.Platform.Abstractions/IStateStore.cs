using PrivLock.Domain.Models;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Represents the user's persisted protection preferences across application restarts.
/// </summary>
public sealed record DesiredState
{
    public StandardProtectionState CameraStandard { get; set; } = StandardProtectionState.Inactive;
    public SecureProtectionState CameraSecure { get; set; } = SecureProtectionState.Unavailable;

    public StandardProtectionState MicrophoneStandard { get; set; } = StandardProtectionState.Inactive;
    public SecureProtectionState MicrophoneSecure { get; set; } = SecureProtectionState.Unavailable;

    public string Language { get; set; } = "es";
    public bool Autostart { get; set; }
    public bool AdvancedProtectionEnabled { get; set; }
}

/// <summary>
/// Contract for persisting user desired state and application settings to storage.
/// </summary>
public interface IStateStore
{
    DesiredState Load();
    void Save(DesiredState state);
}
