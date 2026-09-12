using System.Diagnostics;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Linux.Devices;
using Serilog;

namespace PrivLock.Platform.Linux;

/// <summary>
/// Native Linux implementation of IDeviceProtectionProvider.
/// Supports two-tier protection:
/// 1. Standard: PipeWire (wpctl) and PulseAudio (pactl) audio server source mute (0 root elevation).
/// 2. Secure: V4L2 device node permission revocation (/dev/video*) & driver unbind (on-demand polkit).
/// </summary>
public sealed class LinuxProtectionProvider : IDeviceProtectionProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxProtectionProvider>();

    private readonly LinuxDeviceDetector _deviceDetector;
    private readonly LinuxDeviceController _deviceController;
    private readonly IStateStore _stateStore;

    public LinuxProtectionProvider(
        LinuxDeviceDetector deviceDetector,
        LinuxDeviceController deviceController,
        IStateStore stateStore)
    {
        _deviceDetector = deviceDetector;
        _deviceController = deviceController;
        _stateStore = stateStore;
    }

    public async Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Enabling Linux Standard Protection: Target={Target}", target);

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            var micResult = await _deviceController.BlockMicrophoneAsync();
            if (!micResult.Success)
            {
                Log.Warning("Linux microphone mute returned warning: {Error}", micResult.ErrorMessage);
            }
        }

        return OperationResult.Ok();
    }

    public async Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Disabling Linux Standard Protection: Target={Target}", target);

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            await _deviceController.UnblockMicrophoneAsync();
        }

        return OperationResult.Ok();
    }

    public async Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Enabling Linux Secure Protection (V4L2 node lockdown): Target={Target}", target);

        if (target is BlockTarget.Camera or BlockTarget.Both)
        {
            var cameras = await _deviceDetector.DetectCamerasAsync(cancellationToken);
            var camResult = await _deviceController.BlockCameraAsync(cameras);
            if (!camResult.Success)
            {
                return camResult;
            }
        }

        return OperationResult.Ok();
    }

    public async Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Disabling Linux Secure Protection: Target={Target}", target);

        if (target is BlockTarget.Camera or BlockTarget.Both)
        {
            var cameras = await _deviceDetector.DetectCamerasAsync(cancellationToken);
            await _deviceController.UnblockCameraAsync(cameras);
        }

        return OperationResult.Ok();
    }

    public Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new FullProtectionState
        {
            Camera = new TargetProtectionStatus
            {
                Target = BlockTarget.Camera,
                StandardState = StandardProtectionState.Unknown,
                SecureState = SecureProtectionState.Unknown,
                IsVerified = false,
                StatusMessage = "Exact Linux privacy-state verification and recovery are not implemented."
            },
            Microphone = new TargetProtectionStatus
            {
                Target = BlockTarget.Microphone,
                StandardState = StandardProtectionState.Unknown,
                SecureState = SecureProtectionState.Unknown,
                IsVerified = false,
                StatusMessage = "Exact Linux privacy-state verification and recovery are not implemented."
            }
        });
    }
}
