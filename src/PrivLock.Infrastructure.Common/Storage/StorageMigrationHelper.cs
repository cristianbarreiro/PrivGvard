using Serilog;

namespace PrivLock.Infrastructure.Common.Storage;

/// <summary>
/// Safely migrates persistent state and recovery journals from legacy %LOCALAPPDATA%\PrivLock
/// to %LOCALAPPDATA%\PrivGvard without data loss or race conditions.
/// </summary>
public static class StorageMigrationHelper
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(StorageMigrationHelper));
    private static readonly object SyncLock = new();
    private static bool _migrationAttempted;

    public static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, "PrivGvard");
        var legacyDir = Path.Combine(localAppData, "PrivLock");

        EnsureMigrated(targetDir, legacyDir);

        return targetDir;
    }

    public static void EnsureMigrated(string targetDir, string legacyDir)
    {
        if (_migrationAttempted) return;

        lock (SyncLock)
        {
            if (_migrationAttempted) return;
            _migrationAttempted = true;

            MigrateDirectory(targetDir, legacyDir);
        }
    }

    public static void MigrateDirectory(string targetDir, string legacyDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir))
                return;

            Directory.CreateDirectory(targetDir);

                // 1. Migrate settings state.json
                var legacyState = Path.Combine(legacyDir, "state.json");
                var targetState = Path.Combine(targetDir, "state.json");
                if (File.Exists(legacyState) && !File.Exists(targetState))
                {
                    File.Copy(legacyState, targetState, overwrite: false);
                    Log.Information("Migrated legacy state.json from {Legacy} to {Target}", legacyState, targetState);
                }

                // 2. Migrate Recovery sessions and active marker
                var legacyRecovery = Path.Combine(legacyDir, "Recovery");
                var targetRecovery = Path.Combine(targetDir, "Recovery");
                if (Directory.Exists(legacyRecovery))
                {
                    Directory.CreateDirectory(targetRecovery);
                    foreach (var file in Directory.GetFiles(legacyRecovery))
                    {
                        var fileName = Path.GetFileName(file);
                        var targetFile = Path.Combine(targetRecovery, fileName);
                        if (!File.Exists(targetFile))
                        {
                            File.Copy(file, targetFile, overwrite: false);
                            Log.Information("Migrated recovery file {File} to {Target}", fileName, targetFile);
                        }
                    }
                }

                // 3. Migrate CrashReports if any
                var legacyCrash = Path.Combine(legacyDir, "CrashReports");
                var targetCrash = Path.Combine(targetDir, "CrashReports");
                if (Directory.Exists(legacyCrash))
                {
                    Directory.CreateDirectory(targetCrash);
                    foreach (var file in Directory.GetFiles(legacyCrash))
                    {
                        var fileName = Path.GetFileName(file);
                        var targetFile = Path.Combine(targetCrash, fileName);
                        if (!File.Exists(targetFile))
                        {
                            File.Copy(file, targetFile, overwrite: false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Non-fatal error during legacy PrivLock to PrivGvard storage migration");
            }
    }
}
