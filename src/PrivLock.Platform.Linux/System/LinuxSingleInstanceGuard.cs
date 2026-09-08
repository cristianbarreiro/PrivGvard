using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Linux.System;

/// <summary>
/// Ensures single-instance execution on Linux using a PID lockfile in the user's cache/runtime directory.
/// </summary>
public sealed class LinuxSingleInstanceGuard : ISingleInstanceGuard
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxSingleInstanceGuard>();
    private readonly string _lockFilePath;
    private FileStream? _fileStream;
    private bool _hasAcquired;

    public LinuxSingleInstanceGuard()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrivGvard");

        Directory.CreateDirectory(appData);
        _lockFilePath = Path.Combine(appData, "privgvard.lock");
    }

    public bool TryAcquireSingleInstance()
    {
        try
        {
            _fileStream = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _hasAcquired = true;
            return true;
        }
        catch (IOException)
        {
            Log.Information("Another instance of PrivGvard is already running on Linux.");
            _hasAcquired = false;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create single-instance lock file on Linux");
            return false;
        }
    }

    public void Release()
    {
        if (_fileStream != null && _hasAcquired)
        {
            try
            {
                _fileStream.Dispose();
                _fileStream = null;
                // Keep the inode in place. Deleting after unlocking lets two successors lock two
                // different inodes during the close/unlink/open race.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error releasing Linux single-instance lock file");
            }
            finally
            {
                _hasAcquired = false;
            }
        }
    }

    public void Dispose()
    {
        Release();
    }
}
