using System.Diagnostics;
using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using CamMicBlocker.Infrastructure.Win32;
using Serilog;

namespace CamMicBlocker.Infrastructure;

/// <summary>
/// Direct in-process device controller using CfgMgr32.dll P/Invoke.
/// Executes device enable/disable operations directly within the elevated application process.
/// 
/// Advantages:
/// - Zero IPC overhead (0ms execution delay)
/// - Zero extra UAC prompts (app runs elevated via requireAdministrator manifest)
/// - Clean, direct diagnostic tracing
/// </summary>
public sealed class DeviceController : IDeviceController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DeviceController>();

    public Task<OperationResult> DisableDevicesAsync(IEnumerable<DeviceInfo> devices)
    {
        var deviceList = devices.ToList();
        if (deviceList.Count == 0)
        {
            Log.Warning("No devices provided to disable");
            return Task.FromResult(OperationResult.Ok());
        }

        var sw = Stopwatch.StartNew();
        Log.Information("Disabling {Count} physical device(s) in-process", deviceList.Count);

        var errors = new List<string>();

        foreach (var device in deviceList)
        {
            var result = DisableDevice(device.InstanceId);
            if (!result.Success)
            {
                errors.Add($"[{device.FriendlyName}]: {result.ErrorMessage}");
            }
        }

        sw.Stop();

        if (errors.Count > 0)
        {
            var combinedError = string.Join("; ", errors);
            Log.Error("Failed to disable some devices in {DurationMs}ms: {Error}", sw.ElapsedMilliseconds, combinedError);
            return Task.FromResult(OperationResult.Fail(combinedError));
        }

        Log.Information("Successfully disabled {Count} device(s) in {DurationMs}ms", deviceList.Count, sw.ElapsedMilliseconds);
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<OperationResult> EnableDevicesAsync(IEnumerable<DeviceInfo> devices)
    {
        var deviceList = devices.ToList();
        if (deviceList.Count == 0)
        {
            Log.Warning("No devices provided to enable");
            return Task.FromResult(OperationResult.Ok());
        }

        var sw = Stopwatch.StartNew();
        Log.Information("Enabling {Count} physical device(s) in-process", deviceList.Count);

        var errors = new List<string>();

        foreach (var device in deviceList)
        {
            var result = EnableDevice(device.InstanceId);
            if (!result.Success)
            {
                errors.Add($"[{device.FriendlyName}]: {result.ErrorMessage}");
            }
        }

        sw.Stop();

        if (errors.Count > 0)
        {
            var combinedError = string.Join("; ", errors);
            Log.Error("Failed to enable some devices in {DurationMs}ms: {Error}", sw.ElapsedMilliseconds, combinedError);
            return Task.FromResult(OperationResult.Fail(combinedError));
        }

        Log.Information("Successfully enabled {Count} device(s) in {DurationMs}ms", deviceList.Count, sw.ElapsedMilliseconds);
        return Task.FromResult(OperationResult.Ok());
    }

    private static OperationResult DisableDevice(string instanceId)
    {
        var locateResult = CfgMgrInterop.CM_Locate_DevNodeW(
            out uint devInst,
            instanceId,
            CfgMgrInterop.CM_LOCATE_DEVNODE_NORMAL);

        if (locateResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Locate_DevNodeW failed for device {InstanceId}: error 0x{ErrorCode:X8}", instanceId, locateResult);
            return OperationResult.Fail($"Failed to locate device: error 0x{locateResult:X8}");
        }

        var disableResult = CfgMgrInterop.CM_Disable_DevNode(devInst, CfgMgrInterop.CM_DISABLE_UI_NOT_OK);
        if (disableResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Disable_DevNode failed for device {InstanceId}: error 0x{ErrorCode:X8}", instanceId, disableResult);
            return OperationResult.Fail($"Failed to disable device: error 0x{disableResult:X8}");
        }

        Log.Debug("Device {InstanceId} disabled successfully", instanceId);
        return OperationResult.Ok();
    }

    private static OperationResult EnableDevice(string instanceId)
    {
        var locateResult = CfgMgrInterop.CM_Locate_DevNodeW(
            out uint devInst,
            instanceId,
            CfgMgrInterop.CM_LOCATE_DEVNODE_NORMAL);

        if (locateResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Locate_DevNodeW failed for device {InstanceId}: error 0x{ErrorCode:X8}", instanceId, locateResult);
            return OperationResult.Fail($"Failed to locate device: error 0x{locateResult:X8}");
        }

        var enableResult = CfgMgrInterop.CM_Enable_DevNode(devInst, 0);
        if (enableResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Enable_DevNode failed for device {InstanceId}: error 0x{ErrorCode:X8}", instanceId, enableResult);
            return OperationResult.Fail($"Failed to enable device: error 0x{enableResult:X8}");
        }

        Log.Debug("Device {InstanceId} enabled successfully", instanceId);
        return OperationResult.Ok();
    }
}
