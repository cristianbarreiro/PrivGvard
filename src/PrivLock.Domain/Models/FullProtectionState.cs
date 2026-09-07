namespace PrivLock.Domain.Models;

/// <summary>
/// Aggregated system-wide protection state containing the individual statuses for Camera and Microphone.
/// </summary>
public sealed record FullProtectionState
{
    public required TargetProtectionStatus Camera { get; init; }
    public required TargetProtectionStatus Microphone { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public bool BothProtected => Camera.IsProtected && Microphone.IsProtected;
    public bool BothSecure => Camera.SecureState == SecureProtectionState.Active &&
                              Microphone.SecureState == SecureProtectionState.Active;
    public bool AdvancedProtectionEnabled { get; init; }
    public bool CameraAdvancedProtected => Camera.IsProtected && AdvancedProtectionEnabled;
    public bool MicrophoneAdvancedProtected => Microphone.IsProtected && AdvancedProtectionEnabled;
}
