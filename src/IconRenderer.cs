using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;

namespace Juice;

enum IconTone { Normal, Low, Charging, Stale }

/// <summary>Zeichnet das Taskleisten-Symbol: oben ein Gerätesymbol, darunter die Prozentzahl.</summary>
static class IconRenderer
{
    public static Icon Render(int? percent, IconTone tone, DeviceKind kind, int? size = null)
    {
        string text = percent?.ToString() ?? "–";
        int px = size ?? SystemInformation.SmallIconSize.Width;
        var color = ColorFor(tone);

        using var bitmap = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            using var brush = new SolidBrush(color);

            // Oberes gutes Drittel: Gerätesymbol. Darunter die Zahl, so groß wie in die Breite passt.
            float glyphHeight = px * 0.40f;
            DrawGlyph(g, kind, new RectangleF(0, 0, px, glyphHeight), color);

            var numberArea = new RectangleF(0, glyphHeight - px * 0.04f, px, px - glyphHeight + px * 0.04f);
            float emSize = numberArea.Height * 1.05f;
            Font font = new("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            while (emSize > 5 && g.MeasureString(text, font, PointF.Empty, format).Width > px + 1)
            {
                font.Dispose();
                emSize -= 0.5f;
                font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using (font)
                g.DrawString(text, font, brush, numberArea, format);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    /// <summary>Gefüllte Mini-Symbole; Details werden ausgestanzt, damit sie auch bei 16 px erkennbar bleiben.</summary>
    static void DrawGlyph(Graphics g, DeviceKind kind, RectangleF band, Color color)
    {
        using var fill = new SolidBrush(color);
        float h = band.Height * 0.92f;
        float cx = band.X + band.Width / 2, cy = band.Y + band.Height / 2;
        float line = Math.Max(1f, h * 0.13f);

        switch (kind)
        {
            case DeviceKind.Mouse:
            {
                float w = h * 0.68f;
                var body = new RectangleF(cx - w / 2, cy - h / 2, w, h);
                using (var path = RoundedRect(body, w / 2))
                    g.FillPath(fill, path);
                float split = body.Top + h * 0.42f;
                Punch(g, new RectangleF(cx - line / 2, body.Top, line, split - body.Top));
                Punch(g, new RectangleF(body.Left, split - line / 2, w, line));
                break;
            }
            case DeviceKind.Keyboard:
            {
                // Auf ganze Pixel gerechnet, sonst laufen die kleinen Tasten ineinander.
                int key = Math.Max(1, (int)Math.Round(h * 0.18f));
                int columns = 4;
                int step = key * 2 + 1;
                int w = columns * step + key + 1;
                int kh = key * 3 + 4;
                int left = (int)Math.Round(cx - w / 2f), top = (int)Math.Round(cy - kh / 2f);
                using (var path = RoundedRect(new RectangleF(left, top, w, kh), Math.Max(1, key)))
                    g.FillPath(fill, path);
                // Zwei Tastenreihen und eine Leertaste ausstanzen.
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < columns; col++)
                        Punch(g, new RectangleF(left + key + 1 + col * step, top + 1 + row * (key + 1), key, key));
                Punch(g, new RectangleF(left + step, top + 1 + 2 * (key + 1), w - 2 * step, key));
                break;
            }
            case DeviceKind.Headset:
            {
                float w = h * 1.1f;
                using (var pen = new Pen(color, line * 1.3f))
                    g.DrawArc(pen, cx - w / 2, cy - h / 2, w, h * 1.1f, 180, 180);
                float pad = w * 0.28f;
                using (var left = RoundedRect(new RectangleF(cx - w / 2 - line / 2, cy, pad, h / 2), pad / 3))
                    g.FillPath(fill, left);
                using (var right = RoundedRect(new RectangleF(cx + w / 2 - pad + line / 2, cy, pad, h / 2), pad / 3))
                    g.FillPath(fill, right);
                break;
            }
            case DeviceKind.Gamepad:
            {
                float w = h * 1.7f, gh = h * 0.75f;
                using (var path = RoundedRect(new RectangleF(cx - w / 2, cy - gh / 2, w, gh), gh / 2))
                    g.FillPath(fill, path);
                float dot = Math.Max(1f, gh * 0.25f);
                Punch(g, new RectangleF(cx - w * 0.28f - dot / 2, cy - dot / 2, dot, dot));
                Punch(g, new RectangleF(cx + w * 0.28f - dot / 2, cy - dot / 2, dot, dot));
                break;
            }
            default:
            {
                float d = h * 0.8f;
                g.FillEllipse(fill, cx - d / 2, cy - d / 2, d, d);
                break;
            }
        }
    }

    /// <summary>Macht einen Bereich wieder durchsichtig (für Details in gefüllten Symbolen).</summary>
    static void Punch(Graphics g, RectangleF area)
    {
        var (mode, smoothing, offset) = (g.CompositingMode, g.SmoothingMode, g.PixelOffsetMode);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var clear = new SolidBrush(Color.Transparent))
            g.FillRectangle(clear, (float)Math.Round(area.X), (float)Math.Round(area.Y),
                Math.Max(1f, (float)Math.Round(area.Width)), Math.Max(1f, (float)Math.Round(area.Height)));
        (g.CompositingMode, g.SmoothingMode, g.PixelOffsetMode) = (mode, smoothing, offset);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Color ColorFor(IconTone tone) => tone switch
    {
        IconTone.Low => Color.FromArgb(0xE5, 0x39, 0x35),
        IconTone.Charging => Color.FromArgb(0x2E, 0xB8, 0x5C),
        IconTone.Stale => Color.FromArgb(0x8A, 0x8A, 0x8A),
        _ => LightTaskbar() ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White,
    };

    static bool LightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }
}
