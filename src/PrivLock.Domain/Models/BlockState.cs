namespace PrivLock.Domain.Models;

/// <summary>
/// Represents the blocking status for a specific device type (camera or microphone),
/// tracking the user's desired state, policy/system level state, and hardware/stream level state.
/// </summary>
public sealed class DeviceBlockState
{
    /// <summary>What the user requested (persisted in configuration).</summary>
    public BlockStatus DesiredStatus { get; init; } = BlockStatus.Allowed;

    /// <summary>What the OS privacy direct policy currently dictates.</summary>
    public BlockStatus PolicyStatus { get; init; } = BlockStatus.Unknown;

    /// <summary>What the hardware device or audio/video stream controller status actually is.</summary>
    public BlockStatus DeviceStatus { get; init; } = BlockStatus.Unknown;

    /// <summary>
    /// The effective state: is the device actually protected/blocked from the user's perspective?
    /// If hardware/stream is blocked, or if system policy is blocked, the user is protected.
    /// A device is considered effectively blocked if either policy or hardware is actively blocking it.
    /// </summary>
    public BlockStatus EffectiveStatus
    {
        get
        {
            if (PolicyStatus == BlockStatus.Blocked || DeviceStatus == BlockStatus.Blocked)
                return BlockStatus.Blocked;
            if (PolicyStatus == BlockStatus.Allowed && DeviceStatus == BlockStatus.Allowed)
                return BlockStatus.Allowed;
            if (PolicyStatus == BlockStatus.Allowed && DeviceStatus == BlockStatus.Unknown)
                return BlockStatus.Allowed;
            if (PolicyStatus == BlockStatus.Unknown && DeviceStatus == BlockStatus.Allowed)
                return BlockStatus.Allowed;

            return BlockStatus.Unknown;
        }
    }

    /// <summary>
    /// Whether the effective protection state aligns with the user's desired state.
    /// </summary>
    public bool IsConsistent => DesiredStatus == EffectiveStatus;
}

/// <summary>
/// Aggregates the overall system blocking state for both camera and microphone.
/// </summary>
public sealed class BlockState
{
    public DeviceBlockState Camera { get; init; } = new();
    public DeviceBlockState Microphone { get; init; } = new();

    /// <summary>
    /// Quick check: are both camera and microphone effectively blocked?
    /// </summary>
    public bool AllBlocked =>
        Camera.EffectiveStatus == BlockStatus.Blocked &&
        Microphone.EffectiveStatus == BlockStatus.Blocked;

    /// <summary>
    /// Quick check: are both camera and microphone allowed?
    /// </summary>
    public bool AllAllowed =>
        Camera.EffectiveStatus == BlockStatus.Allowed &&
        Microphone.EffectiveStatus == BlockStatus.Allowed;

    /// <summary>
    /// Whether both camera and microphone states match the user's desired states.
    /// </summary>
    public bool IsFullyConsistent => Camera.IsConsistent && Microphone.IsConsistent;
}
