using System.Text.Json;
using PrivLock.Domain.Models;
using PrivLock.Domain.Results;
using PrivLock.Platform.Abstractions;
using PrivLock.Platform.Windows.Devices;
using PrivLock.Platform.Windows.Policies;
using PrivLock.Platform.Windows.Privileged;
using Serilog;

namespace PrivLock.Platform.Windows;

/// <summary>
/// Exact Windows state adapter for HKCU ConsentStore, Core Audio mute, HKLM AppPrivacy,
/// and Configuration Manager PnP nodes.
/// </summary>
public sealed class WindowsPrivacySessionPlatformAdapter : IPrivacySessionPlatformAdapter
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsPrivacySessionPlatformAdapter>();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WindowsDeviceDetector _deviceDetector;
    private readonly WindowsDeviceController _deviceController;
    private readonly WindowsCoreAudioController _coreAudioController;

    public bool SupportsPersistentRecovery => true;

    public Task<OperationResult> EnsureMutationQuiescenceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = WindowsPrivilegedSession.Instance.CloseSession();
        var remaining = WindowsPrivilegedExecutor.WaitForPreviousPrivilegedOperation(TimeSpan.FromSeconds(30));
        return Task.FromResult(remaining);
    }

    public WindowsPrivacySessionPlatformAdapter(
        WindowsDeviceDetector deviceDetector,
        WindowsDeviceController deviceController,
        WindowsCoreAudioController coreAudioController)
    {
        _deviceDetector = deviceDetector;
        _deviceController = deviceController;
        _coreAudioController = coreAudioController;
    }

    public async Task<IReadOnlyList<PrivacyResourceState>> CaptureAsync(
        ProtectionLayer layer,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var resources = new List<PrivacyResourceState>();

        if (layer == ProtectionLayer.Standard)
        {
            CaptureStandardRegistry(resources, target, operationId);
            if (target is BlockTarget.Microphone or BlockTarget.Both)
            {
                var endpointQuery = _coreAudioController.GetMicrophoneMuteStates();
                if (!endpointQuery.Success)
                    throw new InvalidOperationException(endpointQuery.ErrorMessage ?? "Could not snapshot Core Audio endpoints.");

                foreach (var endpoint in endpointQuery.States)
                {
                    var now = DateTimeOffset.UtcNow;
                    resources.Add(new OriginalAudioEndpointState
                    {
                        ResourceId = $"audio-mute:{endpoint.EndpointId}",
                        OperationId = operationId,
                        Layer = ProtectionLayer.Standard,
                        Target = BlockTarget.Microphone,
                        CapturedAtUtc = now,
                        LastUpdatedAtUtc = now,
                        EndpointId = endpoint.EndpointId,
                        OriginalMutedState = endpoint.IsMuted,
                        ProtectedMutedState = true
                    });
                }
            }
        }
        else
        {
            CaptureSecureRegistry(resources, target, operationId);
            await CaptureDevicesAsync(resources, target, operationId, cancellationToken);
        }

        return resources;
    }

    public async Task<PrivacyResourceObservation> ObserveAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resource is OriginalAudioEndpointState endpoint)
        {
            // A PnP capture endpoint can take a short time to re-register with Core Audio after
            // CM_Enable_DevNode. Retry only during restoration; a physically unplugged endpoint
            // remains Missing and durable in the journal after this bounded grace period.
            var attempts = endpoint.JournalState == PrivacyResourceJournalState.RestorePending ? 4 : 1;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var audioObservation = ObserveAudioEndpoint(endpoint);
                if (audioObservation.Kind != PrivacyResourceObservationKind.Missing || attempt == attempts)
                    return audioObservation;
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
        }

        return resource switch
        {
            OriginalPolicyState policy => WindowsRegistryValueCodec.Observe(policy),
            OriginalDeviceState device => ObserveDevice(device),
            _ => new PrivacyResourceObservation(
                PrivacyResourceObservationKind.Error,
                $"Unsupported Windows privacy resource type: {resource.GetType().Name}.")
        };
    }

    public async Task<PrivacyOwnershipAttestation> VerifyOwnershipAttestationAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = resource switch
        {
            OriginalPolicyState { RegistryHive: PrivacyRegistryHive.LocalMachine } => "verify-policy-ownership",
            OriginalDeviceState => "verify-device-ownership",
            _ => null
        };
        if (command == null)
            return new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Unsupported);

        var payload = EncodePayload(resource);
        var result = await WindowsPrivilegedExecutor.InvokeOnDemandElevationAsync(command, payload);
        if (result.Success)
            return new PrivacyOwnershipAttestation(PrivacyOwnershipAttestationKind.Confirmed);
        if (result.ErrorMessage?.StartsWith(
                WindowsPrivilegedExecutor.OwnershipNotConfirmedPrefix,
                StringComparison.Ordinal) == true)
        {
            return new PrivacyOwnershipAttestation(
                PrivacyOwnershipAttestationKind.NotConfirmed,
                result.ErrorMessage[WindowsPrivilegedExecutor.OwnershipNotConfirmedPrefix.Length..]);
        }

        return new PrivacyOwnershipAttestation(
            PrivacyOwnershipAttestationKind.Error,
            result.ErrorMessage ?? "Machine ownership attestation verification failed.");
    }

    public async Task<OperationResult> RestoreOriginalAsync(
        PrivacyResourceState resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return resource switch
        {
            OriginalPolicyState policy => await RestorePolicyAsync(policy),
            OriginalDeviceState device => await RestoreDeviceAsync(device),
            OriginalAudioEndpointState endpoint => RestoreAudioEndpoint(endpoint),
            _ => OperationResult.Fail($"Unsupported Windows privacy resource type: {resource.GetType().Name}.")
        };
    }

    public void CompleteRecoveryPass()
    {
        WindowsPrivilegedSession.Instance.CloseSession();
    }

    private static void CaptureStandardRegistry(
        ICollection<PrivacyResourceState> resources,
        BlockTarget target,
        string operationId)
    {
        if (target is BlockTarget.Camera or BlockTarget.Both)
        {
            resources.Add(CaptureConsentValue(
                WindowsUserPrivacyManager.ConsentStoreCameraPath,
                BlockTarget.Camera,
                operationId));
            resources.Add(CaptureConsentValue(
                WindowsUserPrivacyManager.ConsentStoreCameraNonPackagedPath,
                BlockTarget.Camera,
                operationId));
        }

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            resources.Add(CaptureConsentValue(
                WindowsUserPrivacyManager.ConsentStoreMicPath,
                BlockTarget.Microphone,
                operationId));
            resources.Add(CaptureConsentValue(
                WindowsUserPrivacyManager.ConsentStoreMicNonPackagedPath,
                BlockTarget.Microphone,
                operationId));
        }
    }

    private static OriginalPolicyState CaptureConsentValue(
        string path,
        BlockTarget target,
        string operationId) =>
        WindowsRegistryValueCodec.Capture(
            PrivacyRegistryHive.CurrentUser,
            PrivacyRegistryView.Default,
            path,
            WindowsUserPrivacyManager.ConsentValueName,
            ProtectionLayer.Standard,
            target,
            operationId,
            PrivacyRegistryValueKind.String,
            "Deny");

    private static void CaptureSecureRegistry(
        ICollection<PrivacyResourceState> resources,
        BlockTarget target,
        string operationId)
    {
        if (target is BlockTarget.Camera or BlockTarget.Both)
        {
            resources.Add(CaptureMachinePolicy(
                WindowsPolicyManager.CameraValueName,
                BlockTarget.Camera,
                operationId));
        }

        if (target is BlockTarget.Microphone or BlockTarget.Both)
        {
            resources.Add(CaptureMachinePolicy(
                WindowsPolicyManager.MicrophoneValueName,
                BlockTarget.Microphone,
                operationId));
        }
    }

    private static OriginalPolicyState CaptureMachinePolicy(
        string valueName,
        BlockTarget target,
        string operationId) =>
        WindowsRegistryValueCodec.Capture(
            PrivacyRegistryHive.LocalMachine,
            PrivacyRegistryView.Default,
            WindowsPolicyManager.PolicyRegistryPath,
            valueName,
            ProtectionLayer.Secure,
            target,
            operationId,
            PrivacyRegistryValueKind.DWord,
            WindowsPolicyManager.PolicyDeny.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

    private async Task CaptureDevicesAsync(
        ICollection<PrivacyResourceState> resources,
        BlockTarget target,
        string operationId,
        CancellationToken cancellationToken)
    {
        var devices = new List<DeviceInfo>();
        if (target is BlockTarget.Camera or BlockTarget.Both)
            devices.AddRange(await _deviceDetector.DetectCamerasAsync(cancellationToken));
        if (target is BlockTarget.Microphone or BlockTarget.Both)
            devices.AddRange(await _deviceDetector.DetectMicrophonesAsync(cancellationToken));

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeState = _deviceController.GetDeviceNodeState(device.Id);
            if (!nativeState.QuerySucceeded)
                throw new InvalidOperationException(nativeState.ErrorMessage ?? "Could not snapshot PnP device state.");
            if (!nativeState.IsPresent)
            {
                Log.Warning("Device disappeared while its reversible snapshot was being captured");
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var targetForDevice = device.DeviceType == DeviceType.Camera
                ? BlockTarget.Camera
                : BlockTarget.Microphone;
            resources.Add(new OriginalDeviceState
            {
                ResourceId = $"pnp:{device.Id}",
                OperationId = operationId,
                Layer = ProtectionLayer.Secure,
                Target = targetForDevice,
                CapturedAtUtc = now,
                LastUpdatedAtUtc = now,
                InstanceId = device.Id,
                FriendlyName = device.FriendlyName,
                DeviceClass = device.PlatformIdentifier ?? device.ClassName ?? device.DeviceType.ToString(),
                IsPresentAtCapture = true,
                OriginalEnabledState = nativeState.ProblemCode != CfgMgrInterop.CM_PROB_DISABLED,
                OriginalProblemCode = nativeState.ProblemCode,
                ProtectedEnabledState = false,
                IsSafelyRestorable = nativeState.ProblemCode is 0 or CfgMgrInterop.CM_PROB_DISABLED
            });
        }
    }

    private PrivacyResourceObservation ObserveDevice(OriginalDeviceState device)
    {
        var current = _deviceController.GetDeviceNodeState(device.InstanceId);
        if (!current.QuerySucceeded)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.Error, current.ErrorMessage);
        if (!current.IsPresent)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.Missing);
        if (current.ProblemCode == device.OriginalProblemCode)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
        if (current.ProblemCode == CfgMgrInterop.CM_PROB_DISABLED && !device.ProtectedEnabledState)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesProtected);
        return new PrivacyResourceObservation(
            PrivacyResourceObservationKind.Conflict,
            "PnP state differs from both the original and PrivLock-applied states.");
    }

    private PrivacyResourceObservation ObserveAudioEndpoint(OriginalAudioEndpointState endpoint)
    {
        var current = _coreAudioController.GetMicrophoneMuteState(endpoint.EndpointId);
        if (!current.QuerySucceeded)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.Error, current.ErrorMessage);
        if (!current.IsPresent)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.Missing);
        if (current.IsMuted == endpoint.OriginalMutedState)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesOriginal);
        if (current.IsMuted == endpoint.ProtectedMutedState)
            return new PrivacyResourceObservation(PrivacyResourceObservationKind.MatchesProtected);
        return new PrivacyResourceObservation(PrivacyResourceObservationKind.Conflict);
    }

    private async Task<OperationResult> RestorePolicyAsync(OriginalPolicyState policy)
    {
        if (policy.RegistryHive == PrivacyRegistryHive.CurrentUser)
            return WindowsRegistryValueCodec.CompareAndRestore(policy);

        var payload = EncodePayload(policy);
        return await WindowsPrivilegedExecutor.InvokeOnDemandElevationAsync("restore-policy", payload);
    }

    private async Task<OperationResult> RestoreDeviceAsync(OriginalDeviceState device)
    {
        if (!device.IsSafelyRestorable)
            return OperationResult.Ok();

        var payload = EncodePayload(device);
        return await WindowsPrivilegedExecutor.InvokeOnDemandElevationAsync("restore-device", payload);
    }

    private OperationResult RestoreAudioEndpoint(OriginalAudioEndpointState endpoint)
    {
        var observation = ObserveAudioEndpoint(endpoint);
        if (observation.Kind == PrivacyResourceObservationKind.MatchesOriginal)
            return OperationResult.Ok();
        if (observation.Kind != PrivacyResourceObservationKind.MatchesProtected)
            return OperationResult.Fail("Audio restore conflict: external state was preserved.");
        return _coreAudioController.SetMicrophoneMute(endpoint.EndpointId, endpoint.OriginalMutedState);
    }

    internal static string EncodePayload<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return Convert.ToBase64String(json);
    }

    internal static T? DecodePayload<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 64 * 1024)
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(payload), JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            Log.Warning(ex, "Rejected malformed privileged restore payload");
            return default;
        }
    }
}
