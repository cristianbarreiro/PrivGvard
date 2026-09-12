using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace PrivLock.Infrastructure.Common.Logging;

/// <summary>
/// Cross-platform Serilog configuration for PrivLock.
/// Writes rolling logs to the platform AppData directory.
/// </summary>
public static class LoggingConfiguration
{
    public static void Initialize(string? customLogDir = null)
    {
        var logDir = customLogDir ?? GetLogDirectory();
        Directory.CreateDirectory(logDir);

        using var process = Process.GetCurrentProcess();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "PrivGvard")
            .Enrich.WithProperty("ProcessId", process.Id)
            .WriteTo.File(
                path: Path.Combine(logDir, "PrivGvard-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [PID:{ProcessId}] [Thread:{ThreadId}] [{SourceContext}] {Message:lj}{Properties:j}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== PrivGvard Logging Initialized ===");
        Log.Information("OS: {OS} ({Desc})", Environment.OSVersion, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Log.Information("Arch: {Arch}", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        Log.Information("Process ID: {PID}", process.Id);
        Log.Information(".NET Runtime: {Runtime}", Environment.Version);
        Log.Information("Log directory: {LogDir}", logDir);
    }

    public static string GetLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrivGvard", "Logs");
    }
}
