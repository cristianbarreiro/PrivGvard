using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Infrastructure.Common.Logging;
using Serilog;

namespace PrivLock.Platform.Windows.Devices;

/// <summary>
/// Direct in-process PnP hardware device controller using CfgMgr32.dll on Windows.
/// </summary>
public sealed class WindowsDeviceController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsDeviceController>();

    internal Task<OperationResult> DisableDevicesAsync(IEnumerable<DeviceInfo> devices)
    {
        var deviceList = devices.ToList();
        if (deviceList.Count == 0)
        {
            Log.Warning("No devices provided to disable");
            return Task.FromResult(OperationResult.Ok());
        }

        var sw = Stopwatch.StartNew();
        Log.Information("Disabling {Count} physical device(s) on Windows via CfgMgr32", deviceList.Count);

        var details = new List<DeviceOperationDetail>();
        var hasFailure = false;

        foreach (var device in deviceList)
        {
            var opResult = DisableDevice(device.Id);
            details.Add(new DeviceOperationDetail
            {
                DeviceId = device.Id,
                FriendlyName = device.FriendlyName,
                Success = opResult.Success,
                OutcomeUncertain = opResult.OutcomeUncertain,
                ErrorMessage = opResult.ErrorMessage
            });

            if (!opResult.Success)
            {
                hasFailure = true;
            }
        }

        sw.Stop();

        if (hasFailure)
        {
            var combinedError = string.Join("; ", details.Where(d => !d.Success).Select(d => d.ErrorMessage));
            Log.Error("Failed to disable some devices in {DurationMs}ms: {Error}", sw.ElapsedMilliseconds, combinedError);
            return Task.FromResult(OperationResult.Fail(
                combinedError,
                details,
                details.Any(detail => detail.OutcomeUncertain)));
        }

        Log.Information("Successfully disabled {Count} device(s) in {DurationMs}ms", deviceList.Count, sw.ElapsedMilliseconds);
        return Task.FromResult(OperationResult.Ok(details));
    }

    internal Task<OperationResult> EnableDevicesAsync(IEnumerable<DeviceInfo> devices)
    {
        var deviceList = devices.ToList();
        if (deviceList.Count == 0)
        {
            Log.Warning("No devices provided to enable");
            return Task.FromResult(OperationResult.Ok());
        }

        var sw = Stopwatch.StartNew();
        Log.Information("Enabling {Count} physical device(s) on Windows via CfgMgr32", deviceList.Count);

        var details = new List<DeviceOperationDetail>();
        var hasFailure = false;

        foreach (var device in deviceList)
        {
            var opResult = EnableDevice(device.Id);
            details.Add(new DeviceOperationDetail
            {
                DeviceId = device.Id,
                FriendlyName = device.FriendlyName,
                Success = opResult.Success,
                OutcomeUncertain = opResult.OutcomeUncertain,
                ErrorMessage = opResult.ErrorMessage
            });

            if (!opResult.Success)
            {
                hasFailure = true;
            }
        }

        sw.Stop();

        if (hasFailure)
        {
            var combinedError = string.Join("; ", details.Where(d => !d.Success).Select(d => d.ErrorMessage));
            Log.Error("Failed to enable some devices in {DurationMs}ms: {Error}", sw.ElapsedMilliseconds, combinedError);
            return Task.FromResult(OperationResult.Fail(
                combinedError,
                details,
                details.Any(detail => detail.OutcomeUncertain)));
        }

        Log.Information("Successfully enabled {Count} device(s) in {DurationMs}ms", deviceList.Count, sw.ElapsedMilliseconds);
        return Task.FromResult(OperationResult.Ok(details));
    }

    /// <summary>
    /// Reads the authoritative Configuration Manager state for one stable PnP Instance ID.
    /// A missing device is distinct from a query failure and remains retryable by recovery.
    /// </summary>
    public WindowsDeviceNodeState GetDeviceNodeState(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return WindowsDeviceNodeState.Failed("Device Instance ID is empty.");
        }

        var locateResult = CfgMgrInterop.CM_Locate_DevNodeW(
            out var devInst,
            instanceId,
            CfgMgrInterop.CM_LOCATE_DEVNODE_NORMAL);

        if (locateResult == CfgMgrInterop.CR_NO_SUCH_DEVNODE)
        {
            return WindowsDeviceNodeState.Missing();
        }

        if (locateResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error(
                "CM_Locate_DevNodeW state query failed for resource {Resource}: error 0x{ErrorCode:X8}",
                ToSafeLogId(instanceId),
                locateResult);
            return WindowsDeviceNodeState.Failed($"Failed to locate device: error 0x{locateResult:X8}");
        }

        var statusResult = CfgMgrInterop.CM_Get_DevNode_Status(
            out var statusFlags,
            out var problemCode,
            devInst,
            0);

        if (statusResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error(
                "CM_Get_DevNode_Status failed for resource {Resource}: error 0x{ErrorCode:X8}",
                ToSafeLogId(instanceId),
                statusResult);
            return WindowsDeviceNodeState.Failed($"Failed to query device state: error 0x{statusResult:X8}");
        }

        return WindowsDeviceNodeState.Present(statusFlags, problemCode);
    }

    private static OperationResult DisableDevice(string instanceId)
    {
        var locateResult = CfgMgrInterop.CM_Locate_DevNodeW(
            out uint devInst,
            instanceId,
            CfgMgrInterop.CM_LOCATE_DEVNODE_NORMAL);

        if (locateResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Locate_DevNodeW failed for resource {Resource}: error 0x{ErrorCode:X8}", ToSafeLogId(instanceId), locateResult);
            var error = $"Failed to locate device: error 0x{locateResult:X8}";
            ReportNativeFailure("WindowsDeviceController.Disable.Locate", error);
            return OperationResult.Fail(error);
        }

        var disableResult = CfgMgrInterop.CM_Disable_DevNode(devInst, CfgMgrInterop.CM_DISABLE_UI_NOT_OK);
        if (disableResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Disable_DevNode failed for resource {Resource}: error 0x{ErrorCode:X8}", ToSafeLogId(instanceId), disableResult);
            var error = $"Failed to disable device: error 0x{disableResult:X8}";
            ReportNativeFailure("WindowsDeviceController.Disable.NativeCall", error);
            return OperationResult.Fail(error);
        }

        var verified = new WindowsDeviceController().GetDeviceNodeState(instanceId);
        if (!verified.QuerySucceeded || !verified.IsPresent || verified.ProblemCode != CfgMgrInterop.CM_PROB_DISABLED)
        {
            var error = verified.ErrorMessage ??
                $"Device disable could not be verified (problem code {verified.ProblemCode}).";
            Log.Error("Resource {Resource} disable verification failed: {Error}", ToSafeLogId(instanceId), error);
            ReportNativeFailure("WindowsDeviceController.Disable.Verify", error);
            return OperationResult.Fail(error, outcomeUncertain: true);
        }

        Log.Debug("Resource {Resource} disabled successfully via CfgMgr32", ToSafeLogId(instanceId));
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
            Log.Error("CM_Locate_DevNodeW failed for resource {Resource}: error 0x{ErrorCode:X8}", ToSafeLogId(instanceId), locateResult);
            var error = $"Failed to locate device: error 0x{locateResult:X8}";
            ReportNativeFailure("WindowsDeviceController.Enable.Locate", error);
            return OperationResult.Fail(error);
        }

        var enableResult = CfgMgrInterop.CM_Enable_DevNode(devInst, 0);
        if (enableResult != CfgMgrInterop.CR_SUCCESS)
        {
            Log.Error("CM_Enable_DevNode failed for resource {Resource}: error 0x{ErrorCode:X8}", ToSafeLogId(instanceId), enableResult);
            var error = $"Failed to enable device: error 0x{enableResult:X8}";
            ReportNativeFailure("WindowsDeviceController.Enable.NativeCall", error);
            return OperationResult.Fail(error);
        }

        var verified = new WindowsDeviceController().GetDeviceNodeState(instanceId);
        if (!verified.QuerySucceeded || !verified.IsPresent || verified.ProblemCode != 0)
        {
            var error = verified.ErrorMessage ??
                $"Device enable did not return to the clean problem code 0 (actual {verified.ProblemCode}).";
            Log.Error("Resource {Resource} enable verification failed: {Error}", ToSafeLogId(instanceId), error);
            ReportNativeFailure("WindowsDeviceController.Enable.Verify", error);
            return OperationResult.Fail(error, outcomeUncertain: true);
        }

        Log.Debug("Resource {Resource} enabled successfully via CfgMgr32", ToSafeLogId(instanceId));
        return OperationResult.Ok();
    }

    private static string ToSafeLogId(string resourceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(resourceId));
        return Convert.ToHexString(digest.AsSpan(0, 6));
    }

    private static void ReportNativeFailure(string context, string error) =>
        CrashReporter.GenerateCrashReport(new InvalidOperationException(error), context);
}

public sealed record WindowsDeviceNodeState(
    bool QuerySucceeded,
    bool IsPresent,
    uint StatusFlags,
    uint ProblemCode,
    string? ErrorMessage)
{
    public bool IsEnabled => QuerySucceeded && IsPresent && ProblemCode != CfgMgrInterop.CM_PROB_DISABLED;

    public static WindowsDeviceNodeState Present(uint statusFlags, uint problemCode) =>
        new(true, true, statusFlags, problemCode, null);

    public static WindowsDeviceNodeState Missing() =>
        new(true, false, 0, 0, null);

    public static WindowsDeviceNodeState Failed(string errorMessage) =>
        new(false, false, 0, 0, errorMessage);
}
