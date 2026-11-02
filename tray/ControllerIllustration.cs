using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ShieldController;

internal enum IllustrationMode { Pair, Searching, Wake, Connected, Error, Location }

/// <summary>An original drawing of the P2570, with the touch target in its real position.</summary>
internal sealed class ControllerIllustration : Control
{
    private static readonly Color Ink = Color.FromArgb(24, 44, 39);
    private static readonly Color Accent = Color.FromArgb(169, 228, 75);
    private IllustrationMode mode;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IllustrationMode Mode
    {
        get => mode;
        set
        {
            mode = value;
            AccessibleDescription = value switch
            {
                IllustrationMode.Connected => "Your controller is connected.",
                IllustrationMode.Wake => "Touch the green NVIDIA triangle at the top centre of your controller once to wake it.",
                IllustrationMode.Searching => "Leave the NVIDIA triangle flashing while the app connects your controller.",
                IllustrationMode.Location => "The touch-sensitive NVIDIA logo is the green triangle at the top centre of your controller. It does not click.",
                IllustrationMode.Error => "NVIDIA SHIELD P2570 controller.",
                _ => "Touch and hold the green NVIDIA triangle at the top centre of your controller for three seconds, then release it when it flashes."
            };
            Invalidate();
        }
    }

    internal ControllerIllustration()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Your SHIELD controller";
        TabStop = false;
        Mode = IllustrationMode.Pair;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float scale = Math.Min(ClientSize.Width / 400f, ClientSize.Height / 240f);
        g.TranslateTransform((ClientSize.Width - 400 * scale) / 2, (ClientSize.Height - 240 * scale) / 2);
        g.ScaleTransform(scale, scale);

        using (var shadow = new SolidBrush(Color.FromArgb(15, Ink)))
            g.FillEllipse(shadow, 81, 215, 238, 16);

        using var body = BodyPath();
        using (var fill = new LinearGradientBrush(new Point(0, 55), new Point(0, 230),
            Color.FromArgb(48, 69, 61), Ink)) g.FillPath(fill, body);
        using (var edge = new Pen(Color.FromArgb(18, 33, 29), 1.5f)) g.DrawPath(edge, body);

        // The original P2570 has a broad centre face and matching low thumbsticks.
        using (var face = new SolidBrush(Color.FromArgb(55, 77, 67)))
            g.FillPolygon(face, new Point[] {new(140, 67), new(260, 67), new(222, 132), new(222, 151),
                new(258, 196), new(142, 196), new(178, 151), new(178, 132)});
        using (var seam = new Pen(Color.FromArgb(76, 95, 84), 1))
        {
            g.DrawLines(seam, new Point[] {new(140, 67), new(178, 132), new(178, 151), new(142, 196)});
            g.DrawLines(seam, new Point[] {new(260, 67), new(222, 132), new(222, 151), new(258, 196)});
            g.DrawLine(seam, 81, 153, 84, 219);
            g.DrawLine(seam, 319, 153, 316, 219);
        }

        DrawDpad(g, 110, 110);
        DrawStick(g, 155, 159);
        DrawStick(g, 245, 159);
        DrawFaceButton(g, 289, 92, "Y");
        DrawFaceButton(g, 272, 110, "X");
        DrawFaceButton(g, 306, 110, "B");
        DrawFaceButton(g, 289, 128, "A");

        // Small quiet navigation marks, with the illuminated top touch triangle dominant.
        using (var navigation = new Pen(Color.FromArgb(153, 170, 157), 1.5f))
        {
            g.DrawLines(navigation, new Point[] {new(180, 116), new(176, 119), new(180, 122)});
            g.DrawLines(navigation, new Point[] {new(196, 119), new(200, 115), new(204, 119), new(204, 123), new(196, 123), new(196, 119)});
            g.DrawLine(navigation, 219, 117, 224, 117);
            g.DrawLine(navigation, 219, 120, 224, 120);
            g.DrawLine(navigation, 219, 123, 224, 123);
        }
        using (var touchpad = new SolidBrush(Color.FromArgb(38, 57, 49)))
            g.FillPolygon(touchpad, new Point[] {new(200, 155), new(233, 194), new(167, 194)});
        using (var divider = new Pen(Color.FromArgb(79, 100, 85), 1))
            g.DrawLine(divider, 157, 200, 243, 200);
        using (var volume = new Pen(Color.FromArgb(149, 167, 151), 1.5f))
        {
            g.DrawLine(volume, 167, 205, 173, 205);
            g.DrawLine(volume, 227, 205, 233, 205);
            g.DrawLine(volume, 230, 202, 230, 208);
        }

