using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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

    // 用户可调不透明度（%），通用设置页滑轨写入；默认 70（原固定值）。
    private int _opacityPercent = 70;
    public int OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            _opacityPercent = Math.Clamp(value, 40, 100);
            ApplyOpacity();
        }
    }

    /// <summary>按当前 OpacityPercent 应用不透明度（100% = 不透明）。</summary>
    public void ApplyOpacity()
        => Opacity = _opacityPercent >= 100 ? 1.0 : _opacityPercent / 100.0;

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
        // 不透明度由 MainController 按用户设置（OverlayOpacityPercent，默认 70）
        // 在构造后立即设置并应用，此处不再硬编码。
        TopMost = true;

        // 整窗双缓冲：滚轮快速连调时每秒多次重绘，无双缓冲会看到滑轨/文字
        // 逐帧擦除重画的闪烁。
        DoubleBuffered = true;

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
        // 命中区面板自绘滑轨；开双缓冲避免每次调亮度时轨道/thumb 闪烁。
        typeof(Panel).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_sliderHitArea, true);

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

    // ---- 多行 OSD（独立模式）：每屏滑轨可拖动 ----

    /// <summary>根据 Y 坐标命中第几行滑轨（-1 = 未命中）。多行几何与绘制/布局一致。</summary>
    private int HitTestRow(int y)
    {
        if (!_multiRowMode || _displayRows.Count == 0) return -1;
        float dpiScale = _osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f;
        int rowH = (int)(22 * dpiScale);
        int topPad = (int)(4 * dpiScale);
        int idx = (y - topPad) / rowH;
        if (idx >= 0 && idx < _displayRows.Count && !_displayRows[idx].Enabled) return -1;
        return (idx >= 0 && idx < _displayRows.Count) ? idx : -1;
    }

    /// <summary>命中行的滑轨左起点（不包含右侧值列，拖动只在滑轨区域生效）。</summary>
    private int RowTrackLeft => (int)(6 * (_osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f));

    /// <summary>命中行的滑轨可交互宽度（与 MultiRowTrackWidth 一致）。</summary>
    private int RowTrackWidthHit => MultiRowTrackWidth(_osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_multiRowMode) return;
        int idx = HitTestRow(e.Y);
        if (idx < 0) return;
        _dragRowIndex = idx;
        // 捕获鼠标，拖动可越出 OSD 边界持续跟随
        Capture = true;
        // 归一化 X 到滑轨 [left, left+trackWidth]，取当前亮度作为起点
        UpdateRowBrightnessFromMouse(idx, e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragRowIndex >= 0)
        {
            UpdateRowBrightnessFromMouse(_dragRowIndex, e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragRowIndex = -1;
        Capture = false;
    }

    /// <summary>把行内鼠标 X 映射为亮度并触发行级事件。</summary>
    private void UpdateRowBrightnessFromMouse(int rowIndex, int x)
    {
        if (rowIndex < 0 || rowIndex >= _displayRows.Count) return;
        var row = _displayRows[rowIndex];
        if (!row.Enabled) return;
        int left = RowTrackLeft;
        int w = RowTrackWidthHit;
        float ratio = Math.Clamp((float)(x - left) / w, 0f, 1f);
        float newB = Math.Clamp((float)Math.Round(ratio * 100) / 100f, 0f, 1f);
        if (newB == row.Brightness) return;
        // 更新本地副本供重绘；同时通知 MainController 改对应屏
        _displayRows[rowIndex] = row with { Brightness = newB };
        if (rowIndex < _rowLabels.Count)
        {
            _rowLabels[rowIndex].Text = $"{Math.Round(newB * 100)}%";
        }
        OnRowBrightnessChanged?.Invoke(row.EdidId, newB);
        Invalidate();
    }

    /// <summary>
    /// Draws the whole slider: gray track, white fill and the thumb circle.
    /// Everything is drawn on this opaque panel in one pass so nothing can
    /// be painted over the circle afterwards (a transparent panel would let
    /// the track below repaint on top of it when the fill shrinks).
    /// </summary>
    /// <summary>
    /// 3.6.0: 多行 OSD 轨道绘制（独立模式）。每启用屏一行：轨道 + 填充 + 拇指。
    /// 停用行整轨灰显（无填充、拇指置最左）；行顺序与弹窗一致。
    /// </summary>
    private void OverlayPaint_MultiRow(object? sender, PaintEventArgs e)
    {
        if (_displayRows.Count == 0) return;
        // 与 ShowDisplays 的布局共用同一缩放比例与滑轨宽度：
        // 滑轨只占左侧，右侧留给值文本列，两者不再重叠。
        float dpiScale = _osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f;
        int rowH = (int)(22 * dpiScale);
        int margin = (int)(6 * dpiScale);
        int topPad = (int)(4 * dpiScale);
        int barH = Math.Max(3, (int)(4 * dpiScale));
        int barW = MultiRowTrackWidth(dpiScale);
        int trackRadius = Math.Max(1, barH / 2);

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        for (int i = 0; i < _displayRows.Count; i++)
        {
            var row = _displayRows[i];
            int barTop = topPad + i * rowH + (rowH - barH) / 2;
            var trackRect = new Rectangle(margin, barTop, barW, barH);

            // Track
            using (var track = new SolidBrush(ThemeManager.PopupTrack))
            using (var trackPath = RoundedRect(trackRect, trackRadius))
            {
                g.FillPath(track, trackPath);
            }

            if (!row.Enabled)
            {
                // 停用行：全灰无填充，拇指置最左
                int r = Math.Max(3, (int)(4 * dpiScale));
                int cx = margin + r;
                int cy = barTop + barH / 2;
                using var brush = new SolidBrush(ThemeManager.PopupThumbOutline);
                g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                continue;
            }

            // Fill
            int fillW = Math.Max(1, (int)(barW * row.Brightness));
            var fillRect = new Rectangle(margin, barTop, fillW, barH);
            using (var fill = new SolidBrush(ThemeManager.PopupFill))
            using (var fillPath = RoundedRect(fillRect, trackRadius))
            {
                g.FillPath(fill, fillPath);
            }

            // Thumb
            int radius = Math.Max(3, (int)(4 * dpiScale));
            int cx2 = Math.Min(margin + fillW, margin + barW - radius);
            cx2 = Math.Max(cx2, margin + radius);
            int cy2 = barTop + barH / 2;
            using (var brush = new SolidBrush(ThemeManager.PopupThumb))
            using (var pen = new Pen(ThemeManager.PopupThumbOutline, 1f))
            {
                g.FillEllipse(brush, cx2 - radius, cy2 - radius, radius * 2, radius * 2);
                g.DrawEllipse(pen, cx2 - radius, cy2 - radius, radius * 2, radius * 2);
            }
        }
    }


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
        // 3.6.0: 多行模式同步行标签颜色并重绘轨道
        foreach (var lbl in _rowLabels) lbl.ForeColor = ThemeManager.PopupText;
        if (_multiRowMode) Invalidate();
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

    /// <summary>
    /// 每屏一行的 OSD 数据（独立模式）。EdidId 用于把拖动映射回具体显示器；
    /// OSD 不显示名称，仅按行顺序对应各屏。Enabled=false 的行冻结显示当前值。
    /// </summary>
    public readonly record struct DisplayRow(string EdidId, float Brightness, bool Enabled);

    private readonly List<DisplayRow> _displayRows = new();
    private bool _multiRowMode;
    /// <summary>
    /// 多行 OSD 当前使用的缩放比例（来自光标所在显示器的真实 DPI）。
    /// 布局、绘制、定位三处共用，避免各自读 DeviceDpi 得到不一致的陈旧值。
    /// </summary>
    private float _osdDpiScale = 1f;
    /// <summary>当前正在拖动的是第几行（-1 = 未拖动）。</summary>
    private int _dragRowIndex = -1;

    /// <summary>多行 OSD 中某一行亮度被拖动改变时触发（edidId, 新亮度）。</summary>
    public event Action<string, float>? OnRowBrightnessChanged;

    /// <summary>多行 OSD 右侧值文本列的宽度（滑轨右方，3.6.0 定稿：不显示显示器名称）。
    /// 需容纳 "100%" 及部分语言较长的禁用文本（如法语 Désactivé），过窄会被省略号截成 "1..."。</summary>
    private int MultiRowValueWidth(float dpiScale) => (int)(54 * dpiScale);

    /// <summary>滑轨与右侧值文本列之间的间距。</summary>
    private int MultiRowColumnGap(float dpiScale) => (int)(4 * dpiScale);

    /// <summary>
    /// 多行 OSD 的滑轨宽度 = 客户区宽 - 左右边距 - 值列宽 - 列间距。
    /// 布局（ShowDisplays）与绘制（OverlayPaint_MultiRow）共用此计算，避免两处漂移。
    /// </summary>
    private int MultiRowTrackWidth(float dpiScale)
    {
        int margin = (int)(6 * dpiScale);
        int w = ClientSize.Width - margin * 2 - MultiRowValueWidth(dpiScale) - MultiRowColumnGap(dpiScale);
        return Math.Max(20, w);
    }

    /// <summary>
    /// 光标所在显示器的真实 DPI（GetDpiForMonitor 实时查询）。DeviceDpi 在隐藏窗口上
    /// 收不到 WM_DPICHANGED，跨不同缩放比例的显示器时是陈旧值。
    /// </summary>
    private int GetCursorMonitorDpi()
    {
        var pt = new NativeMethods.POINT { x = Cursor.Position.X, y = Cursor.Position.Y };
        IntPtr hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return (int)dpiX;
        }
        return DeviceDpi > 0 ? DeviceDpi : 96;
    }

    /// <summary>
    /// 光标所在显示器的工作区，物理像素（MonitorFromPoint + GetMonitorInfo rcWork）。
    /// Screen.WorkingArea 是逻辑像素，与已按 dpiScale 放大的 OSD 尺寸混算会导致偏移。
    /// </summary>
    private Rectangle GetCursorMonitorWorkArea()
    {
        var pt = new NativeMethods.POINT { x = Cursor.Position.X, y = Cursor.Position.Y };
        IntPtr hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero)
        {
            var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                return new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
            }
        }
        return Screen.FromPoint(Cursor.Position).WorkingArea;
    }

    /// <summary>
    /// 独立模式下显示所有启用屏的 OSD（每屏一行，不显示名称，顺序与弹窗一致）。
    /// 多行时滑块不可交互（仅显示）；统一模式仍用单行交互滑块。
    /// </summary>
    public void ShowDisplays(IReadOnlyList<DisplayRow> rows)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<IReadOnlyList<DisplayRow>>(ShowDisplays), rows);
            return;
        }

        // 行数不变的连调（滚轮连续滚动）：不重建标签，原位更新数值 + 重绘。
        // 否则每次滚轮都 Remove/Dispose/New 整组 Label，会看到明显闪烁跳跃。
        if (_multiRowMode && rows.Count > 0 && _rowLabels.Count == rows.Count)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                _displayRows[i] = rows[i];
                _rowLabels[i].Text = rows[i].Enabled
                    ? $"{Math.Round(rows[i].Brightness * 100)}%"
                    : Localization.Get("DisplayDisabled");
            }
            Invalidate();
            if (!Visible) base.Show();
            PositionAboveTaskbar();
            _hideTimer.Stop();
            _hideTimer.Start();
            return;
        }

        // 3.6.0 多行 OSD：独立模式（PerMonitorEnabled）下所有启用屏每屏一行，
        // 显示百分比值（不显示名称、按顺序对应 A/B/C）、不限高；停用屏显示禁用文本。
        // 重写要点：Paint 事件先 -= 再 +=（幂等，防重复挂载累积导致卡顿）；
        // 行标签先 Controls.Remove 再 Dispose（防已释放控件仍占位绘制）。
        _displayRows.Clear();
        _displayRows.AddRange(rows);
        bool multi = rows.Count > 0;

        if (!multi)
        {
            // 空列表：隐藏多行并走单行逻辑
            ExitMultiRowMode();
            return;
        }

        // 用光标所在显示器的真实 DPI：DeviceDpi 在隐藏窗口上收不到 WM_DPICHANGED，
        // 跨不同缩放的显示器时是陈旧值，会让 OSD 尺寸与内部布局按错误比例计算。
        float dpiScale = GetCursorMonitorDpi() / 96.0f;
        _osdDpiScale = dpiScale;
        int rowH = (int)(22 * dpiScale);
        int margin = (int)(6 * dpiScale);
        int topPad = (int)(4 * dpiScale);
        int barH = Math.Max(3, (int)(4 * dpiScale));
        int height = topPad * 2 + rowH * rows.Count;
        // 比单行 OSD(120) 加宽：值文本列移到滑轨右侧后需要额外列宽；
        // 滑轨长度仍保持与单行模式相当（约 114 逻辑像素）。
        int width = (int)(184 * dpiScale);

        Size = new Size(width, height);
        ApplyRoundedCorners(8);

        _label.Visible = false;
        _sliderHitArea.Visible = false;
        _multiRowMode = true;
        // 幂等挂载：先退订再订阅，连续滚轮快速复用不会累积 handler
        Paint -= OverlayPaint_MultiRow;
        Paint += OverlayPaint_MultiRow;

        // 重建行标签：先全部 Remove + Dispose，再按当前行数新建
        foreach (var c in _rowLabels)
        {
            Controls.Remove(c);
            c.Dispose();
        }
        _rowLabels.Clear();
        int trackW = MultiRowTrackWidth(dpiScale);
        int valueX = margin + trackW + MultiRowColumnGap(dpiScale);
        for (int i = 0; i < rows.Count; i++)
        {
            var lbl = new Label
            {
                ForeColor = ThemeManager.PopupText,
                BackColor = Color.Transparent,
                Font = _cachedFont ?? new Font("Segoe UI", Math.Max(7, (int)(7 * dpiScale)), FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                // 值写在滑轨右方（3.6.0 定稿：OSD 不显示显示器名称）。
                // 原先值标签铺满整行，文字直接压在滑轨上；现在只占右侧值列，
                // 长文本（如法语 Désactivé）省略号截断，不挤占滑轨。
                AutoEllipsis = true,
                Text = rows[i].Enabled ? $"{Math.Round(rows[i].Brightness * 100)}%" : Localization.Get("DisplayDisabled"),
                AutoSize = false,
                Location = new Point(valueX, topPad + i * rowH),
                Size = new Size(MultiRowValueWidth(dpiScale), rowH)
            };
            _rowLabels.Add(lbl);
            Controls.Add(lbl);
        }

        Invalidate();
        if (!Visible)
        {
            base.Show();
        }
        // 定位放在 Show 之后：SetWindowPos 对已创建的窗口才稳定，
        // 行数变化时（热插拔/停用屏）也能重新贴合任务栏上方。
        PositionAboveTaskbar();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>退出多行模式：退订绘制、恢复单行布局、清理行标签。
    /// 仅当确实处于多行状态（窗口被放大过）才恢复紧凑尺寸——纯单行模式下
    /// 窗口尺寸在构造时已按 DeviceDpi 设好，绝不能在 Hide/Show 中改写，
    /// 否则会把正常尺寸的 OSD 缩坏（曾出现只剩 1px 白点）。</summary>
    private void ExitMultiRowMode()
    {
        _dragRowIndex = -1;
        bool wasMultiRow = _multiRowMode;
        if (wasMultiRow)
        {
            Paint -= OverlayPaint_MultiRow;
            _multiRowMode = false;
        }
        _label.Visible = true;
        _sliderHitArea.Visible = true;
        foreach (var c in _rowLabels)
        {
            Controls.Remove(c);
            c.Dispose();
        }
        _rowLabels.Clear();
        // 多行放大过的窗口切回单行时恢复紧凑尺寸；_osdDpiScale 本身是缩放系数。
        if (wasMultiRow)
        {
            float dpiScale = _osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f;
            RestoreSingleRowSize(dpiScale);
        }
    }

    private readonly List<Label> _rowLabels = new();

    /// <summary>
    /// 把 OSD 尺寸恢复为单行（紧凑）尺寸，并对齐圆角。仅在当前尺寸非单行尺寸时
    /// 才改写，避免每次 Show 都触发窗口/region 重建。
    /// </summary>
    private void RestoreSingleRowSize(float dpiScale)
    {
        int compactW = (int)(120 * dpiScale);
        int compactH = (int)(38 * dpiScale);
        if (ClientSize.Width != compactW || ClientSize.Height != compactH)
        {
            Size = new Size(compactW, compactH);
            ApplyRoundedCorners(8);
        }
    }

    /// <summary>把 OSD 定位到光标所在屏工作区底部（多行模式用）。</summary>
    private void PositionAboveTaskbar()
    {
        // 工作区取物理像素：Screen.WorkingArea 是逻辑像素，而 Width/Height 已按
        // dpiScale 放大，混算会让 OSD 在高缩放下明显偏移（175% 时约偏 60px）。
        var workingArea = GetCursorMonitorWorkArea();
        float dpiScale = _osdDpiScale > 0 ? _osdDpiScale : DeviceDpi / 96.0f;
        int osdX = workingArea.Left + (workingArea.Width - Width) / 2;
        int osdY = workingArea.Bottom - Height - (int)(10 * dpiScale);
        osdX = Math.Max(workingArea.Left, Math.Min(osdX, workingArea.Right - Width));
        osdY = Math.Max(workingArea.Top, Math.Min(osdY, workingArea.Bottom - Height));
        // 走 SetWindowPos 的坐标系与物理工作区一致（同 BrightnessPopup 的做法）；
        // 只移动不改尺寸，避免触发圆角 region 重建。
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, osdX, osdY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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

        // 单行模式入口：仅当上一次确实处于多行（独立控制）状态时才退出多行并恢复
        // 单行紧凑尺寸。纯单行模式绝不能在 Show() 里改窗口尺寸——_osdDpiScale 是
        // 缩放系数而非 DPI，若把它再除以 96 会把窗口缩成 ~1px（只剩一个白点），
        // 这正是默认 OSD "消失" 的根因。窗口尺寸在构造时已按 DeviceDpi 设好，
        // 3.5.0 起从未在 Show() 中改过。
        if (_multiRowMode)
        {
            ExitMultiRowMode();
        }

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

        // Position: centered above taskbar（3.5.0 原版逻辑坐标路径：窗口尺寸构造时已按
        // DeviceDpi 设好，与 Screen.WorkingArea(逻辑) 同坐标系，混算问题由 DPI 变更
        // 时的重排块处理。此前改成物理 SetWindowPos + 运行时改尺寸导致默认 OSD 塌缩。）
        var cursorPos = Cursor.Position;
        var screen = Screen.FromPoint(cursorPos);
        var workingArea = screen.WorkingArea;

        int osdX = workingArea.Left + (workingArea.Width - Width) / 2;
        int osdY = workingArea.Bottom - Height - (int)(10 * dpiScale);

        osdX = Math.Max(workingArea.Left, Math.Min(osdX, workingArea.Right - Width));
        osdY = Math.Max(workingArea.Top, Math.Min(osdY, workingArea.Bottom - Height));

        // 位置未变就不重复移动：滚轮连调时每次 Show() 若都 Location=同值，
        // 会反复触发窗口移动 + 区域重绘，造成肉眼可见的跳跃/闪烁。
        if (Location.X != osdX || Location.Y != osdY)
        {
            Location = new Point(osdX, osdY);
        }

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

        // 3.6.0: 多行模式退出时恢复单行布局（标签/滑轨可见）
        ExitMultiRowMode();
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

