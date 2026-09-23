using System.Drawing;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// Label 自绘变体：禁用时用主题灰色绘制文字，而非 WinForms 默认的
/// SystemColors.GrayText（Windows 深色主题下近乎黑色）。启用时与基类
/// Label 完全一致（直接委托 base.OnPaint）。
/// </summary>
public sealed class ThemedLabel : Label
{
    /// <summary>禁用文字色：与 SettingsForm.TextDim 一致（深 130 / 浅 Gray）。</summary>
    private static Color DisabledColor =>
        ThemeManager.IsDark ? Color.FromArgb(130, 130, 130) : Color.Gray;

    /// <summary>
    /// true（默认）：沿用原行为 —— 禁用时用 <see cref="DisabledColor"/> 自绘。
    ///
    /// false：**始终**用 <c>ForeColor</c> 绘制，禁用与否都不改色。
    ///
    /// ⚠️ 2026-09-20 新增此开关的原因（实测踩坑）：
    ///   `Enabled` 是 WinForms 的**级联**属性 —— getter 会逐级向上查父容器。
    ///   因此"改 Enabled 来表达灰化"这种做法，一旦某个祖先容器处于禁用状态，
    ///   即便把本控件的 Enabled 设回 true，绘制仍然走禁用分支 ⇒
    ///   **文字变不回正常颜色**（用户实测："功能关闭停止生效时，文字还是灰色的"）。
    ///   由调用方显式接管 ForeColor 时把本开关置 false，颜色就完全可控、不受级联干扰。
    /// </summary>
    public bool GrayWhenDisabled { get; set; } = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        // 启用状态（或调用方已接管配色）：完全复用基类绘制，避免任何视觉回归。
        if (Enabled || !GrayWhenDisabled)
        {
            base.OnPaint(e);
            return;
        }

        // 禁用状态：基类会用 GrayText（深色下是黑色），这里改成主题灰。
        var flags = TextFormatFlags.NoPrefix;
        if (AutoSize) flags |= TextFormatFlags.SingleLine;
        else flags |= TextFormatFlags.WordBreak;
        if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;

        switch (TextAlign)
        {
            case ContentAlignment.TopLeft:
                flags |= TextFormatFlags.Top | TextFormatFlags.Left; break;
            case ContentAlignment.TopCenter:
                flags |= TextFormatFlags.Top | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.TopRight:
                flags |= TextFormatFlags.Top | TextFormatFlags.Right; break;
            case ContentAlignment.MiddleLeft:
                flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left; break;
            case ContentAlignment.MiddleCenter:
                flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.MiddleRight:
                flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right; break;
            case ContentAlignment.BottomLeft:
                flags |= TextFormatFlags.Bottom | TextFormatFlags.Left; break;
            case ContentAlignment.BottomCenter:
                flags |= TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter; break;
            case ContentAlignment.BottomRight:
                flags |= TextFormatFlags.Bottom | TextFormatFlags.Right; break;
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, DisabledColor, flags);
    }
}
