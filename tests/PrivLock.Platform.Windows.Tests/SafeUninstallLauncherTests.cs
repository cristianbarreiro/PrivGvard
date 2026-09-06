using PrivLock.Desktop;
using Xunit;

namespace PrivLock.Platform.Windows.Tests;

public sealed class SafeUninstallLauncherTests : IDisposable
{
    private const string HandoffToken = "0123456789abcdef0123456789abcdef";
    private readonly string _trustedRoot;
    private readonly string _installDirectory;
    private readonly string _applicationPath;
    private readonly string _uninstallerPath;

    public SafeUninstallLauncherTests()
    {
        _trustedRoot = Path.Combine(Path.GetTempPath(), $"PrivLockUninstallTests_{Guid.NewGuid():N}");
        _installDirectory = Path.Combine(_trustedRoot, "PrivLock");
        Directory.CreateDirectory(_installDirectory);
        _applicationPath = Path.Combine(
            _installDirectory,
            SafeUninstallLauncher.ExpectedApplicationFileName);
        _uninstallerPath = Path.Combine(
            _installDirectory,
            SafeUninstallLauncher.ExpectedUninstallerFileName);
        File.WriteAllBytes(_applicationPath, [0x4D, 0x5A]);
        File.WriteAllBytes(_uninstallerPath, [0x4D, 0x5A]);
    }

    [Fact]
    public void ValidRequest_CreatesFixedElevatedInnoLaunchWithoutForwardedInput()
    {
        var args = new[] { SafeUninstallLauncher.Command, _uninstallerPath };

        var valid = SafeUninstallLauncher.TryCreatePlan(
            args,
            _applicationPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        Assert.NotNull(plan);
        var startInfo = SafeUninstallLauncher.CreateStartInfo(plan!, HandoffToken);
        Assert.Equal(_uninstallerPath, startInfo.FileName);
        Assert.Equal(_installDirectory, startInfo.WorkingDirectory);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal([SafeUninstallLauncher.HandoffArgumentPrefix + HandoffToken], startInfo.ArgumentList);
    }

    [Fact]
    public void QuietRequest_AddsOnlyFixedInnoQuietArguments()
    {
        var args = new[]
        {
            SafeUninstallLauncher.Command,
            _uninstallerPath,
            SafeUninstallLauncher.QuietSwitch
        };

        var valid = SafeUninstallLauncher.TryCreatePlan(
            args,
            _applicationPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        var startInfo = SafeUninstallLauncher.CreateStartInfo(plan!, HandoffToken);
        Assert.Equal(
            [
                SafeUninstallLauncher.HandoffArgumentPrefix + HandoffToken,
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART"
            ],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef /LOG=other")]
    [InlineData("g123456789abcdef0123456789abcdef")]
    public void Launch_RejectsMalformedHandoffToken(string token)
    {
        Assert.Throws<ArgumentException>(() => SafeUninstallLauncher.CreateStartInfo(
            new SafeUninstallPlan(_uninstallerPath, Quiet: false), token));
    }

    [Fact]
    public void Request_RejectsUninstallerOutsideApplicationDirectory()
    {
        var otherDirectory = Path.Combine(_trustedRoot, "Other");
        Directory.CreateDirectory(otherDirectory);
        var otherUninstaller = Path.Combine(
            otherDirectory,
            SafeUninstallLauncher.ExpectedUninstallerFileName);
        File.WriteAllBytes(otherUninstaller, [0x4D, 0x5A]);

        var valid = SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, otherUninstaller],
            _applicationPath,
            _trustedRoot,
            out _,
            out _);

        Assert.False(valid);
    }

    [Theory]
    [InlineData("other.exe")]
    [InlineData("unins001.exe")]
    [InlineData("unins000.exe.cmd")]
    public void Request_RejectsUnexpectedUninstallerName(string fileName)
    {
        var candidate = Path.Combine(_installDirectory, fileName);
        File.WriteAllBytes(candidate, [0x4D, 0x5A]);

        var valid = SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, candidate],
            _applicationPath,
            _trustedRoot,
            out _,
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void Request_RejectsAdditionalOrUnrecognizedArguments()
    {
        Assert.False(SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, _uninstallerPath, "--quiet", "/LOG=attacker"],
            _applicationPath,
            _trustedRoot,
            out _,
            out _));

        Assert.False(SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, _uninstallerPath, "/VERYSILENT"],
            _applicationPath,
            _trustedRoot,
            out _,
            out _));
    }

    [Fact]
    public void Request_RejectsApplicationOutsideTrustedInstallRoot()
    {
        var externalDirectory = Path.Combine(Path.GetTempPath(), $"PrivLockExternal_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(externalDirectory);
            var externalApp = Path.Combine(
                externalDirectory,
                SafeUninstallLauncher.ExpectedApplicationFileName);
            var externalUninstaller = Path.Combine(
                externalDirectory,
                SafeUninstallLauncher.ExpectedUninstallerFileName);
            File.WriteAllBytes(externalApp, [0x4D, 0x5A]);
            File.WriteAllBytes(externalUninstaller, [0x4D, 0x5A]);

            Assert.False(SafeUninstallLauncher.TryCreatePlan(
                [SafeUninstallLauncher.Command, externalUninstaller],
                externalApp,
                _trustedRoot,
                out _,
                out _));
        }
        finally
        {
            if (Directory.Exists(externalDirectory))
                Directory.Delete(externalDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_trustedRoot))
            Directory.Delete(_trustedRoot, recursive: true);
    }
}
