using System.Security.Principal;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.Elevation;

/// <summary>
/// Checks and requests administrative elevation on Windows using WindowsIdentity and UAC 'runas' verb.
/// </summary>
public sealed class WindowsElevationProvider : IElevationProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsElevationProvider>();

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to determine Windows elevation status");
                return false;
            }
        }
    }

    public Task<ElevationResult> RequestElevationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ElevationResult.Fail(
            "Whole-application elevation is disabled. Privileged operations use an authenticated transient worker."));
    }
}
