using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Linux.Elevation;

/// <summary>
/// Checks and requests root/polkit elevation on Linux.
/// </summary>
public sealed class LinuxElevationProvider : IElevationProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxElevationProvider>();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public bool IsElevated
    {
        get
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return GetEuid() == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to check Linux euid");
                return false;
            }
        }
    }

    public Task<ElevationResult> RequestElevationAsync(CancellationToken cancellationToken = default)
    {
        if (IsElevated)
        {
            return Task.FromResult(ElevationResult.Success());
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return Task.FromResult(ElevationResult.Fail("Cannot determine executable path."));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"\"{exePath}\"",
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                return Task.FromResult(ElevationResult.Success());
            }

            return Task.FromResult(ElevationResult.Fail("Failed to execute pkexec."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to request elevation via pkexec on Linux");
            return Task.FromResult(ElevationResult.Fail(ex.Message));
        }
    }
}
