using PrivLock.Infrastructure.Common.Storage;
using Xunit;

namespace PrivLock.Infrastructure.Tests;

public sealed class FileActivePrivacySessionMarkerTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"PrivLock_ActiveMarkerTests_{Guid.NewGuid():N}");

    private string MarkerPath => Path.Combine(
        _testDirectory,
        FileActivePrivacySessionMarker.FileName);

    [Fact]
    public void EnsureActive_IsIdempotentButRefusesToReplaceAnotherSession()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        var sessionId = Guid.NewGuid();

        Assert.True(marker.EnsureActive(sessionId));
        Assert.False(marker.EnsureActive(sessionId));

        Assert.Throws<IOException>(() => marker.EnsureActive(Guid.NewGuid()));
        Assert.Equal(sessionId.ToString("D"), File.ReadAllText(MarkerPath));
    }

    [Fact]
    public void Reconcile_WithValidatedActiveSession_ReplacesStaleMarker()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        var staleSessionId = Guid.NewGuid();
        var activeSessionId = Guid.NewGuid();
        marker.EnsureActive(staleSessionId);

        marker.Reconcile(activeSessionId);

        Assert.Equal(activeSessionId.ToString("D"), File.ReadAllText(MarkerPath));
        Assert.False(marker.EnsureActive(activeSessionId));
    }

    [Fact]
    public void Reconcile_WithValidatedActiveSession_ReplacesMalformedMarker()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        var activeSessionId = Guid.NewGuid();
        File.WriteAllText(MarkerPath, "not-a-session-id");

        marker.Reconcile(activeSessionId);

        Assert.Equal(activeSessionId.ToString("D"), File.ReadAllText(MarkerPath));
    }

    [Fact]
    public void Reconcile_WithoutActiveSession_RemovesAnyStaleMarker()
    {
        var marker = new FileActivePrivacySessionMarker(_testDirectory);
        marker.EnsureActive(Guid.NewGuid());

        marker.Reconcile(activeSessionId: null);

        Assert.False(File.Exists(MarkerPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }
}
