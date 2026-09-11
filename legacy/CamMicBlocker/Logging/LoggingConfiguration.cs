using System.Diagnostics;
using System.IO;
using Serilog;
using Serilog.Events;

namespace CamMicBlocker.Logging;

/// <summary>
/// Configures Serilog for the application.
/// 
/// Storage location: %LOCALAPPDATA%\CamMicBlocker\Logs\
/// Features:
/// - Enriched with ProcessId, ThreadId, MachineName, Application Name, and OperationId (from LogContext)
/// - Output template includes thread & correlation ID when available
/// - Rolling daily files, max 10MB per file, 7-day retention
/// </summary>
public static class LoggingConfiguration
{
    public static void Initialize()
    {
        var logDir = GetLogDirectory();
        Directory.CreateDirectory(logDir);

        var process = Process.GetCurrentProcess();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext() // Enables ambient OperationId correlation
            .Enrich.WithProperty("Application", "CamMicBlocker")
            .Enrich.WithProperty("ProcessId", process.Id)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .WriteTo.File(
                path: Path.Combine(logDir, "CamMicBlocker-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB limit per file
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [PID:{ProcessId}] [Thread:{ThreadId}] [{SourceContext}] {Message:lj}{Properties:j}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== CamMicBlocker Logging Initialized ===");
        Log.Information("OS: {OS}", Environment.OSVersion);
        Log.Information("User: {User}", Environment.UserName);
        Log.Information("Machine: {Machine}", Environment.MachineName);
        Log.Information("Process ID: {PID}", process.Id);
        Log.Information(".NET Runtime: {Runtime}", Environment.Version);
        Log.Information("Log directory: {LogDir}", logDir);
    }

    /// <summary>
    /// Returns the path to the log directory for diagnostic inspection.
    /// </summary>
    public static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CamMicBlocker", "Logs");
    }
}
