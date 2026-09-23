using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A simple sliding toggle switch control.
/// Left = label area (user-supplied, placed beside it), right = the switch.
/// Track is theme-aware: blue when ON (Windows accent), neutral gray when
/// OFF — the same blue/gray language as the popup slider, instead of a
/// jarring green/red. The knob slides smoothly between the two states
/// using a lightweight animation timer.
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private float _knobPos;      // 0 = off, 1 = on
    private System.Windows.Forms.Timer? _animTimer;
    private const int AnimSteps = 6;
    private ModeIconKind _mode = ModeIconKind.Brightness;
    private bool _showKnobIcon;  // popup mode switch only; settings toggles stay clean
    private Bitmap? _knobIcon;   // cached icon drawn on the knob
    private ModeIconKind _knobIconMode;
    private bool _knobIconTheme;
    private bool _suppressAnim;  // set during ctor initializer; skip slide animation
    private bool _usePopupTheme;  // popup switches follow PopupIsDark, settings switches follow IsDark

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(44, 22);
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        _knobPos = 0f;
        _suppressAnim = true;  // ctor: object-initializer sets Checked later; don't animate
    }

    /// <summary>
    /// Mode shown as the icon on the sliding knob (brightness sun or
    /// temperature ring). Switches along with the control state so the
    /// knob always carries the icon of the ACTIVE mode.
    /// </summary>
    public ModeIconKind Mode
    {
        get => _mode;
        set { _mode = value; _knobIcon = null; Invalidate(); }
    }

    /// <summary>
    /// Whether the mode glyph is drawn on the knob. Default OFF: settings
    /// window toggles are plain (no icon). The brightness popup's mode
    /// switch enables this so its knob shows the active mode's icon.
    /// </summary>
    public bool ShowKnobIcon
    {
        get => _showKnobIcon;
        set { _showKnobIcon = value; Invalidate(); }
    }

    /// <summary>
    /// True when the switch lives on a floating popup and must follow
    /// the popup theme (PopupIsDark) instead of the main UI theme
    /// (IsDark). Settings-window switches keep the default (false).
    /// </summary>
    public bool UsePopupTheme
    {
        get => _usePopupTheme;
        set { _usePopupTheme = value; Invalidate(); }
    }

    /// <summary>
    /// Re-applies the base size scaled by the given DPI factor.
    /// Called by the settings window when the display scale changes so the
    /// switch (whose size is fixed at construction, unlike auto-scaling
    /// labels) grows with the UI.
    /// </summary>
    public void ApplyDpiScale(float scale)
    {
        Size = new Size((int)Math.Round(44 * scale), (int)Math.Round(22 * scale));
    }

    /// <summary>Gets or sets the switch state.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (_suppressAnim) { _knobPos = _checked ? 1f : 0f; }
            else { StartAnimation(); }
            OnCheckedChanged();
        }
    }

    public event EventHandler? CheckedChanged;

    private void OnCheckedChanged()
    {
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _suppressAnim = false;  // after ctor/layout: real user interactions animate
    }
    private void StartAnimation()
    {
        _animTimer?.Stop();
        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += (_, _) =>
        {
            float target = _checked ? 1f : 0f;
            _knobPos += (target - _knobPos) * 0.35f;
            if (Math.Abs(target - _knobPos) < 0.02f)
            {
                _knobPos = target;
                _animTimer.Stop();
                _animTimer.Dispose();
                _animTimer = null;
            }
            Invalidate();
        };
        _animTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int h = Height - 4;
        int w = Math.Max(20, Width - 4);
        int trackH = Math.Max(12, h - 8);
        var trackRect = new Rectangle(2, (Height - trackH) / 2, w, trackH);

        // Track: blue when on (Windows accent), neutral gray when off.
        // Theme-aware: popup switches follow the popup theme (PopupIsDark),
        // settings switches follow the main UI theme (IsDark).
        bool dark = UsePopupTheme ? ThemeManager.PopupIsDark : ThemeManager.IsDark;
        var trackColor = _checked
            ? (dark ? Color.FromArgb(76, 194, 255) : Color.FromArgb(0, 120, 215))
            : (dark ? Color.FromArgb(90, 90, 90) : Color.FromArgb(200, 200, 200));
        using (var trackBrush = new SolidBrush(trackColor))
        {
            using var path = RoundedRect(trackRect, trackH / 2);
            g.FillPath(trackBrush, path);
        }

        // Knob (white circle), slides horizontally
        int knobD = h - 4;
        int xMin = 2 + knobD / 2;
        int xMax = 2 + w - knobD / 2;
        float cx = xMin + (xMax - xMin) * _knobPos;
        var knobRect = new RectangleF(cx - knobD / 2f, (Height - knobD) / 2f, knobD, knobD);
        using (var knobBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(knobBrush, knobRect);
        }
        // subtle knob border
        using (var pen = new Pen(Color.FromArgb(40, 40, 40, 40)))
        {
            g.DrawEllipse(pen, knobRect);
        }

        // Mode icon on the knob: only when explicitly enabled (popup mode
        // switch). Settings toggles keep a plain knob.
        if (_showKnobIcon)
        {
            var icon = GetKnobIcon();
            if (icon != null)
            {
                int iconSize = (int)(knobD * 0.62f);
                var iconRect = new RectangleF(
                    knobRect.X + (knobRect.Width - iconSize) / 2f,
                    knobRect.Y + (knobRect.Height - iconSize) / 2f,
                    iconSize, iconSize);
                g.DrawImage(icon, iconRect);
            }
        }
    }

    /// <summary>
    /// Loads the icon for the current mode (cached until mode/theme
    /// changes): brightness sun or temperature gradient ring, in a
    /// size suitable for drawing on the knob.
    /// </summary>
    private Bitmap? GetKnobIcon()
    {
        bool theme = UsePopupTheme ? ThemeManager.PopupIsDark : ThemeManager.IsDark;
        if (_knobIcon != null && _knobIconMode == _mode && _knobIconTheme == theme)
        {
            return _knobIcon;
        }

        _knobIcon?.Dispose();
        _knobIcon = LoadModeIcon(_mode);
        _knobIconMode = _mode;
        _knobIconTheme = theme;
        return _knobIcon;
    }

    private static Bitmap? LoadModeIcon(ModeIconKind kind)
    {
        string suffix;
        if (kind == ModeIconKind.Brightness)
        {
                        // Sun: knob is always white, so always use the black sun
                        // glyph regardless of theme (white-on-white would vanish).
                        suffix = "tray-sun-black.png";
        }
        else
        {
            // Temperature: colorful gradient ring, always visible.
            suffix = "colortemp-ring-color-24.png";
        }

        var asm = typeof(ToggleSwitch).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        return stream == null ? null : new Bitmap(stream);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }
}
