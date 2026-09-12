namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Ensures only one instance of PrivLock runs at a time on the machine.
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Attempts to acquire the single-instance lock.
    /// </summary>
    /// <returns>True if this is the first/only instance; false if another instance is running.</returns>
    bool TryAcquireSingleInstance();

    /// <summary>
    /// Releases the single-instance lock cleanly on application exit.
    /// </summary>
    void Release();
}
