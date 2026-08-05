using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Persistent brightness slider popup shown above the tray icon when the
/// user left-clicks the tray icon. Dismissed by clicking outside.
/// </summary>
public sealed class BrightnessPopup : Form
{
    private readonly Label _label;
    private readonly Panel _sliderHitArea;
    private readonly Button _powerButton;
    private PowerTipForm? _powerTip;
    private System.Windows.Forms.Timer? _tipHideTimer;
    private const int TipHideDelayMs = 250;
    private const int TipHideDelayShortMs = 150;
    private bool _isDragging;
    private int _currentPercentage = 100;

    public event EventHandler<float>? OnBrightnessChanged;

    public BrightnessPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        float dpiScale = DeviceDpi / 96.0f;
        int baseWidth = 140;   // Slightly wider than OSD
        int baseHeight = 60;   // Label + slider + power-off button, compact
        Size = new Size((int)(baseWidth * dpiScale), (int)(baseHeight * dpiScale));
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Opacity = 0.9;  // More opaque than OSD for a "panel" feel
        TopMost = true;

        ApplyRoundedCorners(8);

        // Layout parameters mirror the wheel OSD (BrightnessOverlay) which the
        // user confirmed looks right: small font (7pt base), generous label
        // height (20px base) and loose spacing. The left-click popup previously
        // used an 8pt font in an 18px label: at 150% DPI that becomes 12pt text
        // in an 18px label, so the glyphs nearly filled the label and their
        // bottom strokes collided with the slider hit area below (fake-
        // transparent panels erase what they cover).
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;

        // Percentage label - same font size as the wheel OSD
        int fontSize = Math.Max(7, (int)(7 * dpiScale));
        _label = new Label
        {
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", fontSize, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "100%",
            AutoSize = false,
            Location = new Point(margin, topPadding),
            Size = new Size(contentWidth, labelHeight)
        };

        // The visual bar's Y inside the popup (label bottom + gap).
        int barY = topPadding + labelHeight + gap;

        // Slider hit area (invisible, captures mouse events for dragging).
        // Starts at the label's bottom edge (barY - gap) so it never overlaps
        // the text; combined with the small font the text cannot be covered.
        // OPAQUE (window base color) and fully self-draws the slider (gray
        // track + white fill + thumb circle) in SliderHitArea_Paint. The old
        // design used a separate semi-transparent progress Panel + Dock=Left
        // fill child below this panel; that fake-transparent panel repaints
        // the parent background when its fill width changes, erasing the
        // thumb circle painted on the hit area above it.
        _sliderHitArea = new Panel
        {
            BackColor = Color.FromArgb(32, 32, 32),
            Location = new Point(margin, barY - gap),
            Size = new Size(contentWidth, barHeight + (int)(12 * dpiScale)),
            Cursor = Cursors.Hand
        };

        _sliderHitArea.MouseDown += SliderHitArea_MouseDown;
        _sliderHitArea.MouseMove += SliderHitArea_MouseMove;
        _sliderHitArea.MouseUp += SliderHitArea_MouseUp;
        _sliderHitArea.MouseWheel += SliderHitArea_MouseWheel;
        _sliderHitArea.Paint += SliderHitArea_Paint;

        // Full content width, and its height stretches to fill the remaining
        // popup space so the whole window is used (no empty band at the bottom).
        int btnWidth = contentWidth;
        int btnY = barY + barHeight + (int)(4 * dpiScale);
        int bottomPadding = (int)(4 * dpiScale);
        int btnHeight = Math.Max(18, ClientSize.Height - btnY - bottomPadding);
        _powerButton = new Button
        {
            Text = string.Empty,   // icon drawn in Paint (language-independent)
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat,
            Location = new Point(margin, btnY),
            Size = new Size(btnWidth, btnHeight),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _powerButton.FlatAppearance.BorderSize = 0;
        _powerButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(90, 90, 90);
        _powerButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 45, 45);
        _powerButton.Image = CreatePowerIcon((int)(15 * dpiScale), (int)(15 * dpiScale));
        _powerButton.ImageAlign = ContentAlignment.MiddleCenter;
        _powerButton.Click += (s, e) => PowerOffDisplay();
        _powerButton.MouseEnter += (s, e) => ShowPowerTip();
        _powerButton.MouseLeave += (s, e) => ScheduleTipHide(TipHideDelayMs);

