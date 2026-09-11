using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CamMicBlocker.Domain.Interfaces;
using Serilog;

namespace CamMicBlocker.Infrastructure;

/// <summary>
/// Launches the elevated helper process (CamMicBlocker.Elevated.exe) to perform
/// operations that require administrator privileges.
/// 
/// Communication protocol:
///   1. Main app launches helper with CLI arguments via Process.Start + Verb="runas"
///   2. Windows shows a single UAC prompt
///   3. Helper performs the operation, writes JSON result to stdout, then exits
///   4. Main app reads stdout and exit code
/// 
/// This avoids running the main app as admin, respecting least privilege.
/// The UAC prompt is only shown when the user explicitly requests a block/unblock.
/// </summary>
public sealed class PrivilegedOperationClient : IDeviceController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PrivilegedOperationClient>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _helperPath;

    public PrivilegedOperationClient()
    {
        // The elevated helper should be in the same directory as the main app
        var appDir = AppContext.BaseDirectory;
        _helperPath = Path.Combine(appDir, "CamMicBlocker.Elevated.exe");
    }

    public async Task<OperationResult> DisableDevicesAsync(IEnumerable<Domain.Models.DeviceInfo> devices)
    {
        var instanceIds = devices.Select(d => d.InstanceId).ToList();
        if (instanceIds.Count == 0)
        {
            Log.Warning("No devices to disable");
            return OperationResult.Ok();
        }

        Log.Information("Requesting device disable for {Count} device(s)", instanceIds.Count);
        return await ExecuteAsync("disable-devices", string.Join("|", instanceIds));
    }

    public async Task<OperationResult> EnableDevicesAsync(IEnumerable<Domain.Models.DeviceInfo> devices)
    {
        var instanceIds = devices.Select(d => d.InstanceId).ToList();
        if (instanceIds.Count == 0)
        {
            Log.Warning("No devices to enable");
            return OperationResult.Ok();
        }

        Log.Information("Requesting device enable for {Count} device(s)", instanceIds.Count);
        return await ExecuteAsync("enable-devices", string.Join("|", instanceIds));
    }

    /// <summary>
    /// Executes a command via the elevated helper process.
    /// </summary>
    internal async Task<OperationResult> ExecuteAsync(string command, string argument)
    {
        if (!File.Exists(_helperPath))
        {
            var error = $"Elevated helper not found at: {_helperPath}";
            Log.Error(error);
            return OperationResult.Fail(error);
        }

        var sw = Stopwatch.StartNew();
        Log.Debug("Launching elevated helper: Path={Path}, Command={Command}, Argument={Argument}", _helperPath, command, argument);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _helperPath,
                Arguments = $"{command} \"{argument}\"",
                Verb = "runas",                    // Triggers UAC prompt
                UseShellExecute = true,            // Required for Verb="runas"
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            var resultFilePath = Path.Combine(Path.GetTempPath(), $"CamMicBlocker_result_{Guid.NewGuid():N}.json");
            startInfo.Arguments = $"{command} \"{argument}\" --result-file \"{resultFilePath}\"";

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                sw.Stop();
                Log.Error("Failed to start elevated helper process: Command={Command}, DurationMs={DurationMs}", command, sw.ElapsedMilliseconds);
                return OperationResult.Fail("Failed to start elevated helper process");
            }

            await process.WaitForExitAsync();
            sw.Stop();

            Log.Debug("Elevated helper finished: Command={Command}, ExitCode={ExitCode}, DurationMs={DurationMs}",
                command, process.ExitCode, sw.ElapsedMilliseconds);

            if (process.ExitCode != 0)
            {
                // Try to read error from result file
                if (File.Exists(resultFilePath))
                {
                    var errorJson = await File.ReadAllTextAsync(resultFilePath);
                    var errorResult = JsonSerializer.Deserialize<ElevatedResult>(errorJson, JsonOptions);
                    CleanupResultFile(resultFilePath);
                    return OperationResult.Fail(errorResult?.Error ?? $"Helper exited with code {process.ExitCode}");
                }
                return OperationResult.Fail($"Elevated helper exited with code {process.ExitCode}");
            }

            // Read result
            if (File.Exists(resultFilePath))
            {
                var json = await File.ReadAllTextAsync(resultFilePath);
                CleanupResultFile(resultFilePath);
                var result = JsonSerializer.Deserialize<ElevatedResult>(json, JsonOptions);
                if (result != null && result.Success)
                {
                    Log.Information("Elevated operation succeeded: {Command}", command);
                    return OperationResult.Ok();
                }
                return OperationResult.Fail(result?.Error ?? "Unknown error from elevated helper");
            }

            // No result file but exit code 0 — assume success
            return OperationResult.Ok();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED (1223) = User clicked "No" on UAC prompt
            Log.Warning("User cancelled UAC elevation prompt");
            return OperationResult.Fail("Operation cancelled: administrator permission was denied.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute elevated operation: {Command}", command);
            return OperationResult.Fail($"Failed to execute elevated operation: {ex.Message}");
        }
    }

    private static void CleanupResultFile(string path)
    {
        try { File.Delete(path); }
        catch { /* Best effort cleanup */ }
    }

    /// <summary>DTO matching the elevated helper's JSON output.</summary>
    private sealed class ElevatedResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
