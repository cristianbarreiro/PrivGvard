namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Registers and listens for system-wide global hotkeys across platforms.
/// </summary>
public interface IGlobalHotkeyProvider : IDisposable
{
    /// <summary>
    /// Whether the hotkey is currently registered and listening.
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// Fired when the registered global hotkey combination is pressed.
    /// </summary>
    event Action? HotkeyPressed;

    /// <summary>
    /// Registers a global hotkey (default: Ctrl+Alt+B).
    /// </summary>
    /// <param name="keyGesture">Key combination string, e.g. "Ctrl+Alt+B".</param>
    /// <returns>True if registered successfully, false if in use or unsupported.</returns>
    bool Register(string keyGesture = "Ctrl+Alt+B");

    /// <summary>
    /// Unregisters the current hotkey.
    /// </summary>
    void Unregister();
}
