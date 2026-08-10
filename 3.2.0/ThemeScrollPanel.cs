using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// Scrollable panel with a slim theme-aware scrollbar, replacing the chunky
/// system AutoScroll bar that clashes with the settings window theme.
/// Children are stacked manually (no Dock) so each row's vertical Margin is
/// honored — Dock=Top silently ignores margins, which glued rows together.
/// The scrollbar is 6px wide, rounded, recolored via ApplyTheme, and only
/// appears when content overflows.
/// </summary>
public sealed class ThemeScrollPanel : Panel
{
    private const int ScrollBarWidth = 6;    // 6px wide bar
    private const int ScrollBarMargin = 10;  // gap between the bar and the right edge
    private const int ScrollBarRadius = 2;   // 2px radius = small rounded corners, bar shape (not capsule)
    private const int MinThumbHeight = 24;
    private const int WheelScrollStep = 48;  // px per wheel tick (matches the 48px row height)

    private Color _bg;
    private Color _thumbColor = Color.FromArgb(88, 88, 96);
    private Color _thumbHoverColor = Color.FromArgb(120, 120, 128);

    private int _scrollPos;      // current scroll offset in px
    private int _maxScroll;      // max scroll offset
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartPos;
    private bool _hover;
    private Rectangle _thumbRect;

    private readonly Panel _content;
    private bool _updating;

    /// <summary>
    /// Right-side clearance (px) between the row borders and the scrollbar.
    /// Rows stop this far from the panel's right edge; the scrollbar sits at
    /// the far right (ScrollBarWidth + ScrollBarMargin = 16) and the extra
    /// 6px is visible space between the rows and the bar.
    /// </summary>
    public int RightGap { get; set; } = 22;

    /// <summary>Container for the scrollable children (rows).</summary>
    public Panel Content => _content;

