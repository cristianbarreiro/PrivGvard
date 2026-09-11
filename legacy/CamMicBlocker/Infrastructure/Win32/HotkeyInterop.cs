using System.Runtime.InteropServices;

namespace CamMicBlocker.Infrastructure.Win32;

/// <summary>
/// P/Invoke declarations for global hotkey registration via user32.dll.
/// </summary>
internal static class HotkeyInterop
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Modifier keys
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes
    internal const uint VK_B = 0x42;

    // Window messages
    internal const int WM_HOTKEY = 0x0312;
}
