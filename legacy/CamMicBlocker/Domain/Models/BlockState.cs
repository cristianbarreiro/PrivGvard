namespace CamMicBlocker.Domain.Models;

/// <summary>
/// Represents the complete blocking state for one device type (camera or microphone).
/// Tracks three distinct dimensions of state to detect inconsistencies.
/// </summary>
public sealed class DeviceBlockState
{
    /// <summary>What the user wants (persisted across app restarts).</summary>
    public BlockStatus DesiredStatus { get; init; } = BlockStatus.Allowed;

    /// <summary>What the Windows privacy policy currently says (registry).</summary>
    public BlockStatus PolicyStatus { get; init; } = BlockStatus.Unknown;

    /// <summary>What the PnP device status actually is (enabled/disabled).</summary>
    public BlockStatus DeviceStatus { get; init; } = BlockStatus.Unknown;

    /// <summary>
    /// The effective state: is the device actually blocked from the user's perspective?
    /// A device is considered effectively blocked only if BOTH policy and device agree.
    /// A mismatch indicates a problem that should be reported to the user.
    /// </summary>
    public BlockStatus EffectiveStatus
    {
        get
        {
            if (PolicyStatus == BlockStatus.Blocked && DeviceStatus == BlockStatus.Blocked)
                return BlockStatus.Blocked;
            if (PolicyStatus == BlockStatus.Allowed && DeviceStatus == BlockStatus.Allowed)
                return BlockStatus.Allowed;
            // Any mismatch or unknown → report as unknown so UI can flag it
            return BlockStatus.Unknown;
        }
    }

    /// <summary>
    /// Whether the actual state matches what the user wants.
    /// </summary>
    public bool IsConsistent => DesiredStatus == EffectiveStatus;
}

/// <summary>
/// Aggregates the blocking state for both camera and microphone.
/// This is the top-level state model for the entire application.
/// </summary>
public sealed class BlockState
{
    public DeviceBlockState Camera { get; init; } = new();
    public DeviceBlockState Microphone { get; init; } = new();

    /// <summary>
    /// Quick check: are both camera and microphone in a consistent, blocked state?
    /// </summary>
    public bool AllBlocked =>
        Camera.EffectiveStatus == BlockStatus.Blocked &&
        Microphone.EffectiveStatus == BlockStatus.Blocked;

    /// <summary>
    /// Quick check: are both camera and microphone in a consistent, allowed state?
    /// </summary>
    public bool AllAllowed =>
        Camera.EffectiveStatus == BlockStatus.Allowed &&
        Microphone.EffectiveStatus == BlockStatus.Allowed;

    /// <summary>
    /// Whether all states are internally consistent (desired == effective).
    /// </summary>
    public bool IsFullyConsistent => Camera.IsConsistent && Microphone.IsConsistent;
}
