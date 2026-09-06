using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PrivLock.Desktop;

internal sealed record SafeUninstallPlan(string UninstallerPath, bool Quiet);

/// <summary>
/// Validates the standard-user uninstall wrapper and coordinates a race-free handoff to Inno.
/// </summary>
internal static class SafeUninstallLauncher
{
    internal const string Command = "--safe-uninstall";
    internal const string QuietSwitch = "--quiet";
    internal const string HandoffArgumentPrefix = "/PRIVLOCK_HANDOFF=";
    internal const string HandoffEventPrefix = @"Global\PrivLock_UninstallReady_";
    internal const string ExpectedApplicationFileName = "PrivLock.exe";
    internal const string ExpectedUninstallerFileName = "unins000.exe";
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(30);

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
                error = "PrivLock must start under an unelevated user token.";
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

        if (!string.Equals(
                Path.GetFileName(applicationPath),
                ExpectedApplicationFileName,
                StringComparison.OrdinalIgnoreCase) ||
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
            error = "The uninstaller is not in the trusted PrivLock installation directory.";
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
    internal static bool TryLaunch(
        SafeUninstallPlan plan,
        Func<bool> releaseUninstallGate,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(releaseUninstallGate);
        error = null;
        try
        {
            var handoffToken = Guid.NewGuid().ToString("N");
            using var admissionEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                HandoffEventPrefix + handoffToken,
                out var createdNew);
            if (!createdNew)
            {
                error = "Could not create a unique PrivLock uninstall handoff event.";
                return false;
            }

            // The main single-instance mutex remains owned until Inno confirms that it owns the
            // uninstall gate. No new PrivLock process can enter the gap between these two owners.
            if (!releaseUninstallGate())
            {
                error = "Could not release the PrivLock uninstall gate for handoff.";
                return false;
            }

            using var process = Process.Start(CreateStartInfo(plan, handoffToken));
            if (process == null)
            {
                error = "Windows did not start the PrivLock uninstaller.";
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < AdmissionTimeout)
            {
                if (admissionEvent.WaitOne(TimeSpan.FromMilliseconds(250)))
                    return true;
                if (process.HasExited)
                {
                    error = $"The PrivLock uninstaller exited before safe admission (code {process.ExitCode}).";
                    return false;
                }
            }

            error = "Timed out waiting for the PrivLock uninstaller safety admission.";
            return false;
        }
        catch (global::System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Uninstall elevation was cancelled by the user.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"The PrivLock uninstaller could not be started ({ex.GetType().Name}).";
            return false;
        }
    }

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
