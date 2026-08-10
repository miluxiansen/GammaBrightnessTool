using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
        ApplyTheme(BackColor, ForeColor);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F) // WM_PAINT: repaint the border last
        {
            using var g = CreateGraphics();
            using var pen = new Pen(_borderColor);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Refreshes the brushes from the current theme palette.</summary>
    public void ApplyTheme(Color background, Color foreground)
    {
        BackColor = background;
        ForeColor = foreground;
        _bgBrush.Color = background;
        _itemBgBrush.Color = background;
        _borderColor = ThemeManager.IsDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(160, 160, 160);
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

        // Dropdown list item background: highlight for hovered, else dark/light.
        var bg = selected
            ? (dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251))
            : (dark ? Color.FromArgb(37, 37, 38) : Color.White);
        using (var bgBrush = new SolidBrush(bg))
        {
            e.Graphics.FillRectangle(bgBrush, e.Bounds);
        }

        var text = Items[e.Index]?.ToString() ?? "";
        var fg = selected
            ? (dark ? Color.White : Color.FromArgb(20, 20, 20))
            : (dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40));
        var rect = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, e.Font, rect, fg,
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
}
