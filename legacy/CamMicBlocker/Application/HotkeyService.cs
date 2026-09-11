using System.Runtime.InteropServices;
using System.Windows.Interop;
using CamMicBlocker.Infrastructure.Win32;
using Serilog;

namespace CamMicBlocker.Application;

/// <summary>
/// Manages global hotkey registration for the application.
/// 
/// Uses a hidden WPF HwndSource to receive WM_HOTKEY messages.
/// The hotkey is registered via Win32 RegisterHotKey and does not
/// require administrator privileges.
/// 
/// Default hotkey: Ctrl + Alt + B
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<HotkeyService>();

    private const int HotkeyId = 9000;
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    /// <summary>Fired when the global hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    /// <summary>
    /// Registers the global hotkey. Must be called from the UI thread.
    /// </summary>
    /// <returns>True if registration succeeded, false if the hotkey is already taken.</returns>
    public bool Register()
    {
        if (_isRegistered)
        {
            Log.Warning("Hotkey already registered");
            return true;
        }

        try
        {
            // Create a hidden window to receive WM_HOTKEY messages
            var parameters = new HwndSourceParameters("CamMicBlocker_HotkeyWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0, // WS_OVERLAPPED = invisible
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);

            // Register Ctrl + Alt + B with MOD_NOREPEAT to avoid repeated events while held
            bool success = HotkeyInterop.RegisterHotKey(
                _hwndSource.Handle,
                HotkeyId,
                HotkeyInterop.MOD_CONTROL | HotkeyInterop.MOD_ALT | HotkeyInterop.MOD_NOREPEAT,
                HotkeyInterop.VK_B);

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                Log.Error("Failed to register hotkey Ctrl+Alt+B (Win32 error {ErrorCode}). " +
                          "Another application may have registered this combination.", error);
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.Dispose();
                _hwndSource = null;
                return false;
            }

            _isRegistered = true;
            Log.Information("Global hotkey Ctrl+Alt+B registered successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception while registering hotkey");
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotkeyInterop.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Log.Debug("Hotkey Ctrl+Alt+B pressed");
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwndSource != null)
        {
            if (_isRegistered)
            {
                HotkeyInterop.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
                _isRegistered = false;
                Log.Debug("Hotkey unregistered");
            }
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
