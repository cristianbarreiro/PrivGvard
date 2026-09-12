using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CamMicBlocker.UI.TrayIcon;

/// <summary>
/// Custom ToolStripRenderer that applies CamMicBlocker's Fluent Dark visual identity
/// to system tray context menus. Uses the same color palette as MainWindow.
/// </summary>
public sealed class FluentDarkRenderer : ToolStripProfessionalRenderer
{
    public FluentDarkRenderer() : base(new FluentDarkColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }

        // Apply hover background with subtle rounded corners
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        rect.Inflate(-2, -1); // Subtle inset

        using var brush = new SolidBrush(Color.FromArgb(45, 45, 50)); // #2D2D32
        using var path = GetRoundedRectPath(rect, 4);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Render image margin with dark background (no white strip)
        using var brush = new SolidBrush(Color.FromArgb(37, 37, 40)); // #252528
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        // Render a thin, centered horizontal separator
        var rect = new Rectangle(
            e.Item.ContentRectangle.Left + 8,
            e.Item.ContentRectangle.Height / 2,
            e.Item.ContentRectangle.Width - 16,
            1
        );

        using var pen = new Pen(Color.FromArgb(62, 62, 69)); // #3E3E45
        e.Graphics.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Top);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        // Render menu background with rounded corners
        using var brush = new SolidBrush(Color.FromArgb(37, 37, 40)); // #252528
        using var path = GetRoundedRectPath(e.AffectedBounds, 6);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // No border - clean look
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Ensure text is white with proper font
        e.TextColor = Color.White;
        e.TextFont = new Font("Segoe UI", 9F, FontStyle.Regular);
        base.OnRenderItemText(e);
    }

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}

/// <summary>
/// Custom ProfessionalColorTable implementing CamMicBlocker's Fluent Dark palette.
/// </summary>
internal sealed class FluentDarkColorTable : ProfessionalColorTable
{
    // Primary Fluent Dark colors
    private static readonly Color BackgroundColor = Color.FromArgb(37, 37, 40);      // #252528
    private static readonly Color HoverColor = Color.FromArgb(45, 45, 50);          // #2D2D32
    private static readonly Color BorderColor = Color.FromArgb(62, 62, 69);         // #3E3E45
    private static readonly Color SeparatorColor = Color.FromArgb(62, 62, 69);      // #3E3E45

    // Menu item colors
    public override Color MenuItemSelected => HoverColor;
    public override Color MenuItemSelectedGradientBegin => HoverColor;
    public override Color MenuItemSelectedGradientEnd => HoverColor;
    public override Color MenuItemBorder => BorderColor;

    // Menu background colors
    public override Color MenuStripGradientBegin => BackgroundColor;
    public override Color MenuStripGradientEnd => BackgroundColor;
    public override Color ToolStripDropDownBackground => BackgroundColor;
    public override Color ImageMarginGradientBegin => BackgroundColor;
    public override Color ImageMarginGradientMiddle => BackgroundColor;
    public override Color ImageMarginGradientEnd => BackgroundColor;

    // Border colors
    public override Color MenuBorder => BorderColor;
    public override Color ToolStripBorder => BorderColor;

    // Separator colors
    public override Color SeparatorDark => SeparatorColor;
    public override Color SeparatorLight => SeparatorColor;

    // Pressed state (when clicking)
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(0, 122, 204); // #007ACC accent
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(0, 122, 204);
    public override Color MenuItemPressedGradientMiddle => Color.FromArgb(0, 122, 204);
}
