using Microsoft.Win32;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Platform.Windows.Policies;

/// <summary>
/// Manages Windows AppPrivacy Group Policies in HKLM registry.
/// </summary>
public sealed class WindowsPolicyManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsPolicyManager>();

    internal const string PolicyRegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    internal const string CameraValueName = "LetAppsAccessCamera";
    internal const string MicrophoneValueName = "LetAppsAccessMicrophone";
    internal const int PolicyDeny = 2;

    public BlockStatus GetCameraPolicyStatus()
    {
        return ReadPolicyValue(CameraValueName);
    }

    public BlockStatus GetMicrophonePolicyStatus()
    {
        return ReadPolicyValue(MicrophoneValueName);
    }

    internal OperationResult SetPolicy(BlockTarget target, BlockStatus status)
    {
        Log.Information("Setting Windows AppPrivacy policy: Target={Target}, Status={Status}", target, status);

        try
        {
            if (status == BlockStatus.Blocked)
            {
                using var key = Registry.LocalMachine.CreateSubKey(PolicyRegistryPath);
                if (key == null)
                    return OperationResult.Fail("Failed to create/open registry key in HKLM.");

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

            Log.Debug("Windows policy updated successfully for {Target} -> {Status}", target, status);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write registry policy for {Target}", target);
            return OperationResult.Fail($"Registry policy error: {ex.Message}");
        }
    }

    private BlockStatus ReadPolicyValue(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath);
            if (key == null)
                return BlockStatus.Allowed;

            if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                return BlockStatus.Allowed;

            if (key.GetValueKind(valueName) != RegistryValueKind.DWord)
            {
                Log.Warning("Windows policy {ValueName} has a non-DWORD Registry type", valueName);
                return BlockStatus.Unknown;
            }

            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is int intValue
                ? intValue == PolicyDeny ? BlockStatus.Blocked : BlockStatus.Allowed
                : BlockStatus.Unknown;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read Windows policy value {ValueName}", valueName);
            return BlockStatus.Unknown;
        }
    }
}
