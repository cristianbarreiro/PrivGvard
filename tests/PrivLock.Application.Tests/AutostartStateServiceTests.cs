using Moq;
using System.Collections.Concurrent;
using PrivLock.Application.Services;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Xunit;

namespace PrivLock.Application.Tests;

public sealed class AutostartStateServiceTests
{
    [Fact]
    public async Task ExternalChangePublishesOneEffectiveStateToAllSubscribers()
    {
        var provider = new Mock<IAutostartProvider>();
        var store = CreateStateStore();
        var effective = new AutostartStatus(AutostartState.Disabled);
        provider.Setup(p => p.GetAutostartStatusAsync())
            .Returns(() => Task.FromResult(effective));
        var observer = new FakeAutostartChangeObserver();
        var state = new AutostartStateService(new SettingsService(provider.Object, store.Object), observer);
        var mainViewUpdates = new ConcurrentQueue<AutostartStatus>();
        var settingsViewUpdates = new ConcurrentQueue<AutostartStatus>();
        state.StatusChanged += status => mainViewUpdates.Enqueue(status);
        state.StatusChanged += status => settingsViewUpdates.Enqueue(status);

        await state.StartAsync();
        effective = new AutostartStatus(AutostartState.Enabled);
        observer.NotifyChange();

        await WaitUntilAsync(() =>
            state.Status.State == AutostartState.Enabled &&
            mainViewUpdates.Count == 2 &&
            settingsViewUpdates.Count == 2);

        Assert.Equal(AutostartState.Enabled, mainViewUpdates.Last().State);
        Assert.Equal(AutostartState.Enabled, settingsViewUpdates.Last().State);
        await state.StopAsync();
    }

    [Fact]
    public async Task RapidExternalChangesAreDebouncedAndFinalStateIsRead()
    {
        var provider = new Mock<IAutostartProvider>();
        var store = CreateStateStore();
        var effective = new AutostartStatus(AutostartState.Disabled);
        var reads = 0;
        provider.Setup(p => p.GetAutostartStatusAsync())
            .Returns(() =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(effective);
            });
        var observer = new FakeAutostartChangeObserver();
        var state = new AutostartStateService(new SettingsService(provider.Object, store.Object), observer);

        await state.StartAsync();
        effective = new AutostartStatus(AutostartState.Enabled);
        for (var i = 0; i < 5; i++)
            observer.NotifyChange();

        await WaitUntilAsync(() => state.Status.State == AutostartState.Enabled);

        Assert.Equal(2, reads);
        await state.StopAsync();
    }

    [Fact]
    public async Task LocalTogglePublishesVerifiedStateWithoutStartingAnotherOperation()
    {
        var provider = new Mock<IAutostartProvider>();
        var store = CreateStateStore();
        var effective = new AutostartStatus(AutostartState.Disabled);
        provider.Setup(p => p.GetAutostartStatusAsync())
            .Returns(() => Task.FromResult(effective));
        provider.Setup(p => p.EnableAutostartAsync())
            .Callback(() => effective = new AutostartStatus(AutostartState.Enabled))
            .ReturnsAsync(OperationResult.Ok());
        var observer = new FakeAutostartChangeObserver();
        var state = new AutostartStateService(new SettingsService(provider.Object, store.Object), observer);
        var updates = new ConcurrentQueue<AutostartStatus>();
        state.StatusChanged += status => updates.Enqueue(status);

        await state.StartAsync();
        var result = await state.SetAutostartAsync(true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(AutostartState.Enabled, state.Status.State);
        Assert.Equal(
            new[] { AutostartState.Disabled, AutostartState.Enabled },
            updates.Select(status => status.State));
        provider.Verify(p => p.EnableAutostartAsync(), Times.Once);
        await state.StopAsync();
    }

    [Fact]
    public async Task FailedReadRemainsUnknownInsteadOfBecomingDisabled()
    {
        var provider = new Mock<IAutostartProvider>();
        provider.Setup(p => p.GetAutostartStatusAsync())
            .ReturnsAsync(new AutostartStatus(AutostartState.Failed, "Registry access denied."));
        var state = new AutostartStateService(
            new SettingsService(provider.Object, CreateStateStore().Object),
            new FakeAutostartChangeObserver());

        await state.StartAsync();

        Assert.Equal(AutostartState.Failed, state.Status.State);
        Assert.Equal("Registry access denied.", state.Status.Message);
        await state.StopAsync();
    }

    [Fact]
    public async Task PersistedAutostartPreferenceNeverOverridesEffectiveWindowsState()
    {
        var provider = new Mock<IAutostartProvider>();
        var store = CreateStateStore(new DesiredState { Autostart = true });
        provider.Setup(p => p.GetAutostartStatusAsync())
            .ReturnsAsync(new AutostartStatus(AutostartState.Disabled));
        var state = new AutostartStateService(
            new SettingsService(provider.Object, store.Object),
            new FakeAutostartChangeObserver());

        await state.StartAsync();

        Assert.Equal(AutostartState.Disabled, state.Status.State);
        provider.Verify(p => p.EnableAutostartAsync(), Times.Never);
        await state.StopAsync();
    }

    [Fact]
    public async Task StopCancelsObservationAndIgnoresLaterCallbacks()
    {
        var provider = new Mock<IAutostartProvider>();
        provider.Setup(p => p.GetAutostartStatusAsync())
            .ReturnsAsync(new AutostartStatus(AutostartState.Disabled));
        var observer = new FakeAutostartChangeObserver();
        var state = new AutostartStateService(
            new SettingsService(provider.Object, CreateStateStore().Object),
            observer);

        await state.StartAsync();
        await state.StopAsync();
        observer.NotifyChange();
        await Task.Delay(180);

        provider.Verify(p => p.GetAutostartStatusAsync(), Times.Once);
    }

    private static Mock<IStateStore> CreateStateStore(DesiredState? desiredState = null)
    {
        var store = new Mock<IStateStore>();
        store.Setup(s => s.Load()).Returns(desiredState ?? new DesiredState());
        return store;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeAutostartChangeObserver : IAutostartChangeObserver
    {
        private Action? _onChanged;

        public bool RequiresVisiblePolling { get; init; }

        public Task ObserveChangesAsync(
            Action onChanged,
            Action<Exception> onError,
            CancellationToken cancellationToken)
        {
            _onChanged = onChanged;
            return WaitForStopAsync(cancellationToken);
        }

        public void NotifyChange() => _onChanged?.Invoke();

        private static async Task WaitForStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
