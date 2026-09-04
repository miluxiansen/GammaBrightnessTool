using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Small dark tooltip-like window shown next to the power-off button while
/// the mouse hovers it. Matches the brightness popup look (dark, rounded),
/// never activates and never steals focus. Positioned with PHYSICAL
/// coordinates via SetWindowPos, same as BrightnessPopup.PositionAbove.
/// </summary>
public sealed class PowerTipForm : Form
{
    private readonly Label _label;

    public PowerTipForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = ThemeManager.TipBg;
        Opacity = 0.95;
        // Physical target size; WinForms stores it as DIP (divides by the
        // current scale) so the rendered size comes back as physical pixels,
        // same convention as BrightnessPopup. Fits the longest localized tip
        // text (English, ~113px at 4pt) with comfortable padding.
        Size = new Size(140, 32);

        ApplyRoundedCorners(8);

        float dpiScale = DeviceDpi / 96.0f;
        _label = new Label
        {
            ForeColor = ThemeManager.TipText,
            BackColor = Color.Transparent,
            // Small font (4pt base) so the tip stays compact; the tip is
            // only a hint, no need for large text.
            Font = new Font("Segoe UI", Math.Max(4, (int)(4 * dpiScale))),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Dock = DockStyle.Fill
        };
        Controls.Add(_label);

        // Repaint with the new palette when the app theme changes.
        ThemeManager.PopupThemeChanged += OnThemeChanged;

        // Forward label hover events so the owner can keep the tip alive
        // while the mouse travels from the button onto the tip window.
        _label.MouseEnter += (s, e) => OnMouseEnter(EventArgs.Empty);
        _label.MouseLeave += (s, e) => OnMouseLeave(EventArgs.Empty);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>
    /// Repaints the tip with the current theme palette when the app theme
    /// changes while the tip is visible.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnThemeChanged(sender, e)));
            return;
        }

        BackColor = ThemeManager.TipBg;
        _label.ForeColor = ThemeManager.TipText;
    }

    public void SetText(string text)
    {
        _label.Text = text;
    }

    /// <summary>
    /// Positions next to the given PHYSICAL button rect and shows the tip.
    /// Prefers the right side, falls back to the left when the right side
    /// would leave the monitor's physical working area; vertically centered
    /// against the button and clamped inside the working area.
    /// </summary>
    public void ShowNear(Rectangle btnPhys)
    {
        ShowNear(btnPhys, null);
    }

    /// <summary>
    /// Same as <see cref="ShowNear(Rectangle)"/> but makes the given form
    /// the owner. An owned window is ALWAYS kept above its owner by the
    /// window manager — even when the owner is a TopMost window that gets
    /// activated — so the tip can never be covered by the popup it
    /// belongs to.
    /// </summary>
    public void ShowNear(Rectangle btnPhys, Form? owner)
    {
        if (owner != null && Owner != owner)
        {
            Owner = owner;   // owned window: always above owner, hides with it
        }

        // Physical placement, same mechanism as BrightnessPopup.PositionAbove.
        int physW = Width;   // DIP size; WinForms scales it to physical
        int physH = Height;
        int x = btnPhys.Right + 6;
        int y = btnPhys.Top + (btnPhys.Height - physH) / 2;

        // Physical working area of the monitor under the button.
        var wa = GetWorkingArea(btnPhys);
        if (x + physW > wa.Right)
        {
            x = btnPhys.Left - physW - 6;   // not enough room on the right
        }
        x = Math.Max(wa.Left + 2, x);
        y = Math.Max(wa.Top + 2, Math.Min(y, wa.Bottom - physH - 2));

        SetWindowPos(Handle, IntPtr.Zero, x, y, physW, physH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        // Show() is required for WinForms to create the child Label handle
        // (SetWindowPos+SWP_SHOWWINDOW alone renders an empty window). Same
        // order as BrightnessPopup.ShowAbove: position first, then show.
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>
    /// Physical working area of the monitor containing the given physical
    /// point, from MONITORINFO (no DIP conversion needed).
    /// </summary>
    private Rectangle GetWorkingArea(Rectangle pt)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)) };
        IntPtr hMon = MonitorFromPoint(new POINT { x = pt.Left, y = pt.Top }, MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref mi))
        {
            return new Rectangle(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);
        }

        // Fallback: under PerMonitorV2 Screen.WorkingArea is already
        // physical pixels, matching the MONITORINFO path above.
        return Screen.FromRectangle(pt).WorkingArea;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.PopupThemeChanged -= OnThemeChanged;
        }
        base.Dispose(disposing);
    }

    private void ApplyRoundedCorners(int radius)
    {
        // Region(Region) copies the geometry so the path can be disposed here;
        // the previous Region is released too (the setter does not dispose it).
        using (var path = new GraphicsPath())
        {
            int diameter = radius * 2;
            var rect = new Rectangle(0, 0, Width, Height);

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }
    }
}
