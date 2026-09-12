using System.Collections.Concurrent;
using System.Text;
using PrivLock.Platform.Abstractions;

namespace PrivLock.Infrastructure.Common.Storage;

/// <summary>
/// Durable marker placed at a machine-discoverable location below the user's profile. The Windows
/// uninstaller checks every profile for this marker before removing the recovery-capable binary.
/// </summary>
public sealed class FileActivePrivacySessionMarker : IActivePrivacySessionMarker
{
    public const string FileName = "active-session-v1.marker";
    private const int MaximumMarkerBytes = 128;

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _markerPath;
    private readonly object _pathLock;

    public FileActivePrivacySessionMarker(string markerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerDirectory);
        var directory = Path.GetFullPath(markerDirectory);
        Directory.CreateDirectory(directory);
        _markerPath = Path.Combine(directory, FileName);
        _pathLock = PathLocks.GetOrAdd(_markerPath, static _ => new object());
    }

    public bool EnsureActive(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("The active-session marker requires a non-empty session ID.", nameof(sessionId));

        lock (_pathLock)
        {
            if (File.Exists(_markerPath))
            {
                var existing = ReadMarker();
                if (existing == sessionId)
                    return false;

                throw new IOException(
                    "A different or invalid active privacy-session marker already exists; refusing to replace it.");
            }

            WriteMarker(sessionId, overwrite: false);
            return true;
        }
    }

    public void Delete(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("The active-session marker requires a non-empty session ID.", nameof(sessionId));

        lock (_pathLock)
        {
            if (!File.Exists(_markerPath))
                return;
            if (ReadMarker() != sessionId)
            {
                throw new IOException(
                    "The active privacy-session marker belongs to a different or invalid session; refusing to delete it.");
            }

            File.Delete(_markerPath);
            if (File.Exists(_markerPath))
                throw new IOException("The active privacy-session marker deletion could not be verified.");
        }
    }

    public void Reconcile(Guid? activeSessionId)
    {
        if (activeSessionId == Guid.Empty)
            throw new ArgumentException("The active-session marker requires a non-empty session ID.", nameof(activeSessionId));

        lock (_pathLock)
        {
            if (activeSessionId.HasValue)
            {
                if (File.Exists(_markerPath) && ReadMarker() == activeSessionId.Value)
                    return;

                // Load calls Reconcile only after validating the durable journal. That journal is
                // authoritative for this user's one active session, so atomically replace a stale
                // or malformed marker instead of leaving recovery permanently inaccessible.
                WriteMarker(activeSessionId.Value, overwrite: true);
                return;
            }

            if (File.Exists(_markerPath))
            {
                File.Delete(_markerPath);
                if (File.Exists(_markerPath))
                    throw new IOException("The stale privacy-session marker deletion could not be verified.");
            }
        }
    }

    private void WriteMarker(Guid sessionId, bool overwrite)
    {
        var tempPath = _markerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Encoding.ASCII.GetBytes(sessionId.ToString("D"));
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: MaximumMarkerBytes,
                options: FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _markerPath, overwrite);
            if (ReadMarker() != sessionId)
                throw new IOException("The active privacy-session marker could not be verified after writing.");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private Guid ReadMarker()
    {
        var length = new FileInfo(_markerPath).Length;
        if (length is <= 0 or > MaximumMarkerBytes)
            return Guid.Empty;

        using var stream = new FileStream(_markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: MaximumMarkerBytes,
            leaveOpen: false);
        var contents = reader.ReadToEnd();
        return Guid.TryParseExact(contents, "D", out var sessionId) ? sessionId : Guid.Empty;
    }
}