    public ThemeScrollPanel()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        _content = new Panel
        {
            Location = new Point(0, 0)
        };
        // Recompute layout whenever children change (added, removed, resized,
        // or relaid out). Dock is NOT used on children, so this is the only
        // place that positions them.
        _content.Layout += (_, _) => LayoutContent();
        _content.ControlAdded += (_, _) => LayoutContent();
        _content.ControlRemoved += (_, _) => LayoutContent();
        base.Controls.Add(_content);
    }

    /// <summary>
    /// Scrollable children are added through this property. It forwards to
    /// the internal content container, so callers can use the familiar
    /// <c>scroll.Controls.Add(row)</c> pattern (mirroring AutoScroll usage)
    /// without knowing about the inner panel.
    /// </summary>
    public new Control.ControlCollection Controls => _content.Controls;

    /// <summary>
    /// Applies the page colors. Call again on theme change (RebuildUi recreates
    /// the pages, so this naturally refreshes).
    /// </summary>
    public void ApplyTheme(Color bg, Color track, Color thumb, Color thumbHover)
    {
        _bg = bg;
        _thumbColor = thumb;
        _thumbHoverColor = thumbHover;
        _content.BackColor = bg;
        Invalidate();
    }

    private bool ScrollBarVisible => _maxScroll > 0;

    /// <summary>
    /// Manually stacks the children top-down, honoring each child's vertical
    /// Margin. Children must NOT use Dock=Top (that would let the Dock layout
    /// engine reposition them and ignore margins). Width is set to the
    /// content width so rows always span the full row area.
    /// </summary>
    private void LayoutContent()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            int y = 0;
            int rowW = Math.Max(0, _content.Width);
            // Rows are added bottom-most first, top-most last (matching the
            // old Dock=Top reverse-z-order convention), so stack them in
            // reverse collection order to get title on top, first row second,
            // etc.
            for (int i = _content.Controls.Count - 1; i >= 0; i--)
            {
                Control c = _content.Controls[i];
                c.Anchor = AnchorStyles.None;      // take full manual control
                c.Dock = DockStyle.None;           // Dock would override our Y
                c.SetBounds(0, y + c.Margin.Top, rowW, c.Height);
                y += c.Margin.Top + c.Height + c.Margin.Bottom;
            }
            _content.Height = y;
            UpdateScrollMetrics();
        }
        finally
        {
            _updating = false;
        }
    }

    // Rows span the full page width minus RightGap (the bar's gutter); the
    // scrollbar sits at the panel's far right edge.
    private void UpdateScrollMetrics()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            int outerW = Width;   // outer bounds including any Padding set by the parent
            int viewH = Height;

            _content.Width = outerW - RightGap;
            _maxScroll = Math.Max(0, _content.Height - viewH);
            _scrollPos = Math.Clamp(_scrollPos, 0, _maxScroll);
            _content.Top = -_scrollPos;
            Invalidate();
        }
        finally
        {
            _updating = false;
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        _content.Width = Width - RightGap;
        // Re-stack children to the new width.
        LayoutContent();
        UpdateScrollMetrics();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!ScrollBarVisible) return;
        int delta = e.Delta > 0 ? -WheelScrollStep : WheelScrollStep;
        SetScrollPos(_scrollPos + delta);
    }

    private void SetScrollPos(int pos)
    {
        _scrollPos = Math.Clamp(pos, 0, _maxScroll);
        _content.Top = -_scrollPos;
        Invalidate();
    }

    // ---- thumb dragging ----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!ScrollBarVisible) return;
        if (_thumbRect.Contains(e.Location))
        {
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartPos = _scrollPos;
            Capture = true;
        }
        else if (e.Button == MouseButtons.Left && IsOverScrollBar(e.Location))
        {
            // Page up/down when clicking the track.
            SetScrollPos(_scrollPos + (e.Y < _thumbRect.Y ? -ClientSize.Height : ClientSize.Height));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = ScrollBarVisible && IsOverScrollBar(e.Location);
        if (over != _hover) { _hover = over; Invalidate(); }

        if (_dragging)
        {
            int viewH = ClientSize.Height;
            int trackH = viewH - 2 * ScrollBarMargin;
            int thumbH = Math.Max(MinThumbHeight, (int)(viewH * (float)viewH / Math.Max(1, _content.Height)));
            float pxPerPos = Math.Max(1f, (trackH - thumbH) / (float)_maxScroll);
            SetScrollPos(_dragStartPos + (int)((e.Y - _dragStartY) / pxPerPos));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Capture = false; }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }

    private bool IsOverScrollBar(Point p)
        => p.X >= Width - ScrollBarWidth - ScrollBarMargin;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ScrollBarVisible) return;

        int x = Width - ScrollBarWidth - ScrollBarMargin;
        int viewH = Height;

        // Capsule-shaped thumb: semicircles on both ends (radius = width/2).
        // When ScrollBarWidth is even (e.g. 6px -> radius 3px), the two
        // semicircles are mirror images and every pixel column is symmetric.
        // Earlier 3px-corner on a 10px-wide bar was asymmetric, which is
        // why we briefly used a plain rectangle to confirm the issue.
        int thumbH = Math.Max(MinThumbHeight, (int)(viewH * (float)viewH / Math.Max(1, _content.Height)));
        int maxY = (viewH - 2 * ScrollBarMargin) - thumbH;
        int y = ScrollBarMargin + (maxY > 0 ? (int)(_scrollPos * (float)maxY / _maxScroll) : 0);
        _thumbRect = new Rectangle(x, y, ScrollBarWidth, thumbH);

        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var path = BarPath(_thumbRect);
        using (var thumbBrush = new SolidBrush(_hover || _dragging ? _thumbHoverColor : _thumbColor))
        {
            e.Graphics.FillPath(thumbBrush, path);
        }
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Default;
    }

    /// <summary>
    /// Bar-shaped thumb: a rectangle with all four corners rounded by
    /// <see cref="ScrollBarRadius"/>. When the thumb is tall (Height > Width)
    /// this looks like a normal bar with rounded ends; when the thumb is
    /// short (Height <= Width) the corners eat into each other and it
    /// degenerates into a capsule, which is fine for tiny thumbs.
    /// The radius equals half the bar's width, so left and right corners
    /// are mirror images at every thumb height.
    /// </summary>
    private static GraphicsPath BarPath(Rectangle r)
    {
        int radius = Math.Min(ScrollBarRadius, r.Width / 2);
        int d = radius * 2;
        var path = new GraphicsPath();
        // Explicit lines between arcs to guarantee exact connectivity
        // and avoid any GDI+ auto-line artefacts that might leave gaps.
        path.AddArc(r.X, r.Y, d, d, 180, 90);                          // top-left
        path.AddLine(r.X + radius, r.Y, r.Right - radius, r.Y);         // top edge
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);                   // top-right
        path.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom - radius); // right edge
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);            // bottom-right
        path.AddLine(r.Right - radius, r.Bottom, r.X + radius, r.Bottom); // bottom edge
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);                     // bottom-left
        path.AddLine(r.X, r.Bottom - radius, r.X, r.Y + radius);          // left edge
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CapsulePath(Rectangle r)
    {
        var path = new GraphicsPath();
        int d = r.Width;
        // Diameter = width guarantees radius = width/2 and the arcs fit
        // exactly within the rectangle's height when r.Height >= r.Width.
        // For tall thumbs (Height > Width), use the smaller of the two.
        int dia = Math.Min(d, r.Height);
        path.AddArc(r.X, r.Y, dia, dia, 90, 180);                            // left semicircle
        path.AddArc(r.Right - dia, r.Y, dia, dia, 270, 180);                 // right semicircle
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
