using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Serilog;

namespace CamMicBlocker.Logging;

/// <summary>
/// Captures unhandled exceptions and writes structured JSON crash reports
/// to %LOCALAPPDATA%\CamMicBlocker\CrashReports\
/// 
/// Provides complete diagnostic context for post-mortem analysis:
/// - Exact exception, inner exception, and stack trace
/// - Thread ID, Process ID, Process Uptime
/// - Operating System version, .NET version, User Name
/// - Current Application State
/// </summary>
public static class CrashReporter
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(CrashReporter));
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GenerateCrashReport(Exception exception, string sourceContext, object? appState = null)
    {
        lock (FileLock)
        {
            try
            {
                var crashDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CamMicBlocker", "CrashReports");

                Directory.CreateDirectory(crashDir);

                var reportPath = Path.Combine(crashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");

                var process = Process.GetCurrentProcess();

                var report = new CrashReportModel
                {
                    Timestamp = DateTime.UtcNow,
                    SourceContext = sourceContext,
                    ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                    Message = exception.Message,
                    StackTrace = exception.StackTrace,
                    InnerException = exception.InnerException != null ? new InnerExceptionModel
                    {
                        Type = exception.InnerException.GetType().FullName ?? exception.InnerException.GetType().Name,
                        Message = exception.InnerException.Message,
                        StackTrace = exception.InnerException.StackTrace
                    } : null,
                    EnvironmentInfo = new EnvironmentInfoModel
                    {
                        MachineName = Environment.MachineName,
                        UserName = Environment.UserName,
                        OsVersion = Environment.OSVersion.ToString(),
                        RuntimeVersion = Environment.Version.ToString(),
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName,
                        ProcessUptimeMs = (long)(DateTime.Now - process.StartTime).TotalMilliseconds,
                        Is64BitProcess = Environment.Is64BitProcess,
                        Is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                    },
                    AppState = appState
                };

                var json = JsonSerializer.Serialize(report, JsonOptions);
                File.WriteAllText(reportPath, json);

                Log.Fatal("Crash report written to {ReportPath} for exception {ExceptionType}: {Message}",
                    reportPath, report.ExceptionType, report.Message);

                return reportPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to write crash report to disk");
                return string.Empty;
            }
        }
    }

    private sealed class CrashReportModel
    {
        public DateTime Timestamp { get; set; }
        public required string SourceContext { get; set; }
        public required string ExceptionType { get; set; }
        public required string Message { get; set; }
        public string? StackTrace { get; set; }
        public InnerExceptionModel? InnerException { get; set; }
        public required EnvironmentInfoModel EnvironmentInfo { get; set; }
        public object? AppState { get; set; }
    }

    private sealed class InnerExceptionModel
    {
        public required string Type { get; set; }
        public required string Message { get; set; }
        public string? StackTrace { get; set; }
    }

    private sealed class EnvironmentInfoModel
    {
        public required string MachineName { get; set; }
        public required string UserName { get; set; }
        public required string OsVersion { get; set; }
        public required string RuntimeVersion { get; set; }
        public int ProcessId { get; set; }
        public required string ProcessName { get; set; }
        public long ProcessUptimeMs { get; set; }
        public bool Is64BitProcess { get; set; }
        public bool Is64BitOperatingSystem { get; set; }
    }
}
