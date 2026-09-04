using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// 设置页通用自绘滑块。仿弹窗滑块样式（圆角胶囊轨道 + 圆点拇指），
/// 适配设置页主题色。拖动过程中实时触发 ValueChanged，松手触发
/// ValueCommitted（用于"拖动实时应用、松手保存"）。
/// </summary>
public sealed class SettingSlider : Control
{
    private float _min = 0f;
    private float _max = 100f;
    private float _value = 50f;
    private float _step = 1f;
    private bool _dragging;
    private bool _hover;
    private Func<float, string>? _format;

    private Color _track = Color.Gray;
    private Color _thumb = Color.Gray;
    private Color _thumbHover = Color.Gray;
    private Color _fill = Color.SteelBlue;

    /// <summary>禁用时配色：色温功能关闭时色温滑轨深色=灰填充、浅色=蓝灰填充，与亮度滑轨区分。</summary>
    private bool IsDisabled => !Enabled;
    private Color DisabledFill => ThemeManager.IsDark ? Color.FromArgb(110, 110, 116) : Color.FromArgb(90, 135, 180);
    private Color DisabledThumb => ThemeManager.IsDark ? Color.FromArgb(90, 90, 98) : Color.FromArgb(170, 170, 176);
    private Color DisabledText => ThemeManager.IsDark ? Color.FromArgb(130, 130, 130) : Color.Gray;

    public float Min
    {
        get => _min;
        set { _min = value; ClampValue(); Invalidate(); }
    }

    public float Max
    {
        get => _max;
        set { _max = value; ClampValue(); Invalidate(); }
    }

    /// <summary>
    /// 把当前值夹到 [Min,Max]。构造期（对象初始化器）Min/Max 可能先赋
    /// Min 后赋 Max，此时区间暂为倒置（min&gt;max），不能直接 Math.Clamp，
    /// 否则抛 ArgumentException。这里仅在区间有效时才夹。
    /// </summary>
    private void ClampValue()
    {
        if (_min > _max) return; // 构造期区间尚未就绪，跳过夹取
        _value = Math.Clamp(_value, _min, _max);
    }

    /// <summary>步进吸附（0 表示不吸附）。</summary>
    public float Step { get => _step; set => _step = value; }

    public float Value
    {
        get => _value;
        set
        {
            // 区间未就绪（min>max）时直接存值，避免 Math.Clamp 抛异常。
            float clamped = _min > _max ? value : Math.Clamp(value, _min, _max);
            if (Math.Abs(clamped - _value) < 0.0001f) return;
            _value = clamped;
            Invalidate();
        }
    }

    /// <summary>可选：把值格式化为显示文本（设置页滑块通常不需要内置文本）。</summary>
    public Func<float, string>? Format { get => _format; set { _format = value; Invalidate(); } }

    /// <summary>拖动/点击过程中实时触发（用于预览、实时应用）。</summary>
    public event Action<float>? ValueChanged;

    /// <summary>松手时触发（用于持久化保存）。</summary>
    public event Action<float>? ValueCommitted;

    public SettingSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 26;
        Cursor = Cursors.Hand;
    }

    public void ApplyTheme(Color track, Color thumb, Color thumbHover, Color fill)
    {
        _track = track;
        _thumb = thumb;
        _thumbHover = thumbHover;
        _fill = fill;
        Invalidate();
    }



    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 右侧预留值文本区（Format 非空时）。文本与轨道不相交。
        int valueW = 0;
        string? text = null;
        if (_format != null)
        {
            text = _format(_value);
            // 预留宽度取"实际绘制文本"与"最大值文本"两者较宽者：只按 _max 测量时，
            // 若当前值文本更宽（如位数更多/带符号），右端会被裁剪。
            string maxText = _format(_max);
            int drawnW = TextRenderer.MeasureText(text, Font).Width;
            int maxW = TextRenderer.MeasureText(maxText, Font).Width;
            valueW = Math.Min(Width / 3, Math.Max(drawnW, maxW) + 8);
        }

        int barH = Math.Max(4, (int)(Height * 0.18f));
        int barY = (Height - barH) / 2;
        int trackRadius = Math.Max(1, barH / 2);
        int radius = Math.Max(4, (int)(Height * 0.30f));
        int usableW = Math.Max(1, Width - radius * 2 - valueW);
        int barX = radius;
        int innerW = Math.Max(1, usableW);

        // Track
        using (var trackBrush = new SolidBrush(_track))
        using (var trackPath = RoundedRect(new Rectangle(barX, barY, innerW, barH), trackRadius))
        {
            g.FillPath(trackBrush, trackPath);
        }

        // Fill
        float ratio = (_max - _min) <= 0 ? 0 : (_value - _min) / (_max - _min);
        ratio = Math.Clamp(ratio, 0f, 1f);
        int fillW = (int)(innerW * ratio);
        if (fillW > 0)
        {
            using var fillBrush = new SolidBrush(IsDisabled ? DisabledFill : _fill);
            using var fillPath = RoundedRect(new Rectangle(barX, barY, fillW, barH), trackRadius);
            g.FillPath(fillBrush, fillPath);
        }

        // Thumb
        int cx = Math.Clamp(barX + fillW, barX + radius, barX + innerW - radius);
        int cy = Height / 2;
        Color thumbColor = IsDisabled ? DisabledThumb
            : (_hover || _dragging ? _thumbHover : _thumb);
        using (var thumbBrush = new SolidBrush(thumbColor))
        {
            g.FillEllipse(thumbBrush, cx - radius, cy - radius, radius * 2, radius * 2);
        }

        // Value text（右对齐，位于轨道右侧空白区）
        if (text != null)
        {
            Color textColor = IsDisabled ? DisabledText : ForeColor;
            using var brush = new SolidBrush(textColor);
            var rect = new Rectangle(Width - valueW, 0, valueW - 4, Height);
            TextRenderer.DrawText(g, text, Font, rect, textColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            UpdateFromX(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) UpdateFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            ValueCommitted?.Invoke(_value);
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private void UpdateFromX(int x)
    {
        int valueW = 0;
        if (_format != null)
        {
            valueW = Math.Min(Width / 3, TextRenderer.MeasureText(_format(_max), Font).Width + 8);
        }
        int radius = Math.Max(4, (int)(Height * 0.30f));
        int usable = Width - 2 * radius - valueW;
        if (usable <= 0) usable = 1;
        // 轨道起点为 radius（与 OnPaint 的 barX 一致），不是 (Width-usable)/2：
        // 后者在右侧有 Format 文本区(valueW>0)时会多偏移 valueW/2，导致拖动
        // 映射整体偏左、鼠标拖到最右端也达不到最大值（约差 6~7%）。
        float ratio = Math.Clamp((float)(x - radius) / usable, 0f, 1f);
        float raw = _min + ratio * (_max - _min);
        float snapped = Snap(raw);
        if (Math.Abs(snapped - _value) < 0.0001f) return;
        _value = snapped;
        Invalidate();
        ValueChanged?.Invoke(_value);
    }

    private float Snap(float v)
    {
        if (_step <= 0) return v;
        return (float)Math.Round(v / _step) * _step;
    }
}
