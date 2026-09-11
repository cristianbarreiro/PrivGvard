using System.Diagnostics;
using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using Serilog;
using Serilog.Context;

namespace CamMicBlocker.Application;

/// <summary>
/// Orchestrates the block/unblock workflow:
/// 1. Detect target devices
/// 2. Set/remove privacy policy (elevated)
/// 3. Disable/enable devices (elevated)
/// 4. Update desired state
/// 5. Reconcile actual state
/// </summary>
public sealed class BlockingService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<BlockingService>();

    private readonly IDeviceDetector _deviceDetector;
    private readonly IDeviceController _deviceController;
    private readonly IPolicyManager _policyManager;
    private readonly IStateStore _stateStore;

    public BlockingService(
        IDeviceDetector deviceDetector,
        IDeviceController deviceController,
        IPolicyManager policyManager,
        IStateStore stateStore)
    {
        _deviceDetector = deviceDetector;
        _deviceController = deviceController;
        _policyManager = policyManager;
        _stateStore = stateStore;
    }

    /// <summary>
    /// Fired when the blocking state changes (after a block/unblock operation or reconciliation).
    /// </summary>
    public event Action<BlockState>? StateChanged;

    /// <summary>
    /// Blocks the specified target (camera, microphone, or both).
    /// Enriched with OperationId correlation and Stopwatch duration timing.
    /// </summary>
    public async Task<OperationResult> BlockAsync(BlockTarget target)
    {
        var opId = $"Op-{Guid.NewGuid().ToString("N")[..8]}";
        using (LogContext.PushProperty("OperationId", opId))
        {
            var sw = Stopwatch.StartNew();
            Log.Information("Block requested: Target={Target}, OperationId={OperationId}", target, opId);

            // 1. Set privacy policy
            var policyResult = await _policyManager.SetPolicyAsync(target, BlockStatus.Blocked);
            if (!policyResult.Success)
            {
                sw.Stop();
                Log.Error("Failed to set policy: Target={Target}, Error={Error}, DurationMs={DurationMs}",
                    target, policyResult.ErrorMessage, sw.ElapsedMilliseconds);
                return policyResult;
            }

            // 2. Detect and disable physical devices
            var devices = GetDevicesForTarget(target);
            if (devices.Count > 0)
            {
                var deviceResult = await _deviceController.DisableDevicesAsync(devices);
                if (!deviceResult.Success)
                {
                    Log.Warning("Some devices could not be disabled: Error={Error}. Policy was still applied.",
                        deviceResult.ErrorMessage);
                }
            }
            else
            {
                Log.Warning("No {Target} devices detected to disable, but policy was applied", target);
            }

            // 3. Update desired state
            var state = _stateStore.Load();
            if (target is BlockTarget.Camera or BlockTarget.Both)
                state.Camera = BlockStatus.Blocked;
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                state.Microphone = BlockStatus.Blocked;
            _stateStore.Save(state);

            // 4. Reconcile and notify
            var blockState = GetCurrentState();
            StateChanged?.Invoke(blockState);

            sw.Stop();
            Log.Information("Block completed successfully: Target={Target}, DurationMs={DurationMs}",
                target, sw.ElapsedMilliseconds);

            return OperationResult.Ok();
        }
    }

    /// <summary>
    /// Unblocks the specified target (camera, microphone, or both).
    /// Enriched with OperationId correlation and Stopwatch duration timing.
    /// </summary>
    public async Task<OperationResult> UnblockAsync(BlockTarget target)
    {
        var opId = $"Op-{Guid.NewGuid().ToString("N")[..8]}";
        using (LogContext.PushProperty("OperationId", opId))
        {
            var sw = Stopwatch.StartNew();
            Log.Information("Unblock requested: Target={Target}, OperationId={OperationId}", target, opId);

            // 1. Remove privacy policy
            var policyResult = await _policyManager.SetPolicyAsync(target, BlockStatus.Allowed);
            if (!policyResult.Success)
            {
                sw.Stop();
                Log.Error("Failed to remove policy: Target={Target}, Error={Error}, DurationMs={DurationMs}",
                    target, policyResult.ErrorMessage, sw.ElapsedMilliseconds);
                return policyResult;
            }

            // 2. Detect and enable physical devices
            var devices = GetDevicesForTarget(target);
            if (devices.Count > 0)
            {
                var deviceResult = await _deviceController.EnableDevicesAsync(devices);
                if (!deviceResult.Success)
                {
                    Log.Warning("Some devices could not be enabled: Error={Error}", deviceResult.ErrorMessage);
                }
            }

            // 3. Update desired state
            var state = _stateStore.Load();
            if (target is BlockTarget.Camera or BlockTarget.Both)
                state.Camera = BlockStatus.Allowed;
            if (target is BlockTarget.Microphone or BlockTarget.Both)
                state.Microphone = BlockStatus.Allowed;
            _stateStore.Save(state);

            // 4. Reconcile and notify
            var blockState = GetCurrentState();
            StateChanged?.Invoke(blockState);

            sw.Stop();
            Log.Information("Unblock completed successfully: Target={Target}, DurationMs={DurationMs}",
                target, sw.ElapsedMilliseconds);

            return OperationResult.Ok();
        }
    }

    /// <summary>
    /// Toggles the blocking state. If currently blocked → unblock; if allowed → block.
    /// </summary>
    public async Task<OperationResult> ToggleAsync(BlockTarget target = BlockTarget.Both)
    {
        var currentState = GetCurrentState();
        bool shouldBlock = target switch
        {
            BlockTarget.Camera => currentState.Camera.EffectiveStatus != BlockStatus.Blocked,
            BlockTarget.Microphone => currentState.Microphone.EffectiveStatus != BlockStatus.Blocked,
            BlockTarget.Both => !currentState.AllBlocked,
            _ => true
        };

        return shouldBlock
            ? await BlockAsync(target)
            : await UnblockAsync(target);
    }

    /// <summary>
    /// Reads current actual state from system (registry + devices) and compares with desired state.
    /// </summary>
    public BlockState GetCurrentState()
    {
        var sw = Stopwatch.StartNew();

        var desired = _stateStore.Load();
        var cameraPolicyStatus = _policyManager.GetCameraPolicyStatus();
        var micPolicyStatus = _policyManager.GetMicrophonePolicyStatus();

        var cameras = _deviceDetector.DetectCameras();
        var microphones = _deviceDetector.DetectMicrophones();

        var cameraDeviceStatus = DetermineDeviceStatus(cameras);
        var micDeviceStatus = DetermineDeviceStatus(microphones);

        var state = new BlockState
        {
            Camera = new DeviceBlockState
            {
                DesiredStatus = desired.Camera,
                PolicyStatus = cameraPolicyStatus,
                DeviceStatus = cameraDeviceStatus
            },
            Microphone = new DeviceBlockState
            {
                DesiredStatus = desired.Microphone,
                PolicyStatus = micPolicyStatus,
                DeviceStatus = micDeviceStatus
            }
        };

        sw.Stop();
        Log.Debug("Reconciled state in {DurationMs}ms: Camera(desired={CD}, effective={CE}), Mic(desired={MD}, effective={ME})",
            sw.ElapsedMilliseconds,
            state.Camera.DesiredStatus, state.Camera.EffectiveStatus,
            state.Microphone.DesiredStatus, state.Microphone.EffectiveStatus);

        if (!state.IsFullyConsistent)
        {
            Log.Warning("State inconsistency detected: Camera(desired={CD}, effective={CE}), Mic(desired={MD}, effective={ME})",
                state.Camera.DesiredStatus, state.Camera.EffectiveStatus,
                state.Microphone.DesiredStatus, state.Microphone.EffectiveStatus);
        }

        return state;
    }

    public IReadOnlyList<DeviceInfo> GetDetectedDevices(BlockTarget target = BlockTarget.Both)
    {
        return GetDevicesForTarget(target);
    }

    private List<DeviceInfo> GetDevicesForTarget(BlockTarget target)
    {
        var devices = new List<DeviceInfo>();
        if (target is BlockTarget.Camera or BlockTarget.Both)
            devices.AddRange(_deviceDetector.DetectCameras());
        if (target is BlockTarget.Microphone or BlockTarget.Both)
            devices.AddRange(_deviceDetector.DetectMicrophones());
        return devices;
    }

    private static BlockStatus DetermineDeviceStatus(IReadOnlyList<DeviceInfo> devices)
    {
        if (devices.Count == 0) return BlockStatus.Unknown;
        var allEnabled = devices.All(d => d.IsEnabled);
        var allDisabled = devices.All(d => !d.IsEnabled);

        if (allDisabled) return BlockStatus.Blocked;
        if (allEnabled) return BlockStatus.Allowed;
        return BlockStatus.Unknown;
    }
}
