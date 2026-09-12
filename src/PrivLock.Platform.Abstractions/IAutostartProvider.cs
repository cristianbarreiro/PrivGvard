using PrivLock.Domain.Results;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Manages registering/unregistering the application to start automatically with the operating system.
/// </summary>
public interface IAutostartProvider
{
    /// <summary>
    /// Checks whether autostart is currently enabled for the current user.
    /// </summary>
    bool IsAutostartEnabled();

    /// <summary>
    /// Enables autostart with the operating system.
    /// </summary>
    OperationResult EnableAutostart();

    /// <summary>
    /// Disables autostart with the operating system.
    /// </summary>
    OperationResult DisableAutostart();
}
