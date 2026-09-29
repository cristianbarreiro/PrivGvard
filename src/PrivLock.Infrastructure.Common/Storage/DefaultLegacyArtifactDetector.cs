using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Infrastructure.Common.Storage;

/// <summary>
/// Checks for explicit legacy markers or unmigrated legacy directories on disk without
/// relying on user DesiredState as historical evidence.
/// </summary>
public sealed class DefaultLegacyArtifactDetector : ILegacyArtifactDetector
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DefaultLegacyArtifactDetector>();
    private readonly string? _customDirectory;

    public DefaultLegacyArtifactDetector(string? customDirectory = null)
    {
        _customDirectory = customDirectory;
    }

    public bool HasLegacyEvidence()
    {
        try
        {
            var dataDir = _customDirectory ?? StorageMigrationHelper.GetDefaultDataDirectory();
            var recoveryDir = Path.Combine(dataDir, "Recovery");
            var explicitMarker = Path.Combine(recoveryDir, "legacy-untracked.marker");
            if (File.Exists(explicitMarker))
                return true;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacyRecoveryDir = Path.Combine(localAppData, "PrivLock", "Recovery");
            if (File.Exists(Path.Combine(legacyRecoveryDir, "legacy-untracked.marker")))
                return true;

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error while checking for legacy recovery artifacts; assuming no legacy evidence");
            return false;
        }
    }
}
