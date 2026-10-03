using System.ComponentModel;
using System.Diagnostics;
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
    public void ValidRequest_AcceptsLegacyApplicationFileName()
    {
        var legacyAppPath = Path.Combine(
            _installDirectory,
            SafeUninstallLauncher.LegacyExpectedApplicationFileName);
        File.WriteAllBytes(legacyAppPath, [0x4D, 0x5A]);

        var args = new[] { SafeUninstallLauncher.Command, _uninstallerPath };

        var valid = SafeUninstallLauncher.TryCreatePlan(
            args,
            legacyAppPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        Assert.NotNull(plan);
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

    [Theory]
    [InlineData()]
    [InlineData("--safe-uninstall")]
    [InlineData("--wrong-command", "dummy")]
    [InlineData("--safe-uninstall", "dummy", "--extra", "--another")]
    public void Request_RejectsInvalidCommandLine(params string[] args)
    {
        Assert.False(SafeUninstallLauncher.TryCreatePlan(
            args,
            _applicationPath,
            _trustedRoot,
            out _,
            out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Request_RejectsMissingUninstallerFile()
    {
        File.Delete(_uninstallerPath);

        var valid = SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, _uninstallerPath],
            _applicationPath,
            _trustedRoot,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("missing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Request_RejectsMissingApplicationFile()
    {
        File.Delete(_applicationPath);

        var valid = SafeUninstallLauncher.TryCreatePlan(
            [SafeUninstallLauncher.Command, _uninstallerPath],
            _applicationPath,
            _trustedRoot,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("missing", error, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ProgramsAndFeatures_StandardInvocation_CreatesValidPlan()
    {
        // Windows Programs and Features registers and invokes:
        // "C:\Program Files\PrivGvard\PrivGvard.exe" --safe-uninstall "C:\Program Files\PrivGvard\unins000.exe"
        var args = new[] { SafeUninstallLauncher.Command, _uninstallerPath };

        var valid = SafeUninstallLauncher.TryCreatePlan(
            args,
            _applicationPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        Assert.NotNull(plan);
        Assert.False(plan!.Quiet);
        Assert.Equal(_uninstallerPath, plan.UninstallerPath);
    }

    [Fact]
    public void UninstallString_MatchesExpectedFormat()
    {
        var expectedCommand = $"\"{_applicationPath}\" --safe-uninstall \"{_uninstallerPath}\"";

        // Parse simulated registry command
        var parts = new[] { SafeUninstallLauncher.Command, _uninstallerPath };
        var valid = SafeUninstallLauncher.TryCreatePlan(
            parts,
            _applicationPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        Assert.NotNull(plan);
        Assert.False(plan!.Quiet);
        Assert.Contains("--safe-uninstall", expectedCommand);
    }

    [Fact]
    public void QuietUninstallString_MatchesExpectedFormat()
    {
        var expectedCommand = $"\"{_applicationPath}\" --safe-uninstall \"{_uninstallerPath}\" --quiet";

        var parts = new[] { SafeUninstallLauncher.Command, _uninstallerPath, SafeUninstallLauncher.QuietSwitch };
        var valid = SafeUninstallLauncher.TryCreatePlan(
            parts,
            _applicationPath,
            _trustedRoot,
            out var plan,
            out var error);

        Assert.True(valid, error);
        Assert.NotNull(plan);
        Assert.True(plan!.Quiet);
        Assert.Contains("--quiet", expectedCommand);
    }

    [Fact]
    public void TryLaunch_ProcessStartFails_ReturnsLaunchFailed()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: _ => null);

        Assert.Equal(SafeUninstallStatus.LaunchFailed, result.Status);
        Assert.False(result.Success);
        Assert.False(gateReleased); // Gate must NOT be released if process start fails
    }

    [Fact]
    public void TryLaunch_ProcessStartThrowsGenericException_ReturnsLaunchFailed()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: _ => throw new InvalidOperationException("Failed to spawn process"));

        Assert.Equal(SafeUninstallStatus.LaunchFailed, result.Status);
        Assert.False(result.Success);
        Assert.False(gateReleased);
    }

    [Fact]
    public void TryLaunch_UserCancelsUac_ReturnsUserCancelled_AndDoesNotReleaseGate()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: _ => throw new Win32Exception(1223)); // ERROR_CANCELLED

        Assert.Equal(SafeUninstallStatus.UserCancelled, result.Status);
        Assert.False(result.Success);
        Assert.Equal(1223, result.ExitCode);
        Assert.False(gateReleased); // Critical: Gate remains protected when user cancels UAC
    }

    [Fact]
    public void TryLaunch_GateReleaseFails_ReturnsLaunchFailed()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => false,
            processStarter: _ => Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit 0",
                CreateNoWindow = true,
                UseShellExecute = false
            }));

        Assert.Equal(SafeUninstallStatus.LaunchFailed, result.Status);
        Assert.False(result.Success);
    }

    [Fact]
    public void TryLaunch_AdmissionTimeout_ReturnsAdmissionTimeout()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        // Process starts and stays alive briefly, but no admission event is ever signaled
        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: _ => Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping 127.0.0.1 -n 2 > nul",
                CreateNoWindow = true,
                UseShellExecute = false
            }),
            admissionTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(SafeUninstallStatus.AdmissionTimeout, result.Status);
        Assert.False(result.Success);
        Assert.True(gateReleased);
    }

    [Fact]
    public void TryLaunch_AdmissionConfirmed_ReturnsSuccess()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: startInfo =>
            {
                var tokenArg = startInfo.ArgumentList.First(a => a.StartsWith(SafeUninstallLauncher.HandoffArgumentPrefix));
                var token = tokenArg[SafeUninstallLauncher.HandoffArgumentPrefix.Length..];
                var eventName = SafeUninstallLauncher.HandoffEventPrefix + token;

                // Simulate Inno Setup signaling admission
                Task.Run(async () =>
                {
                    await Task.Delay(50);
                    if (EventWaitHandle.TryOpenExisting(eventName, out var evt))
                    {
                        using (evt)
                        {
                            evt.Set();
                        }
                    }
                });

                return Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 2 > nul",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            admissionTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(SafeUninstallStatus.AdmissionConfirmed, result.Status);
        Assert.True(result.Success);
        Assert.True(gateReleased);
    }

    [Fact]
    public void TryLaunch_HandoffConfirmed_AfterInitialProcessExitedWithCodeZero()
    {
        // This reproduces the exact Inno Setup behavior where unins000.exe (Phase 1)
        // spawns Phase 2 in %TEMP% and exits immediately with code 0.
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: startInfo =>
            {
                var tokenArg = startInfo.ArgumentList.First(a => a.StartsWith(SafeUninstallLauncher.HandoffArgumentPrefix));
                var token = tokenArg[SafeUninstallLauncher.HandoffArgumentPrefix.Length..];
                var eventName = SafeUninstallLauncher.HandoffEventPrefix + token;

                // Simulate Inno Setup Phase 2 signaling admission after Phase 1 has already exited
                Task.Run(async () =>
                {
                    await Task.Delay(120); // Wait for cmd.exe to exit first
                    if (EventWaitHandle.TryOpenExisting(eventName, out var evt))
                    {
                        using (evt)
                        {
                            evt.Set();
                        }
                    }
                });

                // Phase 1 exits immediately with exit code 0
                return Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c exit 0",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            admissionTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(SafeUninstallStatus.AdmissionConfirmed, result.Status);
        Assert.True(result.Success);
        Assert.True(gateReleased);
    }

    [Fact]
    public void TryLaunch_InitialProcessExitedWithNonZeroCode_ReturnsUninstallerExitedPrematurely()
    {
        var plan = new SafeUninstallPlan(_uninstallerPath, Quiet: false);
        var gateReleased = false;

        var result = SafeUninstallLauncher.TryLaunch(
            plan,
            releaseUninstallGate: () => { gateReleased = true; return true; },
            processStarter: _ => Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit 42",
                CreateNoWindow = true,
                UseShellExecute = false
            }),
            admissionTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(SafeUninstallStatus.UninstallerExitedPrematurely, result.Status);
        Assert.False(result.Success);
        Assert.Equal(42, result.ExitCode);
        Assert.True(gateReleased);
    }

    public void Dispose()
    {
        if (Directory.Exists(_trustedRoot))
            Directory.Delete(_trustedRoot, recursive: true);
    }
}

