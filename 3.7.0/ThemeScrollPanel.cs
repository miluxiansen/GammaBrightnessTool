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
    /// <summary>按下时"光标屏幕 Y − 拇指顶部屏幕 Y"。拖动全程用绝对光标位置回算拇指位置，
    /// 不再累积 MouseMove 位移 —— 那样一旦光标移出窗口 / 事件丢失，拇指就会停在半路（用户实测 175% 缩放下拖到底只走半程）。</summary>
    private int _dragGrabOffset;
    /// <summary>拖动期间每 15ms 按光标绝对位置重算一次（兜底：捕获丢失时仍跟手）。</summary>
    private System.Windows.Forms.Timer? _dragTimer;
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

    /// <summary>当前滚动偏移（px）。重建/恢复用：先刷新指标再设值，内容未布局
    /// （_maxScroll=0）时设值会被夹到 0，恢复方需在布局完成后（BeginInvoke）再设。</summary>
    public int ScrollPosition
    {
        get => _scrollPos;
        set
        {
            UpdateScrollMetrics();               // 确保 _maxScroll 与内容高度为最新
            _scrollPos = Math.Clamp(value, 0, _maxScroll);
            _content.Top = -_scrollPos;
            Invalidate();
        }
    }

    public ThemeScrollPanel()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        _content = new Panel
        {
            Location = new Point(0, 0)
        };
        // Double-buffer the content so fast scrollbar drags redraw the
        // children off-screen first — without this the rows flash as they
        // are re-rendered while scrolling into view. DoubleBuffered is
        // protected on Control, so set it via reflection.
        typeof(Panel).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_content, true);
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
        BackColor = bg;  // panel itself (scrollbar gutter + uncovered area)
        Invalidate();
    }

    private bool ScrollBarVisible => _maxScroll > 0;
    /// <summary>P5 只读探针（2026-09-21）：最大滚动偏移（px）。0 = 内容未溢出、滚动条不显示。</summary>
    public int MaxScrollPosition => _maxScroll;


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

    /// <summary>
    /// 强制"重排内容 + 刷新滚动指标"。适用：需要主动重建子控件位置且无法依赖
    /// 自动布局的场景。注意：WinForms 对"已有子控件运行时改变高度/可见性"（如
    /// 折叠体展开/收起）会同步触发父内容多次自动布局（LayoutContent），位置已
    /// 自动就位——此时再用本方法会多跑一次全量 SetBounds，属于冗余重排（折叠
    /// 操作"闪一下"的来源之一）。该场景应改调 <see cref="RefreshMetrics"/>，
    /// 只刷新 _maxScroll/滚动条显隐。1107 曾误判"不触发任何事件"，已由布局
    /// 探针（_devtools/popup-render-test foldprobe）实测纠正。
    /// </summary>
    public void RefreshLayout()
    {
        LayoutContent();
        UpdateScrollMetrics();
    }

    /// <summary>
    /// 只刷新滚动指标（_maxScroll / 滚动条显隐 / _content 位置），不重排已有
    /// 子控件。用于"某子控件运行时高度已变、位置已由 WinForms 自动布局更新
    /// 到位"的场景（如显示器页折叠体展开/收起）：此时再跑一遍 LayoutContent
    /// 会对全部子控件重复 SetBounds，等于额外一次全量重排与重绘——折叠操作
    /// 时页面"闪一下"的重复渲染来源。见 SettingsForm.SetFoldBody。
    /// </summary>
    public void RefreshMetrics()
    {
        UpdateScrollMetrics();
    }

    private bool _updateLocked;

    /// <summary>
    /// 开始原子更新：通过 WM_SETREDRAW 关闭本面板及其整棵子控件树的重绘。
    /// 批量修改子控件尺寸/可见性/位置时（如折叠体展开/收起），WinForms 会在
    /// 消息循环里可能呈现"中间帧"（内容先上移/下移、文本瞬时错位再恢复的
    /// 跳动渲染）。关闭重绘后所有变更只在内存完成，配合 <see cref="EndUpdate"/>
    /// 一次性绘制终态，从系统层面杜绝中间帧。与 SuspendLayout 不同，布局照常
    /// 执行（不会引起 1427 那种整体位移）。
    /// </summary>
    public void BeginUpdate()
    {
        if (_updateLocked || !IsHandleCreated) return;
        _updateLocked = true;
        NativeMethods.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);   // WM_SETREDRAW = false
    }

    /// <summary>结束原子更新：恢复重绘并强制整树重画一次（只呈现终态）。</summary>
    public void EndUpdate()
    {
        if (!_updateLocked) return;
        _updateLocked = false;
        if (IsHandleCreated)
        {
            NativeMethods.SendMessage(Handle, 0x000B, new IntPtr(1), IntPtr.Zero); // WM_SETREDRAW = true
            Invalidate(true);
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

    private const int WS_EX_COMPOSITED = 0x02000000;

    /// <summary>
    /// 让 OS 把**整棵子树（含子控件）**合成到离屏缓冲后一次性 blit 出来。
    ///
    /// 为什么需要：本面板虽有 <c>DoubleBuffered</c>，但它只缓冲**本窗口自身**的绘制；
    /// 内容里的行/标签/开关都是**独立子 HWND**，滚动（移动 <c>_content</c>）时它们各自
    /// 触发 WM_PAINT，父窗的双缓冲完全帮不上 → 尚未渲染的区域先露空白、随后才补上，
    /// 且重绘次数翻倍（用户实测：快速拖动出现空白 + 卡顿 + CPU 从 0.1% 升到 1.5%）。
    /// WS_EX_COMPOSITED 正是为这个症状设计的：一次合成、一次输出。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_COMPOSITED;
            return cp;
        }
    }

    private void SetScrollPos(int pos)
    {
        _scrollPos = Math.Clamp(pos, 0, _maxScroll);
        _content.Top = -_scrollPos;
        Invalidate();
    }

    // ---- thumb dragging（按光标绝对屏幕位置，1:1 跟手）----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!ScrollBarVisible) return;
        if (_thumbRect.Contains(e.Location))
        {
            _dragging = true;
            // 记录"抓取偏移"：按下时光标相对拇指顶部的距离，全程保持不变。
            _dragGrabOffset = Cursor.Position.Y - PointToScreen(new Point(0, _thumbRect.Y)).Y;
            Capture = true;
            _dragTimer ??= new System.Windows.Forms.Timer { Interval = 15 };
            _dragTimer.Tick -= OnDragTimerTick;
            _dragTimer.Tick += OnDragTimerTick;
            _dragTimer.Start();
        }
        else if (e.Button == MouseButtons.Left && IsOverScrollBar(e.Location))
        {
            // 点轨道空白 = **跳到点击处**（拇指中心对齐点击点），不再是 ±一屏。
            //
            // ⛔ 2026-09-20 用户实测报告的两个问题，原实现（`_scrollPos ± ClientSize.Height`）：
            //   ① **来回弹**：一次滚一**整屏**。若 `_maxScroll` 不足一屏（设置页很多页就是），
            //      一点就 clamp 到极值 ⇒ 拇指**越过**点击点 ⇒ 下次点击 `e.Y < _thumbRect.Y`
            //      判成反方向 ⇒ 反向再跳一整屏 ⇒ 用户看到
            //      「顶部 ↔ 中间」「中间 ↔ 最底」往复弹跳（点击点没变，方向却反复）。
            //   ② 载荷太大：一屏一屏跳，短页面直接"跳到底"而无中间态。
            //
            // ✅ 改为 jump-to-click（现代 UI 通例）后：
            //   · 点到哪就停到哪，拇指中心**落在点击点** ⇒ **重复点同一位置不再变化**
            //     （目标 ≈ 当前）⇒ 从根上消除往复；
            //   · 与拖动手感一致（同一套 `maxY / _maxScroll` 映射，见 ApplyDragFromCursor）。
            int thumbH = CurrentThumbHeight();
            int maxY = Math.Max(0, (Height - 2 * ScrollBarMargin) - thumbH);
            if (maxY > 0 && _maxScroll > 0)
            {
                // 目标：拇指中心对准 e.Y（上下各留半拇指），再夹到轨道范围内
                long targetY = (long)e.Y - ScrollBarMargin - thumbH / 2;
                targetY = Math.Clamp(targetY, 0, maxY);
                SetScrollPos((int)(targetY * _maxScroll / maxY));
            }
        }
    }

    private void OnDragTimerTick(object? sender, EventArgs e) => ApplyDragFromCursor();

    /// <summary>当前拇指高度（像素）。绘制与拖动映射必须共用同一个值。</summary>
    private int CurrentThumbHeight()
    {
        int viewH = Height;
        return Math.Max(MinThumbHeight, (int)(viewH * (float)viewH / Math.Max(1, _content.Height)));
    }

    /// <summary>按光标的**绝对屏幕位置**把拇指移到鼠标所在处（与系统滚动条行为一致）。
    /// 与 MouseMove 事件是否连续无关，因此光标拖出窗口也不会"停半路"。</summary>
    private void ApplyDragFromCursor()
    {
        if (!_dragging) return;
        int maxY = Math.Max(0, (Height - 2 * ScrollBarMargin) - CurrentThumbHeight());
        int trackTopScreen = PointToScreen(new Point(0, ScrollBarMargin)).Y;
        long thumbTop = (long)Cursor.Position.Y - _dragGrabOffset - trackTopScreen;
        thumbTop = Math.Clamp(thumbTop, 0, maxY);

        // ⚠️ 2026-09-20：**像素级短路**，防"按住拇指不动也漂移"。
        //   `pos → 拇指顶像素` 与 `拇指顶像素 → pos` 都是整数除法，往返必然有取整误差
        //   （实测 pos 668 → 拇指顶 324 → 反算 687，漂移 19px）。
        //   于是"点住拇指不松手"或"再点同一位置（此时该点已在拇指内 ⇒ 走本分支）"
        //   都会看到拇指轻微移动。⇒ 只要目标像素位置与当前一致，就直接返回、不动 pos。
        int curTop = maxY > 0 ? (int)((long)_scrollPos * maxY / Math.Max(1, _maxScroll)) : 0;
        if (thumbTop == curTop) return;

        int pos = maxY > 0 ? (int)(thumbTop * _maxScroll / maxY) : 0;
        SetScrollPos(pos);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = ScrollBarVisible && IsOverScrollBar(e.Location);
        if (over != _hover) { _hover = over; Invalidate(); }

        if (_dragging)
        {
            if (!Capture) Capture = true;   // 防捕获丢失后彻底失联
            ApplyDragFromCursor();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            _dragTimer?.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dragTimer?.Stop();
            _dragTimer?.Dispose();
            _dragTimer = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }

    /// <summary>
    /// 光标是否在**可见滚动条那一列**上（拇指 + 该列的轨道空白）。
    ///
    /// ⛔ 2026-09-20 用户实测报告：原来只判 `p.X >= Width - ScrollBarWidth - ScrollBarMargin`
    ///   且**没有上界** ⇒ `[Width-16, Width]` 全算命中，把右侧那 10px
    ///   **视觉上不属于滚动条的留白**（<see cref="ScrollBarMargin"/>）也当成了轨道
    ///   ⇒ 「鼠标点击滚动条**右侧**，页面却被强行滚动」。
    /// ✅ 加上界，只认**看得见的那条**：`[Width-16, Width-10)`（宽 = <see cref="ScrollBarWidth"/>）。
    ///    这也与拇指拖动的命中区（`_thumbRect`）保持同一个 X 范围，语义一致。
    /// </summary>
    private bool IsOverScrollBar(Point p)
        => p.X >= Width - ScrollBarWidth - ScrollBarMargin
           && p.X < Width - ScrollBarMargin;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ScrollBarVisible) return;

        int x = Width - ScrollBarWidth - ScrollBarMargin;
        int thumbH = CurrentThumbHeight();
        int maxY = (Height - 2 * ScrollBarMargin) - thumbH;
        int y = ScrollBarMargin + (maxY > 0 ? (int)((long)_scrollPos * maxY / Math.Max(1, _maxScroll)) : 0);
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
