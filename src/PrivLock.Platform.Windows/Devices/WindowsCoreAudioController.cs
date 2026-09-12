using System.Runtime.InteropServices;
using PrivLock.Domain.Results;
using PrivLock.Infrastructure.Common.Logging;
using Serilog;

namespace PrivLock.Platform.Windows.Devices;

/// <summary>
/// Controls and queries individual Windows Core Audio capture endpoint mute states.
/// Endpoint-level operations ensure recovery never unmutes a microphone that was muted before PrivLock.
/// </summary>
public sealed class WindowsCoreAudioController
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsCoreAudioController>();
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const uint CLSCTX_ALL = 23;
    private const uint DEVICE_STATE_ACTIVE = 1;

    public WindowsAudioEndpointQueryResult GetMicrophoneMuteStates()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE_ACTIVE, out collection!);
            if (hr != 0 || collection == null)
                return QueryFailure($"Failed to enumerate capture devices. HR=0x{hr:X8}");

            hr = collection.GetCount(out var count);
            if (hr != 0)
                return QueryFailure($"Failed to count capture devices. HR=0x{hr:X8}");

            var states = new List<WindowsAudioEndpointMuteState>((int)count);
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                IAudioEndpointVolume? volume = null;
                try
                {
                    hr = collection.Item(index, out device!);
                    if (hr != 0 || device == null)
                        return QueryFailure($"Failed to open capture endpoint {index}. HR=0x{hr:X8}");

                    hr = device.GetId(out var endpointId);
                    if (hr != 0 || string.IsNullOrWhiteSpace(endpointId))
                        return QueryFailure($"Failed to read capture endpoint ID {index}. HR=0x{hr:X8}");

                    var iid = IID_IAudioEndpointVolume;
                    hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volumeObject);
                    if (hr != 0 || volumeObject is not IAudioEndpointVolume endpointVolume)
                    {
                        ReleaseComObject(volumeObject);
                        return QueryFailure($"Failed to activate capture endpoint {index}. HR=0x{hr:X8}");
                    }

                    volume = endpointVolume;
                    hr = volume.GetMute(out var isMuted);
                    if (hr != 0)
                        return QueryFailure($"Failed to query capture endpoint mute. HR=0x{hr:X8}");

                    states.Add(new WindowsAudioEndpointMuteState(endpointId, isMuted));
                }
                finally
                {
                    ReleaseComObject(volume);
                    ReleaseComObject(device);
                }
            }

            return WindowsAudioEndpointQueryResult.Ok(states);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Core Audio endpoint enumeration failed");
            CrashReporter.GenerateCrashReport(ex, "WindowsCoreAudioController.Query");
            return WindowsAudioEndpointQueryResult.Failed($"Core Audio query error: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(enumerator);
        }
    }

    public WindowsAudioEndpointLookupResult GetMicrophoneMuteState(string endpointId)
    {
        var query = GetMicrophoneMuteStates();
        if (!query.Success)
            return WindowsAudioEndpointLookupResult.Failed(query.ErrorMessage ?? "Core Audio query failed.");

        var match = query.States.FirstOrDefault(state =>
            string.Equals(state.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        return match == null
            ? WindowsAudioEndpointLookupResult.Missing()
            : WindowsAudioEndpointLookupResult.Found(match.IsMuted);
    }

    public OperationResult SetMicrophoneMute(string endpointId, bool mute)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        var mutationStarted = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDevice(endpointId, out device!);
            if (hr != 0 || device == null)
                return MutationFailure($"Audio capture endpoint is unavailable. HR=0x{hr:X8}");

            var iid = IID_IAudioEndpointVolume;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volumeObject);
            if (hr != 0 || volumeObject is not IAudioEndpointVolume endpointVolume)
            {
                ReleaseComObject(volumeObject);
                return MutationFailure($"Could not activate endpoint volume. HR=0x{hr:X8}");
            }

            volume = endpointVolume;
            var eventContext = Guid.Empty;
            mutationStarted = true;
            hr = volume.SetMute(mute, ref eventContext);
            if (hr != 0)
                return MutationFailure(
                    $"Could not set endpoint mute. HR=0x{hr:X8}",
                    outcomeUncertain: true);

            hr = volume.GetMute(out var verifiedMute);
            if (hr != 0 || verifiedMute != mute)
                return MutationFailure(
                    $"Endpoint mute verification failed. HR=0x{hr:X8}, Expected={mute}, Actual={verifiedMute}",
                    outcomeUncertain: true);

            return OperationResult.Ok([
                new DeviceOperationDetail
                {
                    DeviceId = endpointId,
                    FriendlyName = "Audio capture endpoint",
                    Success = true
                }
            ]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Core Audio endpoint mute operation failed");
            CrashReporter.GenerateCrashReport(ex, "WindowsCoreAudioController.SetMute");
            return OperationResult.Fail(
                $"Core Audio mute error: {ex.Message}",
                outcomeUncertain: mutationStarted);
        }
        finally
        {
            ReleaseComObject(volume);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    internal OperationResult SetMicrophonesMute(bool mute)
    {
        Log.Information("Setting Windows Core Audio capture endpoints mute={Mute}", mute);
        var query = GetMicrophoneMuteStates();
        if (!query.Success)
            return OperationResult.Fail(query.ErrorMessage ?? "Failed to enumerate capture endpoints.");

        var details = new List<DeviceOperationDetail>();
        foreach (var state in query.States)
        {
            var result = SetMicrophoneMute(state.EndpointId, mute);
            details.Add(new DeviceOperationDetail
            {
                DeviceId = state.EndpointId,
                FriendlyName = "Audio capture endpoint",
                Success = result.Success,
                ErrorMessage = result.ErrorMessage
            });
        }

        var failures = details.Where(detail => !detail.Success).ToList();
        return failures.Count == 0
            ? OperationResult.Ok(details)
            : OperationResult.Fail(string.Join("; ", failures.Select(failure => failure.ErrorMessage)), details);
    }

    public bool IsDefaultMicrophoneMuted()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out device!);
            if (hr != 0 || device == null)
            {
                Log.Warning("Failed to open default microphone. HR={Hr}", hr);
                return false;
            }

            var iid = IID_IAudioEndpointVolume;
            hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volumeObject);
            if (hr != 0 || volumeObject is not IAudioEndpointVolume endpointVolume)
            {
                ReleaseComObject(volumeObject);
                Log.Warning("Failed to activate default microphone volume. HR={Hr}", hr);
                return false;
            }

            volume = endpointVolume;
            hr = volume.GetMute(out var isMuted);
            if (hr != 0)
            {
                Log.Warning("Failed to query default microphone mute. HR={Hr}", hr);
                return false;
            }

            return isMuted;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query default microphone mute state");
            return false;
        }
        finally
        {
            ReleaseComObject(volume);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private static WindowsAudioEndpointQueryResult QueryFailure(string error)
    {
        CrashReporter.GenerateCrashReport(
            new InvalidOperationException(error),
            "WindowsCoreAudioController.Query");
        return WindowsAudioEndpointQueryResult.Failed(error);
    }

    private static OperationResult MutationFailure(string error, bool outcomeUncertain = false)
    {
        CrashReporter.GenerateCrashReport(
            new InvalidOperationException(error),
            "WindowsCoreAudioController.SetMute");
        return OperationResult.Fail(error, outcomeUncertain: outcomeUncertain);
    }

    #region COM Interop Definitions

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
        [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    #endregion
}

public sealed record WindowsAudioEndpointMuteState(string EndpointId, bool IsMuted);

public sealed record WindowsAudioEndpointQueryResult(bool Success, IReadOnlyList<WindowsAudioEndpointMuteState> States, string? ErrorMessage)
{
    public static WindowsAudioEndpointQueryResult Ok(IReadOnlyList<WindowsAudioEndpointMuteState> states) => new(true, states, null);
    public static WindowsAudioEndpointQueryResult Failed(string errorMessage) => new(false, [], errorMessage);
}

public sealed record WindowsAudioEndpointLookupResult(bool QuerySucceeded, bool IsPresent, bool IsMuted, string? ErrorMessage)
{
    public static WindowsAudioEndpointLookupResult Found(bool isMuted) => new(true, true, isMuted, null);
    public static WindowsAudioEndpointLookupResult Missing() => new(true, false, false, null);
    public static WindowsAudioEndpointLookupResult Failed(string errorMessage) => new(false, false, false, errorMessage);
}
