using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A flat button with rounded corners. Fully owner-drawn: the background is
/// a rounded rect (themed), the border a 1px rounded outline, and the text
/// is drawn vertically centered with optional bold font. Hover / pressed
/// states are tinted via the theme-aware MouseOverBackColor / pressed color.
/// </summary>
public sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 6;

    private Color _border = Color.FromArgb(205, 205, 205);
    private Color _bg = Color.White;
    private Color _mouseOver = Color.FromArgb(229, 241, 251);
    private Color _pressed = Color.FromArgb(192, 208, 228);
    // Background of the parent surface (the card inner panel or the page
    // panel). The rounded corners outside the button's own rounded rect are
    // filled with this color. Do NOT use BackColor=Transparent here: the
    // WinForms transparency pass is unreliable for owner-drawn controls and
    // occasionally fills the corners with ARGB(0,0,0) (black) until the next
    // repaint (hover) brings the parent background in.
    private Color _parentBg = Color.White;
    private bool _hover;
    private bool _down;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0; // we draw the border ourselves
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    /// <summary>Applies the theme palette (background, text, border, hover, pressed).</summary>
    public void ApplyTheme(Color background, Color foreground, Color border,
        Color mouseOver, Color pressed)
    {
        _border = border;
        _bg = background;
        _mouseOver = mouseOver;
        _pressed = pressed;
        ForeColor = foreground;
        BackColor = background; // opaque; corners are overpainted in OnPaint
        SetParentBackground(background); // corners blend into the parent surface
        Invalidate();
    }

    /// <summary>Sets the color used to fill the rounded corners (the parent
    /// surface background) so the button blends into its container.</summary>
    public void SetParentBackground(Color color)
    {
        _parentBg = color;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _down = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _down = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _down = false;
        Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }


    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Math.Min(CornerRadius, Math.Min(Width, Height) / 2);
        using var path = RoundedRect(rect, radius);

        // Fill the whole client area with the parent background first so the
        // four rounded corners always show the container color (never black).
        using (var parentBrush = new SolidBrush(_parentBg))
        {
            e.Graphics.FillRectangle(parentBrush, ClientRectangle);
        }

        // Background (rounded)
        var bg = Enabled ? (_down ? _pressed : _hover ? _mouseOver : _bg) : Blend(_bg, _parentBg, 0.45f);
        using (var bgBrush = new SolidBrush(bg))
        {
            e.Graphics.FillPath(bgBrush, path);
        }

        // 1px border
        using (var pen = new Pen(Enabled ? _border : Blend(_border, _parentBg, 0.45f)))
        {
            e.Graphics.DrawPath(pen, path);
        }

        // Image (optional, drawn centered) — used by the popup power button.
        // The base Button would draw Image automatically, but this control
        // fully overrides OnPaint, so it must draw it explicitly.
        if (Image != null)
        {
            int imgW = Image.Width;
            int imgH = Image.Height;
            var imgRect = new Rectangle((Width - imgW) / 2, (Height - imgH) / 2, imgW, imgH);
            e.Graphics.DrawImage(Image, imgRect);
        }

        // Text
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Blend(ForeColor, _parentBg, 0.45f), flags);
    }

    private static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb(
            Math.Clamp((int)(a.R + (b.R - a.R) * t), 0, 255),
            Math.Clamp((int)(a.G + (b.G - a.G) * t), 0, 255),
            Math.Clamp((int)(a.B + (b.B - a.B) * t), 0, 255));
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
