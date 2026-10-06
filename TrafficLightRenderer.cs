using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TrafficLight;

internal enum ButtonKind { Close, Minimize, Zoom }

internal readonly record struct LightButton(ButtonKind Kind, PointF Center, float Radius, bool Enabled)
{
    public bool HitTest(Point p, float slop)
    {
        float dx = p.X - Center.X, dy = p.Y - Center.Y;
        float r = Radius + slop;
        return dx * dx + dy * dy <= r * r;
    }
}

/// <summary>What one overlay should look like. Produced by the tracker, consumed by the overlay thread.</summary>
internal sealed record OverlayState(
    bool Visible,
    Native.RECT Bounds,
    Color Background,
    bool Active,
    bool Maximized,
    bool CanMinimize,
    bool CanMaximize,
    bool CloseOnly,
    int CornerRadius,
    float Scale,
    bool Topmost);

internal static class TrafficLightRenderer
{
    // macOS (Big Sur and later) palette.
    private static readonly Color CloseFill = Color.FromArgb(0xFF, 0x5F, 0x57);
    private static readonly Color CloseBorder = Color.FromArgb(0xE0, 0x44, 0x3E);
    private static readonly Color CloseGlyph = Color.FromArgb(0x6E, 0x0B, 0x05);
    private static readonly Color MinFill = Color.FromArgb(0xFE, 0xBC, 0x2E);
    private static readonly Color MinBorder = Color.FromArgb(0xDE, 0xA1, 0x23);
    private static readonly Color MinGlyph = Color.FromArgb(0x8A, 0x4B, 0x00);
    private static readonly Color ZoomFill = Color.FromArgb(0x28, 0xC8, 0x40);
    private static readonly Color ZoomBorder = Color.FromArgb(0x1A, 0xAB, 0x29);
    private static readonly Color ZoomGlyph = Color.FromArgb(0x05, 0x56, 0x08);

    public const float BaseDiameter = 14f;
    public const float BaseSpacing = 22f; // center to center

    public static LightButton[] Layout(OverlayState s)
    {
        int w = s.Bounds.Width, h = s.Bounds.Height;
        float d = BaseDiameter * s.Scale;
        float spacing = BaseSpacing * s.Scale;
        var kinds = s.CloseOnly
            ? new[] { ButtonKind.Close }
            : new[] { ButtonKind.Close, ButtonKind.Minimize, ButtonKind.Zoom };

        float groupWidth = d + spacing * (kinds.Length - 1);
        float x0 = (w - groupWidth) / 2f + d / 2f;
        float cy = h / 2f;

        var result = new LightButton[kinds.Length];
        for (int i = 0; i < kinds.Length; i++)
        {
            bool enabled = kinds[i] switch
            {
                ButtonKind.Minimize => s.CanMinimize,
                ButtonKind.Zoom => s.CanMaximize,
                _ => true,
            };
            result[i] = new LightButton(kinds[i], new PointF(x0 + spacing * i, cy), d / 2f, enabled);
        }
        return result;
    }

    public static RectangleF GroupBounds(LightButton[] buttons, float pad)
    {
        var first = buttons[0];
        var last = buttons[^1];
        return RectangleF.FromLTRB(
            first.Center.X - first.Radius - pad, first.Center.Y - first.Radius - pad,
            last.Center.X + last.Radius + pad, last.Center.Y + last.Radius + pad);
    }

