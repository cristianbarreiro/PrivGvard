using System.Runtime.InteropServices;

namespace PrivLock.Platform.MacOS.Devices;

/// <summary>
/// P/Invoke declarations for CoreAudio HAL on macOS.
/// Used to mute and control hardware audio input endpoints directly.
/// </summary>
internal static class CoreAudioInterop
{
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;
    }

    [DllImport(CoreAudioLib)]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        IntPtr outData);

    [DllImport(CoreAudioLib)]
    internal static extern int AudioObjectSetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        uint inDataSize,
        IntPtr inData);

    internal const uint kAudioObjectSystemObject = 1;
    internal const uint kAudioObjectPropertyScopeGlobal = 0x676c6f62; // 'glob'
    internal const uint kAudioObjectPropertyScopeInput = 0x696e7074;  // 'inpt'
    internal const uint kAudioObjectPropertyElementMain = 0;

    internal const uint kAudioHardwarePropertyDefaultInputDevice = 0x64496e20; // 'dIn '
    internal const uint kAudioDevicePropertyMute = 0x6d757465; // 'mute'
}
