using Avalonia.Threading;
using PrivLock.Application.Services;
using PrivLock.Domain.Capabilities;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using PrivLock.UI.ViewModels;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class AutostartViewModelSyncTests
{
    [Fact]
    public async Task MainAndSettingsViewModelsShareExternalEffectiveStateAndUnknownRead()
    {
        var stateStore = new TestStateStore();
        var autostartProvider = new FakeAutostartProvider();
        var protectionService = new ProtectionService(
            null!,
            null!,
            new TestCapabilityProvider(),
            stateStore);
        var localizationService = new LocalizationService(stateStore);
        localizationService.Initialize();
        var sharedState = new AutostartStateService(
            new SettingsService(autostartProvider, stateStore),
            new NoOpAutostartChangeObserver());
        await sharedState.StartAsync();

        var mainViewModel = new MainViewModel(
            protectionService,
            sharedState,
            localizationService);
        var settingsViewModel = new SettingsViewModel(
            sharedState,
            localizationService,
            protectionService);

        autostartProvider.EffectiveStatus = new AutostartStatus(AutostartState.Enabled);
        await sharedState.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(mainViewModel.IsAutostartEnabled);
        Assert.True(settingsViewModel.IsAutostartEnabled);
        Assert.True(settingsViewModel.IsAutostartStatusKnown);

        autostartProvider.EffectiveStatus =
            new AutostartStatus(AutostartState.Failed, "Registry access denied.");
        await sharedState.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(mainViewModel.IsAutostartEnabled);
        Assert.Null(settingsViewModel.IsAutostartEnabled);
        Assert.False(settingsViewModel.IsAutostartStatusKnown);
        Assert.False(settingsViewModel.IsAutostartToggleEnabled);
        Assert.Contains("Registry access denied", settingsViewModel.AutostartStatusMessage);

        settingsViewModel.Dispose();
        mainViewModel.Dispose();
        await sharedState.StopAsync();
    }

    private sealed class TestStateStore : IStateStore
    {
        public DesiredState State { get; private set; } = new();

        public DesiredState Load() => State;

        public void Save(DesiredState state) => State = state;
    }

    private sealed class FakeAutostartProvider : IAutostartProvider
    {
        public AutostartStatus EffectiveStatus { get; set; } =
            new(AutostartState.Disabled);

        public Task<AutostartStatus> GetAutostartStatusAsync() =>
            Task.FromResult(EffectiveStatus);

        public Task<OperationResult> EnableAutostartAsync() =>
            Task.FromResult(OperationResult.Ok());

        public Task<OperationResult> DisableAutostartAsync() =>
            Task.FromResult(OperationResult.Ok());
    }

    private sealed class TestCapabilityProvider : IPlatformCapabilityProvider
    {
        public PlatformCapabilities Capabilities { get; } = new();

        public PlatformInfo PlatformInfo { get; } = new()
        {
            OperatingSystemName = "Windows",
            OsVersion = "Windows 11",
            Architecture = "x64",
            Is64Bit = true,
            IsElevated = false
        };
    }
}
