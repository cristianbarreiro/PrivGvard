using Microsoft.Win32;
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

        _testDirectory = Path.Combine(Path.GetTempPath(), $"PrivGvardAutostart_{testId}");
        Directory.CreateDirectory(_testDirectory);
        _dummyExePath = Path.Combine(_testDirectory, "PrivGvard.exe");
        File.WriteAllBytes(_dummyExePath, [0x4D, 0x5A]); // Dummy PE header

        // Ensure clean test registry keys
        using var runKey = Registry.CurrentUser.CreateSubKey(_testRunKeyPath);
        using var approvedKey = Registry.CurrentUser.CreateSubKey(_testStartupApprovedKeyPath);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testRootKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best effort cleanup
        }

        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void EnableAutostart_WritesQuotedPathWithMinimizedArg()
    {
        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        var result = provider.EnableAutostart();

        Assert.True(result.Success, result.ErrorMessage);

        using var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath);
        Assert.NotNull(runKey);
        var value = runKey.GetValue("PrivGvard") as string;
        Assert.Equal($"\"{_dummyExePath}\" --minimized", value);
    }

    [Fact]
    public void EnableAutostart_RemovesLegacyPrivLockValue()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivLock", "old_command.exe");
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        var result = provider.EnableAutostart();

        Assert.True(result.Success);

        using var verifyKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath);
        Assert.NotNull(verifyKey);
        Assert.Null(verifyKey.GetValue("PrivLock"));
        Assert.NotNull(verifyKey.GetValue("PrivGvard"));
    }

    [Fact]
    public void EnableAutostart_RemovesStartupApprovedDisableOverride()
    {
        // Simulate Task Manager having disabled the entry (byte[0] = 0x03)
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
        {
            approvedKey?.SetValue("PrivGvard", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        var result = provider.EnableAutostart();

        Assert.True(result.Success);

        using var verifyApprovedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath);
        Assert.NotNull(verifyApprovedKey);
        Assert.Null(verifyApprovedKey.GetValue("PrivGvard"));
    }

    [Fact]
    public void DisableAutostart_RemovesBothPrivGvardAndLegacyEntries()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivGvard", $"\"{_dummyExePath}\" --minimized");
            runKey?.SetValue("PrivLock", "old_command.exe");
        }

        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
        {
            approvedKey?.SetValue("PrivGvard", new byte[] { 0x02, 0x00, 0x00 }, RegistryValueKind.Binary);
            approvedKey?.SetValue("PrivLock", new byte[] { 0x03, 0x00, 0x00 }, RegistryValueKind.Binary);
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        var result = provider.DisableAutostart();

        Assert.True(result.Success);

        using var verifyRun = Registry.CurrentUser.OpenSubKey(_testRunKeyPath);
        Assert.NotNull(verifyRun);
        Assert.Null(verifyRun.GetValue("PrivGvard"));
        Assert.Null(verifyRun.GetValue("PrivLock"));

        using var verifyApproved = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath);
        Assert.NotNull(verifyApproved);
        Assert.Null(verifyApproved.GetValue("PrivGvard"));
        Assert.Null(verifyApproved.GetValue("PrivLock"));
    }

    [Fact]
    public void IsAutostartEnabled_ReturnsTrue_WhenValidRunEntryExists()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivGvard", $"\"{_dummyExePath}\" --minimized");
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        Assert.True(provider.IsAutostartEnabled());
    }

    [Fact]
    public void IsAutostartEnabled_ReturnsFalse_WhenRunEntryDoesNotExist()
    {
        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        Assert.False(provider.IsAutostartEnabled());
    }

    [Fact]
    public void IsAutostartEnabled_ReturnsFalse_WhenExecutableDoesNotExistOnDisk()
    {
        var nonExistentExe = Path.Combine(_testDirectory, "DeletedExe.exe");
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivGvard", $"\"{nonExistentExe}\" --minimized");
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => nonExistentExe);

        // When executable path is missing or points to a non-existent file, it must return false
        Assert.False(provider.IsAutostartEnabled());
    }

    [Fact]
    public void IsAutostartEnabled_ReturnsFalse_WhenStartupApprovedDisablesEntry()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivGvard", $"\"{_dummyExePath}\" --minimized");
        }

        // 0x03 in byte[0] indicates disabled by Windows Task Manager
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
        {
            approvedKey?.SetValue("PrivGvard", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        Assert.False(provider.IsAutostartEnabled());
    }

    [Fact]
    public void IsAutostartEnabled_ReturnsTrue_WhenStartupApprovedEnablesEntry()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivGvard", $"\"{_dummyExePath}\" --minimized");
        }

        // 0x02 or 0x06 in byte[0] indicates enabled by Windows Task Manager
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(_testStartupApprovedKeyPath, writable: true))
        {
            approvedKey?.SetValue("PrivGvard", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        Assert.True(provider.IsAutostartEnabled());
    }

    [Fact]
    public void IsAutostartEnabled_DetectsLegacyPrivLockEntry()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(_testRunKeyPath, writable: true))
        {
            runKey?.SetValue("PrivLock", $"\"{_dummyExePath}\" --minimized");
        }

        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        Assert.True(provider.IsAutostartEnabled());
    }

    [Fact]
    public void ExtractExecutablePath_CorrectlyExtractsQuotedAndUnquotedPaths()
    {
        Assert.Equal(@"C:\Program Files\PrivGvard\PrivGvard.exe",
            WindowsAutostartProvider.ExtractExecutablePath("\"C:\\Program Files\\PrivGvard\\PrivGvard.exe\" --minimized"));

        Assert.Equal(@"C:\PrivGvard\PrivGvard.exe",
            WindowsAutostartProvider.ExtractExecutablePath("C:\\PrivGvard\\PrivGvard.exe --minimized"));

        Assert.Equal(@"C:\App\test.exe",
            WindowsAutostartProvider.ExtractExecutablePath("\"C:\\App\\test.exe\""));

        Assert.Equal(@"C:\App\test.exe",
            WindowsAutostartProvider.ExtractExecutablePath("C:\\App\\test.exe"));

        Assert.Null(WindowsAutostartProvider.ExtractExecutablePath(null));
        Assert.Null(WindowsAutostartProvider.ExtractExecutablePath("   "));
        Assert.Null(WindowsAutostartProvider.ExtractExecutablePath("\"\""));
    }

    [Fact]
    public void EnableAutostart_FailsGracefully_WhenProcessPathNull()
    {
        var provider = new WindowsAutostartProvider(
            _testRunKeyPath,
            _testStartupApprovedKeyPath,
            () => null);

        var result = provider.EnableAutostart();

        Assert.False(result.Success);
        Assert.Contains("Cannot determine current process path", result.ErrorMessage);
    }

    [Fact]
    public void EnableAutostart_WhenRunKeyDoesNotExist_CreatesKeyAndSucceeds()
    {
        var nonExistentRunPath = $@"{_testRootKeyPath}\NonExistent_{Guid.NewGuid():N}\Run";
        var provider = new WindowsAutostartProvider(
            nonExistentRunPath,
            _testStartupApprovedKeyPath,
            () => _dummyExePath);

        var result = provider.EnableAutostart();

        Assert.True(result.Success, result.ErrorMessage);
        using var createdKey = Registry.CurrentUser.OpenSubKey(nonExistentRunPath);
        Assert.NotNull(createdKey);
        Assert.Equal($"\"{_dummyExePath}\" --minimized", createdKey.GetValue("PrivGvard"));
    }
}
