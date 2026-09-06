namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Publishes a machine-visible, per-user indication that a durable privacy session may own
/// operating-system state. Implementations must fail closed: Save cannot authorize a native
/// mutation unless the active marker is durable first.
/// </summary>
public interface IActivePrivacySessionMarker
{
    /// <returns>True only when this call created a new marker.</returns>
    bool EnsureActive(Guid sessionId);

    void Delete(Guid sessionId);

    /// <summary>
    /// Removes stale markers owned by the current user and recreates the expected active marker.
    /// </summary>
    void Reconcile(Guid? activeSessionId);
}

