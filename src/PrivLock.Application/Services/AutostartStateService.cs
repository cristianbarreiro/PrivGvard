using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Application.Services;

/// <summary>
/// Owns the single effective autostart state shared by the application's view models.
/// </summary>
public sealed class AutostartStateService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AutostartStateService>();
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(120);

    private readonly SettingsService _settingsService;
    private readonly IAutostartChangeObserver _changeObserver;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _lifetime;
    private Task? _observerTask;
    private long _changeVersion;
    private bool _started;
    private string? _observerErrorMessage;

    public AutostartStateService(
        SettingsService settingsService,
        IAutostartChangeObserver changeObserver)
    {
        _settingsService = settingsService;
        _changeObserver = changeObserver;
    }

    public AutostartStatus Status { get; private set; } =
        new(AutostartState.Failed);

    public bool RequiresVisiblePolling => _changeObserver.RequiresVisiblePolling;

    public event Action<AutostartStatus>? StatusChanged;

    public async Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            if (_started)
                return;

            _started = true;
            _observerErrorMessage = null;
            _lifetime = new CancellationTokenSource();
            _observerTask = _changeObserver.ObserveChangesAsync(
                QueueExternalRefresh,
                OnObserverError,
                _lifetime.Token);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    public Task<AutostartStatus> RefreshAsync() =>
        RefreshAsync(CancellationToken.None);

    private async Task<AutostartStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAndPublishStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OperationResult> SetAutostartAsync(bool enable)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _settingsService.SetAutostartAsync(enable).ConfigureAwait(false);
            var effective = await ReadAndPublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Success && !MatchesRequestedState(effective, enable))
            {
                return OperationResult.Fail(
                    effective.Message ??
                    $"Windows did not confirm the requested autostart state ({effective.State}).");
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        Task? observerTask;
        lock (_lifecycleLock)
        {
            lifetime = _lifetime;
            observerTask = _observerTask;
            _lifetime = null;
            _observerTask = null;
            _started = false;
        }

        if (lifetime == null)
            return;

        lifetime.Cancel();
        Interlocked.Increment(ref _changeVersion);
        if (observerTask != null)
        {
            try
            {
                await observerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        lifetime.Dispose();
    }

    private async Task<AutostartStatus> ReadAndPublishStatusAsync(CancellationToken cancellationToken)
    {
        AutostartStatus status;
        try
        {
            status = await _settingsService.GetAutostartStatusAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read the effective autostart state");
            status = new AutostartStatus(
                AutostartState.Failed,
                $"Windows startup state could not be read: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observerError = Volatile.Read(ref _observerErrorMessage);
        if (!string.IsNullOrWhiteSpace(observerError))
        {
            status = status with
            {
                Message = string.IsNullOrWhiteSpace(status.Message)
                    ? observerError
                    : $"{status.Message} {observerError}"
            };
        }
        Publish(status);
        return status;
    }

    private void Publish(AutostartStatus status)
    {
        if (Status == status)
            return;

        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void QueueExternalRefresh()
    {
        CancellationToken token;
        lock (_lifecycleLock)
        {
            if (_lifetime is not { IsCancellationRequested: false } lifetime)
                return;

            token = lifetime.Token;
        }

        var version = Interlocked.Increment(ref _changeVersion);
        _ = RefreshAfterDebounceAsync(version, token);
    }

    private async Task RefreshAfterDebounceAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ChangeDebounce, cancellationToken).ConfigureAwait(false);
            if (version == Interlocked.Read(ref _changeVersion))
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Debounced autostart refresh failed");
        }
    }

    private void OnObserverError(Exception exception)
    {
        lock (_lifecycleLock)
        {
            if (_lifetime is not { IsCancellationRequested: false })
                return;
            Volatile.Write(
                ref _observerErrorMessage,
                $"Windows startup changes can no longer be monitored: {exception.Message}");
        }

        Log.Error(exception, "External autostart change observation stopped");
        Publish(new AutostartStatus(
            AutostartState.Failed,
            Volatile.Read(ref _observerErrorMessage)));
    }

    private static bool MatchesRequestedState(AutostartStatus status, bool enable) =>
        enable
            ? status.State == AutostartState.Enabled
            : status.State is AutostartState.Disabled
                or AutostartState.DisabledByUser
                or AutostartState.DisabledByPolicy;
}

public sealed class NoOpAutostartChangeObserver : IAutostartChangeObserver
{
    public bool RequiresVisiblePolling => false;

    public Task ObserveChangesAsync(
        Action onChanged,
        Action<Exception> onError,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
