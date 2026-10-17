using Microsoft.Win32;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.System;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class WindowsAutostartProviderTests : IDisposable
{
    private readonly string _testRootKeyPath;
    private readonly string _testRunKeyPath;
    private readonly string _testStartupApprovedKeyPath;
    private readonly string _testDirectory;
    private readonly string _dummyExePath;

    public WindowsAutostartProviderTests()
    {
        var testId = Guid.NewGuid().ToString("N");
        _testRootKeyPath = $@"Software\PrivGvardTests\Autostart_{testId}";
        _testRunKeyPath = $@"{_testRootKeyPath}\Run";
        _testStartupApprovedKeyPath = $@"{_testRootKeyPath}\StartupApproved\Run";
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PrivGvardAutostart {testId}");
        Directory.CreateDirectory(_testDirectory);
        _dummyExePath = Path.Combine(_testDirectory, "PrivGvard.exe");
        File.WriteAllBytes(_dummyExePath, [0x4D, 0x5A]);

        using var runKey = Registry.CurrentUser.CreateSubKey(_testRunKeyPath);
        using var approvedKey = Registry.CurrentUser.CreateSubKey(_testStartupApprovedKeyPath);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testRootKeyPath, throwOnMissingSubKey: false);
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [Fact]
    public async Task Win32EnableAndDisable_VerifyRunEntryAndEffectiveReadback()
    {
        var provider = CreateRegistryProvider();

        var enabled = await provider.EnableAutostartAsync();

        Assert.True(enabled.Success, enabled.ErrorMessage);
        Assert.Equal(
            $"\"{_dummyExePath}\" --minimized",
            new WindowsAutostartRegistry(_testRunKeyPath, _testStartupApprovedKeyPath)
                .ReadRunValue("PrivGvard"));
        Assert.Equal(AutostartState.Enabled, (await provider.GetAutostartStatusAsync()).State);

        var disabled = await provider.DisableAutostartAsync();

        Assert.True(disabled.Success, disabled.ErrorMessage);
        Assert.Null(new WindowsAutostartRegistry(_testRunKeyPath, _testStartupApprovedKeyPath)
            .ReadRunValue("PrivGvard"));
        Assert.Equal(AutostartState.Disabled, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task Win32LegacyMigration_UsesRealRegistryWithoutCreatingDuplicates()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
            runKey!.SetValue("PrivLock", "\"C:\\Old\\PrivLock.exe\" --minimized", RegistryValueKind.String);

        var provider = CreateRegistryProvider();
        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.True(result.Success, result.ErrorMessage);
        using var verifyRun = Registry.CurrentUser.OpenSubKey(_testRunKeyPath);
        Assert.Equal($"\"{_dummyExePath}\" --minimized", verifyRun!.GetValue("PrivGvard"));
        Assert.Null(verifyRun.GetValue("PrivLock"));
    }

    [Fact]
    public async Task EnableAutostart_RejectsDevelopmentPathAndWrongExecutable()
    {
        var developmentPath = Path.Combine(
            _testDirectory, "bin", "Debug", "net10.0-windows", "PrivGvard.exe");
        var unstable = CreateProvider(
            new MemoryRegistry(),
            pathProvider: () => developmentPath,
            fileExists: _ => true,
            isProductExecutable: _ => true);

        var unstableResult = await unstable.EnableAutostartAsync();

        Assert.False(unstableResult.Success);
        Assert.Contains("development build path", unstableResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var wrongExecutable = Path.Combine(_testDirectory, "OtherApp.exe");
        var invalid = CreateProvider(
            new MemoryRegistry(),
            pathProvider: () => wrongExecutable,
            fileExists: _ => true,
            isProductExecutable: _ => true);

        var invalidResult = await invalid.EnableAutostartAsync();

        Assert.False(invalidResult.Success);
        Assert.Contains("not named PrivGvard.exe", invalidResult.ErrorMessage);
    }

    [Fact]
    public async Task EnableAutostart_AllowsReleasePathWithoutTargetFrameworkSegment()
    {
        var installedPath = Path.Combine(_testDirectory, "bin", "Release", "PrivGvard.exe");
        var provider = CreateProvider(
            new MemoryRegistry(),
            pathProvider: () => installedPath,
            fileExists: _ => true,
            isProductExecutable: _ => true);

        var result = await provider.EnableAutostartAsync();

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task EnableAutostart_RejectsMissingAndNonFullyQualifiedPaths()
    {
        var missing = CreateProvider(
            new MemoryRegistry(),
            pathProvider: () => Path.Combine(_testDirectory, "Missing", "PrivGvard.exe"),
            fileExists: _ => false,
            isProductExecutable: _ => true);
        var missingResult = await missing.EnableAutostartAsync();
        Assert.False(missingResult.Success);
        Assert.Contains("does not exist on disk", missingResult.ErrorMessage);

        var relative = CreateProvider(
            new MemoryRegistry(),
            pathProvider: () => @"PrivGvard.exe",
            fileExists: _ => true,
            isProductExecutable: _ => true);
        var relativeResult = await relative.EnableAutostartAsync();
        Assert.False(relativeResult.Success);
        Assert.Contains("fully qualified", relativeResult.ErrorMessage);
    }

    [Fact]
    public async Task GetStatus_DistinguishesMissingInvalidAndStaleRegistryPaths()
    {
        var registry = new MemoryRegistry();
        var provider = CreateProvider(registry);

        registry.RunValues["PrivGvard"] = "\"unterminated";
        Assert.Equal(AutostartState.InvalidRegistration, (await provider.GetAutostartStatusAsync()).State);

        registry.RunValues["PrivGvard"] = $"\"{Path.Combine(_testDirectory, "Deleted.exe")}\" --minimized";
        Assert.Equal(AutostartState.InvalidRegistration, (await provider.GetAutostartStatusAsync()).State);

        var oldPath = Path.Combine(_testDirectory, "old", "PrivGvard.exe");
        registry.RunValues["PrivGvard"] = $"\"{oldPath}\" --minimized";
        var stale = await provider.GetAutostartStatusAsync();
        Assert.Equal(AutostartState.MissingExecutable, stale.State);

        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllBytes(oldPath, [0x4D, 0x5A]);
        stale = await provider.GetAutostartStatusAsync();
        Assert.Equal(AutostartState.MigrationRequired, stale.State);
    }

    [Fact]
    public async Task GetStatus_RejectsEmptyOrNonBinaryApprovalValues()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
            runKey!.SetValue("PrivGvard", $"\"{_dummyExePath}\" --minimized", RegistryValueKind.String);
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
            approvedKey!.SetValue("PrivGvard", Array.Empty<byte>(), RegistryValueKind.Binary);

        var provider = CreateRegistryProvider();
        Assert.Equal(AutostartState.Failed, (await provider.GetAutostartStatusAsync()).State);

        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
            approvedKey!.SetValue("PrivGvard", "unexpected type", RegistryValueKind.String);
        Assert.Equal(AutostartState.Failed, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task EnableAutostart_FailsOnRegistryWriteReadAndPostWriteVerificationErrors()
    {
        var writeFailure = new MemoryRegistry { FailWrites = true };
        var failedWrite = await CreateProvider(writeFailure).EnableAutostartAsync();
        Assert.False(failedWrite.Success);
        Assert.Contains("registry write failure", failedWrite.ErrorMessage);

        var readFailure = new MemoryRegistry { FailReads = true };
        var failedRead = await CreateProvider(readFailure).GetAutostartStatusAsync();
        Assert.Equal(AutostartState.Failed, failedRead.State);
        Assert.Contains("registry read failure", failedRead.Message);

        var ignoredWrite = new MemoryRegistry { IgnoreWrites = true };
        var failedVerification = await CreateProvider(ignoredWrite).EnableAutostartAsync();
        Assert.False(failedVerification.Success);
    }

    [Fact]
    public async Task EnableAutostart_RespectsWindowsStartupApprovedUserDisable()
    {
        var registry = new MemoryRegistry();
        registry.SetApproval("PrivGvard", [0xA1, 0x4B], WindowsStartupApprovalState.DisabledByUser);
        var result = await CreateProvider(registry).EnableAutostartAsync();

        Assert.False(result.Success);
        Assert.Equal(AutostartState.DisabledByUser, (await CreateProvider(registry).GetAutostartStatusAsync()).State);
        Assert.Equal(new byte[] { 0xA1, 0x4B }, registry.ApprovedValues["PrivGvard"]);
    }

    [Fact]
    public async Task Migration_RemovesOnlyOwnedLegacyValuesAndLeavesOneClassicMechanism()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivLock"] = "\"C:\\Old\\PrivLock.exe\" --minimized";
        registry.SetApproval("PrivLock", [0xB3, 0x17], WindowsStartupApprovalState.DisabledByUser);
        registry.RunValues["OtherApp"] = "other-app.exe";

        var provider = CreateProvider(registry);
        var migrated = await provider.MigrateLegacyEntriesAsync();
        Assert.True(migrated.Success, migrated.ErrorMessage);

        Assert.False(registry.RunValues.ContainsKey("PrivLock"));
        Assert.False(registry.ApprovedValues.ContainsKey("PrivLock"));
        Assert.Equal("other-app.exe", registry.RunValues["OtherApp"]);
        Assert.Equal(new[] { "OtherApp", "PrivGvard" }, registry.RunValues.Keys.OrderBy(key => key).ToArray());
        Assert.Equal(AutostartState.DisabledByUser, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task Migration_PreservesWindowsUserDisablePreference()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivLock"] = "\"C:\\Old\\PrivLock.exe\" --minimized";
        registry.SetApproval("PrivLock", [0xC5, 0x28], WindowsStartupApprovalState.DisabledByUser);
        var provider = CreateProvider(registry);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new byte[] { 0xC5, 0x28 }, registry.ApprovedValues["PrivGvard"]);
        Assert.Equal(AutostartState.DisabledByUser, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task Migration_LeavesLegacyAndInvalidCurrentEntriesUnchanged()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivLock"] = "\"C:\\Old\\PrivLock.exe\" --minimized";
        registry.RunValues["PrivGvard"] = "\"C:\\Missing\\PrivGvard.exe\" --minimized";
        var provider = CreateProvider(registry);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.False(result.Success);
        Assert.Equal("\"C:\\Old\\PrivLock.exe\" --minimized", registry.RunValues["PrivLock"]);
        Assert.Equal("\"C:\\Missing\\PrivGvard.exe\" --minimized", registry.RunValues["PrivGvard"]);
    }

    [Fact]
    public async Task Migration_RejectsConflictingStartupApprovedStatesWithoutChangingEntries()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivLock"] = "\"C:\\Old\\PrivLock.exe\" --minimized";
        registry.RunValues["PrivGvard"] = $"\"{_dummyExePath}\" --minimized";
        registry.SetApproval("PrivLock", [0xC5, 0x28], WindowsStartupApprovalState.DisabledByUser);
        registry.SetApproval("PrivGvard", [0xE9, 0x4A], WindowsStartupApprovalState.Enabled);
        var provider = CreateProvider(registry);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.False(result.Success);
        Assert.Contains("conflicting Windows user states", result.ErrorMessage);
        Assert.Equal("\"C:\\Old\\PrivLock.exe\" --minimized", registry.RunValues["PrivLock"]);
        Assert.Equal($"\"{_dummyExePath}\" --minimized", registry.RunValues["PrivGvard"]);
        Assert.Equal(new byte[] { 0xC5, 0x28 }, registry.ApprovedValues["PrivLock"]);
        Assert.Equal(new byte[] { 0xE9, 0x4A }, registry.ApprovedValues["PrivGvard"]);
    }

    [Fact]
    public async Task Migration_ValidatesAnExistingCurrentEntryBeforeRemovingLegacyEntry()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivLock"] = "\"C:\\Old\\PrivLock.exe\" --minimized";
        registry.RunValues["PrivGvard"] = $"\"{_dummyExePath}\" --minimized";
        registry.SetApproval("PrivLock", [0xF1, 0x5B], WindowsStartupApprovalState.DisabledByUser);
        var provider = CreateProvider(registry);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(registry.RunValues.ContainsKey("PrivLock"));
        Assert.Equal($"\"{_dummyExePath}\" --minimized", registry.RunValues["PrivGvard"]);
        Assert.False(registry.ApprovedValues.ContainsKey("PrivLock"));
        Assert.Equal(new byte[] { 0xF1, 0x5B }, registry.ApprovedValues["PrivGvard"]);
        Assert.Equal(AutostartState.DisabledByUser, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task GetStatus_RefreshesAfterExternalRunAndApprovalStateChanges()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivGvard"] = $"\"{_dummyExePath}\" --minimized";
        var provider = CreateProvider(registry);

        Assert.Equal(AutostartState.Enabled, (await provider.GetAutostartStatusAsync()).State);

        registry.SetApproval("PrivGvard", [0x71, 0x22], WindowsStartupApprovalState.DisabledByUser);
        Assert.Equal(AutostartState.DisabledByUser, (await provider.GetAutostartStatusAsync()).State);

        registry.ApprovalStates["PrivGvard"] = WindowsStartupApprovalState.Enabled;
        Assert.Equal(AutostartState.Enabled, (await provider.GetAutostartStatusAsync()).State);

        registry.RunValues.Remove("PrivGvard");
        Assert.Equal(AutostartState.Disabled, (await provider.GetAutostartStatusAsync()).State);
    }

    [Theory]
    [InlineData("Enabled", AutostartState.Enabled)]
    [InlineData("EnabledByPolicy", AutostartState.Enabled)]
    [InlineData("Disabled", AutostartState.Disabled)]
    [InlineData("DisabledByUser", AutostartState.DisabledByUser)]
    [InlineData("DisabledByPolicy", AutostartState.DisabledByPolicy)]
    public async Task MsixStatus_MapsWindowsStartupTaskState(
        string taskState,
        AutostartState expectedState)
    {
        var provider = CreateProvider(
            new MemoryRegistry(),
            packaged: true,
            startupTask: new FakeStartupTask(Enum.Parse<WindowsStartupTaskState>(taskState)));

        Assert.Equal(expectedState, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task MsixEnable_RequestsPermissionAndVerifiesWindowsState()
    {
        var startupTask = new FakeStartupTask(WindowsStartupTaskState.Disabled)
        {
            StateAfterEnableRequest = WindowsStartupTaskState.Enabled
        };
        var registry = new MemoryRegistry();
        var provider = CreateProvider(registry, packaged: true, startupTask: startupTask);

        var result = await provider.EnableAutostartAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, startupTask.EnableRequestCount);
        Assert.Equal(AutostartState.Enabled, (await provider.GetAutostartStatusAsync()).State);
        Assert.Empty(registry.RunValues);
    }

    [Theory]
    [InlineData("DisabledByUser", AutostartState.DisabledByUser)]
    [InlineData("DisabledByPolicy", AutostartState.DisabledByPolicy)]
    public async Task MsixEnable_DoesNotClaimSuccessWhenWindowsRefuses(
        string refusedStateName,
        AutostartState expectedStatus)
    {
        var refusedState = Enum.Parse<WindowsStartupTaskState>(refusedStateName);
        var startupTask = new FakeStartupTask(refusedState)
        {
            StateAfterEnableRequest = refusedState
        };
        var provider = CreateProvider(new MemoryRegistry(), packaged: true, startupTask: startupTask);

        var result = await provider.EnableAutostartAsync();

        Assert.False(result.Success);
        Assert.Equal(expectedStatus, (await provider.GetAutostartStatusAsync()).State);
        Assert.Equal(refusedState == WindowsStartupTaskState.DisabledByUser ? 1 : 0, startupTask.EnableRequestCount);
    }

    [Fact]
    public async Task MsixMigration_RemovesStaleWin32CommandsFromPreviousPackageVersions()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivGvard"] = "\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized";
        registry.RunValues["PrivLock"] = "\"C:\\OldPackageVersion\\PrivLock.exe\" --minimized";
        registry.SetApproval("PrivGvard", [0x82, 0x6C], WindowsStartupApprovalState.DisabledByUser);
        var startupTask = new FakeStartupTask(WindowsStartupTaskState.Enabled)
        {
            StateAfterDisable = WindowsStartupTaskState.DisabledByUser
        };
        var provider = CreateProvider(
            registry,
            packaged: true,
            startupTask: startupTask);

        Assert.Equal(AutostartState.MigrationRequired, (await provider.GetAutostartStatusAsync()).State);
        var migrated = await provider.MigrateLegacyEntriesAsync();

        Assert.True(migrated.Success, migrated.ErrorMessage);
        Assert.Empty(registry.RunValues);
        Assert.Empty(registry.ApprovedValues);
        Assert.Equal(AutostartState.DisabledByUser, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task MsixMigration_RemovesOldVersionCommandsFromRealRegistry()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey!.SetValue("PrivGvard", "\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized", RegistryValueKind.String);
            runKey.SetValue("PrivLock", "\"C:\\OldPackageVersion\\PrivLock.exe\" --minimized", RegistryValueKind.String);
        }

        var startupTask = new FakeStartupTask(WindowsStartupTaskState.Enabled);
        var provider = new WindowsAutostartProvider(
            new WindowsAutostartRegistry(_testRunKeyPath, _testStartupApprovedKeyPath),
            startupTask,
            () => true,
            () => _dummyExePath);

        var migrated = await provider.MigrateLegacyEntriesAsync();

        Assert.True(migrated.Success, migrated.ErrorMessage);
        using var verifyRun = Registry.CurrentUser.OpenSubKey(_testRunKeyPath);
        Assert.Null(verifyRun!.GetValue("PrivGvard"));
        Assert.Null(verifyRun.GetValue("PrivLock"));
        Assert.Equal(AutostartState.Enabled, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public async Task MsixMigration_LeavesEntriesWhenStartupTaskStateCannotBeRead()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivGvard"] = "\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized";
        var startupTask = new FakeStartupTask(WindowsStartupTaskState.Enabled)
        {
            GetStateFailure = new InvalidOperationException("StartupTask unavailable")
        };
        var provider = CreateProvider(registry, packaged: true, startupTask: startupTask);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.False(result.Success);
        Assert.Contains("StartupTask unavailable", result.ErrorMessage);
        Assert.Equal("\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized", registry.RunValues["PrivGvard"]);
    }

    [Fact]
    public async Task MsixMigration_LeavesEntriesWhenPolicyConflictsWithDisabledWin32Preference()
    {
        var registry = new MemoryRegistry();
        registry.RunValues["PrivGvard"] = "\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized";
        registry.SetApproval("PrivGvard", [0x93, 0x7D], WindowsStartupApprovalState.DisabledByUser);
        var startupTask = new FakeStartupTask(WindowsStartupTaskState.EnabledByPolicy);
        var provider = CreateProvider(registry, packaged: true, startupTask: startupTask);

        var result = await provider.MigrateLegacyEntriesAsync();

        Assert.False(result.Success);
        Assert.Contains("Windows policy enables", result.ErrorMessage);
        Assert.Equal("\"C:\\OldPackageVersion\\PrivGvard.exe\" --minimized", registry.RunValues["PrivGvard"]);
        Assert.Equal(new byte[] { 0x93, 0x7D }, registry.ApprovedValues["PrivGvard"]);
        Assert.Equal(0, startupTask.DisableCount);
    }

    [Fact]
    public async Task MsixDisable_UsesTaskApiAndVerifiesState()
    {
        var startupTask = new FakeStartupTask(WindowsStartupTaskState.Enabled)
        {
            StateAfterDisable = WindowsStartupTaskState.Disabled
        };
        var provider = CreateProvider(new MemoryRegistry(), packaged: true, startupTask: startupTask);

        var result = await provider.DisableAutostartAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, startupTask.DisableCount);
        Assert.Equal(AutostartState.Disabled, (await provider.GetAutostartStatusAsync()).State);
    }

    [Fact]
    public void ExtractExecutablePath_HandlesQuotedAndUnquotedPaths()
    {
        Assert.Equal(@"C:\Program Files\PrivGvard\PrivGvard.exe",
            WindowsAutostartProvider.ExtractExecutablePath("\"C:\\Program Files\\PrivGvard\\PrivGvard.exe\" --minimized"));
        Assert.Equal(@"C:\PrivGvard\PrivGvard.exe",
            WindowsAutostartProvider.ExtractExecutablePath("C:\\PrivGvard\\PrivGvard.exe --minimized"));
        Assert.Null(WindowsAutostartProvider.ExtractExecutablePath("\"\""));
        Assert.Null(WindowsAutostartProvider.ExtractExecutablePath(null));
    }

    private WindowsAutostartProvider CreateRegistryProvider() =>
        new(_testRunKeyPath, _testStartupApprovedKeyPath, () => _dummyExePath);

    private WindowsAutostartProvider CreateProvider(
        MemoryRegistry? registry = null,
        bool packaged = false,
        FakeStartupTask? startupTask = null,
        Func<string?>? pathProvider = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isProductExecutable = null) =>
        new(
            registry ?? new MemoryRegistry(),
            startupTask ?? new FakeStartupTask(WindowsStartupTaskState.Disabled),
            () => packaged,
            pathProvider ?? (() => _dummyExePath),
            fileExists ?? File.Exists,
            isProductExecutable ?? (_ => true));

    private sealed class MemoryRegistry : IWindowsAutostartRegistry
    {
        public Dictionary<string, object?> RunValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> ApprovedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WindowsStartupApprovalState> ApprovalStates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailReads { get; init; }
        public bool FailWrites { get; init; }
        public bool IgnoreWrites { get; init; }

        public void SetApproval(
            string name,
            byte[] opaqueValue,
            WindowsStartupApprovalState state)
        {
            ApprovedValues[name] = opaqueValue;
            ApprovalStates[name] = state;
        }

        public object? ReadRunValue(string name)
        {
            ThrowIfReadFails();
            return RunValues.TryGetValue(name, out var value) ? value : null;
        }

        public void WriteRunValue(string name, string value)
        {
            if (FailWrites)
                throw new InvalidOperationException("registry write failure");
            if (!IgnoreWrites)
                RunValues[name] = value;
        }

        public void DeleteRunValue(string name)
        {
            if (FailWrites)
                throw new InvalidOperationException("registry write failure");
            RunValues.Remove(name);
        }

        public byte[]? ReadStartupApprovedValue(string name)
        {
            ThrowIfReadFails();
            return ApprovedValues.TryGetValue(name, out var value) ? value : null;
        }

        public WindowsStartupApprovalSnapshot? ReadStartupApproval(string name)
        {
            var value = ReadStartupApprovedValue(name);
            if (value == null)
                return null;

            var state = ApprovalStates.TryGetValue(name, out var configuredState)
                ? configuredState
                : WindowsStartupApprovalState.Enabled;
            return new WindowsStartupApprovalSnapshot(value, state);
        }

        public void DeleteStartupApprovedValue(string name)
        {
            if (FailWrites)
                throw new InvalidOperationException("registry write failure");
            ApprovedValues.Remove(name);
            ApprovalStates.Remove(name);
        }

        public void WriteStartupApprovedValue(string name, byte[] value)
        {
            if (FailWrites)
                throw new InvalidOperationException("registry write failure");
            if (!IgnoreWrites)
            {
                var source = ApprovedValues.FirstOrDefault(entry =>
                    !entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(entry.Value, value));
                if (source.Value != null &&
                    ApprovalStates.TryGetValue(source.Key, out var sourceState))
                {
                    ApprovalStates[name] = sourceState;
                }
                ApprovedValues[name] = value;
            }
        }

        private void ThrowIfReadFails()
        {
            if (FailReads)
                throw new InvalidOperationException("registry read failure");
        }
    }

    private sealed class FakeStartupTask(WindowsStartupTaskState state) : IWindowsStartupTaskAccessor
    {
        private WindowsStartupTaskState _state = state;

        public int EnableRequestCount { get; private set; }
        public int DisableCount { get; private set; }
        public WindowsStartupTaskState? StateAfterEnableRequest { get; init; }
        public WindowsStartupTaskState? StateAfterDisable { get; init; }
        public Exception? GetStateFailure { get; init; }

        public Task<WindowsStartupTaskState> GetStateAsync() =>
            GetStateFailure == null
                ? Task.FromResult(_state)
                : Task.FromException<WindowsStartupTaskState>(GetStateFailure);

        public Task<WindowsStartupTaskState> RequestEnableAsync()
        {
            EnableRequestCount++;
            _state = StateAfterEnableRequest ?? _state;
            return Task.FromResult(_state);
        }

        public Task DisableAsync()
        {
            DisableCount++;
            _state = StateAfterDisable ?? WindowsStartupTaskState.Disabled;
            return Task.CompletedTask;
        }
    }
}
