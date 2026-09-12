namespace PrivLock.Domain.Models;

/// <summary>
/// Detailed protection status for a single target (Camera or Microphone),
/// containing explicit states for both Standard and Secure protection levels.
/// </summary>
public sealed record TargetProtectionStatus
{
    public required BlockTarget Target { get; init; }
    public required StandardProtectionState StandardState { get; init; }
    public required SecureProtectionState SecureState { get; init; }
    public bool IsVerified { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Returns true if at least one protection level (Standard or Secure) is active.
    /// </summary>
    public bool IsProtected => StandardState == StandardProtectionState.Active ||
                               SecureState == SecureProtectionState.Active;

    /// <summary>
    /// Returns true if the secure protection button can currently be clicked to enable secure mode.
    /// Requires Standard to be active and Secure to be currently available (or failed and retryable).
    /// </summary>
    public bool CanEnableSecure => StandardState == StandardProtectionState.Active &&
                                   SecureState is SecureProtectionState.Available or SecureProtectionState.Failed;

    /// <summary>
    /// Returns true if secure protection is currently active and can be disabled independently.
    /// </summary>
    public bool CanDisableSecure => SecureState == SecureProtectionState.Active;
}