        if (Mode is IllustrationMode.Pair or IllustrationMode.Wake or IllustrationMode.Location)
        {
            using var halo = new SolidBrush(Color.FromArgb(43, Accent));
            g.FillEllipse(halo, 178, 64, 44, 43);
        }
        using (var touch = new SolidBrush(Accent))
            g.FillPolygon(touch, new Point[] {new(183, 75), new(217, 75), new(203, 99), new(197, 99)});
        // A small white eye mark identifies the logo without relying on tiny label text.
        using (var mark = new Pen(Ink, 1.8f))
        {
            g.DrawArc(mark, 193, 79, 14, 9, 15, 300);
            g.DrawArc(mark, 196, 81, 8, 5, 195, 305);
        }

        if (Mode != IllustrationMode.Error) DrawCallout(g);
    }

    private void DrawCallout(Graphics g)
    {
        bool connected = Mode == IllustrationMode.Connected;
        string label = Mode switch
        {
            IllustrationMode.Connected => "",
            IllustrationMode.Searching => "Keep it flashing",
            IllustrationMode.Wake => "Tap once",
            IllustrationMode.Location => "Touch here",
            _ => "Hold 3 sec"
        };
        float width = connected ? 36 : Mode == IllustrationMode.Searching ? 152 : 128;
        var bounds = new RectangleF(200 - width / 2, 10, width, 36);
        if (!connected)
        {
            using (var line = new Pen(Color.FromArgb(114, 150, 64), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(line, 200, 48, 200, 66);
            using (var pointer = new SolidBrush(Color.FromArgb(114, 150, 64)))
                g.FillPolygon(pointer, new Point[] {new(196, 62), new(204, 62), new(200, 69)});
        }
        using var bubble = RoundedRectangle(bounds, 18);
        using (var fill = new SolidBrush(connected ? Accent : Color.FromArgb(233, 242, 218)))
            g.FillPath(fill, bubble);
        using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Ink);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (connected)
        {
            using var check = new Pen(Ink, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(check, new Point[] {new(192, 28), new(198, 34), new(208, 23)});
            return;
        }
        g.DrawString(label, font, text, bounds, format);
    }

    private static GraphicsPath BodyPath()
    {
        var p = new GraphicsPath();
        p.StartFigure();
        p.AddBezier(69, 91, 84, 73, 111, 58, 128, 59);
        p.AddLine(128, 59, 145, 66); p.AddLine(145, 66, 255, 66); p.AddLine(255, 66, 272, 59);
        p.AddBezier(272, 59, 289, 58, 316, 73, 331, 91);
        p.AddBezier(331, 91, 343, 122, 357, 181, 354, 210);
        p.AddBezier(354, 210, 354, 218, 336, 223, 316, 225);
        p.AddLine(316, 225, 268, 197);
        p.AddBezier(268, 197, 241, 194, 159, 194, 132, 197);
        p.AddLine(132, 197, 84, 225);
        p.AddBezier(84, 225, 64, 223, 46, 218, 46, 210);
        p.AddBezier(46, 210, 43, 181, 57, 122, 69, 91);
        p.CloseFigure();
        return p;
    }

    private static void DrawStick(Graphics g, float x, float y)
    {
        using var socket = new SolidBrush(Color.FromArgb(17, 31, 26));
        using var rim = new Pen(Color.FromArgb(81, 102, 89), 1.3f);
        using var cap = new SolidBrush(Color.FromArgb(50, 72, 59));
        using var innerRim = new Pen(Color.FromArgb(115, 135, 113), 1.2f);
        g.FillEllipse(socket, x - 26, y - 26, 52, 52);
        g.DrawEllipse(rim, x - 26, y - 26, 52, 52);
        g.FillEllipse(cap, x - 19, y - 20, 38, 38);
        g.DrawEllipse(innerRim, x - 16, y - 17, 32, 32);
    }

    private static void DrawDpad(Graphics g, int x, int y)
    {
        using var socket = new SolidBrush(Color.FromArgb(24, 42, 34));
        using var cap = new SolidBrush(Color.FromArgb(111, 131, 111));
        using var rim = new Pen(Color.FromArgb(153, 172, 145), 1);
        g.FillEllipse(socket, x - 26, y - 26, 52, 52);
        using var p = new GraphicsPath();
        p.AddPolygon(new Point[] {new(x - 7, y - 20), new(x + 7, y - 20), new(x + 7, y - 7),
            new(x + 20, y - 7), new(x + 20, y + 7), new(x + 7, y + 7),
            new(x + 7, y + 20), new(x - 7, y + 20), new(x - 7, y + 7),
            new(x - 20, y + 7), new(x - 20, y - 7), new(x - 7, y - 7)});
        g.FillPath(cap, p); g.DrawPath(rim, p);
    }

    private static void DrawFaceButton(Graphics g, int x, int y, string label)
    {
        using var fill = new SolidBrush(Color.FromArgb(20, 37, 29));
        using var rim = new Pen(Color.FromArgb(97, 118, 98), 1);
        using var font = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(225, 236, 216));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var bounds = new RectangleF(x - 11, y - 11, 22, 22);
        g.FillEllipse(fill, bounds); g.DrawEllipse(rim, bounds);
        g.DrawString(label, font, text, bounds, format);
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}
