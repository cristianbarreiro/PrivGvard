namespace CamMicBlocker.Domain.Models;

/// <summary>
/// Specifies what the user wants to block/unblock.
/// Supports independent camera/microphone control while allowing
/// a simple "both" toggle for the common case.
/// </summary>
public enum BlockTarget
{
    Camera,
    Microphone,
    Both
}
