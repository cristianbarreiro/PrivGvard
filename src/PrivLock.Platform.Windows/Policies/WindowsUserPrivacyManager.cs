using Microsoft.Win32;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using Serilog;

namespace PrivLock.Platform.Windows.Policies;

/// <summary>
/// Manages user-level privacy permissions in the Windows Capability Consent Store (HKCU).
/// Enforces standard camera and microphone blocking on Windows without requiring administrative elevation.
/// </summary>
public sealed class WindowsUserPrivacyManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsUserPrivacyManager>();

    internal const string ConsentStoreCameraPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    internal const string ConsentStoreCameraNonPackagedPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged";
    internal const string ConsentStoreMicPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    internal const string ConsentStoreMicNonPackagedPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";
    internal const string ConsentValueName = "Value";

    internal OperationResult SetCameraUserPrivacy(BlockStatus status)
    {
        Log.Information("Setting Windows user privacy for Camera: Status={Status}", status);

        try
        {
            var isBlocked = status == BlockStatus.Blocked;
            var consentValue = isBlocked ? "Deny" : "Allow";

            // 1. Consent Store (UWP / Windows Camera App / Packaged Apps)
            SetRegistryStringValue(Registry.CurrentUser, ConsentStoreCameraPath, ConsentValueName, consentValue);

            // 2. Consent Store NonPackaged (Desktop Apps: Chrome, Firefox, Zoom, Teams, OBS, etc.)
            SetRegistryStringValue(Registry.CurrentUser, ConsentStoreCameraNonPackagedPath, ConsentValueName, consentValue);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update Windows Camera user privacy in ConsentStore");
            return OperationResult.Fail($"Camera user privacy error: {ex.Message}");
        }
    }

    internal OperationResult SetMicrophoneUserPrivacy(BlockStatus status)
    {
        Log.Information("Setting Windows user privacy for Microphone: Status={Status}", status);

        try
        {
            var isBlocked = status == BlockStatus.Blocked;
            var consentValue = isBlocked ? "Deny" : "Allow";

            // 1. Consent Store (UWP / Voice Recorder / Packaged Apps)
            SetRegistryStringValue(Registry.CurrentUser, ConsentStoreMicPath, ConsentValueName, consentValue);

            // 2. Consent Store NonPackaged (Desktop Apps: Chrome, Zoom, Teams, Discord, etc.)
            SetRegistryStringValue(Registry.CurrentUser, ConsentStoreMicNonPackagedPath, ConsentValueName, consentValue);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update Windows Microphone user privacy in ConsentStore");
            return OperationResult.Fail($"Microphone user privacy error: {ex.Message}");
        }
    }

    public BlockStatus GetCameraUserPrivacyStatus()
    {
        return ReadCombinedConsentStatus(
            ConsentStoreCameraPath,
            ConsentStoreCameraNonPackagedPath,
            "Camera");
    }

    public BlockStatus GetMicrophoneUserPrivacyStatus()
    {
        return ReadCombinedConsentStatus(
            ConsentStoreMicPath,
            ConsentStoreMicNonPackagedPath,
            "Microphone");
    }

    private static BlockStatus ReadCombinedConsentStatus(
        string packagedPath,
        string nonPackagedPath,
        string targetName)
    {
        try
        {
            var packaged = ReadConsentValue(packagedPath);
            var nonPackaged = ReadConsentValue(nonPackagedPath);
            if (packaged == BlockStatus.Blocked && nonPackaged == BlockStatus.Blocked)
                return BlockStatus.Blocked;
            if (packaged == BlockStatus.Allowed && nonPackaged == BlockStatus.Allowed)
                return BlockStatus.Allowed;

            Log.Warning(
                "Windows {Target} ConsentStore values disagree; protection state is partial/unknown",
                targetName);
            return BlockStatus.Unknown;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read Windows {Target} user privacy status", targetName);
            return BlockStatus.Unknown;
        }
    }

    private static BlockStatus ReadConsentValue(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key == null || !key.GetValueNames().Contains(ConsentValueName, StringComparer.OrdinalIgnoreCase))
            return BlockStatus.Allowed;
        if (key.GetValueKind(ConsentValueName) != RegistryValueKind.String)
            return BlockStatus.Unknown;

        var value = key?.GetValue(ConsentValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string stringValue)
            return BlockStatus.Unknown;
        if (string.Equals(stringValue, "Deny", StringComparison.OrdinalIgnoreCase))
            return BlockStatus.Blocked;
        if (string.Equals(stringValue, "Allow", StringComparison.OrdinalIgnoreCase))
            return BlockStatus.Allowed;
        return BlockStatus.Unknown;
    }

    private static void SetRegistryStringValue(RegistryKey root, string subKeyPath, string valueName, string value)
    {
        using var key = root.CreateSubKey(subKeyPath, writable: true)
            ?? throw new IOException($"Could not create/open Registry key '{subKeyPath}'.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }
}
