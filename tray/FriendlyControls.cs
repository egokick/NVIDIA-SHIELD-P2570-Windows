using System.Drawing.Drawing2D;

namespace ShieldController;

internal static class Palette
{
    internal static readonly Color Background = Color.FromArgb(247, 248, 244);
    internal static readonly Color Ink = Color.FromArgb(24, 44, 39);
    internal static readonly Color Muted = Color.FromArgb(88, 107, 98);
    internal static readonly Color Green = Color.FromArgb(169, 228, 75);
    internal static readonly Color Soft = Color.FromArgb(231, 237, 226);
    internal static readonly Color Error = Color.FromArgb(149, 64, 36);
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); float d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

// Keep native button keyboard/accessibility behavior; only the paint is custom.
internal sealed class ActionButton : Button
{
    private bool hover;
    internal ActionButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI Semibold", 13); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        bool contrast = SystemInformation.HighContrast;
        using var path = Palette.Round(new RectangleF(1, 1, Width - 3, Height - 3), 14 * DeviceDpi / 96f);
        Color fill = contrast ? SystemColors.Highlight : !Enabled ? Palette.Soft : hover ? Color.FromArgb(184, 239, 99) : Palette.Green;
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            contrast ? SystemColors.HighlightText : Enabled ? Palette.Ink : Palette.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var focus = Palette.Round(new RectangleF(5, 5, Width - 11, Height - 11), 10 * DeviceDpi / 96f);
            using var pen = new Pen(contrast ? SystemColors.HighlightText : Palette.Ink, 2) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawPath(pen, focus);
        }
    }
}
