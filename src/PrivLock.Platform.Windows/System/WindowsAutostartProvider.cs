using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.System;

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsAutostartProvider : IAutostartProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsAutostartProvider>();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "PrivGvard";
    private const string LegacyAppName = "PrivLock";
    private const string StartupTaskId = "PrivGvardStartupTask";

    private readonly IWindowsAutostartRegistry _registry;
    private readonly IWindowsStartupTaskAccessor _startupTask;
    private readonly Func<bool> _isPackaged;
    private readonly Func<string?> _processPathProvider;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _isProductExecutable;

    public WindowsAutostartProvider()
        : this(
            new WindowsAutostartRegistry(RunKeyPath, StartupApprovedKeyPath),
            new WindowsStartupTaskAccessor(StartupTaskId),
            () => PackageIdentityHelper.IsRunningAsPackaged,
            () => Environment.ProcessPath,
            File.Exists,
            IsPrivGvardExecutable)
    {
    }

    internal WindowsAutostartProvider(
        string runKeyPath,
        string startupApprovedKeyPath,
        Func<string?> processPathProvider)
        : this(
            new WindowsAutostartRegistry(runKeyPath, startupApprovedKeyPath),
            new UnavailableWindowsStartupTaskAccessor(),
            () => false,
            processPathProvider,
            File.Exists,
            _ => true)
    {
    }

    internal WindowsAutostartProvider(
        IWindowsAutostartRegistry registry,
        IWindowsStartupTaskAccessor startupTask,
        Func<bool> isPackaged,
        Func<string?> processPathProvider,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isProductExecutable = null)
    {
        _registry = registry;
        _startupTask = startupTask;
        _isPackaged = isPackaged;
        _processPathProvider = processPathProvider;
        _fileExists = fileExists ?? File.Exists;
        _isProductExecutable = isProductExecutable ?? IsPrivGvardExecutable;
    }

    public async Task<AutostartStatus> GetAutostartStatusAsync()
    {
        try
        {
            if (_isPackaged())
                return await GetPackagedStatusAsync().ConfigureAwait(false);

            var legacyValue = _registry.ReadRunValue(LegacyAppName);
            var runValue = _registry.ReadRunValue(AppName);
            if (legacyValue != null)
            {
                return new AutostartStatus(
                    AutostartState.MigrationRequired,
                    "A legacy PrivLock startup entry is still registered and could start alongside PrivGvard.");
            }

            if (runValue == null)
            {
                return new AutostartStatus(AutostartState.Disabled);
            }

            if (runValue is not string commandLine)
            {
                return new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The Windows startup entry is not a valid command string.");
            }

            var registeredPath = ExtractExecutablePath(commandLine);
            if (registeredPath == null)
            {
                return new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The Windows startup command has an invalid executable path.");
            }

            if (!string.Equals(
                    Path.GetFileName(registeredPath),
                    "PrivGvard.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The Windows startup entry points to an executable other than PrivGvard.exe.");
            }

            if (!_fileExists(registeredPath))
            {
                return new AutostartStatus(
                    AutostartState.MissingExecutable,
                    "The executable registered for Windows startup no longer exists.");
            }

            var currentPath = GetValidatedCurrentPath();
            if (currentPath.Error != null)
                return currentPath.Error;

            if (!PathsEqual(registeredPath, currentPath.Path!))
            {
                return new AutostartStatus(
                    AutostartState.MigrationRequired,
                    "The Windows startup entry points to an older or different executable.");
            }

            var expectedCommand = FormatRunCommand(currentPath.Path!);
            if (!string.Equals(commandLine, expectedCommand, StringComparison.Ordinal))
            {
                return new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The Windows startup command does not match PrivGvard's supported launch arguments.");
            }

            var approval = _registry.ReadStartupApproval(AppName);
            if (approval?.State == WindowsStartupApprovalState.DisabledByUser)
            {
                return new AutostartStatus(
                    AutostartState.DisabledByUser,
                    "Windows has disabled PrivGvard startup in Startup Apps. Re-enable it in Windows Settings or Task Manager.");
            }

            return new AutostartStatus(AutostartState.Enabled);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read the effective Windows autostart state");
            return new AutostartStatus(
                AutostartState.Failed,
                $"Windows autostart state could not be read: {ex.Message}");
        }
    }

    public async Task<OperationResult> EnableAutostartAsync()
    {
        if (_isPackaged())
            return await EnablePackagedAutostartAsync().ConfigureAwait(false);

        var currentPath = GetValidatedCurrentPath();
        if (currentPath.Error != null)
            return OperationResult.Fail(currentPath.Error.Message!);

        try
        {
            var legacyCleanup = await MigrateLegacyEntriesAsync().ConfigureAwait(false);
            if (!legacyCleanup.Success)
                return legacyCleanup;

            _registry.WriteRunValue(AppName, FormatRunCommand(currentPath.Path!));
            var status = await GetAutostartStatusAsync().ConfigureAwait(false);
            if (status.State == AutostartState.Enabled)
            {
                Log.Information("Verified Windows Run-key autostart registration");
                return OperationResult.Ok();
            }

            return OperationResult.Fail(
                status.Message ?? $"Windows did not enable autostart (effective state: {status.State}).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write the Windows Run-key autostart entry");
            return OperationResult.Fail($"Windows autostart registration failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> DisableAutostartAsync()
    {
        try
        {
            if (_isPackaged())
            {
                var current = await _startupTask.GetStateAsync().ConfigureAwait(false);
                if (current == WindowsStartupTaskState.EnabledByPolicy)
                {
                    return OperationResult.Fail(
                        "Windows policy requires PrivGvard to start automatically and prevents disabling this startup task.");
                }

                if (current == WindowsStartupTaskState.Enabled)
                    await _startupTask.DisableAsync().ConfigureAwait(false);

                var afterDisable = await _startupTask.GetStateAsync().ConfigureAwait(false);
                if (afterDisable is not (WindowsStartupTaskState.Disabled or
                    WindowsStartupTaskState.DisabledByUser or
                    WindowsStartupTaskState.DisabledByPolicy))
                {
                    return OperationResult.Fail(
                        $"Windows did not disable the startup task (effective state: {afterDisable}).");
                }

                var packagedCleanup = await MigratePackagedEntriesAsync(preserveLegacyDisabledState: false)
                    .ConfigureAwait(false);
                if (!packagedCleanup.Success)
                    return packagedCleanup;

                var status = await GetAutostartStatusAsync().ConfigureAwait(false);
                return status.State is AutostartState.Disabled or AutostartState.DisabledByUser or AutostartState.DisabledByPolicy
                    ? OperationResult.Ok()
                    : OperationResult.Fail(
                        status.Message ?? $"Windows did not disable autostart (effective state: {status.State}).");
            }

            var cleanup = await RemoveLegacyEntriesAsync(removeCurrentRunEntry: true).ConfigureAwait(false);
            if (!cleanup.Success)
                return cleanup;
            var win32Status = await GetAutostartStatusAsync().ConfigureAwait(false);
            return win32Status.State == AutostartState.Disabled
                ? OperationResult.Ok()
                : OperationResult.Fail(
                    win32Status.Message ?? $"Windows did not disable autostart (effective state: {win32Status.State}).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable Windows autostart");
            return OperationResult.Fail($"Windows autostart could not be disabled: {ex.Message}");
        }
    }

    public Task<OperationResult> MigrateLegacyEntriesAsync()
    {
        return _isPackaged()
            ? MigratePackagedEntriesAsync(preserveLegacyDisabledState: true)
            : MigrateLegacyWin32EntryAsync();
    }

    private async Task<OperationResult> MigratePackagedEntriesAsync(bool preserveLegacyDisabledState)
    {
        object? currentRun;
        object? legacyRun;
        byte[]? currentApproval;
        byte[]? legacyApproval;
        WindowsStartupApprovalSnapshot? currentApprovalSnapshot;
        WindowsStartupApprovalSnapshot? legacyApprovalSnapshot;
        WindowsStartupTaskState taskState;
        try
        {
            currentRun = _registry.ReadRunValue(AppName);
            legacyRun = _registry.ReadRunValue(LegacyAppName);
            currentApprovalSnapshot = _registry.ReadStartupApproval(AppName);
            legacyApprovalSnapshot = _registry.ReadStartupApproval(LegacyAppName);
            currentApproval = currentApprovalSnapshot?.Value;
            legacyApproval = legacyApprovalSnapshot?.Value;
            taskState = await _startupTask.GetStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect the MSIX startup task and prior Win32 registrations");
            return OperationResult.Fail($"MSIX startup migration could not be verified: {ex.Message}");
        }

        if (MapStartupTaskState(taskState).State == AutostartState.Failed)
        {
            return OperationResult.Fail(
                $"Windows returned an unknown StartupTask state: {taskState}; Win32 registrations were left unchanged.");
        }

        if (currentRun != null &&
            (currentRun is not string currentCommand || !IsRecognizedRunCommand(currentCommand, legacy: false)))
        {
            return OperationResult.Fail(
                "The PrivGvard Run value is not a recognized app-owned command and was left unchanged.");
        }

        if (legacyRun != null &&
            (legacyRun is not string legacyCommand || !IsRecognizedRunCommand(legacyCommand, legacy: true)))
        {
            return OperationResult.Fail(
                "The PrivLock Run value is not a recognized app-owned command and was left unchanged.");
        }

        var currentDisabled = currentApprovalSnapshot?.State == WindowsStartupApprovalState.DisabledByUser;
        var legacyDisabled = legacyApprovalSnapshot?.State == WindowsStartupApprovalState.DisabledByUser;

        if (preserveLegacyDisabledState &&
            currentApprovalSnapshot != null &&
            legacyApprovalSnapshot != null &&
            currentApprovalSnapshot.State != legacyApprovalSnapshot.State)
        {
            return OperationResult.Fail(
                "The Win32 startup registrations have conflicting Windows user states and were left unchanged.");
        }

        if (preserveLegacyDisabledState && (currentDisabled || legacyDisabled))
        {
            if (taskState == WindowsStartupTaskState.EnabledByPolicy)
            {
                return OperationResult.Fail(
                    "Windows policy enables the MSIX startup task, conflicting with a disabled Win32 startup preference; prior registrations were left unchanged.");
            }

            if (taskState == WindowsStartupTaskState.Enabled)
            {
                try
                {
                    await _startupTask.DisableAsync().ConfigureAwait(false);
                    taskState = await _startupTask.GetStateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not preserve the disabled Win32 preference in the MSIX StartupTask");
                    return OperationResult.Fail(
                        $"The disabled Win32 startup preference could not be preserved; prior registrations were left unchanged: {ex.Message}");
                }

                if (taskState is not (WindowsStartupTaskState.Disabled or WindowsStartupTaskState.DisabledByUser))
                {
                    return OperationResult.Fail(
                        $"The MSIX startup task did not preserve the disabled Win32 preference (effective state: {taskState}); prior registrations were left unchanged.");
                }
            }
        }

        var originalRunValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (currentRun is string currentRunCommand)
            originalRunValues[AppName] = currentRunCommand;
        if (legacyRun is string legacyRunCommand)
            originalRunValues[LegacyAppName] = legacyRunCommand;

        var originalApprovalValues = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (currentApproval != null)
            originalApprovalValues[AppName] = currentApproval;
        if (legacyApproval != null)
            originalApprovalValues[LegacyAppName] = legacyApproval;

        try
        {
            if (!Equals(_registry.ReadRunValue(AppName), currentRun) ||
                !Equals(_registry.ReadRunValue(LegacyAppName), legacyRun) ||
                !ApprovalValuesEqual(_registry.ReadStartupApprovedValue(AppName), currentApproval) ||
                !ApprovalValuesEqual(_registry.ReadStartupApprovedValue(LegacyAppName), legacyApproval) ||
                await _startupTask.GetStateAsync().ConfigureAwait(false) != taskState)
            {
                throw new InvalidOperationException(
                    "The startup task or prior Win32 registrations changed during migration; no further entries were removed.");
            }

            foreach (var name in new[] { AppName, LegacyAppName })
            {
                if (originalRunValues.ContainsKey(name))
                {
                    _registry.DeleteRunValue(name);
                    if (_registry.ReadRunValue(name) != null)
                        throw new InvalidOperationException($"The obsolete Run value '{name}' remained after migration.");
                }

                if (originalApprovalValues.ContainsKey(name))
                {
                    _registry.DeleteStartupApprovedValue(name);
                    if (_registry.ReadStartupApprovedValue(name) != null)
                        throw new InvalidOperationException($"The obsolete Startup Apps value '{name}' remained after migration.");
                }
            }

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            var rollbackErrors = RollbackPackagedMigration(originalRunValues, originalApprovalValues);
            Log.Error(ex, "MSIX transition cleanup failed; attempting to restore previous Win32 registrations");
            var rollbackDetail = rollbackErrors.Count == 0
                ? ""
                : $" Rollback was incomplete: {string.Join("; ", rollbackErrors)}";
            return OperationResult.Fail($"MSIX startup migration failed: {ex.Message}{rollbackDetail}");
        }
    }

    private async Task<OperationResult> MigrateLegacyWin32EntryAsync()
    {
        object? legacyValue;
        object? currentValue;
        byte[]? legacyApproval;
        byte[]? currentApproval;
        WindowsStartupApprovalSnapshot? legacyApprovalSnapshot;
        WindowsStartupApprovalSnapshot? currentApprovalSnapshot;
        try
        {
            legacyValue = _registry.ReadRunValue(LegacyAppName);
            currentValue = _registry.ReadRunValue(AppName);
            legacyApprovalSnapshot = _registry.ReadStartupApproval(LegacyAppName);
            currentApprovalSnapshot = _registry.ReadStartupApproval(AppName);
            legacyApproval = legacyApprovalSnapshot?.Value;
            currentApproval = currentApprovalSnapshot?.Value;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect legacy Win32 autostart values");
            return OperationResult.Fail($"Legacy Windows startup state could not be read: {ex.Message}");
        }

        if (legacyValue == null)
            return await RemoveLegacyEntriesAsync(removeCurrentRunEntry: false).ConfigureAwait(false);

        if (legacyValue is not string legacyCommand)
        {
            return OperationResult.Fail(
                "The legacy PrivLock startup entry is not a valid command string and was left unchanged.");
        }

        if (!IsRecognizedRunCommand(legacyCommand, legacy: true))
        {
            return OperationResult.Fail(
                "The legacy Run value does not identify a PrivLock or PrivGvard executable and was left unchanged.");
        }

        if (legacyApprovalSnapshot?.State == WindowsStartupApprovalState.DisabledByUser &&
            currentApprovalSnapshot?.State == WindowsStartupApprovalState.Enabled)
        {
            return OperationResult.Fail(
                "The legacy and current startup registrations have conflicting Windows user states and were left unchanged.");
        }

        var currentPath = GetValidatedCurrentPath();
        if (currentPath.Error != null)
            return OperationResult.Fail(currentPath.Error.Message!);

        var expectedCurrentCommand = FormatRunCommand(currentPath.Path!);
        if (currentValue != null &&
            (currentValue is not string currentCommand ||
             !string.Equals(currentCommand, expectedCurrentCommand, StringComparison.Ordinal)))
        {
            return OperationResult.Fail(
                "The current PrivGvard Run value is not a verified replacement; both startup registrations were left unchanged.");
        }

        if (legacyApprovalSnapshot != null &&
            currentApprovalSnapshot != null &&
            legacyApprovalSnapshot.State != currentApprovalSnapshot.State)
        {
            return OperationResult.Fail(
                "The legacy and current startup registrations have conflicting Windows user states; both were left unchanged.");
        }

        var currentRunCreated = false;
        var currentApprovalCreated = false;
        try
        {
            if (currentValue == null)
            {
                _registry.WriteRunValue(AppName, expectedCurrentCommand);
                currentRunCreated = true;
            }

            if (_registry.ReadRunValue(AppName) is not string migratedCommand ||
                !string.Equals(migratedCommand, expectedCurrentCommand, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The replacement Run value did not match the verified current executable.");
            }

            if (currentApproval == null && legacyApproval != null)
            {
                _registry.WriteStartupApprovedValue(AppName, legacyApproval);
                currentApprovalCreated = true;
                if (_registry.ReadStartupApprovedValue(AppName) is not { } copiedApproval ||
                    !copiedApproval.SequenceEqual(legacyApproval))
                    throw new InvalidOperationException("The legacy Windows startup approval state could not be copied.");
            }

            var approvalBeforeDelete = _registry.ReadStartupApprovedValue(AppName);
            var legacyApprovalBeforeDelete = _registry.ReadStartupApprovedValue(LegacyAppName);
            if (!ApprovalValuesEqual(approvalBeforeDelete, currentApproval ?? legacyApproval) ||
                !ApprovalValuesEqual(legacyApprovalBeforeDelete, legacyApproval))
            {
                throw new InvalidOperationException(
                    "Windows startup approval state changed or conflicted during migration; the legacy entry was not removed.");
            }

            if (!Equals(_registry.ReadRunValue(LegacyAppName), legacyValue))
                throw new InvalidOperationException("The legacy Run value changed during migration.");

            _registry.DeleteRunValue(LegacyAppName);
            if (_registry.ReadRunValue(LegacyAppName) != null)
                throw new InvalidOperationException("The legacy Run value remained after migration.");

            if (legacyApproval != null)
            {
                _registry.DeleteStartupApprovedValue(LegacyAppName);
                if (_registry.ReadStartupApprovedValue(LegacyAppName) != null)
                    throw new InvalidOperationException("The legacy Startup Apps value remained after migration.");
            }

            var status = await GetAutostartStatusAsync().ConfigureAwait(false);
            if (status.State is not (AutostartState.Enabled or AutostartState.DisabledByUser))
            {
                throw new InvalidOperationException(
                    status.Message ?? $"The migrated startup registration is not effective ({status.State}).");
            }

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            var rollbackErrors = RollbackWin32Migration(
                legacyCommand,
                legacyApproval,
                currentRunCreated,
                currentApprovalCreated,
                expectedCurrentCommand);
            Log.Error(ex, "Legacy Win32 autostart migration failed");
            var rollbackDetail = rollbackErrors.Count == 0
                ? ""
                : $" Rollback was incomplete: {string.Join("; ", rollbackErrors)}";
            return OperationResult.Fail($"Legacy Windows startup migration failed: {ex.Message}{rollbackDetail}");
        }
    }

    private List<string> RollbackPackagedMigration(
        IReadOnlyDictionary<string, string> originalRunValues,
        IReadOnlyDictionary<string, byte[]> originalApprovalValues)
    {
        var errors = new List<string>();
        foreach (var (name, value) in originalRunValues)
        {
            try
            {
                var current = _registry.ReadRunValue(name);
                if (current == null)
                {
                    _registry.WriteRunValue(name, value);
                    if (!Equals(_registry.ReadRunValue(name), value))
                        throw new InvalidOperationException("readback did not match the original value");
                }
                else if (!Equals(current, value))
                {
                    throw new InvalidOperationException("a different value is present; it was preserved");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{name} Run value: {ex.Message}");
            }
        }

        foreach (var (name, value) in originalApprovalValues)
        {
            try
            {
                var current = _registry.ReadStartupApprovedValue(name);
                if (current == null)
                {
                    _registry.WriteStartupApprovedValue(name, value);
                    if (_registry.ReadStartupApprovedValue(name) is not { } restored ||
                        !restored.SequenceEqual(value))
                    {
                        throw new InvalidOperationException("readback did not match the original value");
                    }
                }
                else if (!current.SequenceEqual(value))
                {
                    throw new InvalidOperationException("a different value is present; it was preserved");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{name} Startup Apps state: {ex.Message}");
            }
        }

        return errors;
    }

    private List<string> RollbackWin32Migration(
        string legacyCommand,
        byte[]? legacyApproval,
        bool currentRunCreated,
        bool currentApprovalCreated,
        string expectedCurrentCommand)
    {
        var errors = new List<string>();
        try
        {
            if (_registry.ReadRunValue(LegacyAppName) == null)
            {
                _registry.WriteRunValue(LegacyAppName, legacyCommand);
                if (!Equals(_registry.ReadRunValue(LegacyAppName), legacyCommand))
                    throw new InvalidOperationException("readback did not match the original value");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"legacy Run value: {ex.Message}");
        }

        try
        {
            if (legacyApproval != null && _registry.ReadStartupApprovedValue(LegacyAppName) == null)
            {
                _registry.WriteStartupApprovedValue(LegacyAppName, legacyApproval);
                if (_registry.ReadStartupApprovedValue(LegacyAppName) is not { } restored ||
                    !restored.SequenceEqual(legacyApproval))
                {
                    throw new InvalidOperationException("readback did not match the original approval");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"legacy Startup Apps state: {ex.Message}");
        }

        if (currentRunCreated)
        {
            try
            {
                if (Equals(_registry.ReadRunValue(AppName), expectedCurrentCommand))
                    _registry.DeleteRunValue(AppName);
                if (_registry.ReadRunValue(AppName) != null)
                    throw new InvalidOperationException("the replacement changed externally and was preserved");
            }
            catch (Exception ex)
            {
                errors.Add($"replacement Run value: {ex.Message}");
            }
        }

        if (currentApprovalCreated)
        {
            try
            {
                if (legacyApproval != null &&
                    _registry.ReadStartupApprovedValue(AppName) is { } current &&
                    current.SequenceEqual(legacyApproval))
                {
                    _registry.DeleteStartupApprovedValue(AppName);
                }

                if (_registry.ReadStartupApprovedValue(AppName) != null)
                    throw new InvalidOperationException("the replacement approval changed externally and was preserved");
            }
            catch (Exception ex)
            {
                errors.Add($"replacement Startup Apps state: {ex.Message}");
            }
        }

        return errors;
    }

    private async Task<AutostartStatus> GetPackagedStatusAsync()
    {
        if (_registry.ReadRunValue(AppName) != null || _registry.ReadRunValue(LegacyAppName) != null)
        {
            return new AutostartStatus(
                AutostartState.MigrationRequired,
                "A previous Win32 startup entry is still registered. Disable it before the MSIX startup task can be used.");
        }

        return MapStartupTaskState(await _startupTask.GetStateAsync().ConfigureAwait(false));
    }

    private async Task<OperationResult> EnablePackagedAutostartAsync()
    {
        try
        {
            var taskState = await _startupTask.GetStateAsync().ConfigureAwait(false);
            if (taskState == WindowsStartupTaskState.DisabledByPolicy)
            {
                return OperationResult.Fail(
                    "Windows policy prevents enabling the PrivGvard MSIX startup task.");
            }
            if (taskState is WindowsStartupTaskState.DisabledByUser or WindowsStartupTaskState.Disabled)
            {
                taskState = await _startupTask.RequestEnableAsync().ConfigureAwait(false);
            }

            var status = MapStartupTaskState(taskState);
            if (status.IsEnabled)
            {
                var cleanup = await MigratePackagedEntriesAsync(preserveLegacyDisabledState: false)
                    .ConfigureAwait(false);
                if (!cleanup.Success)
                    return cleanup;

                var verified = await GetPackagedStatusAsync().ConfigureAwait(false);
                return verified.IsEnabled
                    ? OperationResult.Ok()
                    : OperationResult.Fail(verified.Message ?? "The MSIX startup task could not be verified.");
            }

            return OperationResult.Fail(status.Message ?? $"Windows did not enable the startup task ({status.State}).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable the MSIX StartupTask");
            return OperationResult.Fail($"Windows could not enable the MSIX startup task: {ex.Message}");
        }
    }

    private async Task<OperationResult> RemoveLegacyEntriesAsync(bool removeCurrentRunEntry)
    {
        try
        {
            var names = removeCurrentRunEntry
                ? new[] { AppName, LegacyAppName }
                : new[] { LegacyAppName };

            foreach (var name in names)
            {
                if (_registry.ReadRunValue(name) != null)
                    _registry.DeleteRunValue(name);
                if (_registry.ReadRunValue(name) != null)
                {
                    return OperationResult.Fail(
                        $"The obsolete Windows startup entry '{name}' could not be removed.");
                }

                if (_registry.ReadStartupApprovedValue(name) != null)
                    _registry.DeleteStartupApprovedValue(name);
                if (_registry.ReadStartupApprovedValue(name) != null)
                {
                    return OperationResult.Fail(
                        $"The obsolete Windows Startup Apps state for '{name}' could not be removed.");
                }
            }

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to remove a legacy PrivGvard Windows startup entry");
            return OperationResult.Fail($"Legacy Windows startup entries could not be migrated: {ex.Message}");
        }
    }

    private (string? Path, AutostartStatus? Error) GetValidatedCurrentPath()
    {
        try
        {
            var processPath = _processPathProvider();
            if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
            {
                return (null, new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "Windows could not determine a fully qualified PrivGvard executable path."));
            }

            var fullPath = Path.GetFullPath(processPath);
            if (!string.Equals(Path.GetFileName(fullPath), "PrivGvard.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (null, new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The current executable is not named PrivGvard.exe."));
            }

            if (!_fileExists(fullPath))
            {
                return (null, new AutostartStatus(
                    AutostartState.MissingExecutable,
                    "The current PrivGvard executable does not exist on disk."));
            }

            if (!_isProductExecutable(fullPath))
            {
                return (null, new AutostartStatus(
                    AutostartState.InvalidRegistration,
                    "The current executable is not a verified PrivGvard.exe."));
            }

            if (IsDevelopmentPath(fullPath))
            {
                return (null, new AutostartStatus(
                    AutostartState.UnstablePath,
                    "Autostart cannot use a development build path that may disappear after a rebuild. Install or publish PrivGvard to a stable location first."));
            }

            return (fullPath, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to validate the current executable for Windows autostart");
            return (null, new AutostartStatus(
                AutostartState.Failed,
                $"The current PrivGvard executable could not be validated: {ex.Message}"));
        }
    }

    private static bool IsDevelopmentPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "bin", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < segments.Length &&
                (string.Equals(segments[index + 1], "Debug", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segments[index + 1], "Release", StringComparison.OrdinalIgnoreCase)))
            {
                return index + 2 < segments.Length - 1 &&
                       IsTargetFrameworkSegment(segments[index + 2]);
            }

            if (string.Equals(segments[index], "publish_out", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsRecognizedRunCommand(string command, bool legacy)
    {
        var executablePath = ExtractExecutablePath(command);
        if (executablePath == null || !Path.IsPathFullyQualified(executablePath))
            return false;

        var executableName = Path.GetFileName(executablePath);
        var recognizedName = executableName.Equals("PrivGvard.exe", StringComparison.OrdinalIgnoreCase) ||
                             legacy && executableName.Equals("PrivLock.exe", StringComparison.OrdinalIgnoreCase);
        if (!recognizedName)
            return false;

        var trimmed = command.Trim();
        var quotedPath = $"\"{executablePath}\"";
        if (trimmed.Equals(quotedPath, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals($"{quotedPath} --minimized", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !executablePath.Any(char.IsWhiteSpace) &&
               (trimmed.Equals(executablePath, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals($"{executablePath} --minimized", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ApprovalValuesEqual(byte[]? left, byte[]? right) =>
        left == null
            ? right == null
            : right != null && left.SequenceEqual(right);

    private static bool IsTargetFrameworkSegment(string segment) =>
        segment.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
        segment.Length > 3 &&
        char.IsDigit(segment[3]);

    private static bool IsPrivGvardExecutable(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(version.ProductName, "PrivGvard", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(version.OriginalFilename, "PrivGvard.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not verify the executable's PrivGvard file metadata");
            return false;
        }
    }

    private static AutostartStatus MapStartupTaskState(WindowsStartupTaskState state) =>
        state switch
        {
            WindowsStartupTaskState.Enabled or WindowsStartupTaskState.EnabledByPolicy =>
                new AutostartStatus(AutostartState.Enabled),
            WindowsStartupTaskState.Disabled =>
                new AutostartStatus(AutostartState.Disabled),
            WindowsStartupTaskState.DisabledByUser =>
                new AutostartStatus(
                    AutostartState.DisabledByUser,
                    "Windows or the user disabled the PrivGvard startup task. Enable it in Windows Settings > Apps > Startup."),
            WindowsStartupTaskState.DisabledByPolicy =>
                new AutostartStatus(
                    AutostartState.DisabledByPolicy,
                    "Windows policy prevents the PrivGvard startup task from running."),
            _ => new AutostartStatus(
                AutostartState.Failed,
                $"Windows returned an unknown StartupTask state: {state}.")
        };

    private static string FormatRunCommand(string path) => $"\"{path}\" --minimized";

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }

    private sealed class UnavailableWindowsStartupTaskAccessor : IWindowsStartupTaskAccessor
    {
        public Task<WindowsStartupTaskState> GetStateAsync() =>
            Task.FromException<WindowsStartupTaskState>(
                new InvalidOperationException("MSIX StartupTask API was not configured for this provider."));

        public Task<WindowsStartupTaskState> RequestEnableAsync() =>
            Task.FromException<WindowsStartupTaskState>(
                new InvalidOperationException("MSIX StartupTask API was not configured for this provider."));

        public Task DisableAsync() =>
            Task.FromException(new InvalidOperationException("MSIX StartupTask API was not configured for this provider."));
    }
}

internal interface IWindowsAutostartRegistry
{
    object? ReadRunValue(string name);
    void WriteRunValue(string name, string value);
    void DeleteRunValue(string name);
    byte[]? ReadStartupApprovedValue(string name);
    WindowsStartupApprovalSnapshot? ReadStartupApproval(string name);
    void WriteStartupApprovedValue(string name, byte[] value);
    void DeleteStartupApprovedValue(string name);
}

internal enum WindowsStartupApprovalState
{
    Enabled,
    DisabledByUser
}

internal sealed record WindowsStartupApprovalSnapshot(
    byte[] Value,
    WindowsStartupApprovalState State);

internal sealed class WindowsAutostartRegistry(string runKeyPath, string startupApprovedKeyPath)
    : IWindowsAutostartRegistry
{
    public object? ReadRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void WriteRunValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not create the current-user Run key.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? ReadStartupApprovedValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(startupApprovedKeyPath);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
            return null;
        return value as byte[] ??
            throw new InvalidDataException($"StartupApproved value '{name}' is not REG_BINARY.");
    }

    public WindowsStartupApprovalSnapshot? ReadStartupApproval(string name)
    {
        var value = ReadStartupApprovedValue(name);
        if (value == null)
            return null;
        if (value.Length == 0)
            throw new InvalidDataException($"StartupApproved value '{name}' is empty.");

        // StartupApproved is not a documented Win32 contract. Read its existing status
        // conservatively for compatibility, but never write or synthesize this value.
        var state = (value[0] & 0x01) != 0
            ? WindowsStartupApprovalState.DisabledByUser
            : WindowsStartupApprovalState.Enabled;
        return new WindowsStartupApprovalSnapshot(value, state);
    }

    public void WriteStartupApprovedValue(string name, byte[] value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(startupApprovedKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not create the StartupApproved Run key.");
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    public void DeleteStartupApprovedValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(startupApprovedKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
