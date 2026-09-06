using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Application.Services;

/// <summary>
/// One source of truth for tray exit, window exit, OS shutdown/logout, Program.Main fallback,
/// and recoverable fatal-exception cleanup.
/// </summary>
public sealed class ShutdownCoordinator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ShutdownCoordinator>();
    private readonly ProtectionService _protectionService;
    private readonly object _sync = new();
    private Task<PrivacyRecoveryResult>? _activeRestore;

    public ShutdownCoordinator(ProtectionService protectionService)
    {
        _protectionService = protectionService;
    }

    public Task<PrivacyRecoveryResult> RestoreAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_activeRestore != null &&
                (!_activeRestore.IsCompleted ||
                 _protectionService.IsShutdownStarted &&
                 !_protectionService.HasPendingDesiredStateCleanup))
            {
                return _activeRestore;
            }

            // Reject new mutations synchronously, then run the restore outside the caller's
            // synchronization context. In particular, an OS shutdown callback must be able to
            // return to the UI dispatcher so an already-admitted UI operation can finish and
            // release ProtectionService's operation gate.
            _protectionService.SignalShutdown();
            _activeRestore = Task.Run(() => RestoreCoreAsync(reason, cancellationToken));
            return _activeRestore;
        }
    }

    /// <summary>
    /// Observes centralized restoration for a bounded interval without cancelling the underlying
    /// durable recovery pass when the interval expires.
    /// </summary>
    public async Task<PrivacyRecoveryResult?> RestoreWithinAsync(
        string reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var restore = RestoreAsync(reason, CancellationToken.None);
        try
        {
            return await restore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Error(
                "Timed out after {TimeoutMs}ms while observing termination restoration; the durable recovery pass remains active",
                timeout.TotalMilliseconds);
            return null;
        }
    }

    public void AbortShutdownAfterFailedUserExit()
    {
        Log.Warning("User-requested shutdown was cancelled because restoration remains retryable");
        _protectionService.AbortShutdown();
    }

    public bool TryRestoreWithin(string reason, TimeSpan timeout, out PrivacyRecoveryResult? result)
    {
        try
        {
            // RestoreAsync itself dispatches recovery to the pool. This synchronous adapter is
            // reserved for non-UI termination handlers such as ProcessExit/AppDomain callbacks.
            var task = RestoreAsync(reason, CancellationToken.None);
            if (!task.Wait(timeout))
            {
                Log.Error("Timed out after {TimeoutMs}ms while attempting termination restoration", timeout.TotalMilliseconds);
                result = null;
                return false;
            }

            result = task.GetAwaiter().GetResult();
            return result.SafeToExit;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Termination restoration failed for reason {Reason}", reason);
            result = null;
            return false;
        }
    }

    public object GetDiagnosticSummary() => _protectionService.GetRecoveryDiagnosticSummary();

    private async Task<PrivacyRecoveryResult> RestoreCoreAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        Log.Information("Centralized shutdown restoration requested: Reason={Reason}", reason);
        var result = await _protectionService.BeginShutdownAndRestoreAsync(reason, cancellationToken);
        Log.Information(
            "Centralized shutdown restoration completed: SafeToExit={SafeToExit}, Conflicts={Conflicts}, IrreducibleAmbiguities={IrreducibleAmbiguities}, Missing={Missing}, Failed={Failed}",
            result.SafeToExit,
            result.ConflictCount,
            result.IrreducibleAmbiguityCount,
            result.MissingCount,
            result.FailedCount);
        return result;
    }
}