    /// <summary>Renders a premultiplied-ARGB bitmap ready for UpdateLayeredWindow.</summary>
    public static Bitmap Render(OverlayState s, LightButton[] buttons, int pressed, bool groupHover)
    {
        int w = Math.Max(1, s.Bounds.Width), h = Math.Max(1, s.Bounds.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        // Background patch that hides the native caption buttons.
        using (var bg = new SolidBrush(Color.FromArgb(255, s.Background)))
        {
            if (s.CornerRadius > 0)
            {
                using var path = TopRightRounded(new RectangleF(0, 0, w, h), s.CornerRadius);
                g.FillPath(bg, path);
            }
            else
            {
                g.SmoothingMode = SmoothingMode.None;
                g.FillRectangle(bg, 0, 0, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
        }

        bool dark = Luminance(s.Background) < 0.5;
        bool colored = s.Active || groupHover;
        float stroke = Math.Max(1f, 0.75f * s.Scale);

        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            Color fill, border, glyph;
            if (!b.Enabled || !colored)
            {
                fill = dark ? Mix(s.Background, Color.White, 0.22f) : Mix(s.Background, Color.Black, 0.14f);
                border = dark ? Mix(s.Background, Color.White, 0.14f) : Mix(s.Background, Color.Black, 0.22f);
                glyph = Color.Empty;
            }
            else
            {
                (fill, border, glyph) = b.Kind switch
                {
                    ButtonKind.Close => (CloseFill, CloseBorder, CloseGlyph),
                    ButtonKind.Minimize => (MinFill, MinBorder, MinGlyph),
                    _ => (ZoomFill, ZoomBorder, ZoomGlyph),
                };
                if (pressed == i)
                {
                    fill = Mix(fill, Color.Black, 0.2f);
                    border = Mix(border, Color.Black, 0.2f);
                }
            }

            var circle = new RectangleF(b.Center.X - b.Radius, b.Center.Y - b.Radius, b.Radius * 2, b.Radius * 2);
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, circle);
            var inner = RectangleF.Inflate(circle, -stroke / 2, -stroke / 2);
            using (var pen = new Pen(border, stroke)) g.DrawEllipse(pen, inner);

            if (groupHover && b.Enabled && glyph != Color.Empty)
                DrawGlyph(g, b, glyph, s.Scale, s.Maximized);
        }

        return bmp;
    }

    private static void DrawGlyph(Graphics g, LightButton b, Color color, float scale, bool maximized)
    {
        float cx = b.Center.X, cy = b.Center.Y, r = b.Radius;
        float width = Math.Max(1.4f, 1.45f * scale);
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);

        switch (b.Kind)
        {
            case ButtonKind.Close:
            {
                float a = r * 0.40f;
                g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
                g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
                break;
            }
            case ButtonKind.Minimize:
            {
                float a = r * 0.55f;
                g.DrawLine(pen, cx - a, cy, cx + a, cy);
                break;
            }
            case ButtonKind.Zoom:
            {
                // Two right triangles pointing outwards (enter) or inwards (exit), like macOS.
                float o = r * 0.52f;  // reach of the corner
                float l = r * 0.62f;  // leg length
                float gap = r * 0.06f;
                if (!maximized)
                {
                    g.FillPolygon(brush, new[] { new PointF(cx - o, cy - o), new PointF(cx - o + l, cy - o), new PointF(cx - o, cy - o + l) });
                    g.FillPolygon(brush, new[] { new PointF(cx + o, cy + o), new PointF(cx + o - l, cy + o), new PointF(cx + o, cy + o - l) });
                }
                else
                {
                    g.FillPolygon(brush, new[] { new PointF(cx - gap, cy - gap), new PointF(cx - gap - l, cy - gap), new PointF(cx - gap, cy - gap - l) });
                    g.FillPolygon(brush, new[] { new PointF(cx + gap, cy + gap), new PointF(cx + gap + l, cy + gap), new PointF(cx + gap, cy + gap + l) });
                }
                break;
            }
        }
    }

    private static GraphicsPath TopRightRounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddLine(r.Left, r.Bottom, r.Left, r.Top);
        p.AddLine(r.Left, r.Top, r.Right - radius, r.Top);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddLine(r.Right, r.Top + radius, r.Right, r.Bottom);
        p.CloseFigure();
        return p;
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        255,
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    /// <summary>Small three-dot icon for the notification area.</summary>
    public static Icon CreateTrayIcon(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float d = size * 0.30f;
            float gap = (size - d * 3) / 2f;
            float y = (size - d) / 2f;
            Color[] fills = { CloseFill, MinFill, ZoomFill };
            for (int i = 0; i < 3; i++)
            {
                using var brush = new SolidBrush(fills[i]);
                g.FillEllipse(brush, i * (d + gap), y, d, d);
            }
        }
        IntPtr hicon = bmp.GetHicon();
        using var temp = Icon.FromHandle(hicon);
        var icon = (Icon)temp.Clone();
        DestroyIcon(hicon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hicon);
}
