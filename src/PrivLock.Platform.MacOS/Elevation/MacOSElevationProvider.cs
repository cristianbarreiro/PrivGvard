using System.Diagnostics;
using System.Runtime.InteropServices;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.MacOS.Elevation;

/// <summary>
/// Checks and requests administrator privileges on macOS via Authorization Services / osascript with administrator privileges.
/// </summary>
public sealed class MacOSElevationProvider : IElevationProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MacOSElevationProvider>();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public bool IsElevated
    {
        get
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return GetEuid() == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to check macOS euid");
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
                FileName = "osascript",
                Arguments = $"-e \"do shell script \\\"{exePath}\\\" with administrator privileges\"",
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                return Task.FromResult(ElevationResult.Success());
            }

            return Task.FromResult(ElevationResult.Fail("Failed to execute privileged helper."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to request elevation on macOS");
            return Task.FromResult(ElevationResult.Fail(ex.Message));
        }
    }
}
