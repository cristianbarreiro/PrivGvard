using System.Collections.Concurrent;
using PrivLock.Platform.Windows.System;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class WindowsAutostartChangeObserverTests
{
    [Fact]
    public async Task ObserverRearmsAfterEachChangeAndStopsOnCancellation()
    {
        var waiter = new FakeRegistryChangeWaiter();
        var observer = new WindowsAutostartChangeObserver(() => false, waiter);
        using var cancellation = new CancellationTokenSource();
        var changes = 0;
        var errors = new ConcurrentQueue<Exception>();
        var observation = observer.ObserveChangesAsync(
            () => Interlocked.Increment(ref changes),
            errors.Enqueue,
            cancellation.Token);

        await waiter.WaitUntilCalledAsync(1);
        waiter.SignalChange();
        await WaitUntilAsync(() => Volatile.Read(ref changes) == 1);
        await waiter.WaitUntilCalledAsync(2);
        waiter.SignalChange();
        await WaitUntilAsync(() => Volatile.Read(ref changes) == 2);
        await waiter.WaitUntilCalledAsync(3);

        cancellation.Cancel();
        await observation;
        waiter.SignalChange();
        await Task.Delay(30);

        Assert.Equal(2, changes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ObserverReportsRegistryNotificationFailure()
    {
        var expected = new InvalidOperationException("notification registration failed");
        var waiter = new FakeRegistryChangeWaiter { Failure = expected };
        var observer = new WindowsAutostartChangeObserver(() => false, waiter);
        Exception? reported = null;

        await observer.ObserveChangesAsync(
            () => Assert.Fail("A failed observer must not report a change."),
            exception => reported = exception,
            CancellationToken.None);

        Assert.Same(expected, reported);
    }

    [Fact]
    public async Task PackagedObserverRequestsVisiblePollingInsteadOfInventingAnEvent()
    {
        var waiter = new FakeRegistryChangeWaiter();
        var observer = new WindowsAutostartChangeObserver(() => true, waiter);
        var changes = 0;

        Assert.True(observer.RequiresVisiblePolling);
        await observer.ObserveChangesAsync(
            () => changes++,
            exception => Assert.Fail(exception.Message),
            CancellationToken.None);

        Assert.Equal(0, waiter.CallCount);
        Assert.Equal(0, changes);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeRegistryChangeWaiter : IWindowsRegistryChangeWaiter
    {
        private readonly SemaphoreSlim _notifications = new(0);
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _calls = new();
        private int _callCount;

        public Exception? Failure { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task WaitForChangeAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            _calls.GetOrAdd(call, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            if (Failure != null)
                throw Failure;

            await _notifications.WaitAsync(cancellationToken);
        }

        public void SignalChange() => _notifications.Release();

        public async Task WaitUntilCalledAsync(int call)
        {
            var signal = _calls.GetOrAdd(call, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await signal.Task.WaitAsync(timeout.Token);
        }
    }
}
