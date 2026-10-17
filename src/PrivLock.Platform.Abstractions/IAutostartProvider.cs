using PrivLock.Domain.Results;
using System.Threading.Tasks;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Manages registering/unregistering the application to start automatically with the operating system.
/// </summary>
public interface IAutostartProvider
{
    /// <summary>
    /// Reads the effective state of the mechanism used by the current distribution.
    /// </summary>
    Task<AutostartStatus> GetAutostartStatusAsync();

    /// <summary>
    /// Enables and verifies autostart with the operating system.
    /// </summary>
    Task<OperationResult> EnableAutostartAsync();

    /// <summary>
    /// Disables and verifies autostart with the operating system.
    /// </summary>
    Task<OperationResult> DisableAutostartAsync();

    /// <summary>
    /// Removes only registrations owned by earlier PrivGvard versions or distributions.
    /// </summary>
    Task<OperationResult> MigrateLegacyEntriesAsync() =>
        Task.FromResult(OperationResult.Ok());
}

/// <summary>
/// Observes external changes to the effective autostart mechanism.
/// </summary>
public interface IAutostartChangeObserver
{
    /// <summary>
    /// Indicates whether the active settings UI should refresh periodically because the
    /// platform does not expose a native change notification.
    /// </summary>
    bool RequiresVisiblePolling { get; }

    /// <summary>
    /// Waits for external changes. Implementations must return on cancellation and report
    /// terminal observation failures through <paramref name="onError"/>.
    /// </summary>
    Task ObserveChangesAsync(
        Action onChanged,
        Action<Exception> onError,
        CancellationToken cancellationToken);
}
