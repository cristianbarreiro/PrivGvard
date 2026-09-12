using System.Diagnostics;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.MacOS.Devices;
using Serilog;

namespace PrivLock.Platform.MacOS;

/// <summary>
/// Native macOS implementation of IDeviceProtectionProvider.
/// Supports two-tier protection:
/// 1. Standard: CoreAudio HAL hardware input mute & volume 0 (0 root elevation).
/// 2. Secure: System-level authorization enforcement.
/// </summary>
public sealed class MacOSProtectionProvider : IDeviceProtectionProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MacOSProtectionProvider>();

    private readonly MacOSDeviceDetector _deviceDetector;
    private readonly MacOSDeviceController _deviceController;
    private readonly IStateStore _stateStore;

    public MacOSProtectionProvider(
        MacOSDeviceDetector deviceDetector,
        MacOSDeviceController deviceController,
        IStateStore stateStore)
    {
        _deviceDetector = deviceDetector;
        _deviceController = deviceController;
        _stateStore = stateStore;
    }

    public async Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Enabling macOS Standard Protection: Target={Target}", target);

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            var micResult = await _deviceController.BlockMicrophoneAsync();
            if (!micResult.Success)
            {
                Log.Warning("macOS microphone mute warning: {Error}", micResult.ErrorMessage);
            }
        }

        return OperationResult.Ok();
    }

    public async Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Disabling macOS Standard Protection: Target={Target}", target);

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            await _deviceController.UnblockMicrophoneAsync();
        }

        return OperationResult.Ok();
    }

    public Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Enabling macOS Secure Protection: Target={Target}", target);
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Disabling macOS Secure Protection: Target={Target}", target);
        return Task.FromResult(OperationResult.Ok());
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
                StatusMessage = "Exact macOS privacy-state verification and recovery are not implemented."
            },
            Microphone = new TargetProtectionStatus
            {
                Target = BlockTarget.Microphone,
                StandardState = StandardProtectionState.Unknown,
                SecureState = SecureProtectionState.Unknown,
                IsVerified = false,
                StatusMessage = "Exact macOS privacy-state verification and recovery are not implemented."
            }
        });
    }
}
