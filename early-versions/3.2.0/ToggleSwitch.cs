using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A simple sliding toggle switch control.
/// Left = label area (user-supplied, placed beside it), right = the switch.
/// Green track when ON, red track when OFF. The knob slides smoothly
/// between the two states using a lightweight animation timer.
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private float _knobPos;      // 0 = off, 1 = on
    private System.Windows.Forms.Timer? _animTimer;
    private const int AnimSteps = 6;

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
            StartAnimation();
            OnCheckedChanged();
        }
    }

    public event EventHandler? CheckedChanged;

    private void OnCheckedChanged()
    {
        CheckedChanged?.Invoke(this, EventArgs.Empty);
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

        // Track: green when on, red when off
        var trackColor = _checked ? TrackOnColor : TrackOffColor;
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
    }

    private static readonly Color TrackOnColor = Color.FromArgb(52, 168, 83);   // green
    private static readonly Color TrackOffColor = Color.FromArgb(220, 80, 80);   // red

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