        Controls.Add(_label);
        Controls.Add(_sliderHitArea);
        Controls.Add(_powerButton);

        // Click-outside dismissal is handled by the global mouse hook
        // (GlobalMouseHook checks whether the click landed outside this
        // window and calls Dismiss). Deactivate is NOT used because the
        // popup never takes focus (ShowWithoutActivation = true) and
        // relying on focus loss is unreliable.
    }

    /// <summary>
    /// True while the popup is visible on screen.
    /// </summary>
    public bool IsShown => Visible;

    private void ShowPowerTip()
    {
        _tipHideTimer?.Stop();
        if (_powerTip == null)
        {
            _powerTip = new PowerTipForm();
            // Keep the tip alive while the mouse is over it, hide shortly
            // after it leaves.
            _powerTip.MouseEnter += (s, e) => _tipHideTimer?.Stop();
            _powerTip.MouseLeave += (s, e) => ScheduleTipHide(TipHideDelayShortMs);
        }

        // Re-apply the localized text on every show so the tip follows a
        // language switch made while the popup instance already existed.
        _powerTip.SetText(Localization.Get("PowerOffDisplayTip"));

        GetWindowRect(_powerButton.Handle, out var rc);
        _powerTip.ShowNear(new Rectangle(rc.Left, rc.Top, rc.Width, rc.Height));
    }

    private void ScheduleTipHide(int delayMs)
    {
        if (_tipHideTimer == null)
        {
            _tipHideTimer = new System.Windows.Forms.Timer();
            _tipHideTimer.Tick += (s, e) =>
            {
                _tipHideTimer.Stop();
                HidePowerTip();
            };
        }
        _tipHideTimer.Interval = delayMs;
        _tipHideTimer.Start();
    }

    private void HidePowerTip()
    {
        _tipHideTimer?.Stop();
        if (_powerTip != null && _powerTip.Visible)
        {
            _powerTip.Hide();
        }
    }

    private void SliderHitArea_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            UpdateBrightnessFromMouse(e.X);
        }
    }

    private void SliderHitArea_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var sliderPos = _sliderHitArea.PointToClient(Cursor.Position);
            UpdateBrightnessFromMouse(sliderPos.X);
        }
    }

    private void SliderHitArea_MouseUp(object? sender, MouseEventArgs e)
    {
        _isDragging = false;
    }

    private void SliderHitArea_MouseWheel(object? sender, MouseEventArgs e)
    {
        int delta = Math.Sign(e.Delta) * 5;
        int newPercentage = Math.Max(0, Math.Min(100, _currentPercentage + delta));
        UpdateBrightness(newPercentage);
    }

    /// <summary>
    /// Adjusts the slider brightness by a wheel delta, used when the wheel
    /// is scrolled over the tray icon while this popup is open: the popup
    /// stays open and its slider/value move instead of showing the wheel OSD.
    /// </summary>
    public void AdjustByWheel(int wheelDelta)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<int>(AdjustByWheel), wheelDelta);
            return;
        }

        int delta = Math.Sign(wheelDelta) * 5;
        int newPercentage = Math.Max(0, Math.Min(100, _currentPercentage + delta));
        UpdateBrightness(newPercentage);
    }

    private void UpdateBrightnessFromMouse(int mouseX)
    {
        float ratio = Math.Max(0f, Math.Min(1f, (float)mouseX / _sliderHitArea.Width));
        int percentage = (int)(ratio * 100);
        percentage = Math.Max(0, Math.Min(100, percentage));
        UpdateBrightness(percentage);
    }

    private void UpdateBrightness(int percentage)
    {
        if (percentage == _currentPercentage) return;

        _currentPercentage = percentage;
        _label.Text = $"{percentage}%";

        // The slider is fully self-drawn by SliderHitArea_Paint; just
        // invalidate so the fill and thumb redraw at the new level.
        _sliderHitArea.Invalidate();

        OnBrightnessChanged?.Invoke(this, percentage / 100f);
    }

    /// <summary>
    /// Shows the popup anchored above the given tray icon rect (screen coords).
    /// </summary>
    public void ShowAbove(Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<Rectangle>(ShowAbove), iconRect);
            return;
        }

        PositionAbove(iconRect);
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>
    /// Shows the popup anchored above the given tray icon rect, refreshing
    /// the percentage display first.
    /// </summary>
    public void ShowAbove(float brightness, Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<float, Rectangle>(ShowAbove), brightness, iconRect);
            return;
        }

        _currentPercentage = (int)(brightness * 100);
        _label.Text = $"{_currentPercentage}%";
        _sliderHitArea.Invalidate();

        PositionAbove(iconRect);
        if (!Visible)
        {
            Show();
        }
    }

    /// <summary>
    /// Draws the whole slider: gray track, white fill and the thumb circle.
    /// Everything is drawn on this opaque panel in one pass so nothing can
    /// be painted over the circle afterwards (a transparent panel would let
    /// the track below repaint on top of it when the fill shrinks).
    /// </summary>
    private void SliderHitArea_Paint(object? sender, PaintEventArgs e)
    {
        float dpiScale = DeviceDpi / 96.0f;
        // Recompute layout values (same formulas as the constructor).
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int barY = topPadding + labelHeight + gap;
        // The visual bar sits at barY inside the popup; relative to the hit
        // area (whose top is barY - gap) that is exactly `gap`.
        int barTop = barY - _sliderHitArea.Top;
        int barW = _sliderHitArea.Width;
        int fillWidth = Math.Max(1, (int)(barW * (_currentPercentage / 100.0)));

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int radius = Math.Max(3, (int)(4 * dpiScale));
        int cx = Math.Min(fillWidth, barW - radius);
        cx = Math.Max(cx, radius);
        int cy = barTop + barHeight / 2;

        // Gray track (same look as the old progress panel background).
        using (var track = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
        {
            g.FillRectangle(track, 0, barTop, barW, barHeight);
        }

        // White fill (brightness level).
        using (var fill = new SolidBrush(Color.White))
        {
            g.FillRectangle(fill, 0, barTop, fillWidth, barHeight);
        }

        // Thumb circle at the fill edge.
        using (var brush = new SolidBrush(Color.White))
        using (var pen = new Pen(Color.FromArgb(60, 60, 60), 1f))
        {
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
        }
    }

    /// <summary>
    /// Positions the popup above the tray icon using SetWindowPos with
    /// PHYSICAL coordinates (Shell_NotifyIconGetRect returns physical pixels).
    /// WinForms Location/Screen use LOGICAL coordinates; mixing them would
    /// misplace the popup on scaled (e.g. 175%) displays.
    /// </summary>
    private void PositionAbove(Rectangle iconRect)
    {
        // Physical size of the popup window
        int physW = Width;
        int physH = Height;
        int popupX = iconRect.Left + (iconRect.Width - physW) / 2;
        int popupY = iconRect.Top - physH - 6; // 6px gap above the icon

        // Clamp horizontally to the physical working area of the screen
        // that contains the icon
        var screen = Screen.FromRectangle(new Rectangle(iconRect.Left, iconRect.Top, iconRect.Width, iconRect.Height));
        var working = GetPhysicalWorkingArea(screen);
        popupX = Math.Max(working.Left + 2, Math.Min(popupX, working.Right - physW - 2));
        if (popupY < working.Top)
        {
            // Not enough space above: place below the icon instead
            popupY = iconRect.Bottom + 6;
        }
        popupY = Math.Min(popupY, working.Bottom - physH - 2);

        SetWindowPos(Handle, IntPtr.Zero, popupX, popupY, physW, physH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        // NOTE: Location is intentionally NOT set here; SetWindowPos handles
        // physical placement. Assigning Location would re-enter WinForms'
        // logical-coordinate scaling and double-shift the window.
    }

    /// <summary>
    /// Converts a Screen's logical working area to physical pixels using
    /// the popup window's current DPI.
    /// </summary>
    private Rectangle GetPhysicalWorkingArea(Screen screen)
    {
        float scale = GetDpiForWindow(Handle) / 96.0f;
        var wa = screen.WorkingArea;
        return new Rectangle(
            (int)(wa.Left * scale),
            (int)(wa.Top * scale),
            (int)(wa.Width * scale),
            (int)(wa.Height * scale));
    }

    /// <summary>
    /// Closes the popup.
    /// </summary>
    public void Dismiss()
    {
        if (InvokeRequired)
        {
            Invoke(new Action(Dismiss));
            return;
        }

        HidePowerTip();
        if (Visible)
        {
            base.Hide();
        }
    }

    /// <summary>
    /// Turns off the display (息屏) by broadcasting SC_MONITORPOWER.
    /// lParam=2 means full power off; moving the mouse or pressing a key
    /// wakes the display again (standard Windows behavior).
    /// </summary>
    private void PowerOffDisplay()
    {
        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
    }

    /// <summary>
    /// Creates a "monitor + stand" icon bitmap (white vector lines) for the
    /// power-off button, so the button never depends on localized text.
    /// Rendered at DPI-scaled pixel size.
    /// </summary>
    private Bitmap CreatePowerIcon(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float penW = Math.Max(1.2f, w / 14f);
            using var pen = new Pen(Color.White, penW)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };

            // Monitor body + stand, bottom-aligned inside the bitmap so the
            // icon sits lower in the button (leaves space at the top).
            // iconH keeps the feet fully inside the bitmap (1px bottom margin).
            float iconH = h * 0.47f;
            float iconW = iconH / 0.72f;
            float standH = iconH * (0.35f + 0.12f);   // neck + feet
            float totalH = iconH + standH;
            float top = h - totalH - 1f;
            float cx = w / 2f;
            float sx = cx - iconW / 2f;
            float sy = top;

            // Rounded monitor body.
            float r = penW * 2f;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(sx, sy, r, r, 180, 90);
                path.AddArc(sx + iconW - r, sy, r, r, 270, 90);
                path.AddArc(sx + iconW - r, sy + iconH - r, r, r, 0, 90);
                path.AddArc(sx, sy + iconH - r, r, r, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }

            // 45-degree slash crossing the WHOLE icon (screen + stand),
            // from the icon's top-left to bottom-right, to clearly convey
            // "power off". Use a square region centered on the whole icon
            // so the angle stays exactly 45 degrees.
            float iconCx = sx + iconW / 2f;
            float iconCy = top + totalH / 2f;
            float sq = Math.Min(iconW, totalH) * 0.92f;
            g.DrawLine(pen, iconCx - sq / 2f, iconCy - sq / 2f, iconCx + sq / 2f, iconCy + sq / 2f);

            // Stand: neck + feet.
            float neckTop = sy + iconH;
            float neckBottom = neckTop + iconH * 0.35f;
            float feetY = neckBottom + iconH * 0.12f;
            g.DrawLine(pen, cx, neckTop, cx, neckBottom);
            g.DrawLine(pen, cx - iconW * 0.24f, feetY, cx + iconW * 0.24f, feetY);
        }
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tipHideTimer?.Dispose();
            _powerTip?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Tool window: no taskbar entry, never activates; the global
            // mouse hook owns click-outside dismissal.
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    private void ApplyRoundedCorners(int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var rect = new Rectangle(0, 0, Width, Height);

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        Region = new Region(path);
    }
}
