using System.Drawing;

namespace GammaBrightnessTool
{
    /// <summary>
    /// **B' 字体架构的唯一真相源**（2026-09-22）。
    ///
    /// 全 UI 的字体都必须经本类构造，一律返回 <see cref="GraphicsUnit.Pixel"/> 单位的
    /// Font：em 尺寸在**创建时**就写死为 `设计pt × 96/72 × dpiScale` 像素，与进程/屏幕
    /// DPI 彻底解耦。这样 WinForms 的 PerMonitorV2 隐式缩放路径
    /// （`WM_DPICHANGED_BEFOREPARENT → GetScaledFont → SetScaledFont`，只改 `Font.Size`
    /// 的 pt 值）对本项目字体**完全失效** ⇒ 不再需要任何 FontChanged 补丁。
    ///
    /// ⚠ 换算系数为**实测定标**（探针 `_devtools/_fontprobe/Program.cs`，2026-09-22）：
    /// <code>
    ///   new Font("Segoe UI", 10f)                        // pt 单位
    ///       烘焙 DPI 120 → Font.Height = 23 px
    ///       烘焙 DPI 168 → Font.Height = 32 px
    ///   new Font("Segoe UI", 16.667f, …, GraphicsUnit.Pixel) → Font.Height = 23 px
    ///   new Font("Segoe UI", 23.333f, …, GraphicsUnit.Pixel) → Font.Height = 32 px
    /// </code>
    /// 后两者与上表**逐字吻合** ⇒ `em_px = 设计pt × dpiScale × 4/3`（4/3 = 96/72）。
    /// <para/>
    /// ⛔ **不是** `设计pt × dpiScale`——该写法在 175% 下只得到 24px，比正确的 32px
    /// 小 25%（`_devtools/dpi_font_方案对比_20260922.md` §2 的字面表述未含 pt→px 换算，
    /// 实施时以本实测为准）。
    ///
    /// ⛔ 改动 <see cref="PtToPx"/> 或 <see cref="Px"/> = 全 UI 字号整体变化，
    /// 必须重跑完整 DPI 回归（设置窗 / 弹窗 / OSD / 提示 / 热键框 / 下拉框）。
    ///
    /// ⛔ **不做字体缓存**。多处调用点会在换字体后 `Dispose()` 旧实例
    /// （如 `SettingsForm.FitLabelPx` 的 5 个调用点），共享实例会被连带释放 ⇒
    /// 控件 `ToHfont()` 抛 "Parameter is not valid" 崩溃。每次 new 一个，与 B' 之前
    /// 的调用密度一致。
    /// </summary>
    internal static class DpiFont
    {
        /// <summary>pt → px 的换算系数（96/72）。见类注释的实测定标。</summary>
        internal const float PtToPx = 96f / 72f;

        /// <summary>设计 pt → 目标像素（em 尺寸）。</summary>
        internal static float Px(float dpiScale, float designPt) => designPt * dpiScale * PtToPx;

        /// <summary>按 dpiScale 生成 px 单位的 UI 字体（Segoe UI）。</summary>
        internal static Font Get(float dpiScale, float designPt, FontStyle style = FontStyle.Regular)
            => new Font("Segoe UI", Px(dpiScale, designPt), style, GraphicsUnit.Pixel);

        /// <summary>同上，指定字族（如 <c>Segoe MDL2 Assets</c>）。</summary>
        internal static Font Get(float dpiScale, string family, float designPt, FontStyle style = FontStyle.Regular)
            => new Font(family, Px(dpiScale, designPt), style, GraphicsUnit.Pixel);
    }
}
