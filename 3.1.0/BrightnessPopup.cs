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
    private readonly int _baseWidth = 140;   // Slightly wider than OSD

    /// <summary>
    /// Per-wheel-notch brightness step (0..1) as configured in settings;
    /// used by the wheel handlers here so the popup honors the same
    /// step as the wheel OSD path instead of a fixed 5%.
    /// </summary>
    public float StepSize { get; set; } = 0.05f;
    private readonly int _baseHeight = 60;   // Label + slider + power-off button, compact
    private int _lastLayoutDpi;
    // Tracks the physical size the rounded-corner Region was last built for.
    // Do NOT compare against WinForms Width/Height here: after WM_DPICHANGED
    // the framework auto-scales the window (and the Region) before our 200 ms
    // poll runs, so Width/Height already equal the new values and the rebuild
    // guard would be skipped, leaving the framework-scaled (misaligned) region.
    private Size _lastRegionSize;

    public event EventHandler<float>? OnBrightnessChanged;

    /// <summary>
    /// Raised when the popup's visibility changes (shown/hidden).
    /// MainController uses this to start/stop the icon-rect polling timer
    /// that keeps the popup anchored to the tray icon.
    /// </summary>
    public event EventHandler? OnShownChanged;

    public BrightnessPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        float dpiScale = DeviceDpi / 96.0f;
        Size = new Size((int)(_baseWidth * dpiScale), (int)(_baseHeight * dpiScale));
        _lastLayoutDpi = DeviceDpi;
        BackColor = ThemeManager.PopupBg;
        ForeColor = ThemeManager.PopupText;
        Opacity = 0.9;  // More opaque than OSD for a "panel" feel
        TopMost = true;

        // Repaint with the new palette when the app theme changes while the
        // popup is open (e.g. user switches dark/light in the settings).
        ThemeManager.PopupThemeChanged += OnThemeChanged;

        ApplyRoundedCorners(8, Width, Height);
        _lastRegionSize = new Size(Width, Height);

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
            ForeColor = ThemeManager.PopupText,
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
            BackColor = ThemeManager.PopupBg,
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
            ForeColor = ThemeManager.PopupText,
            BackColor = ThemeManager.PopupBtnBg,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(margin, btnY),
            Size = new Size(btnWidth, btnHeight),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _powerButton.FlatAppearance.BorderSize = 0;
        _powerButton.FlatAppearance.MouseOverBackColor = ThemeManager.PopupBtnHover;
        _powerButton.FlatAppearance.MouseDownBackColor = ThemeManager.PopupBtnDown;
        ApplyPowerButtonBorder();
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

    /// <summary>
    /// Repaints the popup with the current theme palette. Called when the
    /// app theme changes while the popup is open; all colors are derived
    /// from ThemeManager at paint/assign time, so this just re-assigns the
    /// control-level colors and invalidates the self-drawn slider.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnThemeChanged(sender, e)));
            return;
        }

        BackColor = ThemeManager.PopupBg;
        ForeColor = ThemeManager.PopupText;
        _label.ForeColor = ThemeManager.PopupText;
        _sliderHitArea.BackColor = ThemeManager.PopupBg;
        _sliderHitArea.Invalidate();

        _powerButton.BackColor = ThemeManager.PopupBtnBg;
        _powerButton.ForeColor = ThemeManager.PopupText;
        _powerButton.FlatAppearance.MouseOverBackColor = ThemeManager.PopupBtnHover;
        _powerButton.FlatAppearance.MouseDownBackColor = ThemeManager.PopupBtnDown;
        ApplyPowerButtonBorder();
        _powerButton.Image?.Dispose();
        _powerButton.Image = CreatePowerIcon((int)(15 * DeviceDpi / 96.0f), (int)(15 * DeviceDpi / 96.0f));
    }

    /// <summary>
    /// Applies the light-mode 1px border around the power button so its
    /// outline stays visible against the light popup background (dark mode
    /// keeps the borderless look).
    /// </summary>
    private void ApplyPowerButtonBorder()
    {
        var border = ThemeManager.PopupBtnBorder;
        bool hasBorder = border != Color.Transparent;
        _powerButton.FlatAppearance.BorderSize = hasBorder ? 1 : 0;
        if (hasBorder)
        {
            _powerButton.FlatAppearance.BorderColor = border;
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
        int delta = Math.Sign(e.Delta) * Math.Max(1, (int)Math.Round(StepSize * 100));
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

        int delta = Math.Sign(wheelDelta) * Math.Max(1, (int)Math.Round(StepSize * 100));
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
    /// Rebuilds the popup's internal control layout (label, slider hit
    /// area, power button) for the current DeviceDpi, and refreshes the
    /// cached font. Called when the popup's DPI changes while on screen
    /// (WM_DPICHANGED) so the controls scale together with the window.
    /// Without this the controls keep their original (constructor) layout
    /// while WinForms auto-scales the window size 鈥?the classic "wrong
    /// after DPI change, correct after restart" symptom.
    /// </summary>
    /// <param name="dpi">The REAL DPI of the icon's monitor, queried live
    /// (see <see cref="GetIconMonitorDpi"/>). DeviceDpi is stale while the
    /// hidden popup receives no WM_DPICHANGED.</param>
    /// <param name="widthPx">Target physical client width for this DPI, so
    /// the layout matches the size PositionAbove will apply. Using the
    /// stale ClientSize would misplace controls after a DPI change.</param>
    private void ApplyLayoutForCurrentDpi(int dpi, int widthPx)
    {
        if (dpi == _lastLayoutDpi) return;
        _lastLayoutDpi = dpi;
        #if DEBUG
        PopupDebug.Log($"ApplyLayoutForCurrentDpi: dpi={dpi}");
        #endif

        float dpiScale = dpi / 96.0f;
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = widthPx;
        int contentWidth = clientWidth - margin * 2;
        int barY = topPadding + labelHeight + gap;
        int btnY = barY + barHeight + (int)(4 * dpiScale);
        int bottomPadding = (int)(4 * dpiScale);
        int btnHeight = Math.Max(18, ClientSize.Height - btnY - bottomPadding);

        _label.Location = new Point(margin, topPadding);
        _label.Size = new Size(contentWidth, labelHeight);
        int fontSize = Math.Max(7, (int)(7 * dpiScale));
        if (_label.Font.SizeInPoints != fontSize)
        {
            _label.Font?.Dispose();
            _label.Font = new Font("Segoe UI", fontSize, FontStyle.Bold);
        }

        _sliderHitArea.Location = new Point(margin, barY - gap);
        _sliderHitArea.Size = new Size(contentWidth, barHeight + (int)(12 * dpiScale));
        _sliderHitArea.Invalidate();

        _powerButton.Location = new Point(margin, btnY);
        _powerButton.Size = new Size(contentWidth, btnHeight);
        _powerButton.Image?.Dispose();
        _powerButton.Image = CreatePowerIcon((int)(15 * dpiScale), (int)(15 * dpiScale));
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

        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ShowAbove: iconRect={iconRect} dpi={dpi}");
        #endif
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
        if (!Visible)
        {
            Show();
            OnShownChanged?.Invoke(this, EventArgs.Empty);
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

        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ShowAbove: iconRect={iconRect} dpi={dpi}");
        #endif
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
        if (!Visible)
        {
            Show();
            OnShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Re-anchors the popup above a NEW tray icon rect (screen coords).
    /// Used when the tray icon moves (DPI change) while the popup is open:
    /// the popup follows the icon to its new position. Also re-applies the
    /// physical size for the new DPI so the popup doesn't stay at the old
    /// scale. Only repositions/resizes; the brightness value is unchanged.
    /// </summary>
    public void ReanchorTo(Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<Rectangle>(ReanchorTo), iconRect);
            return;
        }

        if (!Visible) return;

        // GetIconMonitorDpi queries the icon's monitor live, so this works
        // even when the hidden popup hasn't received WM_DPICHANGED.
        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ReanchorTo: iconRect={iconRect} dpi={dpi}");
        #endif

        // PositionAbove sets both size and position using the live DPI,
        // then the internal layout is rebuilt for that same size.
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
    }

    /// <summary>
    /// Draws the whole slider: gray track, white fill and the thumb circle.
    /// Everything is drawn on this opaque panel in one pass so nothing can
    /// be painted over the circle afterwards (a transparent panel would let
    /// the track below repaint on top of it when the fill shrinks).
    /// </summary>
    private void SliderHitArea_Paint(object? sender, PaintEventArgs e)
    {
        // Use the last applied layout DPI (updated live from the icon's
        // monitor) instead of DeviceDpi, which is stale while the hidden
        // popup receives no WM_DPICHANGED.
        float dpiScale = (_lastLayoutDpi > 0 ? _lastLayoutDpi : DeviceDpi) / 96.0f;
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
        using (var track = new SolidBrush(ThemeManager.PopupTrack))
        {
            g.FillRectangle(track, 0, barTop, barW, barHeight);
        }

        // Fill (brightness level) - blue in light mode, white in dark.
        using (var fill = new SolidBrush(ThemeManager.PopupFill))
        {
            g.FillRectangle(fill, 0, barTop, fillWidth, barHeight);
        }

        // Thumb circle at the fill edge.
        using (var brush = new SolidBrush(ThemeManager.PopupThumb))
        using (var pen = new Pen(ThemeManager.PopupThumbOutline, 1f))
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
    private Size PositionAbove(Rectangle iconRect)
    {
        // Real DPI of the monitor that hosts the icon (NOT DeviceDpi, which
        // is stale while the hidden popup receives no WM_DPICHANGED).
        int dpi = GetIconMonitorDpi(iconRect);
        float dpiScale = dpi / 96.0f;
        int physW = (int)(_baseWidth * dpiScale);
        int physH = (int)(_baseHeight * dpiScale);

        // Physical working area of the icon's monitor (NOT Screen.WorkingArea,
        // whose cache goes stale after a DPI change while the app runs).
        var working = GetIconMonitorWorkArea(iconRect);

        #if DEBUG
        PopupDebug.Log($"PositionAbove: iconRect={iconRect} dpi={dpi} size={physW}x{physH} working={working}");
        #endif

        int popupX = iconRect.Left + (iconRect.Width - physW) / 2;
        int popupY = iconRect.Top - physH - 6; // 6px gap above the icon

        // Clamp horizontally to the physical working area
        popupX = Math.Max(working.Left + 2, Math.Min(popupX, working.Right - physW - 2));
        if (popupY < working.Top)
        {
            // Not enough space above: place below the icon instead
            popupY = iconRect.Bottom + 6;
        }
        popupY = Math.Min(popupY, working.Bottom - physH - 2);

        #if DEBUG
        PopupDebug.Log($"PositionAbove: final=({popupX},{popupY})");
        #endif

        SetWindowPos(Handle, IntPtr.Zero, popupX, popupY, physW, physH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        // NOTE: Location is intentionally NOT set here; SetWindowPos handles
        // physical placement. Assigning Location would re-enter WinForms'
        // logical-coordinate scaling and double-shift the window.

        // The window region (rounded corners) is tied to the window size;
        // after SetWindowPos resizes the window, re-apply it with the NEW
        // physical size or the rounded corners end up misaligned (only the
        // top-left keeps its radius, the other corners become square).
        // Skip when the size is unchanged (this runs every 200 ms while the
        // popup is open) to avoid churning GDI regions. NOTE: track the size
        // ourselves (_lastRegionSize) instead of comparing Width/Height —
        // WinForms auto-scales the window AND the Region on WM_DPICHANGED
        // before the poll runs, so Width/Height already match physW/physH and
        // a Width/Height comparison would wrongly skip the rebuild, leaving
        // the framework-scaled (misaligned) rounded corners.
        if (physW != _lastRegionSize.Width || physH != _lastRegionSize.Height)
        {
            ApplyRoundedCorners(8, physW, physH);
            _lastRegionSize = new Size(physW, physH);
        }

        return new Size(physW, physH);
    }

    /// <summary>
    /// Physical working area of the monitor containing the icon rect.
    /// Uses MonitorFromRect + GetMonitorInfo (rcWork) directly 鈥?this is
    /// ALWAYS physical pixels regardless of the app's cached Screen list,
    /// which goes stale after a DPI change (Screen.WorkingArea can report
    /// a mixed/old value once the scaling changes while the app runs).
    /// </summary>
    private Rectangle GetIconMonitorWorkArea(Rectangle iconRect)
    {
        var r = new NativeMethods.RECT
        {
            Left = iconRect.Left,
            Top = iconRect.Top,
            Right = iconRect.Right,
            Bottom = iconRect.Bottom
        };
        IntPtr hMon = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref mi))
        {
            return new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        }
        // Fallback: the cached Screen (best effort; stale after DPI change)
        var screen = Screen.FromRectangle(iconRect);
        return screen.WorkingArea;
    }

    /// <summary>
    /// Real DPI of the monitor containing the icon rect, queried live via
    /// GetDpiForMonitor. The form's DeviceDpi is NOT reliable here: while
    /// the popup is hidden it receives no WM_DPICHANGED, so DeviceDpi stays
    /// at the value from creation even after the user changes the display
    /// scaling 鈥?which is exactly the bug seen in the field (popup sized
    /// for 175% while the display is now 150%).
    /// </summary>
    private int GetIconMonitorDpi(Rectangle iconRect)
    {
        var r = new NativeMethods.RECT
        {
            Left = iconRect.Left,
            Top = iconRect.Top,
            Right = iconRect.Right,
            Bottom = iconRect.Bottom
        };
        IntPtr hMon = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero)
        {
            if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            {
                return (int)dpiX;
            }
        }
        // Fallback: window DPI (may be stale if hidden; better than nothing)
        return DeviceDpi > 0 ? DeviceDpi : 96;
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
            OnShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Turns off the display (鎭睆) by broadcasting SC_MONITORPOWER.
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
            using var pen = new Pen(ThemeManager.PopupBtnIcon, penW)
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

    protected override void WndProc(ref Message m)
    {
        // 0x02E0 = WM_DPICHANGED. Log whether the popup receives it at all
        // (hidden forms are not sent WM_DPICHANGED; visible ones are).
        if (m.Msg == 0x02E0)
        {
            int newDpi = (m.WParam.ToInt32() >> 16);
            #if DEBUG
            PopupDebug.Log($"BrightnessPopup WndProc: WM_DPICHANGED newDpi={newDpi}");
            #endif
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.PopupThemeChanged -= OnThemeChanged;
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
            // Soft drop shadow drawn by DWM outside the window, so the
            // popup keeps a visible outline against a light desktop.
            cp.ClassStyle |= 0x00020000;  // CS_DROPSHADOW
            return cp;
        }
    }

    private void ApplyRoundedCorners(int radius, int widthPx, int heightPx)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var rect = new Rectangle(0, 0, widthPx, heightPx);

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        // Dispose the previous region (rounded corners are re-applied on
        // every resize/DPI change; leaking a Region per call would exhaust
        // GDI handles over time).
        Region?.Dispose();
        Region = new Region(path);
    }
}
