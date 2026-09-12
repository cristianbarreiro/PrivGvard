using CamMicBlocker.Domain.Interfaces;
using CamMicBlocker.Domain.Models;
using Microsoft.Win32;
using Serilog;

namespace CamMicBlocker.Infrastructure;

/// <summary>
/// Reads and writes Windows privacy policy registry values directly in-process.
/// 
/// Registry path: HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy
/// Values:
///   LetAppsAccessCamera = 2 → Deny camera access
///   LetAppsAccessMicrophone = 2 → Deny microphone access
///   (absent or 0) → Allow (user-controlled, default Windows behavior)
/// </summary>
public sealed class PolicyManager : IPolicyManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PolicyManager>();

    private const string PolicyRegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private const string CameraValueName = "LetAppsAccessCamera";
    private const string MicrophoneValueName = "LetAppsAccessMicrophone";
    private const int PolicyDeny = 2;

    public BlockStatus GetCameraPolicyStatus()
    {
        return ReadPolicyValue(CameraValueName);
    }

    public BlockStatus GetMicrophonePolicyStatus()
    {
        return ReadPolicyValue(MicrophoneValueName);
    }

    public Task<OperationResult> SetPolicyAsync(BlockTarget target, BlockStatus status)
    {
        Log.Information("Setting policy in-process: Target={Target}, Status={Status}", target, status);

        try
        {
            if (status == BlockStatus.Blocked)
            {
                using var key = Registry.LocalMachine.CreateSubKey(PolicyRegistryPath);
                if (key == null)
                    return Task.FromResult(OperationResult.Fail("Failed to create/open registry key"));

                if (target is BlockTarget.Camera or BlockTarget.Both)
                    key.SetValue(CameraValueName, PolicyDeny, RegistryValueKind.DWord);

                if (target is BlockTarget.Microphone or BlockTarget.Both)
                    key.SetValue(MicrophoneValueName, PolicyDeny, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath, writable: true);
                if (key != null)
                {
                    if (target is BlockTarget.Camera or BlockTarget.Both)
                        key.DeleteValue(CameraValueName, throwOnMissingValue: false);

                    if (target is BlockTarget.Microphone or BlockTarget.Both)
                        key.DeleteValue(MicrophoneValueName, throwOnMissingValue: false);
                }
            }

            Log.Debug("Policy updated successfully for {Target} -> {Status}", target, status);
            return Task.FromResult(OperationResult.Ok());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write registry policy for {Target}", target);
            return Task.FromResult(OperationResult.Fail($"Registry policy error: {ex.Message}"));
        }
    }

    private BlockStatus ReadPolicyValue(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath);
            if (key == null)
            {
                Log.Debug("Policy registry key not found — policies not set (Allowed)");
                return BlockStatus.Allowed;
            }

            var value = key.GetValue(valueName);
            if (value == null)
            {
                Log.Debug("Policy value {ValueName} not found — Allowed", valueName);
                return BlockStatus.Allowed;
            }

            var intValue = Convert.ToInt32(value);
            var status = intValue == PolicyDeny ? BlockStatus.Blocked : BlockStatus.Allowed;
            Log.Debug("Policy {ValueName} = {Value} → {Status}", valueName, intValue, status);
            return status;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read policy value {ValueName}", valueName);
            return BlockStatus.Unknown;
        }
    }
}
