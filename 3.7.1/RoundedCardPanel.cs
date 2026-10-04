using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// A rounded "card" container used as the frame of a setting row.
/// OnPaint fills the corners with the page background, draws a rounded
/// background and a 1px rounded border. The inner panel is inset and its
/// Region is rounded too, so its square corners never cover the border arcs
/// (that is what made the corners invisible before).
/// </summary>
public sealed class RoundedCardPanel : Panel
{
    public const int CornerRadius = 6;

    private Color _pageBg;
    private Color _bg;
    private Color _border;

    /// <summary>Inset from the card edge for children (px, not DPI-scaled).</summary>
    public int ContentInset { get; set; } = 1;

    /// <summary>Inner panel that children should be added to.</summary>
    public Panel Inner { get; }

    public RoundedCardPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Inner = new Panel
        {
            BackColor = _bg
        };
        Controls.Add(Inner);
    }

    /// <summary>Applies theme colors: page background (corners), card
    /// background, and the 1px border.</summary>
    public void ApplyTheme(Color pageBg, Color bg, Color border)
    {
        _pageBg = pageBg;
        _bg = bg;
        _border = border;
        Inner.BackColor = bg;
        Invalidate();
        UpdateInnerRegion();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        // Inset the inner panel so its content never reaches the rounded
        // border; then round the inner panel's own Region so its square
        // corners do not cover the border arcs.
        Inner.SetBounds(ContentInset, ContentInset,
            Math.Max(0, Width - ContentInset * 2),
            Math.Max(0, Height - ContentInset * 2));
        UpdateInnerRegion();
    }

    private void UpdateInnerRegion()
    {
        if (Inner.Width < 2 || Inner.Height < 2) return;
        int radius = Math.Max(1, CornerRadius - ContentInset);
        using var path = RoundedRect(new Rectangle(0, 0, Inner.Width - 1, Inner.Height - 1), radius);
        Inner.Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // UserPaint: draw everything ourselves, starting with the page
        // background so the four corners show the page color, not the
        // card's square background.
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pageBrush = new SolidBrush(_pageBg))
        {
            e.Graphics.FillRectangle(pageBrush, ClientRectangle);
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, CornerRadius);

        // Rounded background
        using (var bgBrush = new SolidBrush(_bg))
        {
            e.Graphics.FillPath(bgBrush, path);
        }
        // 1px rounded border
        using (var pen = new Pen(_border))
        {
            e.Graphics.DrawPath(pen, path);
        }
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
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
