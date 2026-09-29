using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace PrivLock.Desktop;

public enum SafeUninstallStatus
{
    AdmissionConfirmed,
    ValidationFailed,
    RestoreFailed,
    WorkerNotStopped,
    LaunchFailed,
    UserCancelled,
    UninstallerExitedPrematurely,
    AdmissionTimeout
}

public sealed record SafeUninstallResult(
    SafeUninstallStatus Status,
    string? Message = null,
    int? ExitCode = null)
{
    public bool Success => Status == SafeUninstallStatus.AdmissionConfirmed;
}

internal sealed record SafeUninstallPlan(string UninstallerPath, bool Quiet);

/// <summary>
/// Validates the standard-user uninstall wrapper and coordinates a race-free handoff to Inno.
/// </summary>
internal static class SafeUninstallLauncher
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(SafeUninstallLauncher));

    internal const string Command = "--safe-uninstall";
    internal const string QuietSwitch = "--quiet";
    internal const string HandoffArgumentPrefix = "/PRIVLOCK_HANDOFF=";
    internal const string HandoffEventPrefix = @"Global\PrivLock_UninstallReady_";
    internal const string ExpectedApplicationFileName = "PrivGvard.exe";
    internal const string LegacyExpectedApplicationFileName = "PrivLock.exe";
    internal const string ExpectedUninstallerFileName = "unins000.exe";
    internal static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(30);

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    internal static bool TryConfirmStandardUserToken(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Safe uninstall is supported only on Windows.";
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                error = "PrivGvard must start under an unelevated user token.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not verify the standard-user token ({ex.GetType().Name}).";
            return false;
        }
    }

    internal static bool TryCreatePlan(
        IReadOnlyList<string> args,
        string? currentProcessPath,
        out SafeUninstallPlan? plan,
        out string? error)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return TryCreatePlan(args, currentProcessPath, programFiles, out plan, out error);
    }

    internal static bool TryCreatePlan(
        IReadOnlyList<string> args,
        string? currentProcessPath,
        string trustedInstallRoot,
        out SafeUninstallPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        var quiet = args.Count == 3 &&
                    string.Equals(args[2], QuietSwitch, StringComparison.OrdinalIgnoreCase);
        if ((args.Count != 2 && !quiet) ||
            !string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase))
        {
            error = "Invalid safe-uninstall command line.";
            return false;
        }

        if (!TryNormalizePath(currentProcessPath, out var applicationPath) ||
            !TryNormalizePath(args[1], out var uninstallerPath) ||
            !TryNormalizeDirectory(trustedInstallRoot, out var trustedRoot))
        {
            error = "Safe-uninstall paths must be absolute, canonical paths.";
            return false;
        }

        var appFileName = Path.GetFileName(applicationPath);
        if ((!string.Equals(appFileName, ExpectedApplicationFileName, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(appFileName, LegacyExpectedApplicationFileName, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(
                Path.GetFileName(uninstallerPath),
                ExpectedUninstallerFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "Safe uninstall rejected an unexpected executable name.";
            return false;
        }

        var applicationDirectory = Path.GetDirectoryName(applicationPath);
        var uninstallerDirectory = Path.GetDirectoryName(uninstallerPath);
        if (string.IsNullOrEmpty(applicationDirectory) ||
            !string.Equals(applicationDirectory, uninstallerDirectory, StringComparison.OrdinalIgnoreCase) ||
            !IsStrictDescendant(applicationDirectory, trustedRoot))
        {
            error = "The uninstaller is not in the trusted PrivGvard installation directory.";
            return false;
        }

        try
        {
            if (!File.Exists(applicationPath) || !File.Exists(uninstallerPath))
            {
                error = "The application or uninstaller file is missing.";
                return false;
            }

            if (HasReparsePoint(applicationPath) ||
                HasReparsePoint(uninstallerPath) ||
                HasReparsePoint(trustedRoot) ||
                HasReparsePointInDirectoryChain(applicationDirectory, trustedRoot))
            {
                error = "Safe uninstall rejected a reparse-point path.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Safe uninstall could not validate trusted file metadata ({ex.GetType().Name}).";
            return false;
        }

        plan = new SafeUninstallPlan(uninstallerPath, quiet);
        return true;
    }

    internal static ProcessStartInfo CreateStartInfo(SafeUninstallPlan plan, string handoffToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsValidHandoffToken(handoffToken))
            throw new ArgumentException("Invalid uninstall handoff token.", nameof(handoffToken));

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.UninstallerPath,
            WorkingDirectory = Path.GetDirectoryName(plan.UninstallerPath)!,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add(HandoffArgumentPrefix + handoffToken);
        if (plan.Quiet)
        {
            startInfo.ArgumentList.Add("/VERYSILENT");
            startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            startInfo.ArgumentList.Add("/NORESTART");
        }

        return startInfo;
    }

    [SupportedOSPlatform("windows")]
    internal static SafeUninstallResult TryLaunch(
        SafeUninstallPlan plan,
        Func<bool> releaseUninstallGate,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        TimeSpan? admissionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(releaseUninstallGate);
        var timeout = admissionTimeout ?? AdmissionTimeout;
        processStarter ??= Process.Start;

        var handoffToken = Guid.NewGuid().ToString("N");
        var eventName = HandoffEventPrefix + handoffToken;

        EventWaitHandle admissionEvent;
        bool createdNew;
        try
        {
            admissionEvent = CreateAdmissionEvent(eventName, out createdNew);
            if (!createdNew)
            {
                admissionEvent.Dispose();
                var msg = "Could not create a unique PrivGvard uninstall handoff event.";
                Log.Error(msg);
                return new SafeUninstallResult(SafeUninstallStatus.LaunchFailed, msg);
            }
            Log.Information("Uninstall admission event created: {EventName}", eventName);
        }
        catch (Exception ex)
        {
            var msg = $"Could not create the uninstall admission event ({ex.GetType().Name}): {ex.Message}";
            Log.Error(ex, "Failed to create uninstall handoff event {EventName}", eventName);
            return new SafeUninstallResult(SafeUninstallStatus.LaunchFailed, msg);
        }

        using (admissionEvent)
        {
            Process? process;
            try
            {
                var startInfo = CreateStartInfo(plan, handoffToken);
                process = processStarter(startInfo);
                if (process == null)
                {
                    var msg = "Windows did not start the PrivGvard uninstaller.";
                    Log.Error(msg);
                    return new SafeUninstallResult(SafeUninstallStatus.LaunchFailed, msg);
                }

                try
                {
                    Log.Information(
                        "Started uninstaller process (PID: {ProcessId}, File: {FileName}) with handoff token",
                        process.Id,
                        plan.UninstallerPath);
                }
                catch
                {
                    Log.Information("Started uninstaller process ({FileName}) with handoff token", plan.UninstallerPath);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                var msg = "Uninstall elevation was cancelled by the user.";
                Log.Warning(msg);
                return new SafeUninstallResult(SafeUninstallStatus.UserCancelled, msg, 1223);
            }
            catch (Exception ex)
            {
                var msg = $"The PrivGvard uninstaller could not be started ({ex.GetType().Name}): {ex.Message}";
                Log.Error(ex, "Failed to start uninstaller process");
                return new SafeUninstallResult(SafeUninstallStatus.LaunchFailed, msg);
            }

            // Coordination: Now that the uninstaller process has been successfully started,
            // release the uninstall gate so that Inno Setup can acquire it during InitializeUninstall.
            // If starting the process had failed (e.g. UAC cancel), the gate would remain safely owned.
            if (!releaseUninstallGate())
            {
                var msg = "Could not release the PrivGvard uninstall gate for handoff.";
                Log.Error(msg);
                return new SafeUninstallResult(SafeUninstallStatus.LaunchFailed, msg);
            }
            Log.Information("Released PrivGvard uninstall gate for handoff to uninstaller.");

            using (process)
            {
                var stopwatch = Stopwatch.StartNew();
                var processExitedLogged = false;

                while (stopwatch.Elapsed < timeout)
                {
                    // 1. Wait for Inno Setup (Phase 2) to signal safe admission.
                    if (admissionEvent.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        Log.Information(
                            "Uninstall admission confirmed via event {EventName} after {ElapsedMs}ms",
                            eventName,
                            stopwatch.ElapsedMilliseconds);
                        return new SafeUninstallResult(SafeUninstallStatus.AdmissionConfirmed);
                    }

                    // 2. Check if the launched process has exited.
                    // Note: Inno Setup unins000.exe (Phase 1) typically spawns a clone in %TEMP% (Phase 2)
                    // and exits with ExitCode 0 after spawning. That is NORMAL behavior.
                    // Only an exit with a NON-ZERO code indicates a premature failure before handoff.
                    if (!processExitedLogged && process.HasExited)
                    {
                        processExitedLogged = true;
                        int exitCode;
                        try { exitCode = process.ExitCode; } catch { exitCode = -1; }

                        Log.Information(
                            "Initial uninstaller process exited with code {ExitCode} after {ElapsedMs}ms.",
                            exitCode,
                            stopwatch.ElapsedMilliseconds);

                        if (exitCode != 0)
                        {
                            var msg = $"The PrivGvard uninstaller exited prematurely with code {exitCode} before admission.";
                            Log.Error(msg);
                            return new SafeUninstallResult(
                                SafeUninstallStatus.UninstallerExitedPrematurely,
                                msg,
                                exitCode);
                        }

                        Log.Information("Initial uninstaller exited cleanly (code 0). Awaiting second-phase admission event...");
                    }
                }

                // If timeout elapsed without admission confirmation:
                var timeoutMsg = $"Timed out waiting for the PrivGvard uninstaller safety admission ({timeout.TotalSeconds}s).";
                Log.Error(timeoutMsg);
                return new SafeUninstallResult(SafeUninstallStatus.AdmissionTimeout, timeoutMsg);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static EventWaitHandle CreateAdmissionEvent(string eventName, out bool createdNew)
    {
        try
        {
            var userSid = WindowsIdentity.GetCurrent().User;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new EventWaitHandleSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            if (userSid != null)
            {
                security.SetOwner(userSid);
                security.AddAccessRule(new EventWaitHandleAccessRule(
                    userSid,
                    EventWaitHandleRights.FullControl,
                    AccessControlType.Allow));
            }
            security.AddAccessRule(new EventWaitHandleAccessRule(
                administrators,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(
                system,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));

            return EventWaitHandleAcl.Create(
                initialState: false,
                EventResetMode.ManualReset,
                eventName,
                out createdNew,
                security);
        }
        catch (PlatformNotSupportedException)
        {
            return new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                eventName,
                out createdNew);
        }
    }

    internal static void ShowErrorFeedback(string message, bool quiet)
    {
        if (quiet || !OperatingSystem.IsWindows())
            return;

        try
        {
            _ = MessageBoxW(IntPtr.Zero, message, "PrivGvard", 0x00000010 /* MB_ICONERROR */ | 0x00000000 /* MB_OK */);
        }
        catch
        {
            // Best-effort UI notification
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal static bool IsValidHandoffToken(string? token) =>
        token is { Length: 32 } && token.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool TryNormalizePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeDirectory(string? value, out string normalized)
    {
        if (!TryNormalizePath(value, out normalized) || !Directory.Exists(normalized))
            return false;

        return true;
    }

    private static bool IsStrictDescendant(string candidate, string root)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparsePointInDirectoryChain(string directory, string trustedRoot)
    {
        var current = new DirectoryInfo(directory);
        while (current != null &&
               !string.Equals(current.FullName, trustedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;

            current = current.Parent;
        }

        return current == null;
    }
}

