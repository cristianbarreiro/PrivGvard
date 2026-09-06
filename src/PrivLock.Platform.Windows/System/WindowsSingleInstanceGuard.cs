using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.System;

/// <summary>
/// Ensures single-instance execution on Windows using a system-wide named Mutex.
/// </summary>
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsSingleInstanceGuard>();
    private const string MutexName = @"Global\PrivLock_SingleInstance";
    private const string UninstallGateMutexName = @"Global\PrivLock_UninstallGate";

    private Mutex? _mutex;
    private Mutex? _uninstallGate;
    private bool _hasAcquired;
    private bool _hasUninstallGate;

    public bool TryAcquireSingleInstance()
    {
        try
        {
            if (_mutex != null || _uninstallGate != null)
                return _hasAcquired && _hasUninstallGate;

            _uninstallGate = new Mutex(initiallyOwned: false, UninstallGateMutexName);
            _hasUninstallGate = TryAcquire(_uninstallGate);
            if (!_hasUninstallGate)
            {
                Log.Information("PrivLock cannot start while the uninstall gate is owned.");
                DisposeUnowned(ref _uninstallGate);
                return false;
            }

            _mutex = new Mutex(initiallyOwned: false, MutexName);
            _hasAcquired = TryAcquire(_mutex);

            if (!_hasAcquired)
            {
                Log.Information("Another instance of PrivLock is already running on Windows.");
                DisposeUnowned(ref _mutex);
                ReleaseUninstallGateCore();
            }

            return _hasAcquired;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create or acquire single-instance mutex on Windows");
            ReleaseMutexCore(ref _mutex, ref _hasAcquired, "single-instance");
            ReleaseUninstallGateCore();
            _hasAcquired = false;
            return false; // Recovery journal safety requires exclusive ownership.
        }
    }

    /// <summary>
    /// Releases only the uninstall gate while retaining the main single-instance mutex. The safe
    /// uninstall wrapper uses this immediately before launching Inno and waits for Inno's admission
    /// handshake before allowing the main mutex to be released.
    /// </summary>
    public bool ReleaseUninstallGateForHandoff()
    {
        if (!_hasAcquired || !_hasUninstallGate)
            return false;

        return ReleaseUninstallGateCore();
    }

    public void Release()
    {
        ReleaseMutexCore(ref _mutex, ref _hasAcquired, "single-instance");
        ReleaseUninstallGateCore();
    }

    public void Dispose()
    {
        Release();
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private bool ReleaseUninstallGateCore() =>
        ReleaseMutexCore(ref _uninstallGate, ref _hasUninstallGate, "uninstall-gate");

    private static bool ReleaseMutexCore(ref Mutex? field, ref bool owned, string purpose)
    {
        var mutex = Interlocked.Exchange(ref field, null);
        if (mutex == null)
            return !owned;

        var released = !owned;
        try
        {
            if (owned)
            {
                mutex.ReleaseMutex();
                released = true;
            }
        }
        catch (ApplicationException ex)
        {
            Log.Warning(ex, "The {MutexPurpose} mutex was not owned when release was attempted", purpose);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error releasing the {MutexPurpose} mutex on Windows", purpose);
        }
        finally
        {
            mutex.Dispose();
            owned = false;
        }

        return released;
    }

    private static void DisposeUnowned(ref Mutex? field)
    {
        var mutex = Interlocked.Exchange(ref field, null);
        mutex?.Dispose();
    }
}
