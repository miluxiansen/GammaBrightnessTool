using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// Small semantic icon shown beside the popup mode switch. Left icon =
/// brightness (the original tray sun glyph), right icon = temperature
/// (the orange→blue thermometer art). The ACTIVE mode's icon renders at
/// full opacity; the inactive one is dimmed (reduced alpha), so the user
/// instantly sees which function the slider currently controls.
/// </summary>
public enum ModeIconKind
{
    Brightness,
    Temperature
}

public sealed class ModeIcon : Control
{
    private ModeIconKind _kind;
    private bool _active = true;
    private Bitmap? _image;
    private ModeIconKind _imageKind;
    private bool _imageTheme;

    public ModeIcon()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        BackColor = Color.Transparent;
    }

    public ModeIconKind Kind
    {
        get => _kind;
        set { _kind = value; _image = null; Invalidate(); }
    }

    /// <summary>True = this mode is currently selected (icon highlighted).</summary>
    public bool Active
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }

    /// <summary>
    /// Resolves the embedded art for the current kind and popup theme.
    /// The brightness sun is monochrome (dark/white variant chosen by the
    /// popup theme). The temperature icon is the colorful colortemp-ring
    /// series (orange→blue gradient ring): pre-rendered per-size PNGs,
    /// picking the frame closest to the given pixel size so the glyph
    /// stays crisp.
    /// </summary>
    private static Bitmap? LoadIconImage(ModeIconKind kind, int pixelSize)
    {
        if (kind == ModeIconKind.Brightness)
        {
            string suffix = ThemeManager.PopupIsDark
                ? "tray-sun-white.png"
                : "tray-sun-black.png";
            return LoadEmbedded(suffix);
        }

        // Temperature: colorful gradient ring, size-matched frame.
        int size = PickSizeFrame(pixelSize);
        return LoadEmbedded($"colortemp-ring-color-{size}.png");
    }

    /// <summary>
    /// Picks the colortemp frame to render for the given pixel size.
    /// Prefers the smallest frame that is AT LEAST the target size
    /// (downscaling keeps details crisp; upscaling blurs), falling back
    /// to the largest frame when the target exceeds the series.
    /// </summary>
    private static int PickSizeFrame(int pixelSize)
    {
        int[] frames = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (int f in frames)
        {
            if (f >= pixelSize) return f;
        }
        return frames[frames.Length - 1];
    }

    private static Bitmap? LoadEmbedded(string suffix)
    {
        var asm = typeof(ModeIcon).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        return stream == null ? null : new Bitmap(stream);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // Theme can change while the popup lives (popup theme is a separate
        // setting), so re-resolve the art on every paint.
        if (_image == null || _imageKind != _kind || _imageTheme != ThemeManager.PopupIsDark)
        {
            _image?.Dispose();
            _image = LoadIconImage(_kind, Math.Max(Height, Width));
            _imageKind = _kind;
            _imageTheme = ThemeManager.PopupIsDark;
        }

        if (_image == null)
        {
            // Fallback: keep the legacy drawn glyph so the popup never ends
            // up with an empty icon if the embedded art is missing.
            DrawLegacy(g);
            return;
        }

        float opacity = _active ? 1.0f : 0.28f;
        using var attrs = new ImageAttributes();
        var cm = new ColorMatrix(new float[][]
        {
            new float[] { 1, 0, 0, 0, 0 },
            new float[] { 0, 1, 0, 0, 0 },
            new float[] { 0, 0, 1, 0, 0 },
            new float[] { 0, 0, 0, opacity, 0 },
            new float[] { 0, 0, 0, 0, 1 }
        });
        attrs.SetColorMatrix(cm);

        // Fit the art into the control bounds preserving aspect ratio
        // (the thermometer PNG is tall; the sun is square).
        float scale = Math.Min((float)Width / _image.Width, (float)Height / _image.Height);
        int w = (int)(_image.Width * scale);
        int h = (int)(_image.Height * scale);
        int x = (Width - w) / 2;
        int y = (Height - h) / 2;

        g.DrawImage(_image, new Rectangle(x, y, w, h),
            0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>
    /// Legacy hand-drawn glyphs used only when the embedded art is missing:
    /// sun (circle + rays) for brightness, warm/cool tinted disc for
    /// temperature.
    /// </summary>
    private void DrawLegacy(Graphics g)
    {
        Color iconColor = _active
            ? ThemeManager.PopupText
            : (ThemeManager.PopupIsDark ? Color.FromArgb(100, 100, 100) : Color.FromArgb(160, 160, 160));

        float penW = Math.Max(1.0f, Width / 10f);
        float cx = Width / 2f;
        float cy = Height / 2f;
        float r = Math.Min(Width, Height) / 2f - penW;

        if (_kind == ModeIconKind.Brightness)
        {
            // Sun: filled disc + 8 short rays.
            float discR = r * 0.55f;
            using (var brush = new SolidBrush(iconColor))
            {
                g.FillEllipse(brush, cx - discR, cy - discR, discR * 2, discR * 2);
            }
            using (var pen = new Pen(iconColor, penW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float rayLen = r * 0.38f;
                float inner = r * 0.72f;
                for (int i = 0; i < 8; i++)
                {
                    double ang = Math.PI / 4 * i;
                    float dx = (float)Math.Cos(ang);
                    float dy = (float)Math.Sin(ang);
                    g.DrawLine(pen,
                        cx + dx * inner, cy + dy * inner,
                        cx + dx * (inner + rayLen), cy + dy * (inner + rayLen));
                }
            }
        }
        else // Temperature
        {
            // Warm/cool tinted disc: a circle split by a vertical gradient
            // (warm orange-top -> neutral white -> cool blue-bottom) so the
            // "color temperature" idea reads at a glance.
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var brush = new LinearGradientBrush(rect,
                    Color.FromArgb(255, 170, 80),   // warm
                    Color.FromArgb(80, 150, 255),   // cool
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(brush, path);
                }
            }
            // Subtle outline so the disc stands out from the track.
            using (var pen = new Pen(_active
                ? Color.FromArgb(90, ThemeManager.PopupText)
                : Color.FromArgb(60, 100, 100, 100), 1f))
            {
                g.DrawEllipse(pen, rect);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
