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

    protected override void OnPaint(PaintEventArgs e)
    {
        // 启用状态：完全复用基类绘制，避免任何视觉回归。
        if (Enabled)
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
