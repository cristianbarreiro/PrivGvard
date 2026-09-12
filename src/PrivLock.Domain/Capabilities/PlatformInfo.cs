namespace PrivLock.Domain.Capabilities;

/// <summary>
/// Diagnostic info about the running OS platform and environment.
/// </summary>
public sealed record PlatformInfo
{
    public required string OperatingSystemName { get; init; }
    public required string OsVersion { get; init; }
    public required string Architecture { get; init; }
    public required bool Is64Bit { get; init; }
    public required bool IsElevated { get; init; }
    public string? DesktopEnvironment { get; init; }
}
