using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A temporary overlay window that shows the current brightness level.
/// Features: interactive slider, mouse hover detection to prevent auto-hide.
/// </summary>
public sealed class BrightnessOverlay : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly Panel _sliderHitArea;  // Invisible hit area for slider interaction
    private bool _isMouseOver;
    private bool _isDragging;
    private int _currentPercentage = 100;
    private int _lastLayoutDpi;            // DPI used for current layout; 0 = not laid out yet
    private Font? _cachedFont;             // Reused across Show() calls to avoid GDI handle leaks

    public event EventHandler<float>? OnBrightnessChanged;

    private bool _isPersistent;   // true = left-click slider popup, false = wheel OSD
    public bool IsPersistent => _isPersistent;

    public BrightnessOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // DPI-aware sizing: base design for 96 DPI, scale up for higher DPI
        float dpiScale = DeviceDpi / 96.0f;
        int baseWidth = 120;   // Compact width
        int baseHeight = 38;   // Compact height
        Size = new Size((int)(baseWidth * dpiScale), (int)(baseHeight * dpiScale));
        BackColor = ThemeManager.PopupBg;
        ForeColor = ThemeManager.PopupText;
        Opacity = 0.7;  // 70% opacity for the form
        TopMost = true;

        // Repaint with the new palette when the app theme changes while the
        // OSD is visible.
        ThemeManager.PopupThemeChanged += OnThemeChanged;

        // Apply rounded corners
        ApplyRoundedCorners(8);

        // Calculate scaled dimensions - compact layout
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(1 * dpiScale);  // Minimal top padding
        int labelHeight = (int)(20 * dpiScale);  // Reduced label height
        int gap = (int)(2 * dpiScale);  // Minimal gap
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;

        // Percentage label
        int fontSize = Math.Max(7, (int)(7 * dpiScale));  // Even smaller font
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

        // The visual bar's Y inside the overlay (label bottom + gap).
        int barY = topPadding + labelHeight + gap;

        // Slider hit area (invisible, captures mouse events for dragging).
        // OPAQUE (window base color) and fully self-draws the slider (gray
        // track + white fill + thumb circle) in SliderHitArea_Paint. The old
        // design kept a separate semi-transparent progress Panel + Dock=Left
        // fill child below this panel; that fake-transparent panel repaints
        // the parent background when its fill width changes, erasing the
        // thumb circle painted on the hit area above it.
        _sliderHitArea = new Panel
        {
            BackColor = ThemeManager.PopupBg,
            Location = new Point(margin, barY - (int)(4 * dpiScale)),  // Slightly larger than visual bar
            Size = new Size(contentWidth, barHeight + (int)(8 * dpiScale)),
            Cursor = Cursors.Hand
        };

        // Mouse event handlers for slider interaction
        _sliderHitArea.MouseDown += SliderHitArea_MouseDown;
        _sliderHitArea.MouseMove += SliderHitArea_MouseMove;
        _sliderHitArea.MouseUp += SliderHitArea_MouseUp;
        _sliderHitArea.MouseWheel += SliderHitArea_MouseWheel;
        _sliderHitArea.Paint += SliderHitArea_Paint;

        Controls.Add(_label);
        Controls.Add(_sliderHitArea);

        // Mouse hover detection for all controls
        SubscribeMouseEvents(this);

        // Auto-hide timer (only used in OSD mode)
        _hideTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _hideTimer.Tick += (s, e) =>
        {
            if (_isPersistent)
            {
                // Persistent mode: never auto-hide; stop timer
                _hideTimer.Stop();
                return;
            }
            if (_isMouseOver || _isDragging)
            {
                // Reset timer if mouse is still over or dragging
                _hideTimer.Stop();
                _hideTimer.Start();
                return;
            }
            Hide();
            _hideTimer.Stop();
        };

        // Click-outside dismissal for the persistent popup is handled by
        // the global mouse hook (GlobalMouseHook checks whether the click
        // landed outside the window and calls Dismiss). Deactivate is NOT
        // reliable here because the OSD is a WS_EX_NOACTIVATE tool window
        // that never takes focus.
    }

    private void SubscribeMouseEvents(Control control)
    {
        control.MouseEnter += (s, e) => _isMouseOver = true;
        control.MouseLeave += (s, e) => CheckMouseLeave();

        // Recursively subscribe for child controls
        foreach (Control child in control.Controls)
        {
            SubscribeMouseEvents(child);
        }
    }

    private void CheckMouseLeave()
    {
        // Check if mouse is still within the form bounds
        var clientPos = PointToClient(Cursor.Position);
        _isMouseOver = ClientRectangle.Contains(clientPos);
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
            // Calculate relative X position within the slider hit area
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
        // Scroll wheel on slider: adjust brightness by steps
        int delta = Math.Sign(e.Delta) * 5;  // 5% per wheel step
        int newPercentage = Math.Max(0, Math.Min(100, _currentPercentage + delta));
        UpdateBrightness(newPercentage);
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
        int topPadding = (int)(1 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int barY = topPadding + labelHeight + gap;
        // The visual bar sits at barY inside the overlay; relative to the hit
        // area (whose top is barY - 4*dpiScale) that is exactly 4*dpiScale.
        int barTop = barY - _sliderHitArea.Top;
        int barW = _sliderHitArea.Width;
        int fillWidth = Math.Max(1, (int)(barW * (_currentPercentage / 100.0)));

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Track and fill are capsule-shaped (fully rounded ends, radius =
        // half the bar height) so the slider matches the rounded visual
        // language of the popup / OSD.
        int trackRadius = Math.Max(1, barHeight / 2);
        var trackRect = new Rectangle(0, barTop, barW, barHeight);
        using (var track = new SolidBrush(ThemeManager.PopupTrack))
        using (var trackPath = RoundedRect(trackRect, trackRadius))
        {
            g.FillPath(track, trackPath);
        }

        // Fill (brightness level) - blue in light mode, white in dark.
        var fillRect = new Rectangle(0, barTop, fillWidth, barHeight);
        using (var fill = new SolidBrush(ThemeManager.PopupFill))
        using (var fillPath = RoundedRect(fillRect, trackRadius))
        {
            g.FillPath(fill, fillPath);
        }

        // Thumb circle at the fill edge.
        int radius = Math.Max(3, (int)(4 * dpiScale));
        int cx = Math.Min(fillWidth, barW - radius);
        cx = Math.Max(cx, radius);
        int cy = barTop + barHeight / 2;
        using (var brush = new SolidBrush(ThemeManager.PopupThumb))
        using (var pen = new Pen(ThemeManager.PopupThumbOutline, 1f))
        {
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
        }
    }

    /// <summary>
    /// Repaints the OSD with the current theme palette when the app theme
    /// changes while the OSD is visible.
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
    }

    private void UpdateBrightnessFromMouse(int mouseX)
    {
        // Add a tiny epsilon so dragging to the far right shows 100% (the
        // raw ratio at the edge is 0.996..., which would truncate to 99).
        float ratio = Math.Max(0f, Math.Min(1f, (float)mouseX / _sliderHitArea.Width));
        int percentage = (int)Math.Round(ratio * 100);
        percentage = Math.Max(0, Math.Min(100, percentage));  // Clamp to 0-100%
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

        // Notify brightness change
        OnBrightnessChanged?.Invoke(this, percentage / 100f);
    }

    public void Show(float brightness)
    {
        Show(brightness, persistent: false);
    }

    /// <summary>
    /// Shows the overlay. In persistent mode it stays open until the user clicks outside.
    /// </summary>
    public void Show(float brightness, bool persistent)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<float, bool>(Show), brightness, persistent);
            return;
        }

        _isPersistent = persistent;
        _currentPercentage = (int)Math.Round(brightness * 100);
        _label.Text = $"{_currentPercentage}%";

        // The slider fill/thumb are self-drawn and must reflect the new
        // brightness even while the OSD is already visible (rapid wheel
        // scrolling reuses the visible form). Without this Invalidate the
        // slider would keep the level from the first Show() call until the
        // form is hidden and re-shown.
        _sliderHitArea.Invalidate();

        // dpiScale is used both for the DPI-change layout rebuild below and
        // for the OSD's distance-from-taskbar offset.
        float dpiScale = DeviceDpi / 96.0f;

        // Recalculate layout ONLY when DPI changed since last layout.
        // Rebuilding the Font on every Show() leaked one GDI font object per
        // call (visible after long usage / rapid wheel scrolling).
        if (DeviceDpi != _lastLayoutDpi)
        {
            _lastLayoutDpi = DeviceDpi;
            int margin = (int)(6 * dpiScale);
            int topPadding = (int)(1 * dpiScale);  // Minimal top padding
            int labelHeight = (int)(20 * dpiScale);  // Reduced label height
            int gap = (int)(2 * dpiScale);  // Minimal gap
            int barHeight = Math.Max(3, (int)(4 * dpiScale));
            int contentWidth = ClientSize.Width - margin * 2;
            int barY = topPadding + labelHeight + gap;

            _label.Location = new Point(margin, topPadding);
            _label.Size = new Size(contentWidth, labelHeight);
            int fontSize = Math.Max(7, (int)(7 * dpiScale));
            if (_cachedFont == null || _cachedFont.SizeInPoints != fontSize)
            {
                _cachedFont?.Dispose();
                _cachedFont = new Font("Segoe UI", fontSize, FontStyle.Bold);
            }
            _label.Font = _cachedFont;

            _sliderHitArea.Location = new Point(margin, barY - (int)(4 * dpiScale));
            _sliderHitArea.Size = new Size(contentWidth, barHeight + (int)(8 * dpiScale));
            _sliderHitArea.Invalidate();
        }

        // Position: centered above taskbar
        var cursorPos = Cursor.Position;
        var screen = Screen.FromPoint(cursorPos);
        var workingArea = screen.WorkingArea;

        int osdX = workingArea.Left + (workingArea.Width - Width) / 2;
        int osdY = workingArea.Bottom - Height - (int)(10 * dpiScale);

        osdX = Math.Max(workingArea.Left, Math.Min(osdX, workingArea.Right - Width));
        osdY = Math.Max(workingArea.Top, Math.Min(osdY, workingArea.Bottom - Height));

        Location = new Point(osdX, osdY);

        if (!Visible)
        {
            base.Show();
        }

        // Persistent 模式全项目已无调用方（OSD 仅作非持久滚轮浮层）。原先该
        // 分支会移除 WS_EX_NOACTIVATE 且 Hide 不恢复，若启用会让 OSD 抢焦点；
        // 分支已清理，标志位不可能被破坏。统一走自动隐藏计时器。
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public new void Hide()
    {
        if (InvokeRequired)
        {
            Invoke(new Action(Hide));
            return;
        }

        _isPersistent = false;
        _hideTimer.Stop();
        base.Hide();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.PopupThemeChanged -= OnThemeChanged;
            _cachedFont?.Dispose();
            _cachedFont = null;
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            // Soft drop shadow drawn by DWM outside the window, so the OSD
            // keeps a visible outline against a light desktop.
            cp.ClassStyle |= 0x00020000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private void ApplyRoundedCorners(int radius)
    {
        // Create a GraphicsPath for rounded rectangle
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int diameter = radius * 2;
        var rect = new Rectangle(0, 0, Width, Height);

        // Top-left arc
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        // Top-right arc
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        // Bottom-right arc
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        // Bottom-left arc
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        Region = new Region(path);
    }

    /// <summary>
    /// Builds a rounded-rectangle GraphicsPath. Radius is clamped so the
    /// rounded ends never exceed the rectangle's own dimensions (a very
    /// narrow fill — e.g. 1px at 0% — degrades gracefully to a pill).
    /// </summary>
    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int rr = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var path = new GraphicsPath();
        if (rr <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = rr * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

