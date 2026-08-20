using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A text box with rounded corners and a themed 1px rounded border.
/// The background is painted by the base control; on WM_PAINT we fill the
/// four corner triangles (outside the rounded path) with the parent surface
/// background so the field reads as truly rounded instead of a square with a
/// rounded outline, then draw the rounded border.
/// </summary>
public class RoundedTextBox : TextBox
{
    public int CornerRadius { get; set; } = 6;

    private Color _borderColor = Color.FromArgb(160, 160, 160);
    // Background of the parent surface (the card inner panel). The rounded
    // corners outside the field's own rounded rect are filled with this
    // color so the field blends into its container.
    private Color _parentBg = Color.White;

    public RoundedTextBox()
    {
        BorderStyle = BorderStyle.None; // we draw the rounded border ourselves
    }

    /// <summary>Applies theme colors (background, text, border).</summary>
    public virtual void ApplyTheme(Color background, Color foreground)
    {
        BackColor = background;
        _parentBg = background;  // rounded corners follow the field color (caller may override via SetParentBackground)
        ForeColor = foreground;
        _borderColor = ThemeManager.IsDark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);
        Invalidate();
    }

    /// <summary>Sets the border color explicitly (defaults to the theme color).</summary>
    public void SetBorderColor(Color color)
    {
        _borderColor = color;
        Invalidate();
    }

    /// <summary>Sets the color used to fill the rounded corners (the parent
    /// surface background) so the field blends into its container.</summary>
    public void SetParentBackground(Color color)
    {
        _parentBg = color;
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F) // WM_PAINT: fill corners + draw the rounded border last
        {
            using var g = CreateGraphics();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int radius = Math.Min(CornerRadius, Math.Min(Width, Height) / 2);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, radius);

            // Overwrite the four corner triangles outside the rounded path
            // with the parent background so the field is truly rounded.
            using (var region = new Region(ClientRectangle))
            using (var parentBrush = new SolidBrush(_parentBg))
            {
                region.Exclude(path);
                g.FillRegion(parentBrush, region);
            }

            using var pen = new Pen(_borderColor);
            g.DrawPath(pen, path);
        }
        else if (m.Msg == 0x0014) // WM_ERASEBKGND: fill the whole client
        {
            // area with the themed background (belt-and-braces).
            using var g = Graphics.FromHdc(m.WParam);
            g.Clear(BackColor);
            m.Result = new IntPtr(1);
        }
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
