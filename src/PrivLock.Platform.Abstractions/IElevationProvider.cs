using PrivLock.Domain.Results;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Manages privilege detection and on-demand elevation without hardcoding OS mechanisms into business logic.
/// </summary>
public interface IElevationProvider
{
    /// <summary>
    /// Whether the current application process has elevated (administrator/root) privileges.
    /// </summary>
    bool IsElevated { get; }

    /// <summary>
    /// Requests privilege elevation using the OS native mechanism (UAC, polkit, sudo, Authorization Services).
    /// </summary>
    Task<ElevationResult> RequestElevationAsync(CancellationToken cancellationToken = default);
}
