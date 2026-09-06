using System.Diagnostics;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Elevation;
using PrivLock.Platform.Windows.Policies;
using PrivLock.Platform.Windows.Privileged;
using Serilog;

namespace PrivLock.Platform.Windows;

/// <summary>
/// Native Windows implementation of IDeviceProtectionProvider.
/// Supports clean two-tier protection:
/// 1. Standard Protection: HKCU Capability Consent Store (Packaged & NonPackaged) + Windows Core Audio WASAPI Mute (0 elevation).
/// 2. Secure Protection: HKLM Group Policy & CfgMgr32 PnP hardware device node disable (on-demand UAC elevation, single prompt).
/// </summary>
public sealed class WindowsProtectionProvider : IDeviceProtectionProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsProtectionProvider>();

    private readonly WindowsDeviceDetector _deviceDetector;
    private readonly WindowsDeviceController _deviceController;
    private readonly WindowsCoreAudioController _coreAudioController;
    private readonly WindowsPolicyManager _policyManager;
    private readonly WindowsUserPrivacyManager _userPrivacyManager;
    private readonly IElevationProvider _elevationProvider;
    private readonly IPrivacySessionStore _privacySessionStore;

    public WindowsProtectionProvider(
        WindowsDeviceDetector deviceDetector,
        WindowsDeviceController deviceController,
        WindowsCoreAudioController coreAudioController,
        WindowsPolicyManager policyManager,
        WindowsUserPrivacyManager userPrivacyManager,
        IElevationProvider elevationProvider,
        IPrivacySessionStore privacySessionStore)
    {
        _deviceDetector = deviceDetector;
        _deviceController = deviceController;
        _coreAudioController = coreAudioController;
        _policyManager = policyManager;
        _userPrivacyManager = userPrivacyManager;
        _elevationProvider = elevationProvider;
        _privacySessionStore = privacySessionStore;
    }

    public Task<OperationResult> EnableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Information("Enabling Windows Standard Protection: Target={Target}", target);
        var details = new List<DeviceOperationDetail>();
        try
        {
            var session = LoadTrackedSession();

            // Apply each captured value independently. CompareAndApplyProtected repeats the
            // comparison immediately before the native write.
            foreach (var policy in GetTrackedResources<OriginalPolicyState>(
                         session, ProtectionLayer.Standard, target).Where(resource => resource.RequiresModification))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = WindowsRegistryValueCodec.CompareAndApplyProtected(policy);
                details.Add(ToResourceDetail(policy, result, "Windows privacy consent value"));
                if (!result.Success)
                    return Task.FromResult(FailForResource(result, details));
            }

            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                foreach (var endpoint in GetTrackedResources<OriginalAudioEndpointState>(
                             session,
                             ProtectionLayer.Standard,
                             BlockTarget.Microphone).Where(resource => resource.RequiresModification))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = CompareAndMuteEndpoint(endpoint);
                    details.Add(ToResourceDetail(endpoint, result, "Audio capture endpoint"));
                    if (!result.Success)
                        return Task.FromResult(FailForResource(result, details));
                }
            }

            return Task.FromResult(OperationResult.Ok(details));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(OperationResult.Fail("The Standard protection operation was cancelled.", details));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected Standard protection failure after {CompletedCount} resource result(s)", details.Count);
            return Task.FromResult(OperationResult.Fail(
                $"Unexpected Standard protection failure: {ex.Message}",
                details,
                outcomeUncertain: true));
        }
    }

    public Task<OperationResult> DisableStandardProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        Log.Error("Rejected non-reversible Windows Standard teardown for Target={Target}", target);
        return Task.FromResult(OperationResult.Fail(
            "Windows protection must be disabled through PrivacySessionService so exact original state is restored."));
    }

    public async Task<OperationResult> EnableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log.Information("Enabling Windows Secure Protection: Target={Target}, IsElevated={IsElevated}",
            target, _elevationProvider.IsElevated);

        var details = new List<DeviceOperationDetail>();
        try
        {
            var session = LoadTrackedSession();
            foreach (var policy in GetTrackedResources<OriginalPolicyState>(
                         session, ProtectionLayer.Secure, target).Where(resource => resource.RequiresModification))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteSecureResourceAsync("apply-policy", policy);
                details.Add(ToResourceDetail(policy, result, "Windows machine privacy policy"));
                if (!result.Success)
                    return FailForResource(result, details);
            }

            foreach (var device in GetTrackedResources<OriginalDeviceState>(
                         session, ProtectionLayer.Secure, target).Where(resource => resource.RequiresModification))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteSecureResourceAsync("apply-device", device);
                details.Add(ToResourceDetail(device, result, device.FriendlyName));
                if (!result.Success)
                    return FailForResource(result, details);
            }

            sw.Stop();
            Log.Information("Windows Secure Protection enabled in {DurationMs}ms", sw.ElapsedMilliseconds);
            return OperationResult.Ok(details);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail("The Secure protection operation was cancelled.", details);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected Secure protection failure after {CompletedCount} resource result(s)", details.Count);
            return OperationResult.Fail(
                $"Unexpected Secure protection failure: {ex.Message}",
                details,
                outcomeUncertain: true);
        }
    }

    public async Task<OperationResult> DisableSecureProtectionAsync(BlockTarget target, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        Log.Error("Rejected non-reversible Windows Secure teardown for Target={Target}", target);
        return OperationResult.Fail(
            "Windows protection must be disabled through PrivacySessionService so exact original state is restored.");
    }

    public async Task<FullProtectionState> GetProtectionStateAsync(CancellationToken cancellationToken = default)
    {
        // Derive every displayed state from current Windows state. DesiredState is a preference,
        // never proof that a privacy mechanism is effectively active.
        var camUserPrivacy = _userPrivacyManager.GetCameraUserPrivacyStatus();
        var micUserPrivacy = _userPrivacyManager.GetMicrophoneUserPrivacyStatus();
        var camPolicy = _policyManager.GetCameraPolicyStatus();
        var micPolicy = _policyManager.GetMicrophonePolicyStatus();
        var microphoneEndpoints = _coreAudioController.GetMicrophoneMuteStates();

        var cameras = await _deviceDetector.DetectCamerasAsync(cancellationToken);
        var mics = await _deviceDetector.DetectMicrophonesAsync(cancellationToken);

        var camDeviceStateKnown = cameras.All(IsCleanOrExplicitlyDisabled);
        var micDeviceStateKnown = mics.All(IsCleanOrExplicitlyDisabled);
        var camDeviceBlocked = cameras.Count > 0 && cameras.All(device =>
            device.ConfigurationProblemCode == CfgMgrInterop.CM_PROB_DISABLED);
        var micDeviceBlocked = mics.Count > 0 && mics.All(device =>
            device.ConfigurationProblemCode == CfgMgrInterop.CM_PROB_DISABLED);
        var camDevicesEnabled = cameras.Count > 0 && cameras.All(device =>
            device.ConfigurationProblemCode == 0);
        var micDevicesEnabled = mics.Count > 0 && mics.All(device =>
            device.ConfigurationProblemCode == 0);

        var camSecureEvaluation = EvaluateSecureState(
            camPolicy, cameras.Count, camDeviceStateKnown, camDeviceBlocked, camDevicesEnabled);
        var micSecureEvaluation = EvaluateSecureState(
            micPolicy, mics.Count, micDeviceStateKnown, micDeviceBlocked, micDevicesEnabled);

        var camStandardState = ToStandardState(camUserPrivacy);
        var micStandardState = ToStandardState(EvaluateMicrophoneStandardStatus(
            micUserPrivacy,
            microphoneEndpoints.Success,
            microphoneEndpoints.States));
        var camSecureState = ToSecureState(
            camSecureEvaluation.IsActive, camSecureEvaluation.IsUnknown, camStandardState);
        var micSecureState = ToSecureState(
            micSecureEvaluation.IsActive, micSecureEvaluation.IsUnknown, micStandardState);

        return new FullProtectionState
        {
            Camera = new TargetProtectionStatus
            {
                Target = BlockTarget.Camera,
                StandardState = camStandardState,
                SecureState = camSecureState,
                IsVerified = camStandardState != StandardProtectionState.Unknown &&
                             camSecureState != SecureProtectionState.Unknown,
                StatusMessage = camStandardState == StandardProtectionState.Unknown ||
                                camSecureState == SecureProtectionState.Unknown
                    ? "Windows camera protection state is partial or could not be fully verified."
                    : null
            },
            Microphone = new TargetProtectionStatus
            {
                Target = BlockTarget.Microphone,
                StandardState = micStandardState,
                SecureState = micSecureState,
                IsVerified = micStandardState != StandardProtectionState.Unknown &&
                             micSecureState != SecureProtectionState.Unknown,
                StatusMessage = micStandardState == StandardProtectionState.Unknown ||
                                micSecureState == SecureProtectionState.Unknown
                    ? "Windows microphone protection state is partial or could not be fully verified."
                    : null
            }
        };
    }

    private static StandardProtectionState ToStandardState(BlockStatus status) => status switch
    {
        BlockStatus.Blocked => StandardProtectionState.Active,
        BlockStatus.Allowed => StandardProtectionState.Inactive,
        _ => StandardProtectionState.Unknown
    };

    private static bool IsCleanOrExplicitlyDisabled(DeviceInfo device) =>
        device.IsStateKnown &&
        device.ConfigurationProblemCode is 0 or CfgMgrInterop.CM_PROB_DISABLED;

    internal static BlockStatus EvaluateMicrophoneStandardStatus(
        BlockStatus consentStatus,
        bool endpointQuerySucceeded,
        IReadOnlyList<WindowsAudioEndpointMuteState> endpoints)
    {
        if (consentStatus == BlockStatus.Unknown || !endpointQuerySucceeded || endpoints.Count == 0)
            return BlockStatus.Unknown;

        var allMuted = endpoints.All(endpoint => endpoint.IsMuted);
        var allUnmuted = endpoints.All(endpoint => !endpoint.IsMuted);
        if (consentStatus == BlockStatus.Blocked && allMuted)
            return BlockStatus.Blocked;
        if (consentStatus == BlockStatus.Allowed && allUnmuted)
            return BlockStatus.Allowed;
        return BlockStatus.Unknown;
    }

    internal static (bool IsActive, bool IsUnknown) EvaluateSecureState(
        BlockStatus policyStatus,
        int detectedDeviceCount,
        bool deviceStateKnown,
        bool allDevicesDisabled,
        bool allDevicesEnabled)
    {
        if (policyStatus == BlockStatus.Unknown || !deviceStateKnown || detectedDeviceCount == 0)
            return (false, true);

        if (policyStatus == BlockStatus.Blocked && allDevicesDisabled)
            return (true, false);

        if (policyStatus == BlockStatus.Allowed && allDevicesEnabled)
            return (false, false);

        // One mechanism being active is only a partial block. Never expose it as verified Secure.
        return (false, true);
    }

    private static SecureProtectionState ToSecureState(
        bool isActive,
        bool isUnknown,
        StandardProtectionState standardState)
    {
        if (isActive)
            return SecureProtectionState.Active;
        if (isUnknown)
            return SecureProtectionState.Unknown;
        return standardState == StandardProtectionState.Active
            ? SecureProtectionState.Available
            : SecureProtectionState.Unavailable;
    }

    private OperationResult CompareAndMuteEndpoint(OriginalAudioEndpointState endpoint)
    {
        var current = _coreAudioController.GetMicrophoneMuteState(endpoint.EndpointId);
        if (!current.QuerySucceeded)
            return OperationResult.Fail(current.ErrorMessage ?? "Could not inspect the audio capture endpoint.");
        if (!current.IsPresent)
            return OperationResult.Fail("Audio capture endpoint disappeared after its snapshot was committed.");
        if (current.IsMuted == endpoint.ProtectedMutedState)
        {
            return endpoint.JournalState == PrivacyResourceJournalState.Applied &&
                   endpoint.ModifiedByPrivLock &&
                   !endpoint.OwnershipUncertain
                ? OperationResult.Ok()
                : OperationResult.Fail(
                    "Audio apply conflict: endpoint reached the protected state after snapshot and was preserved as externally owned.");
        }
        if (current.IsMuted != endpoint.OriginalMutedState)
            return OperationResult.Fail("Audio apply conflict: endpoint state changed after snapshot.");

        return _coreAudioController.SetMicrophoneMute(endpoint.EndpointId, endpoint.ProtectedMutedState);
    }

    private async Task<OperationResult> ExecuteSecureResourceAsync<T>(string command, T resource)
    {
        var payload = WindowsPrivacySessionPlatformAdapter.EncodePayload(resource);
        return await WindowsPrivilegedExecutor.InvokeOnDemandElevationAsync(command, payload);
    }

    private static DeviceOperationDetail ToResourceDetail(
        PrivacyResourceState resource,
        OperationResult result,
        string friendlyName) =>
        new()
        {
            DeviceId = resource.ResourceId,
            FriendlyName = friendlyName,
            Success = result.Success,
            OutcomeUncertain = result.OutcomeUncertain,
            ExecutionStillInFlight = result.ExecutionStillInFlight,
            ErrorMessage = result.ErrorMessage
        };

    private static OperationResult FailForResource(
        OperationResult result,
        IReadOnlyList<DeviceOperationDetail> details) =>
        OperationResult.Fail(
            result.ErrorMessage ?? "A Windows privacy resource could not be protected.",
            details,
            result.OutcomeUncertain,
            result.ExecutionStillInFlight);

    private PrivacySession LoadTrackedSession()
    {
        var session = _privacySessionStore.Load()
            ?? throw new InvalidOperationException("No durable privacy session exists for this Windows mutation.");
        if (!session.IsActive || string.IsNullOrWhiteSpace(session.LastOperationId))
            throw new InvalidOperationException("The privacy session is not active or has no prepared operation.");
        return session;
    }

    private static IReadOnlyList<T> GetTrackedResources<T>(
        PrivacySession session,
        ProtectionLayer layer,
        BlockTarget target)
        where T : PrivacyResourceState
    {
        return session.Resources
            .OfType<T>()
            .Where(resource => resource.Layer == layer)
            .Where(resource => target == BlockTarget.Both || resource.Target == target)
            .Where(resource => string.Equals(resource.OperationId, session.LastOperationId, StringComparison.Ordinal))
            .ToList();
    }
}
