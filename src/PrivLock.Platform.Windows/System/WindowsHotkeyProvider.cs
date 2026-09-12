using System.Runtime.InteropServices;
using PrivLock.Platform.Abstractions;
using Serilog;

namespace PrivLock.Platform.Windows.System;

/// <summary>
/// Registers and manages global system hotkeys on Windows via user32.dll.
/// </summary>
public sealed class WindowsHotkeyProvider : IGlobalHotkeyProvider
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsHotkeyProvider>();

    private const int HotkeyId = 9000;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_B = 0x42;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle = IntPtr.Zero;
    private bool _isRegistered;

    public bool IsRegistered => _isRegistered;

    public event Action? HotkeyPressed;

    /// <summary>
    /// Attaches to an existing native window handle to receive WM_HOTKEY messages.
    /// </summary>
    public void SetWindowHandle(IntPtr handle)
    {
        _windowHandle = handle;
    }

    public bool Register(string keyGesture = "Ctrl+Alt+B")
    {
        if (_isRegistered)
        {
            Log.Warning("Hotkey already registered");
            return true;
        }

        try
        {
            bool success = RegisterHotKey(
                _windowHandle,
                HotkeyId,
                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
                VK_B);

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                Log.Warning("Failed to register Windows global hotkey Ctrl+Alt+B (Win32 error {ErrorCode}). Another application may hold this shortcut.", error);
                return false;
            }

            _isRegistered = true;
            Log.Information("Windows global hotkey Ctrl+Alt+B registered successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception while registering Windows global hotkey");
            return false;
        }
    }

    public void HandleWindowMessage(int msg, IntPtr wParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Log.Debug("Windows global hotkey Ctrl+Alt+B triggered");
            HotkeyPressed?.Invoke();
        }
    }

    public void Unregister()
    {
        if (_isRegistered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _isRegistered = false;
            Log.Debug("Windows global hotkey unregistered");
        }
    }

    public void Dispose()
    {
        Unregister();
    }
}
