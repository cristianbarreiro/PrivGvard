namespace PrivLock.Domain.Capabilities;

/// <summary>
/// Declarative description of what the current platform and operating system environment supports.
/// Avoids false promises by making platform limits explicit to the Application and UI layers.
/// </summary>
public sealed record PlatformCapabilities
{
    /// <summary>Camera protection level (None, Software, Hardware, DualLayer).</summary>
    public CapabilityLevel CameraProtectionLevel { get; init; } = CapabilityLevel.None;

    /// <summary>Microphone protection level (None, Software, Hardware, DualLayer).</summary>
    public CapabilityLevel MicrophoneProtectionLevel { get; init; } = CapabilityLevel.None;

    /// <summary>Whether the platform supports hardware/PnP/driver-level device disablement.</summary>
    public bool SupportsHardwareDisable { get; init; }

    /// <summary>Whether the platform supports OS-level group policies (e.g. Windows AppPrivacy registry).</summary>
    public bool SupportsSystemPolicy { get; init; }

    /// <summary>Whether the platform supports sound server / audio HAL source mute and lock (PipeWire, PulseAudio, CoreAudio).</summary>
    public bool SupportsAudioMuteLock { get; init; }

    /// <summary>Whether blocking requires elevated (admin/root) privileges.</summary>
    public bool RequiresElevationForBlock { get; init; }

    /// <summary>Whether the platform supports global hotkey registration.</summary>
    public bool SupportsGlobalHotkey { get; init; }

    /// <summary>Whether the platform supports autostart registration.</summary>
    public bool SupportsAutostart { get; init; }

    /// <summary>Whether the platform supports system tray integration.</summary>
    public bool SupportsSystemTray { get; init; } = true;
}
