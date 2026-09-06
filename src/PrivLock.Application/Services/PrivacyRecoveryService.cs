using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Application.Services;

/// <summary>
/// Startup-only facade that makes recovery ordering explicit in the composition root.
/// </summary>
public sealed class PrivacyRecoveryService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PrivacyRecoveryService>();
    private readonly ProtectionService _protectionService;

    public PrivacyRecoveryService(ProtectionService protectionService)
    {
        _protectionService = protectionService;
    }

    public async Task<PrivacyRecoveryResult> RecoverAtStartupAsync(
        CancellationToken cancellationToken = default)
    {
        Log.Information("Checking for an unfinished reversible privacy session");
        var result = await _protectionService.RecoverPreviousSessionAsync(cancellationToken);
        Log.Information(
            "Startup recovery finished: Supported={Supported}, Complete={Complete}, Conflicts={Conflicts}, IrreducibleAmbiguities={IrreducibleAmbiguities}, Missing={Missing}, Failed={Failed}",
            result.TrackingSupported,
            result.IsComplete,
            result.ConflictCount,
            result.IrreducibleAmbiguityCount,
            result.MissingCount,
            result.FailedCount);
        return result;
    }
}
