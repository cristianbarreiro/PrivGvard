using PrivLock.Platform.Abstractions;

namespace PrivLock.Infrastructure.Common.Storage;

public sealed class NoOpActivePrivacySessionMarker : IActivePrivacySessionMarker
{
    public bool EnsureActive(Guid sessionId) => false;

    public void Delete(Guid sessionId)
    {
    }

    public void Reconcile(Guid? activeSessionId)
    {
    }
}

