using PrivLock.Domain.Models;

namespace PrivLock.Platform.Abstractions;

/// <summary>
/// Durable store for the active reversible privacy session. Unlike IStateStore, failures are
/// propagated because no operating-system mutation may proceed without a committed journal.
/// </summary>
public interface IPrivacySessionStore
{
    PrivacySession? Load();
    void Save(PrivacySession session);
}
