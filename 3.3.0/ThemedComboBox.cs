using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

internal static class ComboBoxNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);
}

/// <summary>
/// A DropDownList combo box that actually honors BackColor/ForeColor on
/// Windows 10/11. Plain WinForms ComboBox with FlatStyle.Flat still gets
/// drawn with the system colors by the theme-aware Common Controls, so on a
/// dark theme the box body would stay white with black text. This subclass
/// forces the edit/list area colors by handling WM_CTLCOLOR* and repainting
/// the dropdown list items in the theme colors.
/// </summary>
public sealed class ThemedComboBox : ComboBox
{
    private readonly SolidBrush _bgBrush;
    private readonly SolidBrush _itemBgBrush;
    private Color _borderColor = Color.FromArgb(205, 205, 205);
    // Background of the parent surface (the card inner panel). The rounded
    // corners outside the combo's own rounded rect are filled with this
    // color after the system paint, so the native white square corners never
    // show on a dark theme.
    private Color _parentBg = Color.White;

    /// <summary>Corner radius of the closed combo box body.</summary>
    public int CornerRadius { get; set; } = 6;

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        _bgBrush = new SolidBrush(Color.White);
        _itemBgBrush = new SolidBrush(Color.White);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Disable the Win11 themed (UxTheme) drawing so BackColor and the
        // Flat border color are honored. Without this the system draws a
        // white border + white body regardless of our colors.
        ComboBoxNative.SetWindowTheme(Handle, string.Empty, string.Empty);
        // Remove the classic 3D sunken border (WS_EX_CLIENTEDGE): its
        // top/left highlight is white and looks jarring on a dark theme.
        // We draw our own rounded border on WM_PAINT instead.
        const int WS_EX_CLIENTEDGE = 0x00000200;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        if ((ex & WS_EX_CLIENTEDGE) != 0)
        {
            SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_CLIENTEDGE);
            // Redraw the frame so the style change takes effect immediately.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }
        ApplyTheme(BackColor, ForeColor);
    }

    protected override void WndProc(ref Message m)
    {
        // Suppress mouse-wheel value cycling while the dropdown is closed so
        // the page scroll container receives the wheel instead. Without this
        // the user scrolling a settings page accidentally changes the
        // language / theme / popup theme / step size when the cursor passes
        // over a combo box. ComboBox does not call OnMouseWheel for its
        // built-in wheel handling, so we have to intercept the message here.
        if (m.Msg == 0x020A /* WM_MOUSEWHEEL */ && !DroppedDown)
        {
            // Forward the wheel to the parent scroll panel so the page
            // scrolls instead of cycling the combo. Posting (not sending)
            // lets our WndProc return cleanly first.
            if (Parent != null)
            {
                PostMessage(Parent.Handle, 0x020A, m.WParam, m.LParam);
            }
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == 0x000F) // WM_PAINT: repaint the border last
        {
            using var g = CreateGraphics();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int radius = Math.Min(CornerRadius, Math.Min(Width, Height) / 2);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, radius);

            // 1) Overpaint the system 3D sunken border (white top/left
            //    highlight, gray bottom/right shadow) with the field
            //    background using a thick pen along the rounded path.
            using (var coverPen = new Pen(BackColor, 4f))
            {
                g.DrawPath(coverPen, path);
            }

            // 2) Fill the four corner triangles OUTSIDE the rounded path
            //    with the parent background (also reverts the thick pen's
            //    overshoot on the outside of the arcs).
            using (var region = new Region(ClientRectangle))
            using (var parentBrush = new SolidBrush(_parentBg))
            {
                region.Exclude(path);
                g.FillRegion(parentBrush, region);
            }

            // 3) Draw the final 1px themed border.
            using var pen = new Pen(_borderColor);
            g.DrawPath(pen, path);
        }
        else if (m.Msg == 0x0014) // WM_ERASEBKGND: fill the whole client
        {
            // area with the theme background (belt-and-braces with the
            // WM_PAINT corner fill above).
            using var g = Graphics.FromHdc(m.WParam);
            g.Clear(BackColor);
            m.Result = new IntPtr(1);
        }
    }

    /// <summary>Sets the color used to fill the rounded corners (the parent
    /// surface background) so the combo blends into its container.</summary>
    public void SetParentBackground(Color color)
    {
        _parentBg = color;
        Invalidate();
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Refreshes the brushes from the current theme palette.</summary>
    public void ApplyTheme(Color background, Color foreground)
    {
        BackColor = background;
        SetParentBackground(background);  // rounded corners follow the field color
        ForeColor = foreground;
        _bgBrush.Color = background;
        _itemBgBrush.Color = background;
        _borderColor = ThemeManager.IsDark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            base.OnDrawItem(e);
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        bool dark = ThemeManager.IsDark;

        // Dropdown list item background: highlight for hovered, else a tone
        // slightly lighter than the closed field so items read as distinct
        // options, not part of the field background.
        var bg = selected
            ? (dark ? Color.FromArgb(78, 78, 86) : Color.FromArgb(229, 241, 251))
            : (dark ? Color.FromArgb(66, 66, 72) : Color.FromArgb(252, 252, 252));
        using (var bgBrush = new SolidBrush(bg))
        {
            e.Graphics.FillRectangle(bgBrush, e.Bounds);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var fg = selected
            ? (dark ? Color.White : Color.FromArgb(20, 20, 20))
            : (dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40));
        // Defensive: e.Font can be null on the very first draw (before the
        // control's font is fully initialized); fall back to the control's
        // own Font so the first paint never renders garbage.
        var drawFont = e.Font ?? Font;
        var rect = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, drawFont, rect, fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bgBrush.Dispose();
            _itemBgBrush.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Suppress mouse-wheel value cycling while the dropdown is closed so the
    /// page scroll container receives the wheel instead. Without this the
    /// user scrolling a settings page accidentally changes the language /
    /// theme / popup theme / step size when the cursor passes over a combo
    /// box. When the dropdown is open the wheel must cycle items, so we
    /// leave it. The primary intercept is in WndProc (ComboBox does not
    /// route WM_MOUSEWHEEL through OnMouseWheel), but we also guard here
    /// in case a host control forwards wheel via OnMouseWheel.
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!DroppedDown)
        {
            Parent?.Focus();
            return;
        }
        base.OnMouseWheel(e);
    }
}
