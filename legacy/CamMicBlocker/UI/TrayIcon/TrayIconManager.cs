using System.Drawing;
using System.Windows.Forms;
using CamMicBlocker.Application;
using CamMicBlocker.Domain.Models;
using Serilog;

namespace CamMicBlocker.UI.TrayIcon;

/// <summary>
/// Manages the system tray icon, context menu, and tooltip with localized labels.
/// Renders distinct OPEN/UNLOCKED padlock vs CLOSED/LOCKED padlock icons in GDI+.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TrayIconManager>();

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly BlockingService _blockingService;
    private readonly StartupService _startupService;
    private readonly LanguageService _languageService;

    // Menu items
    private readonly ToolStripMenuItem _showAppItem;
    private readonly ToolStripMenuItem _hideAppItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _exitItem;

    // Tray icons (tracked for disposal)
    private Icon? _greenIcon;
    private Icon? _redIcon;
    private Icon? _yellowIcon;
    private Icon? _trayIcon;

    // Menu item icons (tracked for disposal)
    private Image? _showAppIcon;
    private Image? _hideAppIcon;
    private Image? _lockIcon;
    private Image? _unlockIcon;
    private Image? _exitIcon;

    private BlockState? _lastState;

    /// <summary>Fired when the user clicks Show Application.</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>Fired when the user clicks Hide Application.</summary>
    public event Action? HideMainWindowRequested;

    /// <summary>Fired when the user clicks Exit Application.</summary>
    public event Action? ExitRequested;

    public TrayIconManager(BlockingService blockingService, StartupService startupService, LanguageService languageService)
    {
        _blockingService = blockingService;
        _startupService = startupService;
        _languageService = languageService;

        // Generate tray icons: Green = Unlocked Padlock (Open), Red = Locked Padlock (Closed)
        _trayIcon = LoadTrayIcon();
        _greenIcon = CreatePadlockIcon(Color.SeaGreen, isLocked: false);
        _redIcon = CreatePadlockIcon(Color.IndianRed, isLocked: true);
        _yellowIcon = CreatePadlockIcon(Color.Goldenrod, isLocked: false);

        // Generate menu item icons (Segoe MDL2 Assets)
        _showAppIcon = CreateMDL2Icon("\uE8A7", Color.White, 16);  // Window icon
        _hideAppIcon = CreateMDL2Icon("\uE921", Color.White, 16);  // ChromeMinimize icon
        _lockIcon = CreateMDL2Icon("\uE72E", Color.White, 16);     // Closed Lock icon
        _unlockIcon = CreateMDL2Icon("\uE785", Color.White, 16);   // Open Lock icon
        _exitIcon = CreateMDL2Icon("\uE8BB", Color.White, 16);     // ChromeClose icon

        // Build context menu with Fluent Dark renderer
        _contextMenu = new ContextMenuStrip
        {
            Renderer = new FluentDarkRenderer(),
            ImageScalingSize = new Size(16, 16),
            BackColor = Color.FromArgb(37, 37, 40), // #252528
            ForeColor = Color.White,
            Padding = new Padding(2),
            ShowImageMargin = true,
            ShowCheckMargin = false
        };

        // 1. Show Application
        _showAppItem = new ToolStripMenuItem
        {
            Text = _languageService.GetString("TrayShowApp", "Show Application"),
            Image = _showAppIcon,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        _showAppItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();
        _contextMenu.Items.Add(_showAppItem);

        // 2. Hide Application
        _hideAppItem = new ToolStripMenuItem
        {
            Text = _languageService.GetString("TrayHideApp", "Hide Application"),
            Image = _hideAppIcon,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
        _hideAppItem.Click += (_, _) => HideMainWindowRequested?.Invoke();
        _contextMenu.Items.Add(_hideAppItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 3. Lock / Unlock (Toggle Both)
        _toggleItem = new ToolStripMenuItem
        {
            Text = _languageService.GetString("TrayLockUnlock", "Lock / Unlock"),
            Image = _lockIcon,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
        _toggleItem.Click += async (_, _) => await SafeExecuteAsync(() => _blockingService.ToggleAsync(BlockTarget.Both));
        _contextMenu.Items.Add(_toggleItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 4. Exit Application
        _exitItem = new ToolStripMenuItem
        {
            Text = _languageService.GetString("TrayExit", "Exit Application"),
            Image = _exitIcon,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _contextMenu.Items.Add(_exitItem);

        // Create NotifyIcon
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon ?? _greenIcon,
            Text = "CamMicBlocker",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        // Left click opens application window
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainWindowRequested?.Invoke();
            }
        };

        // Double click also opens application window
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();

        // Listen for state and language changes
        _blockingService.StateChanged += OnStateChanged;
        _languageService.LanguageChanged += OnLanguageChanged;

        Log.Information("TrayIconManager initialized");
    }

    public void UpdateState(BlockState state)
    {
        OnStateChanged(state);
    }

    private void OnLanguageChanged(string langCode)
    {
        _showAppItem.Text = _languageService.GetString("TrayShowApp", "Show Application");
        _hideAppItem.Text = _languageService.GetString("TrayHideApp", "Hide Application");
        _exitItem.Text = _languageService.GetString("TrayExit", "Exit Application");
        
        if (_lastState != null)
        {
            OnStateChanged(_lastState);
        }
    }

    private void OnStateChanged(BlockState state)
    {
        _lastState = state;

        if (state.AllBlocked)
        {
            _notifyIcon.Icon = _redIcon;
            _notifyIcon.Text = $"CamMicBlocker — {_languageService.GetString("NotifyBothBlocked", "Camera & Microphone: BLOCKED")}";
            _toggleItem.Text = _languageService.GetString("TrayUnlockBoth", "Unlock (Both)");
            _toggleItem.Image = _unlockIcon;
        }
        else if (state.AllAllowed)
        {
            _notifyIcon.Icon = _greenIcon;
            _notifyIcon.Text = $"CamMicBlocker — {_languageService.GetString("NotifyBothAllowed", "Camera & Microphone: ALLOWED")}";
            _toggleItem.Text = _languageService.GetString("TrayLockBoth", "Lock (Both)");
            _toggleItem.Image = _lockIcon;
        }
        else
        {
            _notifyIcon.Icon = _yellowIcon;
            _notifyIcon.Text = $"CamMicBlocker — {_languageService.GetString("MixedState", "Mixed state")}";
            _toggleItem.Text = _languageService.GetString("TrayLockUnlock", "Lock / Unlock");
            _toggleItem.Image = _lockIcon;
        }

        Log.Debug("Tray UI updated: Camera={CameraState}, Mic={MicState}",
            state.Camera.EffectiveStatus, state.Microphone.EffectiveStatus);
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(2000, title, message, icon);
    }

    private async Task SafeExecuteAsync(Func<Task<Domain.Interfaces.OperationResult>> action)
    {
        try
        {
            var result = await action();
            if (!result.Success)
            {
                ShowNotification("CamMicBlocker", $"Operation failed: {result.ErrorMessage}", ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing tray action");
            ShowNotification("CamMicBlocker", $"Error: {ex.Message}", ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Creates a Segoe MDL2 Assets icon rendered as an image for use in ToolStripMenuItem.
    /// Properly manages GDI resources.
    /// </summary>
    /// <param name="glyph">MDL2 glyph character (e.g., "\uE8A7" for Window)</param>
    /// <param name="color">Icon color</param>
    /// <param name="size">Icon size in pixels</param>
    /// <returns>Rendered icon as Image</returns>
    private static Image CreateMDL2Icon(string glyph, Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using var font = new Font("Segoe MDL2 Assets", size * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);

            // Center the glyph
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            g.DrawString(glyph, font, brush, size / 2f, size / 2f, sf);
        }

        return bmp;
    }

    /// <summary>
    /// Creates a padlock icon in memory. If isLocked is false, renders an open/unlocked shackle.
    /// Includes keyhole detail and proper GDI cleanup.
    /// </summary>
    private static Icon CreatePadlockIcon(Color color, bool isLocked)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 3);

        // Padlock main body
        g.FillRectangle(brush, 6, 14, 20, 15);

        // Keyhole detail (dark inner cutout)
        using var darkBrush = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
        g.FillEllipse(darkBrush, 14, 18, 4, 4);
        g.FillRectangle(darkBrush, 15, 21, 2, 4);

        if (isLocked)
        {
            // Closed shackle (locked padlock)
            g.DrawArc(pen, 9, 5, 14, 14, 180, 180);
            g.DrawLine(pen, 9, 12, 9, 14);
            g.DrawLine(pen, 23, 12, 23, 14);
        }
        else
        {
            // Open shackle (unlocked padlock): shifted up and unhooked on right side
            g.DrawArc(pen, 9, 1, 14, 14, 180, 180);
            g.DrawLine(pen, 9, 8, 9, 14);     // Attached on left side
            g.DrawLine(pen, 23, 8, 23, 10);   // Open gap on right side!
        }

        var hIcon = bmp.GetHicon();
        var tempIcon = Icon.FromHandle(hIcon);
        var icon = (Icon)tempIcon.Clone();
        tempIcon.Dispose();
        DestroyIcon(hIcon);

        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _blockingService.StateChanged -= OnStateChanged;
        _languageService.LanguageChanged -= OnLanguageChanged;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        _greenIcon?.Dispose();
        _redIcon?.Dispose();
        _yellowIcon?.Dispose();
        _trayIcon?.Dispose();
        _greenIcon = null;
        _redIcon = null;
        _yellowIcon = null;
        _trayIcon = null;

        _showAppIcon?.Dispose();
        _hideAppIcon?.Dispose();
        _lockIcon?.Dispose();
        _unlockIcon?.Dispose();
        _exitIcon?.Dispose();
        _showAppIcon = null;
        _hideAppIcon = null;
        _lockIcon = null;
        _unlockIcon = null;
        _exitIcon = null;

        Log.Debug("TrayIconManager disposed");
    }

    /// <summary>
    /// Loads the optimized tray icon from WPF pack resources.
    /// </summary>
    private static Icon? LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/UI/Resources/Icons/tray.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo?.Stream != null)
            {
                return new Icon(streamInfo.Stream);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load pack resource tray.ico for NotifyIcon");
        }
        return null;
    }
}
