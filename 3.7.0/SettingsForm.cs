using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// Non-modal settings window with a left navigation sidebar
/// (通用设置 / 快捷键 / 版本信息) and a right content area.
/// Individual settings pages are implemented incrementally;
/// this version ships the shell with placeholder content.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Panel _navPanel;
    private readonly List<Label> _navItems = new();
    private int _navSelectedIndex;
    private readonly Panel _contentPanel;
    private readonly Label _versionLabel;
    /// <summary>首次 Shown 后置 true：此后 DpiChanged 才做整窗重建（避免构造期误触发）。</summary>
    private bool _dpiRelayoutReady;
    /// <summary>DPI 变更重建防抖（WM_DPICHANGED 可能连续到达）。</summary>
    private System.Windows.Forms.Timer? _dpiDebounce;
    // ⛔ B'（2026-09-22）：原有 `NavFixedFont` / `TitleFixedFont` 两个静态字体
    // 字段已删除。它们只服务于 `KeepFontFixed` / `AttachFontFix` / 旧的
    // `RebuildSkeletonFonts`「把被 WinForms 缩放的 pt 字体拉回固定 pt」这套补丁；
    // B' 起字体一律是 `GraphicsUnit.Pixel`（见 `UiFont`），WinForms 不会再隐式
    // 缩放它们 ⇒ 三个补丁全部成为不必要的，已一并删除。
    // 静态字段也天然拿不到 `_dpiScale`，无法参与 px 换算。

    /// <summary>
    /// 左导航 7 页的本地化键，索引即导航顺序。
    /// 同时被自动化接口（<see cref="NavigateTo"/>）当作页面键使用 —— 顺序即索引，
    /// 勿因布局调整改动顺序（导航条目用显式坐标定位，本就不依赖 z 序）。
    /// </summary>
    private static readonly string[] NavKeys =
    {
        "SettingsGeneral", "SettingsBrightness", "SettingsColorTemp",
        "SolarAdjust", "SettingsHotkeys", "SettingsMonitors",
        "AppWhitelist",   // 2026-09-14 §16：白名单插在「显示器」与「版本信息」之间
        "SettingsAbout"
    };

    /// <summary>
    /// 导航条目的自动化 ID（P5），与 <see cref="NavKeys"/> 同序 ——
    /// 英文、稳定、与 9 种显示语言解耦，切语言后测试脚本不用改（设计稿 §15.4）。
    /// </summary>
    private static readonly string[] NavActionIds =
    {
        "Nav_General", "Nav_Brightness", "Nav_ColorTemp",
        "Nav_Solar", "Nav_Hotkeys", "Nav_Monitors",
        "Nav_AppWhitelist",   // 2026-09-14 §16：与 NavKeys 同序（漏加会 IndexOutOfRange）
        "Nav_About"
    };

    /// <summary>
    /// 页面域前缀（P5）：这些域的动作注册随页面重建而失效，RebuildUi 前必须清理，
    /// 否则陈旧 ID 会指向已 Dispose 的控件。
    /// 窗体外壳域（Win_ / Nav_）只创建一次、不参与重建，故不在此列。
    /// </summary>
    private static readonly string[] PageActionPrefixes =
    {
        "Gen_", "Bri_", "Tmp_", "Sol_", "Hk_", "Mon_", "Wl_", "Abt_"
    };
    private Panel _generalPage;
    private Panel _brightnessPage;
    private Panel _colorTempPage;
    private Panel _solarPage;
    private Panel _hotkeysPage;
    private Panel _aboutPage;
    private Panel _monitorsPage;   // 3.6.0 第 7 页：显示器
    private Panel _appWhitelistPage;   // 2026-09-14 第 8 页：应用白名单（§16）
    private ToggleSwitch? _allHotKeysToggle; // master switch on the hotkeys page
    // 快捷键页所有子开关行（row/toggle/是否色温行/持久值 getter），供
    // SyncHotKeySubToggles 在色温总开关或主开关变化时统一同步 UI 状态
    // （页面静态构建不复用重建，跨页操作色温开关后快捷键页必须主动刷新，
    // 否则 UI 陈旧导致死锁）。
    private readonly List<(Panel Row, ToggleSwitch Toggle, bool IsTemp, Func<bool> Getter)> _hotKeyToggleRows = new();
    // 3.6.0 显示器页：独立控制总开关 + 受控显示器子开关行，供总开关变化时
    // 统一同步（仿 SyncHotKeySubToggles：总开关关→子开关强制关+禁用并保持持久值；
    // 总开关开→恢复各子开关持久值并解锁）。
    private ToggleSwitch? _perMonitorToggle;
    private readonly List<(ToggleSwitch Toggle, Func<bool> Getter)> _monitorSubToggles = new();
    // 色温总开关变化时，同步刷新时间调整页的滑块启停（updateSolarState 引用）。
    private Action? _refreshSolarState;
    // Debounce: LanguageChanged fires twice per language switch
    // (Setting= then Current=), so coalesce them into one RebuildUi.
    private System.Windows.Forms.Timer? _rebuildDebounce;
    // 禁用模式（右键菜单"禁用"）下锁定的调节控件（滑轨/挡位）。
    // Restore 委托在解锁时恢复各自原有 Enabled 逻辑（如色温预设跟随色温总开关）。
    private readonly List<(Control Ctrl, Func<bool> Restore)> _disableLocked = new();
    private bool _disableLockActive;
    private System.Windows.Forms.Timer? _disableUiTimer;
    private ThemedComboBox? _disableCombo;
    private bool _syncingDisable;
    // 挡位下拉同步回调（页面构建时赋值，1 秒轮询 Timer 兜底刷新，
    // 保证时间调整（日出日落）自动变化时挡位下拉也能实时跟随）。
    private Action? _refreshLevelSelection;
    private Action? _refreshLevelDisplay;
    private Action? _refreshPresetSelection;
    private Action? _refreshPresetDisplay;
    private int _lastSyncBrightnessPct = -1;
    private int _lastSyncTemperatureK = -1;

    // Theme-aware palette. The whole window (background, text, borders,
    // navigation, combos, rows) re-reads these on rebuild, so switching
    // theme rebuilds everything with the new colors.
    private static bool Dark => ThemeManager.IsDark;
    private static Color Bg => Dark ? Color.FromArgb(30, 30, 30) : Color.White;
    private static Color BgInner => Dark ? Color.FromArgb(37, 37, 38) : Color.White;
    // Input controls (combo boxes, hotkey capture boxes): slightly lighter
    // than the card inner background so the field stands out from the card
    // and the options/items are clearly distinguishable from the field.
    private static Color InputBg => Dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(250, 250, 250);
    private static Color BgNav => Dark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(245, 245, 245);
    private static Color BgNavSelected => Dark ? Color.FromArgb(92, 92, 98) : Color.FromArgb(225, 230, 240);
    private static Color Border => Dark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(205, 205, 205);
    private static Color TextMain => Dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);
    private static Color TextSub => Dark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(70, 70, 70);
    private static Color TextDim => Dark ? Color.FromArgb(130, 130, 130) : Color.Gray;
    private static Color Accent => Dark ? Color.FromArgb(0, 120, 215) : Color.FromArgb(0, 120, 215);

    // Slim scrollbar palette (theme-aware, close to the page background so
    // it stays discreet). Track is nearly invisible; the thumb is a subtle
    // gray rounded bar.
    private static Color Track => Dark ? Color.FromArgb(38, 38, 42) : Color.FromArgb(238, 238, 240);
    private static Color Thumb => Dark ? Color.FromArgb(90, 90, 98) : Color.FromArgb(178, 178, 184);
    private static Color ThumbHover => Dark ? Color.FromArgb(122, 122, 132) : Color.FromArgb(150, 150, 158);

    // DPI scale factor (DeviceDpi / 96). Fixed dimensions are multiplied by
    // this so everything grows together at 125/150/175% instead of only the
    // fonts scaling and clipping fixed-height controls.
    // 构造时冻结：窗口尺寸与控件布局在创建 DPI 下确定后不再随 DPI 变化
    // （用户偏好：窗口大小固定、观感一致；跨 DPI 场景内容由滚动条容纳）。
    // 3.6.0 运行中 DPI 变更（RelayoutForCurrentDpi）会重算此值并整窗重建。
    private float _dpiScale;
    /// <summary>最近一次 DpiChanged 携带的目标 DPI（重开新窗时按其强制重排，避免
    /// new SettingsForm() 时 Handle 未创建、DeviceDpi 读到旧值导致布局按旧 DPI）。</summary>
    private int _pendingDpi;
    // ---- Self-drawn title bar ----
    // Drawn as a normal themed control so theme switches repaint it
    // instantly (no DWM caption lag). Fixed dialog: title + min + close.
    private const int _titleBarH = 36;          // base height, scaled by _dpiScale
    private const int _titleBtnW = 46;          // base width per caption button
    private const int _pinGlyphSize = 20;        // pin glyph display size, scaled by _dpiScale
    private Panel? _titleBar;                    // the caption strip
    private Label? _titleLabel;                  // window title text
    private Label? _btnMin, _btnClose;           // caption buttons (self-drawn)
    private Label? _btnPin;                      // pin (always-on-top) caption button
    private ToolTip? _trayVisTip;                // P1/P2″：托盘常驻开关的提示（非模态）
    private ToggleSwitch? _trayVisibleToggle;    // P1：托盘常驻开关（阶梯结果回填提示用）
    private ToolTip? _pinToolTip;                // pin tooltip (localized, kept alive)
    private ToolTip? _selfHealTip;               // gamma self-heal row tooltip
    private ToolTip? _fullscreenTip;             // fullscreen pause row tooltip
    // 2026-09-19 全屏「按显示器生效」子开关（通用设置页，展开式：总开关开启后才出现）
    private ToggleSwitch? _fsPerMonToggle;
    private Panel? _fsPerMonRow;
    private ToolTip? _fsPerMonTip;
    /// <summary>程序性给子开关赋 Checked 时置位，避免误写配置（与白名单 _whitelistPerMonSilent 同款）。</summary>
    private bool _fsPerMonSilent;
    /// <summary>
    /// 全屏子开关**当前**的悬停提示文案（2026-09-20）。
    /// 既是 <see cref="UpdateFsPerMonHint"/> 的输出，也供只读探针 <c>Gen_FsPerMonHint</c> 回读 ——
    /// ToolTip 文本对外部不可读（WinForms 自绘），此前"文案对不对"只能靠用户肉眼看。
    /// </summary>
    private string _fsPerMonHintText = "";
    // ⚠ 这三项**必须是字段**：用户可能在**显示器页**开关「独立控制」，而子开关在**通用页**。
    //   写成 BuildGeneralPage 的局部变量时，显示器页拿不到 ⇒ 子开关的锁定态要重开设置窗
    //   才刷新（用户 2026-09-19 实测报告）。实现见 SyncFullscreenPerMonRow()。
    private ThemeScrollPanel? _generalScroll;
    private Panel? _generalFullscreenRow;
    private ToggleSwitch? _generalFullscreenToggle;
    /// <summary>显示器页「独立控制」单屏锁定提示（2026-09-19 D5）。</summary>
    private ToolTip? _monitorsTip;
    /// <summary>时间调整页滑轨「为何被锁定」提示（2026-09-19 D6）。</summary>
    private ToolTip? _solarTip;
    private Icon? _windowIcon;                   // taskbar button icon (borderless)
    private static SettingsForm? _instance;

    /// <summary>设置窗当前是否打开（进程自动重启时据此决定是否带 --show-settings 恢复）。</summary>
    public static bool IsOpen => _instance != null && !_instance.IsDisposed && _instance.Visible;
    // =====================================================================
    //  P5 只读探针（2026-09-21 用户要求）：三类"外部读不到"的 UI 状态
    // =====================================================================
    private IEnumerable<ToolTip> AllTips()
    {
        ToolTip?[] all = { _trayVisTip, _pinToolTip, _selfHealTip, _fullscreenTip,
                           _fsPerMonTip, _monitorsTip, _solarTip, _whitelistToolTip };
        foreach (ToolTip? t in all)
            if (t != null) yield return t;
    }

    private static Control? FindById(Control root, string id)
    {
        foreach (Control c in root.Controls)
        {
            if (string.Equals(c.Name, id, StringComparison.Ordinal)) return c;
            Control? hit = FindById(c, id);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>取某控件上挂的提示文案。
    /// 为什么需要：ToolTip 文本外部不可读（不在窗口树、不在 UIA）⇒ 自动化只能靠这个出口。
    /// 同一控件可能被多个 ToolTip 实例挂过 ⇒ 返回第一个非空。</summary>
    public static string TipOf(string ctrlId)
    {
        SettingsForm? f = _instance;
        if (f == null || f.IsDisposed) return "";
        Control? c = FindById(f, ctrlId);
        if (c == null) return "";
        foreach (ToolTip t in f.AllTips())
        {
            string? s = t.GetToolTip(c);   // ToolTip.GetToolTip 的返回类型可空
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "";
    }

    /// <summary>滚动条指标探针：pos/max/visible。page 属于 general | whitelist。</summary>
    public static string ScrollMetrics(string page)
    {
        SettingsForm? f = _instance;
        if (f == null || f.IsDisposed) return "";
        ThemeScrollPanel? p = string.Equals(page, "whitelist", StringComparison.OrdinalIgnoreCase)
            ? f._whitelistScroll : f._generalScroll;
        if (p == null) return "";
        int pos = p.ScrollPosition, max = p.MaxScrollPosition;
        return pos.ToString() + "/" + max.ToString() + "/" + (max > 0 ? "true" : "false");
    }


    /// <summary>
    /// 自动化接口（P5）：关闭设置窗；未打开时无操作。
    /// </summary>
    public static void CloseIfOpen()
    {
        SettingsForm? f = _instance;
        if (f != null && !f.IsDisposed) f.Close();
    }

    /// <summary>
    /// 自动化接口（P5）：切到指定页面；窗口未打开时先打开。
    /// <paramref name="pageKey"/> 取 <see cref="NavKeys"/> 的字面值，例如
    /// <c>SettingsMonitors</c>。返回是否命中有效页面键。
    /// </summary>
    public static bool NavigateTo(string pageKey)
    {
        int idx = Array.IndexOf(NavKeys, pageKey);
        if (idx < 0) return false;

        ShowOrActivate();
        SettingsForm? f = _instance;
        if (f == null || f.IsDisposed) return false;
        f.SelectNav(idx);
        return true;
    }

    /// <summary>
    /// 强制把设置窗带到**前台 + Z-order 顶部**。
    ///
    /// 为什么需要（用户 2026-09-20 实测）：
    ///   当**另一块屏有全屏窗口**时打开设置窗，窗口虽然"显示了"却被压在**最底层** ——
    ///   必须去任务栏点图标、或把其他窗口最小化才能看到。用户描述：
    ///   「相当于这种情况窗口默认打开就处于底层」。
    ///
    /// 原因：`Form.Show()` / `Form.Activate()` 最终都走 `SetForegroundWindow`，
    ///   而 Windows 的**前台锁定**（Foreground Lock）在这些情形下会**静默失败** ——
    ///   连返回值都常为 true，从调用方看不出失败（本项目测试技能库「坑 5」记过同类）。
    ///
    /// 修法：经典的「**topmost 翻转**」——
    ///   ① 先 `SetWindowPos(HWND_TOPMOST)`：强制把它提到 Z-order 顶部并激活；
    ///      该操作**不受前台锁定限制**（与任务栏点击图标激活同一个机制）。
    ///   ② 再按**用户意图**决定是否撤销：用户没开图钉 ⇒ 翻回 `HWND_NOTOPMOST`；
    ///      开了图钉（`TopMost == true`）⇒ **保持 TOPMOST**，不覆盖用户选择。
    ///   ③ 补一次 `SetForegroundWindow` 拿键盘焦点。
    ///
    /// ⚠️ 全部用**现有** API（`NativeMethods.SetWindowPos` / `HWND_*` / `SWP_*` /
    ///   `SetForegroundWindow`），零新增 Win32 导入。
    /// ⚠️ 用 `SWP_SHOWWINDOW` 保证窗口可见；`SWP_NOMOVE | SWP_NOSIZE` 不动位置尺寸。
    /// </summary>
    private static void ForceToForeground(Form f)
    {
        try
        {
            if (f.IsDisposed || !f.IsHandleCreated) return;
            IntPtr h = f.Handle;
            if (h == IntPtr.Zero) return;

            // 已经在前台 ⇒ 什么都不做。topmost 翻转是**可见的 Z-order 变更**，
            // 无谓执行会带来一次轻微闪烁（用户从托盘反复点时尤其明显）。
            if (NativeMethods.GetForegroundWindow() == h) return;

            bool userWantsTopMost = f.TopMost;   // 图钉状态 = 用户意图
            uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW;

            // ① topmost 翻转：绕过前台锁定，强制到 Z 顶
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
            // ② 按用户意图撤销（开图钉则保持置顶）
            if (!userWantsTopMost)
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            // ③ 拿键盘焦点
            NativeMethods.SetForegroundWindow(h);
        }
        catch
        {
            // 提前台失败不应影响窗口可用性
        }
    }

    /// <summary>
    /// Shows the single settings window (or activates it if already open).
    /// Non-modal: the tray stays fully usable while it is open.
    /// </summary>
    public static void ShowOrActivate()
    {
        if (_instance == null || _instance.IsDisposed)
        {
            _instance = new SettingsForm();
            // ------------------------------------------------------------------
            // 2026-09-19：**窗口失焦/重激活时处理输入框的"程序焦点全选"**。
            //
            // 现象（用户报 + 截图实证）：重命名显示器折叠菜单展开着切走再回来，
            //   输入框整段文字被蓝色全选（双屏时通常是第二个输入框）；
            //   置顶时点其他窗口再点设置窗任意位置同样触发；色温范围输入框同理。
            //
            // 机制（两步，缺一不可）：
            //   ① **失焦**：只清 `ActiveControl` 不够 —— WinForms 在窗体**重新激活**时，
            //      若没有活动控件会按 Tab 顺序自动选一个可聚焦控件 ⇒ 焦点又落回
            //      原生 EDIT ⇒ EDIT 对"程序设置的焦点"的默认行为就是**全选整段**。
            //   ② **重激活**：若焦点确实落回了文本框（不是用户鼠标点的），把选区清掉。
            //      只在"焦点等于失焦前记住的那个"时才清 ⇒ 用户手动点击输入框
            //      （鼠标焦点，本就该全选/正常光标）完全不受影响。
            // ------------------------------------------------------------------
            // ⚠️ 2026-09-20 实测结论（自测脚本 `_verify_focus_sink.py` 连续 3 轮 FAIL）：
            //   **在 `Deactivate` 里设 `ActiveControl` 会被忽略**（窗体正在失去激活，
            //   WinForms 内部会重置活动控件）⇒ 焦点照样回到输入框。
            //   ⇒ 改到 **`Activated` 里处理**，并用 `BeginInvoke` 延后一拍 ——
            //     保证执行时 WinForms 已完成"焦点恢复"（否则我们改完它又设回去）。
            //   判据：焦点落在文本框、且**左键没按着**（= 不是用户自己点的）⇒ 移走。
            //   用户手动点击输入框时左键正按着 ⇒ 完全不干预。
            // 一个屏幕外的 1×1「焦点吸收器」：`ActiveControl` 必须指向**可聚焦**控件，
            // PictureBox/Label/Panel 都不行，所以用 Button（TabStop=false + 屏幕外 ⇒ 不可见不可达）。
            var focusSink = new Button
            {
                Size = new Size(1, 1),
                Location = new Point(-200, -200),
                TabStop = false
            };
            _instance.Controls.Add(focusSink);

            _instance.Activated += (_, _) =>
            {
                // ⛔ 2026-09-22 修：处理器里**不能读静态 _instance**。
                //   OnFormClosing() 会在 base 之前就把 _instance 置 null；
                //   （历史路径 RelaunchAfterDpiChange() 已于 2026-09-22 作为死代码删除）
                //   而关闭过程中 WinForms 仍会派发 WM_ACTIVATE ⇒ 这里读到 null
                //   ⇒ NullReferenceException ⇒ 全局兜底 Application.Exit() ⇒ 程序退出。
                //   （实测崩溃：%TEMP%\GammaBrightnessTool_crash.log 2026-09-21 22:03，连发 2 次）
                //   ⇒ 捕获**当前实例**到局部 f 并判空；内部也一律用 f —— 否则重建后
                //     会把焦点设到新窗上，而且用的是旧窗的 focusSink。
                var f = _instance;
                if (f == null || f.IsDisposed) return;
                f.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (f.IsDisposed) return;
                        if (f.ActiveControl is System.Windows.Forms.TextBoxBase tb
                            && (Control.MouseButtons & MouseButtons.Left) == 0)
                        {
                            tb.SelectionLength = 0;
                            tb.SelectionStart = tb.TextLength;
                            f.ActiveControl = focusSink;   // ⇒ 输入框彻底失焦
                        }
                    }
                    catch { /* 忽略 */ }
                }));
            };
            // 首帧防"系统模式底色"闪现（取证定案：大窗口首次 Show 时 DWM 先以
            // 系统默认背景合成一帧——系统深色=#202020、系统浅色=白——随后 WM_PAINT
            // 才逐控件画成窗口主题色；系统与窗口模式不同时这一帧肉眼可见，
            // 如"系统深+窗口浅"首帧大片 #202020 再变浅）。
            // 处置：先以透明分层(Opacity=0)显示并同步 Update() 完成整树首帧自绘，
            // 再置 Opacity=1 —— 透明首帧取代系统底色帧，用户第一眼即完整主题色。
            // （与弹窗 1143 白闪修复同思路：分层窗口的空表面首帧由内容而非
            // 系统默认色决定。）
            _instance.Opacity = 0;
            _instance.Show();
            _instance.Update();
            _instance.Opacity = 1.0;
            // 2026-09-20（用户报）：另一屏有全屏窗口时，新开的设置窗会停在**最底层**
            // （`Show()` 的激活被前台锁定静默吞掉）⇒ 显式提到前台。
            ForceToForeground(_instance);
        }
        else
        {
            if (_instance.WindowState == FormWindowState.Minimized)
            {
                _instance.WindowState = FormWindowState.Normal;
            }
            _instance.Activate();
            // 同上：`Activate()` 在前台锁定下会静默失败 ⇒ 显式提到前台。
            ForceToForeground(_instance);
        }
    }

    private SettingsForm()
    {
        Text = Localization.Get("SettingsTitle");
        // Self-drawn title bar: the system caption (DWM immersive dark mode)
        // lags behind theme switches on Win11 and needs a forced repaint that
        // flickers + desyncs the nav sidebar. Since the window is a fixed
        // dialog with just a title + two buttons, we draw the caption
        // ourselves as a normal themed control — instant theme refresh, no
        // DWM involvement at all.
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = true; // 任务栏左键点击最小化/恢复需要 WS_MINIMIZEBOX（无边框窗口不显示系统按钮）
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        // Borderless (frame:false) windows do NOT inherit the exe's
        // ApplicationIcon for their taskbar button, so the settings window
        // showed a blank/default icon in the taskbar. Load APP.ico from the
        // embedded resources and pin it explicitly.
        _windowIcon = LoadAppIcon();
        if (_windowIcon != null) Icon = _windowIcon;
        // Non-modal: keep using the tray while the window is open.
        // 置顶 (always-on-top) 由标题栏图钉按钮 _btnPin 控制
        // （见 CreateTitleBar 内 _btnPin.Click → ToggleTopMost），
        // 状态持久化在 AppSettings.SettingsTopMost；此处按已存值恢复。
        TopMost = Program.Instance?.GetTopMost() ?? false;
        // Manual DPI scaling: AutoScaleMode.Dpi only scales fonts and
        // auto-layout, NOT fixed sizes (ToggleSwitch 44x22, row heights...).
        // At 175% the fixed-height switch stayed 22px while the label font
        // grew, so the state text (开/关) was clipped at the bottom and the
        // 14pt title overflowed into the row below. We scale every fixed
        // dimension ourselves with _dpiScale and use scaled fonts.
        //
        // ⚠ B'（2026-09-22）：此处的 `DeviceDpi` 在句柄创建前可能读到**进程启动时的
        //    陈旧值**（见 OnHandleCreated 的探针说明）。B' 起字体 px 完全由 `_dpiScale`
        //    决定 ⇒ 陈旧值会让首帧字号与几何一起偏。⇒ 在 `OnHandleCreated`
        //    （`DeviceDpi` 已可信）里比对，**仅不一致时**才整窗重建一次。
        _dpiScale = DeviceDpi / 96f;
        // Classic 400px height: extra settings rows scroll inside the slim
        // ThemeScrollPanel instead of growing the window.
        ClientSize = new Size((int)(560 * _dpiScale), (int)(400 * _dpiScale) + _titleBarH);
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.None;
        Font = UiFont(9f);

        // ---- Left navigation sidebar ----
        // IMPORTANT: Dock layout is processed in reverse z-order (last added
        // control is docked first). The Fill content panel must be added
        // BEFORE the Left sidebar so the sidebar docks first (occupying the
        // left edge) and the content panel fills the remaining space.
        // Adding them in the opposite order makes the Fill panel cover the
        // whole client area and the Left sidebar overlap it.
        _contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            // Right padding is small (6): the slim scrollbar of the pages
            // sits at the far right edge, so the 24px right gutter would
            // otherwise be wasted empty space.
            Padding = new Padding(24, 20, 6, 20)
        };
        Controls.Add(_contentPanel);

        _navPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = (int)(140 * _dpiScale),
            BackColor = BgNav
        };
        Controls.Add(_navPanel);

        // 7 个自绘导航条目（替换原 ListBox：彻底消除 ListBox 内部 scrollbar well
        // 残留的浅色占位槽，外观与原 ListBox 自绘完全一致）。
        // P5：每个条目补稳定 ID（Nav_General … Nav_About）并注册自动化动作 ——
        // 自绘 Label 在 UIA 树里不可操作，只有本注册表能驱动它。
        int itemH = (int)(40 * _dpiScale);
        for (int i = 0; i < NavKeys.Length; i++)
        {
            int idx = i; // 闭包捕获
            string navId = NavActionIds[idx];
            var item = new Label
            {
                Name = navId,
                AccessibleName = navId,
                // 不用 Dock.Top：其布局依赖 Controls 集合 z 序，DPI/重建触发布局
                // 时 z 序被改动会把导航顺序反转（版本跑到最顶）。改用显式坐标，
                // 由 LayoutNavItems() 统一按索引定位，顺序永不依赖 z 序。
                Location = new Point(0, i * itemH),
                Size = new Size(_navPanel.Width, itemH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Text = Localization.Get(NavKeys[idx]),
                Font = UiFont(10f),
                ForeColor = TextSub,
                BackColor = BgNav,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            item.MouseEnter += (_, _) => UpdateNavHover(idx, true);
            item.MouseLeave += (_, _) => UpdateNavHover(idx, false);
            item.Click += (_, _) => SelectNav(idx);
            _navPanel.Controls.Add(item);
            _navItems.Add(item);
            AutomationBridge.Register(new AutomationAction(
                navId, "nav",
                Get: () => _navSelectedIndex == idx ? "true" : "false",
                Click: () => SelectNav(idx)));
        }
        _navPanel.Resize += (_, _) => LayoutNavItems();   // 面板宽变化时条目宽度跟随
        UpdateNavSelection();


        // Small version tag pinned to the bottom-left corner, under the
        // navigation sidebar. It sits on the sidebar background so it
        // looks like part of the sidebar footer (not rebuilt on language
        // change; text is version-only, colors are theme-derived).
        _versionLabel = new Label
        {
            Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.1.0"),
            AutoSize = true,
            Font = UiFont(10f),
            ForeColor = TextMain,
            BackColor = BgNav,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        // Position inside the sidebar footer area (nav width x bottom strip).
        // ClientSize now includes the self-drawn title bar height, so the
        // version tag anchors to the bottom of the whole client area (below
        // the nav list, which fills down to the content panel bottom).
        _versionLabel.Location = new Point((int)(8 * _dpiScale), ClientSize.Height - (int)(26 * _dpiScale));
        // B'：此处原有 `FontChanged += KeepFontFixed(...)`（把被 WinForms 缩放的
        // pt 字体拉回固定 pt）。px 字体不会被隐式缩放 ⇒ 事件钩子已删除。
        Controls.Add(_versionLabel);
        _versionLabel.BringToFront(); // Keep it above the Fill content panel
        // Build the six pages
        _generalPage = BuildGeneralPage();
        _brightnessPage = BuildBrightnessPage();
        _colorTempPage = BuildColorTempPage();
        _solarPage = BuildSolarPage();
        _hotkeysPage = BuildHotkeysPage();
        _aboutPage = BuildAboutPage();
        _monitorsPage = BuildMonitorsPage();   // 3.6.0 第 7 页
        _appWhitelistPage = BuildAppWhitelistPage();   // 2026-09-14 第 8 页：应用白名单
        _contentPanel.Controls.Add(_generalPage);

        // Default to the first page
        _navSelectedIndex = 0;
        SelectNav(0);

        // Rebuild all UI text when the language changes (from this combo or
        // the tray menu), so the window itself updates immediately instead
        // of only after reopening.
        Localization.LanguageChanged += OnLanguageChanged;

        // Rebuild the whole window when the theme changes so every control
        // (backgrounds, text, borders, combos, navigation) repaints with
        // the new palette.
        ThemeManager.ThemeChanged += OnThemeChanged;

        // 亮度/色温外部变化（托盘滚轮、挡位、快捷键、计划调度）时即时刷新本页
        // 下拉与数值显示。只在构造时挂一次、OnFormClosed 退订——订阅绝不能放在
        // 各页 BuildXxx 里每次重建重复 +=（旧订阅永久累积并持有已 Dispose 控件）。
        if (Program.Instance != null)
        {
            Program.Instance.BrightnessChanged += OnProgramBrightnessChanged;
            Program.Instance.TemperatureChanged += OnProgramTemperatureChanged;
        }

        // First-show repair: OwnerDraw combo boxes can paint once with
        // un-laid-out bounds while the window is still appearing (their
        // SelectedIndex is set during page construction, before the control
        // has a real size/position). That first paint can leave a garbled
        // box until any repaint. Force one clean refresh after the window
        // is actually shown so the first visible frame is always correct.
        Shown += (_, _) =>
        {
            _dpiRelayoutReady = true;
            foreach (var combo in FindAllThemedCombos(this))
            {
                combo.Invalidate();
            }
        };

        // PMv2 下窗口移到不同 DPI 的显示器时收到 DpiChanged，且本窗 DeviceDpi 会
        // 正确更新为新屏 DPI。此时只做窗口内重排（按新屏 DPI 重建页面与字体），
        // 【不】重启进程——否则拖动跨屏会被进程重启打断、无法跟手。
        // 改【系统缩放】：全局 DisplaySettingsChanged → Program 置 SystemScaleChangePending
        // 并进程重启（覆盖托盘菜单）。此处防抖到期若发现重启将至则跳过窗口内重建，
        // 避免"原地重建一帧 + 进程重启"的双重跳动。
        // 平滑化：收到消息立即关闭窗口重绘（挡住 WinForms 自动字体缩放的中间帧与
        // 重建过程的多帧跳变），处理完（重建或判定跳过）再一次性恢复重绘 → 只呈现最终帧。
        DpiChanged += (_, e) =>
        {
            _pendingDpi = e.DeviceDpiNew;
            DpiTrace($"DpiChanged old={e.DeviceDpiOld} new={e.DeviceDpiNew} ready={_dpiRelayoutReady} visible={Visible}");
            DpiProbe("DpiChanged.form", this);
            if (!_dpiRelayoutReady || IsDisposed) return;
            SetRedraw(false);   // 重建/判定期间不重绘，避免多帧跳动
            if (_dpiDebounce == null)
            {
                _dpiDebounce = new System.Windows.Forms.Timer { Interval = 120 };
                _dpiDebounce.Tick += (_, _) =>
                {
                    _dpiDebounce.Stop();
                    bool sysPending = Program.SystemScaleChangePending;
                    SetRedraw(true);    // 先恢复重绘（Relayout 内部会再次关闭并统一开启）
                    if (!IsDisposed && Visible)
                    {
                        if (sysPending)
                        {
                            // 系统级变更（进程重启将至）：跳过原地重建，交给重启一次到位。
                            DpiTrace("DpiChanged skip relayout (system-scale restart pending)");
                            Invalidate(true);
                        }
                        else
                        {
                            DpiTrace($"Debounce -> RelayoutForCurrentDpi({_pendingDpi})");
                            RelayoutForCurrentDpi(_pendingDpi);
                        }
                    }
                };
            }
            _dpiDebounce.Stop();
            _dpiDebounce.Start();
        };

        // 兜底：进程为 PMv2 时 SettingsForm 开着由 DpiChanged 触发；若进程因某种
        // 原因未以 PMv2 激活（SystemAware），改缩放仍会触发系统级 WM_DISPLAYCHANGE。
        // 双路都归口 Program.RequestAutoRestart（内部有冷却与防抖）。
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;

        // Self-drawn caption must be added LAST: Dock layout processes
        // controls in reverse z-order (last added docks first), so a Top-
        // docked bar added first would be laid out after the Left sidebar
        // had already consumed the left strip — ending up beside the nav,
        // not across the top. Added last it docks first: full-width top,
        // and the sidebar/content settle below it.
        CreateTitleBar();
        // B'：此处原有 `AttachFontFix(this);`（递归给全窗体挂 FontChanged 字体修复）。
        // px 字体不再被 WinForms 隐式缩放 ⇒ 该补丁已删除。
        // 探针（只读）：ctor 此刻的 DeviceDpi 是否 = 进程启动时的陈旧值（对照 HandleCreated 行）
        DpiTrace($"ctor.end: dpi={DeviceDpi} _dpiScale={_dpiScale:0.###} " +
                 $"client={ClientSize.Width}x{ClientSize.Height}");
        DpiProbe("ctor.nav0", _navItems.Count > 0 ? _navItems[0] : null);

        // 任务栏按钮左键点击最小化/恢复：FormBorderStyle.None 时
        // WinForms 的 FillInCreateParamsBorderIcons 跳过 WS_MINIMIZEBOX
        // 设置，窗口永远没有该样式，任务栏点击时 SC_MINIMIZE 被忽略。
        // 这里在 CreateParams 里手动补上（仅样式位，无边框不显示按钮）。
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            cp.Style &= ~0x00010000; // WS_MAXIMIZEBOX：MaximizeBox=false 语义（WinForms 对 None 窗口不删默认位）
            return cp;
        }
    }
    /// <summary>Builds the self-drawn caption strip: title text on the
    /// left, minimize + close buttons on the right. Dragging the strip
    /// moves the window via WM_NCLBUTTONDOWN/HTCAPTION (system handles
    /// snapping/snap-layout); double-click minimizes. The buttons and
    /// text are themed controls, so a theme switch repaints them
    /// instantly — no DWM caption involvement.</summary>
    private void CreateTitleBar()
    {
        int h = (int)(_titleBarH * _dpiScale);
        int btnW = (int)(_titleBtnW * _dpiScale);
        int btnH = h;

        _titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = h,
            BackColor = BgNav,
            // No custom cursor: keep the normal arrow (a four-way "move"
            // cursor over a caption is a modern Windows 11 convention but
            // feels wrong on a fixed dialog; the strip still drags).
        };

        // Window title (localized, re-read on language rebuild).
        _titleLabel = new Label
        {
            Text = Localization.Get("SettingsTitle"),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = UiFont(9f),
            ForeColor = TextMain,
            BackColor = BgNav,
            Dock = DockStyle.Fill,
            Padding = new Padding((int)(12 * _dpiScale), 0, 0, 0)
        };
        _titleBar.Controls.Add(_titleLabel);
        // B'：此处原有 `var titleLabel = _titleLabel;` + `titleLabel.FontChanged +=
        // KeepFontFixed(...)`。补丁删除后该局部变量再无用途，一并移除（避免未使用告警）。

        // Pin (always-on-top) button — added FIRST (before min/close) so it
        // docks left of them: [pin][-][x]. Uses the user-supplied PNGs: black
        // for light theme, white for dark; filled when pinned, outline when not.
        // Clicking toggles top-most and syncs the 通用设置 switch.
        _btnPin = new Label
        {
            AutoSize = false,
            BackColor = BgNav,
            Margin = new Padding(0),
            Cursor = Cursors.Hand,
            // Center (no zoom): the glyph is already downscaled to a fixed
            // ~12px bitmap in UpdatePinImage, so the 2048px source never
            // fills the whole 46px caption button.
            BackgroundImageLayout = ImageLayout.Center
        };
        _btnPin.Dock = DockStyle.Right;
        _btnPin.Width = btnW;
        _btnPin.MouseEnter += (_, _) => _btnPin.BackColor = ThemeManager.IsDark ? Color.FromArgb(60, 60, 64) : Color.FromArgb(229, 229, 229);
        _btnPin.MouseLeave += (_, _) => _btnPin.BackColor = BgNav;
        _btnPin.MouseDown += (_, _) => _btnPin.BackColor = ThemeManager.IsDark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(212, 212, 212);
        _btnPin.Click += (_, _) => ToggleTopMost();
        _titleBar.Controls.Add(_btnPin);
        _pinToolTip = new ToolTip();
        _pinToolTip.SetToolTip(_btnPin, Localization.Get("SettingsTopMost"));
        UpdatePinImage();

        // Minimize button — added FIRST so the Close button (added after)
        // docks to the far right, giving the standard [─][✕] order.
        _btnMin = CreateCaptionButton("\u2014", Color.Empty);
        _btnMin.Dock = DockStyle.Right;
        _btnMin.Width = btnW;
        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _titleBar.Controls.Add(_btnMin);

        // Close button — added LAST, docks to the far right (after min).
        _btnClose = CreateCaptionButton("\u2715", Color.FromArgb(232, 17, 35));
        _btnClose.Dock = DockStyle.Right;
        _btnClose.Width = btnW;
        _btnClose.Click += (_, _) => Close();
        _titleBar.Controls.Add(_btnClose);

        // P5：标题栏三个自绘按钮补稳定 ID 并注册动作。
        // Label 没有 PerformClick，故直接调各自的动作方法 —— 与点击语义等价，
        // 且不依赖坐标（自绘 Label 在 UIA 树里不可 Invoke）。
        if (_btnPin != null && _btnMin != null && _btnClose != null)
        {
            _btnPin.Name = _btnPin.AccessibleName = "Win_PinBtn";
            _btnMin.Name = _btnMin.AccessibleName = "Win_MinBtn";
            _btnClose.Name = _btnClose.AccessibleName = "Win_CloseBtn";

            AutomationBridge.Register(new AutomationAction(
                "Win_PinBtn", "btn",
                Get: () => Program.Instance?.GetTopMost() == true ? "true" : "false",
                Click: ToggleTopMost));
            AutomationBridge.Register(new AutomationAction(
                "Win_MinBtn", "btn",
                Click: () => WindowState = FormWindowState.Minimized));
            AutomationBridge.Register(new AutomationAction(
                "Win_CloseBtn", "btn",
                Click: Close));
        }

        // Dragging: the strip (and its children) forward mouse-down to the
        // caption drag message so the system moves the window (with Win11
        // snap layouts) and double-click minimizes. Only the text area
        // drags; the caption buttons handle their own clicks.
        _titleBar.MouseDown += TitleBar_MouseDown;
        _titleBar.MouseDoubleClick += (_, _) => WindowState = FormWindowState.Minimized;
        _titleLabel.MouseDown += TitleBar_MouseDown;
        _titleLabel.MouseDoubleClick += (_, _) => WindowState = FormWindowState.Minimized;

        // Theme refresh: the title strip follows the palette like any control.
        _titleBar.Resize += (_, _) =>
        {
            if (_titleBar == null) return;
            _titleBar.Invalidate();
            _titleLabel?.Invalidate();
        };

        Controls.Add(_titleBar);
        // NOTE: do NOT BringToFront() here. A Dock=Top control must stay at
        // the END of the z-order (bottom) so Dock layout processes it FIRST
        // and it spans the full client width. BringToFront moves it to the
        // top of the z-order, so it is docked LAST — after the Left sidebar
        // has already claimed the left strip — and it ends up beside the nav
        // instead of across the top.
    }

    /// <summary>Creates one caption button (min/close). The label draws a
    /// simple glyph on a hover/pressed background; close gets the classic
    /// red hover. Themed via BackColor/ForeColor so RefreshAllThemes
    /// repaints it instantly.</summary>
    private Label CreateCaptionButton(string glyph, Color hoverBg)
    {
        var btn = new Label
        {
            Text = glyph,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = UiFont(10f),
            ForeColor = TextMain,
            BackColor = BgNav,
            Margin = new Padding(0)
        };
        btn.MouseEnter += (_, _) =>
        {
            btn.BackColor = hoverBg == Color.Empty
                ? (ThemeManager.IsDark ? Color.FromArgb(60, 60, 64) : Color.FromArgb(229, 229, 229))
                : hoverBg;
        };
        btn.MouseLeave += (_, _) => btn.BackColor = BgNav;
        btn.MouseDown += (_, _) =>
        {
            btn.BackColor = hoverBg == Color.Empty
                ? (ThemeManager.IsDark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(212, 212, 212))
                : Color.FromArgb(202, 15, 31);
        };
        return btn;
    }

    /// <summary>Drags the borderless window by the caption strip. The
    /// system takes over once WM_NCLBUTTONDOWN/HTCAPTION is posted, giving
    /// native snap layouts and live drag feedback.</summary>
    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks > 1) { WindowState = FormWindowState.Minimized; return; }
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
    }

    /// <summary>Toggles the settings-window always-on-top flag from the
    /// title-bar pin button; keeps the 通用设置 switch in sync (setting
    /// the same value is a no-op there, so no double fire).</summary>
    private void ToggleTopMost()
    {
        bool newVal = !(Program.Instance?.GetTopMost() ?? false);
        Program.Instance?.SetTopMost(newVal);
        TopMost = newVal; // apply to this window immediately
        UpdatePinImage();
    }

    /// <summary>Applies the pin PNG matching the current theme + top-most
    /// state to the title-bar pin button. Disposes the previous image so
    /// theme/topmost switches do not leak bitmaps.</summary>
    private void UpdatePinImage()
    {
        if (_btnPin == null) return;
        bool top = Program.Instance?.GetTopMost() ?? false;
        var src = LoadPinImage(ThemeManager.IsDark, top);
        var old = _btnPin.BackgroundImage;
        if (src == null)
        {
            _btnPin.BackgroundImage = null;
        }
        else
        {
            int size = Math.Max(1, (int)(_pinGlyphSize * _dpiScale));
            var scaled = new Bitmap(size, size);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            src.Dispose();
            _btnPin.BackgroundImage = scaled;
        }
        old?.Dispose();
    }

    /// <summary>Loads an embedded pin PNG by theme + pinned state. Naming:
    /// (黑色|白色)(未置顶|已置顶).png — black for light theme, white for
    /// dark, outline for unpinned, filled for pinned. Returns null when the
    /// resource is missing (button shows plain background).</summary>
    private static Bitmap? LoadPinImage(bool dark, bool topMost)
    {
        string file = (dark ? "白色" : "黑色") + (topMost ? "已置顶" : "未置顶") + ".png";
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            // Bitmap(Stream) 的流须在 Image 存续期内打开；此处先在流内拷贝独立副本。
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var src = new Bitmap(stream);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    /// <summary>
    /// 主题切换后**立即**把刷新按钮的图标换成对应主题的版本（深色用白、浅色用黑）。
    /// 主题切换走的是 <c>RefreshTheme</c> 递归重刷配色，它不碰 <c>Image</c>，
    /// 所以不补这一步的话，切深色后图标还是黑的，得重开设置窗才会换。
    /// </summary>
    private void RefreshWhitelistIcon()
    {
        if (_whitelistRefreshBtn == null || _whitelistRefreshBtn.IsDisposed) return;
        var old = _whitelistRefreshBtn.Image;
        _whitelistRefreshBtn.Image = LoadRefreshIcon(Math.Max(12, (int)(17 * _dpiScale)));
        old?.Dispose();
    }

    /// <summary>
    /// 加载内嵌的刷新图标（`Resources\refresh-black.ico` / `refresh-white.ico`，
    /// 由 `_devtools\make_refresh_icons.py` 从 `Resources\refresh icons\*.png` 转成 16→256 多尺寸）。
    /// 深色主题用白色版、浅色主题用黑色版。按 <paramref name="size"/> 取最接近的尺寸层，保证不糊。
    /// </summary>
    private static Bitmap? LoadRefreshIcon(int size)
    {
        try
        {
            string file = ThemeManager.IsDark ? "refresh-white.ico" : "refresh-black.ico";
            var asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            // 取 ico 里**最大的一层**（256）再用高质量双三次缩到目标尺寸 —— 与标题栏图钉
            // UpdatePinImage 的做法一致。直接 `new Icon(stream, size)` 是取"最接近层"，
            // 在小尺寸下比缩放假更糊。
            using var icon = new Icon(stream, new Size(256, 256));
            using var srcIcon = icon.ToBitmap();
            int px = Math.Max(8, size);
            var scaled = new Bitmap(px, px);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(srcIcon, 0, 0, px, px);
            }
            return scaled;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads the embedded APP.ico (the exe's ApplicationIcon) for
    /// the taskbar button of this borderless window. Returns null if the
    /// resource is missing (falls back to no icon).</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".APP.ico", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            return stream == null ? null : new Icon(stream);
        }
        catch { return null; }
    }

    private static IEnumerable<ThemedComboBox> FindAllThemedCombos(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ThemedComboBox combo)
                yield return combo;
            foreach (var nested in FindAllThemedCombos(c))
                yield return nested;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 探针（只读）：句柄建好后 WinForms 才把 _deviceDpi 更新为所在屏真实值 ——
        // 对比 ctor 里的 DeviceDpi 即可证实/证伪「ctor 用的是进程启动时的陈旧 DPI」。
        DpiTrace($"HandleCreated: dpi={DeviceDpi} _dpiScale={_dpiScale:0.###} " +
                 $"client={ClientSize.Width}x{ClientSize.Height}");
        // ------------------------------------------------------------------
        // B'（2026-09-22）：把 ctor 的陈旧 `_dpiScale` 更正为所在屏真实值。
        //
        // ctor 里 `DeviceDpi` 在句柄创建前可能是进程启动时的旧值（上方探针即为验证
        // 此事而设）。B' 起 UI 字体是 `GraphicsUnit.Pixel`、尺寸**完全**由 `_dpiScale`
        // 决定 ⇒ 陈旧值会让首帧的字体与几何一起偏（旧实现在这里还有 WinForms 隐式
        // 缩放 + 三套补丁兜着，现在没有了，所以必须在这里收口）。
        //
        // ⛔ 仅在**确有不一致时**才重建：进程 DPI 与设置窗所在屏一致（绝大多数情况，
        //    含开机自启、托盘打开）时本判断为假、**一行都不执行** ⇒ 既有启动路径零变化。
        //    `RelayoutForCurrentDpi` 内部用 SetRedraw(false/true) 包住全程，首帧前
        //    重建不会产生中间帧观感。
        // ------------------------------------------------------------------
        if (Math.Abs(DeviceDpi / 96f - _dpiScale) > 0.001f)
        {
            DpiTrace($"HandleCreated: 陈旧 _dpiScale={_dpiScale:0.###} " +
                     $"-> 真实 {DeviceDpi / 96f:0.###} ⇒ 整窗重建");
            RelayoutForCurrentDpi(DeviceDpi);
        }
        // Borderless window: rounded corners + drop shadow. The system
        // caption is gone, so there is no DWM title-bar theme to manage.
        if (Environment.OSVersion.Version.Major >= 10)
        {
            int pref = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        // ThemeChanged can fire from the thread pool: ThemeManager's 500ms
        // registry poller runs on a System.Threading.Timer (thread-pool
        // thread) and raises ThemeChanged there (System mode). All control
        // mutation + the synchronous Update() repaint MUST happen on the
        // window's UI thread; marshal if we are not on it.
        if (InvokeRequired)
        {
            try { BeginInvoke(OnThemeChanged, sender, e); return; }
            catch (ObjectDisposedException) { return; }
        }
        // No DWM caption involved anymore (self-drawn title bar), so a
        // synchronous repaint of every control — including the caption
        // strip — updates the whole window in one frame: no flicker, no
        // title-vs-nav desync.
        RefreshAllThemes();
    }


    /// <summary>Refreshes the entire window appearance without rebuilding the
    /// control tree. Directly updates every control's colors from the
    /// current theme palette. Avoids the flicker + animation-replay that
    /// a full RebuildUi would cause on theme switches.</summary>
    private void RefreshAllThemes()
    {
        // Form shell
        BackColor = Bg;
        _contentPanel.BackColor = Bg;
        _navPanel.BackColor = BgNav;
        RefreshNavAppearance();  // 更新导航条目主题色并重绘
        if (_versionLabel != null)
        {
            _versionLabel.BackColor = BgNav;
            _versionLabel.ForeColor = TextMain;
        }

        // Self-drawn caption strip: repaint title text + buttons with the
        // new palette in the same frame as everything else.
        if (_titleBar != null)
        {
            _titleBar.BackColor = BgNav;
            _titleLabel!.BackColor = BgNav;
            _titleLabel.ForeColor = TextMain;
            _titleLabel.Text = Localization.Get("SettingsTitle");
            _btnMin!.BackColor = BgNav;
            _btnMin.ForeColor = TextMain;
            _btnClose!.BackColor = BgNav;
            _btnClose.ForeColor = TextMain;
            _btnPin!.BackColor = BgNav;
            UpdatePinImage();
            _titleBar.Invalidate();
        }

        // Refresh every page: first the page panel itself (its BackColor is
        // set at build time and must follow the theme), then its subtree.
        // NOTE: include ALL pages — omitting one (e.g. _monitorsPage) leaves
        // that page stuck on the previous theme on theme switches.
        foreach (var page in new[] { _generalPage, _brightnessPage, _hotkeysPage, _aboutPage, _colorTempPage, _solarPage, _monitorsPage, _appWhitelistPage })
        {
            if (page == null) continue;
            page.BackColor = Bg;  // page root uses the page background
            foreach (Control child in page.Controls)
                RefreshTheme(child, Bg, BgInner, Border,
                             TextMain, Track, Thumb, ThumbHover, InputBg);

            // 刷新按钮是**图标按钮**：RefreshTheme 只重刷配色、不碰 Image，
            // 所以深色主题下图标仍是黑的那张，要重开设置窗才换（用户实测的 bug）。
            // 这里就地换成对应主题的图标。
            if (ReferenceEquals(page, _appWhitelistPage)) RefreshWhitelistIcon();
        }

        // Synchronous repaint: every Invalidate() above only marks the
        // control dirty — WM_PAINT is dispatched asynchronously by the
        // message loop, so nested controls (page -> card -> row -> item)
        // each repaint in a different frame, which reads as a "background
        // first, options later" staggered switch. Update() sends WM_PAINT
        // synchronously to this window, forcing the ENTIRE tree (window +
        // child controls) to repaint in one call stack — one atomic frame
        // for the whole theme switch.
        Update();
    }
    /// <summary>Recursively refreshes a subtree of controls from the
    /// current theme palette. Walks each node once; delegating types
    /// (Panels) recurse deeper. Returns the number of controls touched.</summary>
    private static int RefreshTheme(Control c,
        Color bg, Color bgInner, Color border,
        Color textMain, Color track, Color thumb, Color thumbHover, Color inputBg)
    {
        int count = 0;
        try
        {
            if (c is RoundedButton rb)
            {
                // Preserve the button's hover/pressed colors so the current
                // interaction state is kept across the refresh.
                var rbType = typeof(RoundedButton);
                var fMo = rbType.GetField("_mouseOver",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var fPr = rbType.GetField("_pressed",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var mo = (Color)fMo!.GetValue(rb)!;
                var pr = (Color)fPr!.GetValue(rb)!;
                rb.ApplyTheme(bgInner, textMain, border, mo, pr);
                count++;
            }
            else if (c is RoundedCardPanel rcp)
            {
                rcp.ApplyTheme(bg, bgInner, border);
                count++;
            }
            else if (c is ThemeScrollPanel tsp)
            {
                tsp.ApplyTheme(bg, track, thumb, thumbHover);
                count++;
            }
            else if (c is ThemedComboBox tcb)
            {
                tcb.ApplyTheme(inputBg, textMain);
                // Rounded corners outside the body blend into the card's
                // inner panel, not the input field colour.
                tcb.SetParentBackground(bgInner);
                count++;
            }
            else if (c is RoundedTextBox rtb)
            {
                rtb.ApplyTheme(inputBg, textMain);
                // The field's rounded corners blend into the card inner panel.
                rtb.SetParentBackground(bgInner);
                count++;
            }
            else if (c is SettingSlider ss)
            {
                // 滑轨主题：浅色=蓝色填充，深色=白色填充（参考弹窗滑轨）；
                // 圆形按钮（拇指）：浅色=浅灰，深色=中灰。
                ss.ApplyTheme(track,
                    ThemeManager.IsDark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(200, 200,205),
                    ThemeManager.IsDark ? Color.FromArgb(200, 200, 205) : Color.FromArgb(178, 178, 184),
                    ThemeManager.IsDark ? Color.White : Accent);
                ss.ForeColor = textMain;
                count++;
            }
            else if (c is ToggleSwitch)
            {
                // ToggleSwitch reads ThemeManager.IsDark live in OnPaint.
                // Just force a repaint — no state changes, no animation.
                c.Invalidate();
                count++;
            }
            else if (c is Label lbl)
            {
                // 禁用时置灰（深色主题下 WinForms 默认 GrayText 是黑色）。
                lbl.ForeColor = lbl.Enabled ? textMain : TextDim;
                count++;
            }
            else if (c is FoldBodyPanel)
            {
                // 折叠体容器始终融入页面背景（折叠区不呈现整块卡片底色），
                // 否则主题切换会被下面的 Panel 分支刷成 BgInner。
                c.BackColor = bg;
                count++;
            }
            else if (c is Panel p)
            {
                // The ThemeScrollPanel's inner content panel follows the
                // page background (bg), not the card color (bgInner).
                p.BackColor = (c.Parent is ThemeScrollPanel) ? bg : bgInner;
                count++;
            }
        }
        catch { }  // reflection failures are non-fatal here

        // Recurse into container children (same subtree-visitor pattern
        // used by the rest of the codebase).
        foreach (Control child in c.Controls)
            count += RefreshTheme(child, bg, bgInner, border,
                                textMain, track, thumb, thumbHover, inputBg);
        return count;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        // Debounce: Localization raises LanguageChanged twice per
        // language switch (Setting= then Current=). Coalesce into a
        // single rebuild 40ms after the last event; this also absorbs
        // the redundant BeginInvoke from the combo's own handler.
        if (_rebuildDebounce == null)
        {
            _rebuildDebounce = new System.Windows.Forms.Timer { Interval = 40 };
            _rebuildDebounce.Tick += (_, _) =>
            {
                _rebuildDebounce.Stop();
                if (!IsDisposed) RebuildUi();
            };
        }
        _rebuildDebounce.Stop();
        _rebuildDebounce.Start();
    }

    /// <summary>开关窗体重绘（WM_SETREDRAW）。重建/重排期间关闭可避免中间帧跳动，
    /// 完成后开启并强制重绘一次，用户只看到最终结果（更平滑、减少重复绘制开销）。</summary>
    private void SetRedraw(bool enable)
    {
        if (IsDisposed || !IsHandleCreated) return;
        NativeMethods.SendMessage(Handle, 0x000B, enable ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        if (enable) Invalidate(true);
    }

    /// <summary>
    /// DPI 变更后整窗重建：重算冻结的 _dpiScale → 调整骨架（窗口/导航/标题栏/
    /// 版本标签）→ RebuildUi 重建全部页面与字体。等效于"关闭重开"，但在运行中完成，
    /// 并保留当前导航页。
    /// </summary>
    private void RelayoutForCurrentDpi(int targetDpi = 0)
    {
        if (IsDisposed) return;
        // 关闭重绘：重建全程不呈现中间帧（避免"文字先缩放跳动、再逐步重画"的卡顿观感）
        SetRedraw(false);
        try
        {
            // 优先用调用方指定的目标 DPI（Relaunch 重开时传 DpiChanged 的新值）；
            // new SettingsForm() 时句柄未创建，DeviceDpi 可能读到旧值，不可信。
            int dpi = targetDpi > 0 ? targetDpi : DeviceDpi;
            if (dpi <= 0) dpi = DeviceDpi;
            _dpiScale = dpi / 96f;
            DpiTrace($"Relayout.begin: dpi={dpi} _dpiScale={_dpiScale:0.###}");
            ClientSize = new Size((int)(560 * _dpiScale), (int)(400 * _dpiScale) + _titleBarH);
            _navPanel.Width = (int)(140 * _dpiScale);
            if (_titleBar != null) _titleBar.Height = (int)(_titleBarH * _dpiScale);
            int btnW = (int)(_titleBtnW * _dpiScale);
            if (_btnPin != null) _btnPin.Width = btnW;
            if (_btnMin != null) _btnMin.Width = btnW;
            if (_btnClose != null) _btnClose.Width = btnW;
            if (_titleLabel != null) _titleLabel.Padding = new Padding((int)(12 * _dpiScale), 0, 0, 0);
            _versionLabel.Location = new Point((int)(8 * _dpiScale), ClientSize.Height - (int)(26 * _dpiScale));
            RebuildUi();
            // 学 BrightnessPopup.ApplyLayoutForCurrentDpi：DPI 变化时给仍持有旧 Font 实例
            // 的骨架控件换新实例，强制 GDI 按当前 DPI 重新生成字形句柄（旧实例句柄是
            // 按窗体创建时 DPI 生成的，不换则文字保持旧 DPI 的渲染尺寸 → 与缩放后的
            // 窗口不匹配、导航行被大字号撑出滚动条）。
            RebuildSkeletonFonts();
            DpiProbe("Relayout.after.nav0", _navItems.Count > 0 ? _navItems[0] : null);
            DpiProbe("Relayout.after.title", _titleLabel);
            if (_contentPanel != null && _contentPanel.Controls.Count > 0)
                DpiDumpTree("Relayout.page", _contentPanel.Controls[0]);
            EnsureNavNoScroll();
            Refresh();
        }
        catch (Exception ex)
        {
            // 静默降级：本次重建中断不影响已显示的窗体（用户可关闭重开）。
            System.Diagnostics.Debug.WriteLine("RelayoutForCurrentDpi: " + ex);
        }
        finally
        {
            // 恢复重绘（SetRedraw(true) 内部 Invalidate 触发最终帧绘制）。
            SetRedraw(true);
        }
    }

    /// <summary>递归重建子控件窗口句柄（顶层窗体自身不重建，避免触发二次 DPI 消息循环）。</summary>
    private static readonly MethodInfo? RecreateHandleMethod =
        typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static void RecreateChildHandles(Control c)
    {
        if (c == null) return;
        foreach (Control child in c.Controls) RecreateChildHandles(child);
        if (!(c is Form) && c.IsHandleCreated && c.Created)
        {
            RecreateHandleMethod?.Invoke(c, null);
        }
    }

    /// <summary>改系统缩放/分辨率 → 自动重启整个进程（等效用户手动重启，让设置窗、托盘菜单全部按新 DPI）。</summary>
    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_dpiRelayoutReady || !Visible) return;
        if (_instance != this) return;          // 仅活动实例响应
        // 防抖：系统广播可能连续到达；重开新窗后若又来一次事件则被 Relaunch 内时间戳拦住。
        if (_dpiDebounce == null)
        {
            _dpiDebounce = new System.Windows.Forms.Timer { Interval = 400 };
            _dpiDebounce.Tick += (_, _) =>
            {
                _dpiDebounce.Stop();
                if (!IsDisposed && Visible) Program.RequestAutoRestart();
            };
        }
        _dpiDebounce.Stop();
        _dpiDebounce.Start();
    }

    /// <summary>DPI 诊断（仅写 %TEMP%，不改任何行为；用于确认 DpiChanged/重开是否真的触发）。</summary>
    private static readonly int _dpiTracePid = Environment.ProcessId;
    private static bool _dpiTraceSessionLogged;

    /// <summary>DPI 探针日志：**只追加、永不覆盖**。每次进程启动写一条会话头
    /// （pid + 启动时间），多次重启各成一段，便于对照分析。</summary>
    private static void DpiTrace(string msg)
    {
        try
        {
            var p = Path.Combine(Path.GetTempPath(), "GBT_dpi.log");
            if (!_dpiTraceSessionLogged)
            {
                _dpiTraceSessionLogged = true;
                File.AppendAllText(p, $"==== DPI 会话开始 pid={_dpiTracePid} " +
                    $"启动={System.Diagnostics.Process.GetCurrentProcess().StartTime:yyyy-MM-dd HH:mm:ss} " +
                    $"now={DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
            }
            File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}|P{_dpiTracePid}] {msg}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>DPI 探针（只读）：记录某控件此刻的 DeviceDpi / 字体 pt / 字体像素高 /
    /// 句柄状态。Font.Height（像素）= 该字体 GDI 字形句柄实际生成的尺寸，是判断
    /// 「这个控件的文字到底按哪个 DPI 生成」的硬证据。</summary>
    private static void DpiProbe(string tag, Control? c)
    {
        try
        {
            if (c == null || c.IsDisposed)
            {
                DpiTrace($"{tag} [(null/disposed)]");
                return;
            }
            DpiTrace($"{tag} [{c.GetType().Name}'{c.Name}'] dpi={c.DeviceDpi} " +
                     $"pt={c.Font.SizeInPoints:0.##} px={c.Font.Height} " +
                     $"handle={c.IsHandleCreated} size={c.Size.Width}x{c.Size.Height}");
        }
        catch
        {
        }
    }

    /// <summary>DPI 探针（只读）：递归转储一棵控件树的字体信息。</summary>
    private static void DpiDumpTree(string tag, Control? root)
    {
        try
        {
            if (root == null || root.IsDisposed) return;
            int n = 0;
            void Walk(Control c)
            {
                DpiProbe($"{tag}.{n}", c);
                n++;
                foreach (Control child in c.Controls) Walk(child);
            }
            Walk(root);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 按**当前** `_dpiScale` 重新给骨架控件（导航 / 版本标签 / 标题栏 / 标题栏按钮 /
    /// 窗体自身）赋 px 字体。
    ///
    /// B'（2026-09-22）重写：原实现是"把被 WinForms 按 newDpi/oldDpi 缩放的 pt 字体
    /// 拉回固定 pt"的补丁（依赖 `NavFixedFont` / `TitleFixedFont` 两个静态字段）。
    /// 现在字体一律 `GraphicsUnit.Pixel`（见 <see cref="UiFont"/>），WinForms 不会再
    /// 隐式改动它们 ⇒ 本方法只需在 `_dpiScale` 变化后（`RelayoutForCurrentDpi`）
    /// **按新 scale 重建**即可。页面控件由 `RebuildUi()` 重建时各自 `UiFont(...)`，
    /// 不在此处理。
    ///
    /// 注意：这里只换新实例、**绝不主动 Dispose 旧实例**——WinForms 在控件句柄创建
    /// （OnHandleCreated → SetWindowFont → ToHfont）时会用到控件当前的 Font，若该
    /// Font 已被 Dispose 会抛 "Parameter is not valid" 崩溃（Relayout 在 Show 之前
    /// 执行、句柄延迟创建时极易踩中）。旧字体对象交给 GC 释放，代价远小于崩溃。
    /// </summary>
    private void RebuildSkeletonFonts()
    {
        DpiProbe("RebuildSkeletonFonts.before.nav0", _navItems.Count > 0 ? _navItems[0] : null);

        // 导航栏 / 版本标签：设计 10pt（与原 NavFixedFont 同值，保证观感不变）
        var navFont = UiFont(10f);
        _navPanel.Font = navFont;
        for (int i = 0; i < _navItems.Count; i++) _navItems[i].Font = navFont;
        _versionLabel.Font = navFont;
        // 标题栏：设计 9pt（与原 TitleFixedFont 同值）
        if (_titleLabel != null) _titleLabel.Font = UiFont(9f);
        // 标题栏按钮 [pin][-][x]：设计 10pt
        var captionFont = UiFont(10f);
        if (_btnPin != null) _btnPin.Font = captionFont;
        if (_btnMin != null) _btnMin.Font = captionFont;
        if (_btnClose != null) _btnClose.Font = captionFont;
        Font = UiFont(9f);

        DpiProbe("RebuildSkeletonFonts.after.nav0", _navItems.Count > 0 ? _navItems[0] : null);
        DpiProbe("RebuildSkeletonFonts.after.title", _titleLabel);
        DpiProbe("RebuildSkeletonFonts.after.form", this);
        Invalidate(true);
    }

    private int _rebuildCount;

    private void RebuildUi()
    {
        if (IsDisposed) return;
        _rebuildCount++;
        OpLog.Log($"[settingsForm] RebuildUi #{_rebuildCount} (lang/theme/DPI/reset)");
        int navIndex = _navSelectedIndex;
        // 重建前记录当前页滚动位置；RebuildUi 会重建全部页面（滚动归零），
        // 末尾以 BeginInvoke 恢复（等新页布局完成、_maxScroll 有效后再设值）。
        int savedScroll = GetCurrentPageScroll();
        _disableLocked.Clear();
        _disableLockActive = false;
        _refreshLevelSelection = null;
        _refreshLevelDisplay = null;
        _refreshPresetSelection = null;
        _refreshPresetDisplay = null;
        _lastSyncBrightnessPct = -1;
        _lastSyncTemperatureK = -1;
        // 显示器页每次重建（DPI/语言/主题变更）都会向 _monitorSubToggles 追加，
        // 若不在此清空会翻倍累积已 Dispose 的开关，SyncMonitorSubToggles 遍历时
        // 会访问已释放控件。
        _monitorSubToggles.Clear();

        // Apply the current theme to the form shell itself as well (the
        // pages rebuild with the new palette below; the form background
        // would otherwise stay in the old theme).
        BackColor = Bg;
        _contentPanel.BackColor = Bg;
        _navPanel.BackColor = BgNav;
        RefreshNavAppearance();

        // Version tag sits on the sidebar; refresh its colors so a theme
        // switch (RebuildUi) repaints it instead of leaving the old theme's
        // background (visible as a stale white block in dark mode).
        if (_versionLabel != null)
        {
            _versionLabel.BackColor = BgNav;
            _versionLabel.ForeColor = TextMain;
        }

        // P5：页面即将重建 —— 先清掉页面域的动作注册，避免陈旧 ID 仍指向已 Dispose 的控件。
        // （Win_ / Nav_ 属窗体外壳、不参与重建，无需清理。）
        foreach (string prefix in PageActionPrefixes)
        {
            AutomationBridge.ClearActionsByPrefix(prefix);
        }

        // Detach current pages, then rebuild everything with the new language.
        _contentPanel.Controls.Clear();
        for (int i = 0; i < _navItems.Count && i < NavKeys.Length; i++)
        {
            _navItems[i].Text = Localization.Get(NavKeys[i]);
        }

        _generalPage?.Dispose();
        _brightnessPage?.Dispose();
        _colorTempPage?.Dispose();
        _solarPage?.Dispose();
        _hotkeysPage?.Dispose();
        _aboutPage?.Dispose();
        _monitorsPage?.Dispose();   // 3.6.0 第 7 页
        _appWhitelistPage?.Dispose();   // 2026-09-14 第 8 页

        _generalPage = BuildGeneralPage();
        _brightnessPage = BuildBrightnessPage();
        _colorTempPage = BuildColorTempPage();
        _solarPage = BuildSolarPage();
        _hotkeysPage = BuildHotkeysPage();
        _aboutPage = BuildAboutPage();
        _monitorsPage = BuildMonitorsPage();   // 3.6.0 第 7 页
        _appWhitelistPage = BuildAppWhitelistPage();   // 2026-09-14 第 8 页：应用白名单

        Text = Localization.Get("SettingsTitle");

        // Self-drawn caption: keep the title text + palette in sync on
        // language rebuild (it is a normal themed control, not DWM).
        if (_titleLabel != null)
        {
            _titleLabel.Text = Localization.Get("SettingsTitle");
            _titleLabel.BackColor = BgNav;
            _titleLabel.ForeColor = TextMain;
            _titleBar!.BackColor = BgNav;
            _btnMin!.BackColor = BgNav;
            _btnMin.ForeColor = TextMain;
            _btnClose!.BackColor = BgNav;
            _btnClose.ForeColor = TextMain;
            _btnPin!.BackColor = BgNav;
            UpdatePinImage();
            _pinToolTip?.SetToolTip(_btnPin, Localization.Get("SettingsTopMost"));
        }

        if (navIndex < 0) navIndex = 0;
        SelectNav(navIndex); // 显示对应页面并刷新高亮
        _versionLabel?.BringToFront(); // RebuildUi recreates pages; keep version tag on top
        // B'：此处原有 `AttachFontFix(this);`（页面重建后重新挂载字体修复），已删除。
        EnsureNavNoScroll();
        // 恢复重建前的滚动位置：BeginInvoke 等新页布局完成（_maxScroll 有效）后再设。
        if (savedScroll > 0)
        {
            int s = savedScroll;
            BeginInvoke((Action)(() => SetCurrentPageScroll(s)));
        }
    }

    private Panel BuildGeneralPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        // Slim theme-aware scroll container (6px rounded bar, hidden until
        // the content overflows). The window keeps its classic 400px height;
        // when 8+ rows exceed the content area a discreet scrollbar appears
        // instead of growing the window.
        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);
        _generalScroll = scroll;   // 供 SyncFullscreenPerMonRow 定位子行（跨页刷新）
        // ---- Setting row 1: 开机自启 (startup with Windows) ----
        // All children use Dock=Top so every row's width exactly matches the
        // page width at any DPI. A fixed-width row would overflow the page
        // (the content area is only ~372px wide at 100%, ~558px at 150% DPI)
        // and Windows clips HWND children to the parent's client area,
        // cutting off the right border.
        var startupToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetStartupEnabled() ?? StartupManager.IsStartupEnabled()
        };
        startupToggle.Name = startupToggle.AccessibleName = "Gen_StartupToggle";
        startupToggle.ApplyDpiScale(_dpiScale);
        startupToggle.CheckedChanged += (_, _) =>
        {
            // 必须经 MainController：直接调 StartupManager.SetStartup 只改注册表与磁盘，
            // MainController._settings.StartupEnabled 仍是旧值，退出时 SaveSettings() 会把
            // 旧值写回 → 开关改动静默丢失，且下次启动自检会按旧值把 Run 键改回去。
            try
            {
                MainController? ctrl = Program.Instance;
                if (ctrl != null)
                {
                    ctrl.SetStartupEnabled(startupToggle.Checked);
                }
                else
                {
                    StartupManager.SetStartup(startupToggle.Checked);
                }
            }
            catch
            {
                // SetStartup shows its own error dialog; keep the switch
                // consistent with the actual registry state on failure.
                startupToggle.Checked = StartupManager.IsStartupEnabled();
            }
        };
        var toggleGroup = BuildToggleGroup(startupToggle);
        var startupRow = BuildSettingRow(Localization.Get("Startup"), toggleGroup);

        // ---- Setting row 2: 语言 (language selector) ----
        var langCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        langCombo.ApplyTheme(InputBg, TextMain);
        langCombo.SetParentBackground(BgInner); // rounded corners blend into the card
        // Items are the display names for each language, shown in the
        // language itself so the user can recognize them regardless of the
        // current UI language. Index 0 is "follow the system UI language",
        // indices 1..9 map to Language.SimplifiedChinese .. Language.Russian.
        langCombo.Items.Add(Localization.Get("LangSystem"));
        langCombo.Items.Add(Localization.Get(Language.SimplifiedChinese, "LangSC"));
        langCombo.Items.Add(Localization.Get(Language.TraditionalChinese, "LangTC"));
        langCombo.Items.Add(Localization.Get(Language.English, "LangEN"));
        langCombo.Items.Add(Localization.Get(Language.Japanese, "LangJA"));
        langCombo.Items.Add(Localization.Get(Language.Korean, "LangKO"));
        langCombo.Items.Add(Localization.Get(Language.German, "LangDE"));
        langCombo.Items.Add(Localization.Get(Language.French, "LangFR"));
        langCombo.Items.Add(Localization.Get(Language.Spanish, "LangES"));
        langCombo.Items.Add(Localization.Get(Language.Russian, "LangRU"));
        // Select what the user chose (Language.System stays index 0).
        langCombo.SelectedIndex = Localization.Setting == Language.System ? 0 : (int)Localization.Setting + 1;
        langCombo.SelectedIndexChanged += (_, _) =>
        {
            // Index 0 = System; 1..9 map back to the concrete languages.
            var lang = langCombo.SelectedIndex == 0 ? Language.System : (Language)(langCombo.SelectedIndex - 1);
            // Route through the controller so the in-memory settings stay in
            // sync (otherwise a later save from the controller would overwrite
            // this choice) and the tray tooltip refreshes immediately.
            Program.Instance?.ChangeLanguage(lang);
        };

        var langRow = BuildSettingRow(Localization.Get("Language"), langCombo);
        // ---- Setting row 3: 主题选择 (theme selector) ----
        var themeCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        themeCombo.ApplyTheme(InputBg, TextMain);
        themeCombo.SetParentBackground(BgInner); // rounded corners blend into the card
        // Index 0 = follow system; 1 = dark; 2 = light.
        themeCombo.Items.Add(Localization.Get("ThemeSystem"));
        themeCombo.Items.Add(Localization.Get("ThemeDark"));
        themeCombo.Items.Add(Localization.Get("ThemeLight"));
        themeCombo.SelectedIndex = (int)(Program.Instance?.GetTheme() ?? ThemeMode.System);
        themeCombo.SelectedIndexChanged += (_, _) =>
        {
            // 点击"当前已选项"时 DropdownListPopup 会 ReapplySelection()（先置 -1
            // 再置回）以强制重触发事件；-1 不是合法主题索引，若直接强转成 ThemeMode
            // 会 SetTheme(-1) → 模式瞬时退回"跟随系统"→ 与目标色不同时整窗闪一次。
            // 忽略 -1 中间事件；同项重选（值不变）本就不需要刷新。
            if (themeCombo.SelectedIndex < 0) return;
            var theme = (ThemeMode)themeCombo.SelectedIndex;
            Program.Instance?.SetTheme(theme);
        };

        var themeRow = BuildSettingRow(Localization.Get("Theme"), themeCombo);
        // ---- Setting row 4: 浮窗主题 (popup theme, independent of main UI) ----
        var popupThemeCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        popupThemeCombo.ApplyTheme(InputBg, TextMain);
        popupThemeCombo.SetParentBackground(BgInner); // rounded corners blend into the card
        // Index 0 = follow system; 1 = dark; 2 = light.
        popupThemeCombo.Items.Add(Localization.Get("ThemeSystem"));
        popupThemeCombo.Items.Add(Localization.Get("ThemeDark"));
        popupThemeCombo.Items.Add(Localization.Get("ThemeLight"));
        popupThemeCombo.SelectedIndex = (int)(Program.Instance?.GetPopupTheme() ?? ThemeMode.System);
        popupThemeCombo.SelectedIndexChanged += (_, _) =>
        {
            // 同 themeCombo：忽略 ReapplySelection 的 -1 中间事件，避免把 -1
            // 强转成 ThemeMode 导致弹窗主题瞬时退回"跟随系统"触发闪变。
            if (popupThemeCombo.SelectedIndex < 0) return;
            var theme = (ThemeMode)popupThemeCombo.SelectedIndex;
            Program.Instance?.SetPopupTheme(theme);
        };
        var popupThemeRow = BuildSettingRow(Localization.Get("PopupTheme"), popupThemeCombo);

        // ---- Setting row 4b: disable (same logic as tray menu) ----
        _disableCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(320 * _dpiScale)
        };
        _disableCombo.ApplyTheme(InputBg, TextMain);
        _disableCombo.SetParentBackground(BgInner);
        // 与右键菜单"禁用"子菜单完全一致：关闭/永久/1/5/15/30分钟/1/3/5/12小时/1天/日出日落。
        _disableCombo.Items.Add(Localization.Get("DisableOff"));
        _disableCombo.Items.Add(Localization.Get("DisablePermanent"));
        _disableCombo.Items.Add(Localization.Get("Disable1Min"));
        _disableCombo.Items.Add(Localization.Get("Disable5Min"));
        _disableCombo.Items.Add(Localization.Get("Disable15Min"));
        _disableCombo.Items.Add(Localization.Get("Disable30Min"));
        _disableCombo.Items.Add(Localization.Get("Disable1Hour"));
        _disableCombo.Items.Add(Localization.Get("Disable3Hours"));
        _disableCombo.Items.Add(Localization.Get("Disable5Hours"));
        _disableCombo.Items.Add(Localization.Get("Disable12Hours"));
        _disableCombo.Items.Add(Localization.Get("Disable1Day"));
        _disableCombo.Items.Add(Localization.Get("DisableUntilSunset")); // 占位，刷新时按昼夜切换
        _disableCombo.SelectedIndex = 0;
        _disableCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingDisable) return;
            var inst = Program.Instance;
            if (inst == null) return;
            switch (_disableCombo.SelectedIndex)
            {
                case 0: inst.SetDisable(TimeSpan.Zero); break;           // 关闭
                case 1: inst.SetDisable(null); break;                    // 永久
                case 2: inst.SetDisable(TimeSpan.FromMinutes(1)); break;
                case 3: inst.SetDisable(TimeSpan.FromMinutes(5)); break;
                case 4: inst.SetDisable(TimeSpan.FromMinutes(15)); break;
                case 5: inst.SetDisable(TimeSpan.FromMinutes(30)); break;
                case 6: inst.SetDisable(TimeSpan.FromHours(1)); break;
                case 7: inst.SetDisable(TimeSpan.FromHours(3)); break;
                case 8: inst.SetDisable(TimeSpan.FromHours(5)); break;
                case 9: inst.SetDisable(TimeSpan.FromHours(12)); break;
                case 10: inst.SetDisable(TimeSpan.FromDays(1)); break;
                case 11:
                    // 日出/日落：与右键菜单一致，仅时间调整启用时可选。
                    if (inst.GetSolarAdjustEnabled()) inst.SetDisable(TimeSpan.FromSeconds(-1));
                    break;
            }
            RefreshDisableCombo();
        };
        var disableRight = new Panel
        {
            BackColor = BgInner,
            AutoSize = false,
            Size = new Size(_disableCombo.Width, _disableCombo.Height)
        };
        disableRight.Controls.Add(_disableCombo);
        disableRight.Layout += (_, _) =>
        {
            _disableCombo.Location = new Point(0, 0);
        };
        var disableRow = BuildSettingRow(Localization.Get("DisableMenu"), disableRight);
        RefreshDisableCombo();

        // ---- Setting row 5b: 滚轮调节总开关 (wheel brightness master switch) ----
        var wheelToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetWheelEnabled() ?? true
        };
        wheelToggle.ApplyDpiScale(_dpiScale);
        wheelToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetWheelEnabled(wheelToggle.Checked);
        var wheelGroup = BuildToggleGroup(wheelToggle);
        var wheelRow = BuildSettingRow(Localization.Get("WheelEnabled"), wheelGroup);

        // ---- Setting row 5: 反向滚轮 (invert wheel direction) ----
        var invertToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetInvertScroll() ?? false
        };
        invertToggle.ApplyDpiScale(_dpiScale);
        invertToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetInvertScroll(invertToggle.Checked);
        var invertGroup = BuildToggleGroup(invertToggle);
        var invertRow = BuildSettingRow(Localization.Get("InvertScroll"), invertGroup);


        // ---- Setting row 6: OSD 浮窗开关 (show wheel OSD) ----
        var overlayToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetShowOverlay() ?? true
        };
        overlayToggle.ApplyDpiScale(_dpiScale);
        overlayToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetShowOverlay(overlayToggle.Checked);
        var overlayGroup = BuildToggleGroup(overlayToggle);
        var overlayRow = BuildSettingRow(Localization.Get("ShowOverlay"), overlayGroup);

        // ---- Setting row 8a: Gamma 自愈 (self-heal after sleep/monitor change) ----
        var selfHealToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetGammaSelfHealEnabled() ?? true
        };
        selfHealToggle.ApplyDpiScale(_dpiScale);
        selfHealToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetGammaSelfHealEnabled(selfHealToggle.Checked);
        var selfHealGroup = BuildToggleGroup(selfHealToggle);
        var selfHealRow = BuildSettingRow(Localization.Get("GammaSelfHeal"), selfHealGroup);
        // BuildGeneralPage 每次 RebuildUi 都会重建：旧 ToolTip 先释放
        // （含原生窗口句柄），否则每次重建泄漏一个。
        _selfHealTip?.Dispose();
        _selfHealTip = new ToolTip();
        _selfHealTip.SetToolTip(selfHealRow, Localization.Get("GammaSelfHealHint"));

        // ---- Setting row 8b: 全屏自动暂停 (pause gamma in fullscreen apps) ----
        var fullscreenToggle = new ToggleSwitch
        {
            // ⚠️ 兜底值必须与 `AppSettings.PauseInFullscreenEnabled` 构造默认一致（false）。
            Checked = Program.Instance?.GetPauseInFullscreenEnabled() ?? false
        };
        fullscreenToggle.ApplyDpiScale(_dpiScale);
        fullscreenToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetPauseInFullscreenEnabled(fullscreenToggle.Checked);
        // ⚠ 子行「按显示器生效」的出现/消失由**下方 Dock 布局段**再挂一个 CheckedChanged 处理
        //   —— 那里才能通过编译期的"确定赋值"检查（它引用的 fullscreenRow / 子行在下方才创建）。
        var fullscreenGroup = BuildToggleGroup(fullscreenToggle);
        var fullscreenRow = BuildSettingRow(Localization.Get("PauseInFullscreen"), fullscreenGroup);
        _generalFullscreenToggle = fullscreenToggle;   // 供跨页刷新读"总开关是否开"
        _generalFullscreenRow = fullscreenRow;
        _fullscreenTip?.Dispose();
        _fullscreenTip = new ToolTip();
        _fullscreenTip.SetToolTip(fullscreenRow, Localization.Get("PauseInFullscreenHint"));

        // ---- Setting row 8b-1（2026-09-19 定稿 §三）：全屏「按显示器生效」子开关 ----
        // 用户 D2：形态照抄白名单 —— 总开关**开启后**其下方才出现本行（平时不增加选项）。
        // 门控 D3 = 全屏暂停开 && 独立控制开 && 受控屏≥2（与逐屏白名单**完全同构**）。
        var fsPerMonToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetFullscreenPerMonitor() ?? false
        };
        fsPerMonToggle.ApplyDpiScale(_dpiScale);
        fsPerMonToggle.CheckedChanged += (_, _) =>
        {
            if (_fsPerMonSilent) return;   // 程序性赋值不写配置（同值早退、异值才会发事件）
            Program.Instance?.SetFullscreenPerMonitor(fsPerMonToggle.Checked);
        };
        _fsPerMonToggle = fsPerMonToggle;
        _fsPerMonRow = BuildSettingRow(Localization.Get("FsPerMonitor"), BuildToggleGroup(fsPerMonToggle));
        _fsPerMonTip?.Dispose();
        _fsPerMonTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };
        string fsPerMonHint = Localization.Get("FsPerMonitorHint");
        // 行/标签/开关三处都挂 —— 子控件会吞掉父级的悬停（白名单同款处理）。
        // ⚠️ 2026-09-20：这里只是**初始**文案；真实文案由 UpdateFsPerMonHint() 按门控刷新
        //   （原来固定不变 ⇒ 门控满足时仍显示「独立控制没开」，用户报为错话）。
        _fsPerMonTip.SetToolTip(_fsPerMonRow, fsPerMonHint);
        Label? fsPerMonLabel = FindRowLabel(_fsPerMonRow);
        if (fsPerMonLabel != null) _fsPerMonTip.SetToolTip(fsPerMonLabel, fsPerMonHint);
        _fsPerMonTip.SetToolTip(fsPerMonToggle, fsPerMonHint);
        // 2026-09-20：**只读探针** —— ToolTip 文本对外部不可读（WinForms 自绘），
        // 于是"提示文案对不对"一直只能靠用户肉眼看（此前吃过两次亏：
        // 「一点提示也没有」、「解锁了还提示没开」都无法自动断言）。
        // 这里把当前文案暴露出来，测试脚本可断言"门控满足 ⇒ 文案为空"。
        AutomationBridge.Register(new AutomationAction("Gen_FsPerMonHint", "value",
            Get: () => _fsPerMonHintText));
        AutomationBridge.Wire((fsPerMonToggle, "Gen_FsPerMonitorToggle"));

        // ---- Setting row 8c: 导入/导出设置（一行两按钮）----
        var exportBtn = new RoundedButton
        {
            Text = Localization.Get("ExportSettings"),
            Font = UiFont(9f),
            Width = (int)(100 * _dpiScale),
            Height = (int)(28 * _dpiScale),
            TabStop = false
        };
        exportBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        exportBtn.SetParentBackground(BgInner);
        exportBtn.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                Localization.Get("ExportConfirm"),
                Localization.Get("ExportSettings"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            using var dlg = new SaveFileDialog
            {
                Title = Localization.Get("ExportSettings"),
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "GammaBrightnessTool-settings.json",
                DefaultExt = "json",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (Program.Instance?.ExportSettings(dlg.FileName) == true)
            {
                MessageBox.Show(
                    Localization.Get("ExportDone"),
                    Localization.Get("ExportSettings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        };

        var importBtn = new RoundedButton
        {
            Text = Localization.Get("ImportSettings"),
            Font = UiFont(9f),
            Width = (int)(100 * _dpiScale),
            Height = (int)(28 * _dpiScale),
            TabStop = false
        };
        importBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        importBtn.SetParentBackground(BgInner);
        importBtn.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                Localization.Get("ImportConfirm"),
                Localization.Get("ImportSettings"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            using var dlg = new OpenFileDialog
            {
                Title = Localization.Get("ImportSettings"),
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (Program.Instance?.ImportSettings(dlg.FileName) == true)
            {
                MessageBox.Show(
                    Localization.Get("ImportDone"),
                    Localization.Get("ImportSettings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                RebuildUi();
            }
            else
            {
                MessageBox.Show(
                    Localization.Get("ImportInvalid"),
                    Localization.Get("ImportSettings"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        // 两按钮并排的右侧控件组（与 BuildToggleGroup 相同的容器背景）。
        var importExportGroup = new Panel
        {
            BackColor = BgInner,
            AutoSize = false
        };
        importExportGroup.Controls.Add(importBtn);
        importExportGroup.Controls.Add(exportBtn);
        importExportGroup.Layout += (_, _) =>
        {
            int gap = (int)(8 * _dpiScale);
            importExportGroup.Size = new Size(exportBtn.Width + gap + importBtn.Width, exportBtn.Height);
            exportBtn.Location = new Point(0, 0);
            importBtn.Location = new Point(exportBtn.Width + gap, 0);
        };
        var importExportRow = BuildSettingRow(Localization.Get("ImportExportSettings"), importExportGroup);

        // ---- Setting row 8: 重置设置 (reset all settings to defaults) ----
        var resetBtn = new RoundedButton
        {
            Text = Localization.Get("ResetSettings"),
            Font = UiFont(9f),
            Width = (int)(110 * _dpiScale),
            Height = (int)(28 * _dpiScale),
            TabStop = false
        };
        resetBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        resetBtn.SetParentBackground(BgInner);
        resetBtn.Click += (_, _) =>
        {
            // Confirm before wiping the user's settings.
            var confirm = MessageBox.Show(
                Localization.Get("ResetConfirm"),
                Localization.Get("ResetSettings"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            Program.Instance?.ResetSettings();
            // The window is being rebuilt anyway; clear the top-most flag
            // applied earlier if the switch had turned it on.
            TopMost = Program.Instance?.GetTopMost() ?? false;
            MessageBox.Show(
                Localization.Get("ResetDone"),
                Localization.Get("ResetSettings"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            // Rebuild the whole UI so every row reflects the defaults
            // (language/theme changes also need a full rebuild).
            RebuildUi();
        };
        var resetRow = BuildSettingRow(Localization.Get("ResetSettings"), resetBtn);

        // Dock layout runs in reverse z-order: last added docks first (top).
        // Add bottom-most first, top-most last.
        resetRow.Dock = DockStyle.Top;
        scroll.Controls.Add(resetRow);

        importExportRow.Dock = DockStyle.Top;
        scroll.Controls.Add(importExportRow);

        // ---- 左键弹窗 / OSD 浮窗透明度（两条独立滑轨，位于"导入/导出设置"上方）----
        // Dock=Top 后添加者在上：先 add OSD 行、再 add 弹窗行 → 视觉自上而下为
        // 左键弹窗透明度、OSD 浮窗透明度，紧邻"导入/导出设置"行之上。
        var overlayOpacitySlider = BuildOpacitySlider(
            Program.Instance?.GetOverlayOpacityPercent() ?? 70,
            v => Program.Instance?.SetOverlayOpacityPercent((int)Math.Round(v)));
        var overlayOpacityRow = BuildSettingRow(Localization.Get("OverlayOpacity"), overlayOpacitySlider);
        overlayOpacityRow.Dock = DockStyle.Top;
        scroll.Controls.Add(overlayOpacityRow);

        var popupOpacitySlider = BuildOpacitySlider(
            Program.Instance?.GetPopupOpacityPercent() ?? 90,
            v => Program.Instance?.SetPopupOpacityPercent((int)Math.Round(v)));
        var popupOpacityRow = BuildSettingRow(Localization.Get("PopupOpacity"), popupOpacitySlider);
        popupOpacityRow.Dock = DockStyle.Top;
        scroll.Controls.Add(popupOpacityRow);

        overlayRow.Dock = DockStyle.Top;
        scroll.Controls.Add(overlayRow);

        fullscreenRow.Dock = DockStyle.Top;
        scroll.Controls.Add(fullscreenRow);

        // ------------------------------------------------------------------
        // 2026-09-19：子行「按显示器生效」的显示/隐藏 + 门控置灰。
        // 实现已提升为**类级方法** <see cref="SyncFullscreenPerMonRow"/> ——
        // 因为用户可能在**显示器页**开关「独立控制」，而子开关在**通用页**：
        // 写成局部函数时显示器页拿不到它 ⇒ 子开关的锁定态要重开设置窗才刷新
        // （用户 2026-09-19 实测报告）。
        // ------------------------------------------------------------------
        fullscreenToggle.CheckedChanged += (_, _) => SyncFullscreenPerMonRow();
        // 进入本页时也刷新一次（用户可能刚在显示器页改过「独立控制」）。
        page.VisibleChanged += (_, _) => { if (page.Visible) SyncFullscreenPerMonRow(); };
        SyncFullscreenPerMonRow();   // 构建期按当前状态落定

        selfHealRow.Dock = DockStyle.Top;
        scroll.Controls.Add(selfHealRow);

        invertRow.Dock = DockStyle.Top;
        scroll.Controls.Add(invertRow);

        wheelRow.Dock = DockStyle.Top;
        scroll.Controls.Add(wheelRow);

        // 禁用行：浮窗主题正下方（Dock 逆序，popupTheme 之前 add 则显示在其下方）。
        disableRow.Dock = DockStyle.Top;
        scroll.Controls.Add(disableRow);

        // ---- P1/P2″ Setting row: 托盘图标常驻显示 ----
        var trayVisibleToggle = new ToggleSwitch();
        trayVisibleToggle.ApplyDpiScale(_dpiScale);
        // P2″：系统「隐藏的图标菜单」总开关被用户明确关闭 → 强制常驻并锁定。
        // 只锁 UI（Checked=true + Enabled=false），**不改** KeepTrayIconVisible 的存储值
        // —— 总开关恢复开启后用户原偏好自动生效，无数据丢失（§7.5）。
        // fail-open：只有明确读到 0 才锁；缺失 / 异常 / 非 0 一律不锁。
        bool overflowOff = TrayVisibilityService.IsOverflowMenuExplicitlyOff();
        OpLog.Log($"[TrayVisibility] UI: 总开关明确为关={overflowOff} → 开关" +
                  (overflowOff ? "锁定为「开」且不可点击（P2″ 防图标消失）" : "正常可操作"));
        // 先赋初值**再**挂事件：否则构建期这一次赋值会触发 CheckedChanged，
        // 造成一次无意义的设置写入 + 一次注定读到旧值的注册表回读（误报"未生效"）。
        trayVisibleToggle.Checked = overflowOff || (Program.Instance?.GetKeepTrayIconVisible() ?? true);
        if (overflowOff)
        {
            trayVisibleToggle.Enabled = false;
        }
        // 注意：这里**不做**注册表回读 —— 写入由后台阶梯异步完成（首拍 600ms），
        // 同步读必然读到旧值。提示由 TrayVisibilityLadderFinished 事件驱动（见下）。
        trayVisibleToggle.CheckedChanged += (_, _) =>
        {
            Program.Instance?.SetKeepTrayIconVisible(trayVisibleToggle.Checked);
            if (!trayVisibleToggle.Checked && _trayVisTip != null)
            {
                _trayVisTip.SetToolTip(trayVisibleToggle, "");   // 关闭态无需任何提示
            }
        };
        _trayVisibleToggle = trayVisibleToggle;
        var trayVisibleGroup = BuildToggleGroup(trayVisibleToggle);
        var trayVisibleRow = BuildSettingRow(Localization.Get("TrayVisibility"), trayVisibleGroup);
        trayVisibleRow.Dock = DockStyle.Top;
        scroll.Controls.Add(trayVisibleRow);

        popupThemeRow.Dock = DockStyle.Top;
        scroll.Controls.Add(popupThemeRow);

        themeRow.Dock = DockStyle.Top;
        scroll.Controls.Add(themeRow);

        langRow.Dock = DockStyle.Top;
        scroll.Controls.Add(langRow);

        startupRow.Dock = DockStyle.Top;
        scroll.Controls.Add(startupRow);
        var title = new Label
        {
            Text = Localization.Get("SettingsGeneral"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        // ---- P5：通用页控件的稳定 ID（L0）+ 动作注册（L1）----
        // 集中在此而非散落在每个 new 的位置：这些变量到这里都还在作用域内，且
        // RebuildUi 已按 Gen_ 前缀清过旧注册。注册只驱动控件本身、不复制业务逻辑 ——
        // 控件的既有事件处理器负责落业务，自动化与手点因此走同一条代码。
        // P1/P2″：开关的悬停提示（被锁定 / 条目未生成）。非模态、不打断。
        _trayVisTip = new ToolTip();
        if (overflowOff)
        {
            _trayVisTip.SetToolTip(trayVisibleToggle, Localization.Get("TrayVisibilityBlocked"));
        }

        // P1：阶梯出结果后再更新提示（成功清掉、耗尽仍未成功才显示 NotReady）
        if (Program.Instance != null)
        {
            Program.Instance.TrayVisibilityLadderFinished -= OnTrayVisLadderFinished;
            Program.Instance.TrayVisibilityLadderFinished += OnTrayVisLadderFinished;
        }

        AutomationBridge.Wire(
            (startupToggle, "Gen_StartupToggle"),
            (trayVisibleToggle, "Gen_TrayVisibleToggle"),
            (langCombo, "Gen_LangCombo"),
            (themeCombo, "Gen_ThemeCombo"),
            (popupThemeCombo, "Gen_PopupThemeCombo"),
            (_disableCombo!, "Gen_DisableCombo"),
            (wheelToggle, "Gen_WheelToggle"),
            (invertToggle, "Gen_WheelInvertToggle"),
            (overlayToggle, "Gen_OverlayToggle"),
            (selfHealToggle, "Gen_SelfHealToggle"),
            (fullscreenToggle, "Gen_FullscreenToggle"),
            (overlayOpacitySlider, "Gen_OverlayOpacitySlider"),
            (popupOpacitySlider, "Gen_PopupOpacitySlider"));

        // 这三个按钮会弹模态对话框（文件选择 / 确认框）：对话框期间 UI 线程被占住，
        // 管道命令的 Control.Invoke 会一直阻塞到用户关闭对话框为止 → 接口表现为"卡死"。
        // 故标为破坏性，需 --allow-destructive 才允许执行。
        AutomationBridge.WireDestructive(
            (exportBtn, "Gen_ExportBtn"),
            (importBtn, "Gen_ImportBtn"),
            (resetBtn, "Gen_ResetBtn"));

        return page;
    }

    /// <summary>
    /// Builds a bordered setting row: label text on the left, the given
    /// control on the right, all centered vertically.
    /// The 1px frame is the outer panel's background color shown through a
    /// 1px Padding; children live inside an inner white panel (Dock=Fill),
    /// so they can never paint over the frame. The caller docks the returned
    /// panel (Dock=Top) so its width always matches the parent, which keeps
    /// the whole frame visible at any DPI. Child positions are recomputed in
    /// </summary>
    // ==================================================================
    // B' 字体基础设施（2026-09-22）
    //
    // 设计：**静态核心 + 实例包装**。
    //   · `PxFont` / `PxOf` / `FitLabelPx`（static，显式收 `dpiScale`）是唯一真相源，
    //     供本窗体与**嵌套类**（`ExpandableHeader` 等自持 `_dpiScale`）共用；
    //   · `UiFont` / `FitLabelFont`（实例）只是用 `_dpiScale` 转调。
    //   ⇒ 换算系数只存在一处，不会漂移。
    //   （曾有 `UiFontSize(float)` 实例包装，改为 `PxOf` 后零调用点，已删。）
    // ==================================================================

    /// <summary>
    /// 本窗体的 UI 字体入口 —— **转调 <see cref="DpiFont"/>**（全项目唯一真相源，
    /// 见该类注释里的实测定标与换算系数）。
    ///
    /// 保留本组包装只为两点：① 调用点读起来短（`UiFont(10f)`）；② 嵌套类可通过
    /// <see cref="PxFont(float, float, FontStyle)"/> 显式传自己的 `dpiScale`。
    /// ⛔ 任何新控件都应使用本入口（或 <see cref="DpiFont"/>），**不得**再直接
    /// `new Font(..., 数字F)` —— 那是 pt 单位，会重新引入 DPI 混排。
    /// </summary>
    internal static Font PxFont(float dpiScale, float designPt, FontStyle style = FontStyle.Regular)
        => DpiFont.Get(dpiScale, designPt, style);

    /// <summary>同上，但指定字族（如标题栏按钮用的 <c>Segoe MDL2 Assets</c>）。</summary>
    internal static Font PxFont(float dpiScale, string family, float designPt, FontStyle style = FontStyle.Regular)
        => DpiFont.Get(dpiScale, family, designPt, style);

    /// <summary>设计 pt → **px** 的标量换算（与 <see cref="DpiFont.Px"/> 同系数）。</summary>
    internal static float PxOf(float dpiScale, float designPt) => DpiFont.Px(dpiScale, designPt);

    /// <summary>按当前 `_dpiScale` 建 UI 字体（见 <see cref="PxFont(float, float, FontStyle)"/>）。</summary>
    private Font UiFont(float designPt, FontStyle style = FontStyle.Regular)
        => PxFont(_dpiScale, designPt, style);

    /// <summary>同上，指定字族。</summary>
    private Font UiFont(string family, float designPt, FontStyle style = FontStyle.Regular)
        => PxFont(_dpiScale, family, designPt, style);


    /// <summary>按当前 `_dpiScale` 缩小字号（见 <see cref="FitLabelPx"/>）。</summary>
    private float FitLabelFont(string text, Font font, int maxWidth, int maxHeight)
        => FitLabelPx(_dpiScale, text, font, maxWidth, maxHeight);

    /// <summary>
    /// Shrinks a label's font (down to 6pt design) so its text fits within the
    /// given width/height. Long translations (German, Russian, French, ...)
    /// wrap across lines and the font shrinks only when the wrapped text
    /// would exceed the available height. Returns the chosen font size.
    ///
    /// B'（2026-09-22）：入参/返回值单位一律改为**像素**（原为 pt，与 px 字体混用会
    /// 让"缩字"逻辑量错 1/3）——级差仍是设计的 0.5pt，经 <see cref="PxOf"/> 换算成
    /// px；下限仍是设计 6pt。探测字体也改用 Pixel 单位，与真实字体同源。
    /// 步数固定 8 步（10 → 6.5pt 共 8 档，与旧实现逐档一致），避免浮点累减漂移。
    /// </summary>
    internal static float FitLabelPx(float dpiScale, string text, Font font, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return PxOf(dpiScale, 10f);
        // Always measure from the base size, never from the current
        // (possibly already-shrunk) font, so a label can grow back.
        float size = PxOf(dpiScale, 10f);
        float step = PxOf(dpiScale, 0.5f);
        for (int i = 0; i < 8; i++)
        {
            using var probe = new Font(font.FontFamily, size, font.Style, GraphicsUnit.Pixel);
            var sz = TextRenderer.MeasureText(text, probe, new Size(maxWidth, int.MaxValue),
                TextFormatFlags.WordBreak);
            if (sz.Height <= maxHeight) return size;
            size -= step;
        }
        return PxOf(dpiScale, 6f);
    }

    /// <summary>
    /// Builds a right-side group containing a state label (开/关) and a
    /// ToggleSwitch, laid out live in Layout with the current DPI font so
    /// wider glyphs never overlap the switch. Shared by the startup,
    /// inverted-wheel and OSD rows.
    /// </summary>
    private Panel BuildToggleGroup(ToggleSwitch toggle)
    {
        var stateLabel = new ThemedLabel
        {
            Text = toggle.Checked ? Localization.Get("On") : Localization.Get("Off"),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Font = UiFont(10f),
            ForeColor = TextMain
        };
        toggle.CheckedChanged += (_, _) =>
            stateLabel.Text = toggle.Checked ? Localization.Get("On") : Localization.Get("Off");

        var group = new Panel
        {
            BackColor = BgInner,
            AutoSize = false
        };
        group.Controls.Add(stateLabel);
        group.Controls.Add(toggle);
        group.Layout += (_, _) =>
        {
            // Long translations ("Включено", "Activé", ...) wrap; the font
            // shrinks if the wrapped text still does not fit the row height.
            int maxTextW = (int)(90 * _dpiScale);
            int textW = Math.Min(
                TextRenderer.MeasureText(stateLabel.Text, stateLabel.Font).Width,
                maxTextW);
            int groupH = (int)(22 * _dpiScale);
            float size = FitLabelFont(stateLabel.Text, stateLabel.Font, textW, groupH);
            if (Math.Abs(size - stateLabel.Font.Size) > 0.01f)
            {
                var old = stateLabel.Font;
                stateLabel.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Pixel);
                old.Dispose();
            }
            group.Size = new Size(textW + 10 + toggle.Width, groupH);
            stateLabel.Size = new Size(textW, groupH);
            stateLabel.Location = new Point(0, 0);
            toggle.Location = new Point(textW + 10, (group.Height - toggle.Height) / 2);
        };
        return group;
    }

    /// <summary>
    /// 让 Label 在禁用时显示灰色（WinForms 默认用 SystemColors.GrayText，
    /// 深色主题下是黑色）。启用时恢复正文色。
    /// </summary>

    private Panel BuildSettingRow(string labelText, Control rightControl)
    {
        // Rounded card frame (6px corners) with a 1px rounded border, so
        // rows look like separate cards instead of a table. The 6px top
        // margin creates breathing room between rows.
        var outer = new RoundedCardPanel
        {
            Height = (int)(48 * _dpiScale),
            Margin = new Padding(0, (int)(10 * _dpiScale), 0, 0)
        };
        outer.ApplyTheme(Bg, BgInner, Border);

        var inner = outer.Inner;

        var label = new ThemedLabel
        {
            Text = labelText,
            Font = UiFont(10f),
            // Fixed-size label with word wrap: long translations (German,
            // Russian, French, ...) wrap to extra lines and the font shrinks
            // if needed so they never cover the right control.
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = (int)(24 * _dpiScale),
            ForeColor = TextMain
        };

        inner.Controls.Add(label);
        inner.Controls.Add(rightControl);

        inner.Layout += (_, _) =>
        {
            // Reserve room for the right control plus margins; long labels
            // wrap (and shrink) instead of overlapping it.
            int labelW = Math.Max(0, inner.Width - rightControl.Width - 14 * 2 - (int)(10 * _dpiScale));
            int maxH = inner.Height - (int)(8 * _dpiScale);
            float size = FitLabelFont(label.Text, label.Font, labelW, maxH);
            if (Math.Abs(size - label.Font.Size) > 0.01f)
            {
                var old = label.Font;
                label.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Pixel);
                old.Dispose();
            }
            int textH = Math.Min(
                TextRenderer.MeasureText(label.Text, label.Font, new Size(labelW, int.MaxValue),
                    TextFormatFlags.WordBreak).Height,
                maxH);
            label.SetBounds(14, VerticalCenter(inner.Height, textH), labelW, textH);
            rightControl.Location = new Point(
                inner.Width - rightControl.Width - 14,
                VerticalCenter(inner.Height, rightControl.Height));
        };

        return outer;
    }

    /// <summary>
    /// Returns the Y coordinate that vertically centers a control of height
    /// <paramref name="controlH"/> within a parent of height <paramref name="parentH"/>.
    /// When the gap is odd, the extra pixel is placed at the bottom so the
    /// control center leans slightly toward the bottom rather than the top
    /// (a 1-px "偏上" artifact that was visible in the rename input box at
    /// non-integer DPI scales, where 26*DPI often produces an odd Height).
    /// </summary>
    private static int VerticalCenter(int parentH, int controlH)
    {
        int diff = parentH - controlH;
        return diff / 2 + (diff & 1);   // odd diff → +1 → control center slightly low
    }

    private Panel BuildColorTempPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        // Scrollable container (same pattern as the other pages).
        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);

        // ---- 色温调节总开关 (color temperature master switch) ----
        var colorTempToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetColorTemperatureEnabled() ?? false
        };
        colorTempToggle.ApplyDpiScale(_dpiScale);

        // ---- 色温滚轮步进 (temperature wheel step) ----
        // Presets: 50/100/200/500/1000/2000/3000K. Default 100K.
        var tempStepCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        tempStepCombo.ApplyTheme(InputBg, TextMain);
        tempStepCombo.SetParentBackground(BgInner);
        int[] tempStepPresets = { 50, 100, 200, 500, 1000, 2000, 3000 };
        foreach (int p in tempStepPresets)
            tempStepCombo.Items.Add($"{p}K");
        float savedTempStep = Program.Instance?.GetTemperatureStepSize() ?? GammaController.DEFAULT_TEMPERATURE_STEP;
        int savedTempStepInt = (int)Math.Round(savedTempStep);
        tempStepCombo.SelectedIndex = Array.FindIndex(tempStepPresets, p => p >= savedTempStepInt);
        if (tempStepCombo.SelectedIndex < 0) tempStepCombo.SelectedIndex = tempStepPresets.Length - 1;
        tempStepCombo.SelectedIndexChanged += (_, _) =>
        {
            int idx = tempStepCombo.SelectedIndex;
            if (idx >= 0 && idx < tempStepPresets.Length)
                Program.Instance?.SetTemperatureStepSize(tempStepPresets[idx]);
        };

        // 当色温开关关闭时，锁定步进下拉
        tempStepCombo.Enabled = colorTempToggle.Checked;
        colorTempToggle.CheckedChanged += (_, _) =>
        {
            Program.Instance?.SetColorTemperatureEnabled(colorTempToggle.Checked);
            tempStepCombo.Enabled = colorTempToggle.Checked;
            // 同步时间调整页：色温总开关决定白天/夜晚色温滑块启停。
            _refreshSolarState?.Invoke();
            // 跨页同步快捷键页：色温总开关变化立即反映到色温快捷键子开关
            // （关→锁定，开→解锁），无需切页或重启主开关。
            SyncHotKeySubToggles();
        };

        var colorTempGroup = BuildToggleGroup(colorTempToggle);
        var colorTempRow = BuildSettingRow(Localization.Get("ColorTemperatureEnabled"), colorTempGroup);
        var tempStepRow = BuildSettingRow(Localization.Get("TemperatureStepSize"), tempStepCombo);
        tempStepRow.Enabled = colorTempToggle.Checked;
        colorTempToggle.CheckedChanged += (_, _) => tempStepRow.Enabled = colorTempToggle.Checked;

        // ---- 色温预设 (quick preset dropdown) ----
        // Presets: warm 4000K / 5500K / neutral 6600K (default) / cool 8000K.
        // Selecting applies the temperature directly (via the controller).
        // The dropdown is disabled (grayed) while color temperature is off.
        float[] presets = { 4000f, 5500f, 6600f, 8000f };
        string defaultSuffix = Localization.Get("DefaultSuffix");
        string[] presetLabels =
        {
            "4000K",
            "5500K",
            "6600K" + (string.IsNullOrEmpty(defaultSuffix) ? "" : defaultSuffix),
            "8000K"
        };
        var presetCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        presetCombo.ApplyTheme(InputBg, TextMain);
        presetCombo.SetParentBackground(BgInner);
        foreach (string label in presetLabels) presetCombo.Items.Add(label);

        // Select the preset nearest the current temperature (so the box shows
        // the active preset on open).
        // Guard so programmatic selection (syncing the dropdown to the
        // current temperature) does NOT fire SetColorTemperature. Only a
        // real user pick should change the temperature.
        bool syncingPreset = false;
        Action refreshPresetSelection = () =>
        {
            float current = Program.Instance?.GetCurrentTemperature() ?? GammaController.DEFAULT_TEMPERATURE;
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < presets.Length; i++)
            {
                float d = Math.Abs(current - presets[i]);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            syncingPreset = true;
            try { presetCombo.SelectedIndex = best; }
            finally { syncingPreset = false; }
        };

        // 收起时显示实时色温值（如 4200K），展开列表不变仍为挡位项。
        Action refreshPresetDisplay = () =>
        {
            int k = (int)Math.Round(Program.Instance?.GetCurrentTemperature() ?? GammaController.DEFAULT_TEMPERATURE);
            presetCombo.DisplayText = $"{k}K";
        };
        presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (syncingPreset) return;
            int idx = presetCombo.SelectedIndex;
            if (idx >= 0 && idx < presets.Length)
                Program.Instance?.SetColorTemperature(presets[idx]);
        };

        // When the master switch turns off, the dropdown is disabled but its
        // selection still reflects the saved/last temperature; re-enabling
        // re-syncs it.
        presetCombo.Enabled = colorTempToggle.Checked;
        _disableLocked.Add((presetCombo, () => colorTempToggle.Checked));
        colorTempToggle.CheckedChanged += (_, _) =>
        {
            presetCombo.Enabled = colorTempToggle.Checked;
            if (colorTempToggle.Checked) refreshPresetSelection();
        };
        // 不再在此处订阅 Program.Instance.TemperatureChanged：构建期订阅在
        // RebuildUi 重建时只 += 永不 -=，会累积旧闭包并持有已 Dispose 的控件。
        // 改由构造器 OnProgramTemperatureChanged 单次挂载 + _refreshPreset* 字段转发。
        refreshPresetSelection();
        refreshPresetDisplay();
        _refreshPresetSelection = refreshPresetSelection;
        _refreshPresetDisplay = refreshPresetDisplay;

        // ---- 色温范围 (custom temperature range) ----
        // Two numeric fields (Min ~ Max K) separated by a "~" sign, plus a
        // confirm button. Enter or the button commits; clicking anywhere
        // else (focus leave) discards and restores the saved values.
        var rangePanel = new Panel
        {
            Width = (int)(250 * _dpiScale),
            Height = (int)(26 * _dpiScale),
        };
        var minBox = new RoundedTextBox
        {
            Width = (int)(48 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            Font = UiFont(9f),
            TextAlign = HorizontalAlignment.Center,
            Text = ((int)(Program.Instance?.GetMinTemperature() ?? GammaController.MIN_TEMPERATURE)).ToString()
        };
        minBox.ApplyTheme(InputBg, TextMain);
        minBox.SetParentBackground(BgInner);
        var maxBox = new RoundedTextBox
        {
            Width = (int)(48 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            Font = UiFont(9f),
            TextAlign = HorizontalAlignment.Center,
            Text = ((int)(Program.Instance?.GetMaxTemperature() ?? GammaController.MAX_TEMPERATURE)).ToString()
        };
        maxBox.ApplyTheme(InputBg, TextMain);
        maxBox.SetParentBackground(BgInner);
        var tildeLabel = new ThemedLabel
        {
            Text = "~",
            Font = UiFont(10f),
            ForeColor = TextSub,
            AutoSize = false,
            Height = (int)(20 * _dpiScale),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var rangeConfirmBtn = new RoundedButton
        {
            Text = "\u221A",
            Font = UiFont(9f, FontStyle.Bold),
            Width = (int)(24 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TabStop = false
        };
        rangeConfirmBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        rangeConfirmBtn.SetParentBackground(BgInner);

        Action restoreRange = () =>
        {
            // Discard the edit and restore the saved values (focus left
            // without confirming).
            minBox.Text = ((int)(Program.Instance?.GetMinTemperature() ?? GammaController.MIN_TEMPERATURE)).ToString();
            maxBox.Text = ((int)(Program.Instance?.GetMaxTemperature() ?? GammaController.MAX_TEMPERATURE)).ToString();
        };
        Action commitRange = () =>
        {
            // 恒用 InvariantCulture 解析：用户手动输入的 6600.5 在小数点用逗号的
            // 区域（如德语 de-DE）下，默认区域性解析会失败导致改动被静默丢弃。
            if (!float.TryParse(minBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float minK)
                || !float.TryParse(maxBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float maxK))
            {
                restoreRange();
                return;
            }
            Program.Instance?.SetTemperatureRange(minK, maxK);
            // Reflect back the validated values (the controller clamps them).
            minBox.Text = ((int)(Program.Instance?.GetMinTemperature() ?? GammaController.MIN_TEMPERATURE)).ToString();
            maxBox.Text = ((int)(Program.Instance?.GetMaxTemperature() ?? GammaController.MAX_TEMPERATURE)).ToString();
            refreshPresetSelection();
        };
        // Armed while the confirm button is being pressed, so the input
        // boxes' Leave (which fires before the button Click) does not
        // discard the edit. Leave on Enter keeps the value (commit above).
        bool rangeCommitArmed = false;
        minBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { commitRange(); e.SuppressKeyPress = true; } };
        maxBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { commitRange(); e.SuppressKeyPress = true; } };
        minBox.Leave += (_, _) => BeginInvoke((Action)(() => { if (!rangeCommitArmed) restoreRange(); rangeCommitArmed = false; }));
        maxBox.Leave += (_, _) => BeginInvoke((Action)(() => { if (!rangeCommitArmed) restoreRange(); rangeCommitArmed = false; }));
        rangeConfirmBtn.MouseDown += (_, _) => rangeCommitArmed = true;
        rangeConfirmBtn.Click += (_, _) => { rangeCommitArmed = false; commitRange(); };

        rangePanel.Controls.Add(minBox);
        rangePanel.Controls.Add(tildeLabel);
        rangePanel.Controls.Add(maxBox);
        rangePanel.Controls.Add(rangeConfirmBtn);
        rangePanel.Layout += (_, _) =>
        {
            int gap = (int)(10 * _dpiScale);
            int yBox = (rangePanel.Height - minBox.Height) / 2;
            int ySmall = (rangePanel.Height - (int)(20 * _dpiScale)) / 2;
            int tildeW = TextRenderer.MeasureText("~", tildeLabel.Font).Width;
            int x = 0;
            minBox.Location = new Point(x, yBox); x += minBox.Width + gap;
            tildeLabel.SetBounds(x, ySmall, tildeW, (int)(20 * _dpiScale)); x += tildeW + gap;
            maxBox.Location = new Point(x, yBox); x += maxBox.Width + gap;
            rangeConfirmBtn.Location = new Point(x, ySmall); x += rangeConfirmBtn.Width;
            // Grow the panel to fit its content so the confirm button never
            // extends past the row boundary.
            rangePanel.Width = x;
        };

        var rangeRow = BuildSettingRow(Localization.Get("TemperatureRange"), rangePanel);
        rangeRow.Enabled = colorTempToggle.Checked;
        colorTempToggle.CheckedChanged += (_, _) => rangeRow.Enabled = colorTempToggle.Checked;

        // ---- 色温预设 row (label left, dropdown right) ----
        var presetRow = BuildSettingRow(Localization.Get("TemperaturePresets"), presetCombo);
        presetRow.Enabled = colorTempToggle.Checked;
        colorTempToggle.CheckedChanged += (_, _) => presetRow.Enabled = colorTempToggle.Checked;

        tempStepRow.Dock = DockStyle.Top;
        scroll.Controls.Add(tempStepRow);
        rangeRow.Dock = DockStyle.Top;
        scroll.Controls.Add(rangeRow);
        presetRow.Dock = DockStyle.Top;
        scroll.Controls.Add(presetRow);
        // ---- 色温平滑 (startup / schedule smooth transition) ----
        var tempSmoothToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetTemperatureSmooth() ?? true
        };
        tempSmoothToggle.ApplyDpiScale(_dpiScale);
        tempSmoothToggle.CheckedChanged += (_, _) => Program.Instance?.SetTemperatureSmooth(tempSmoothToggle.Checked);
        var tempSmoothGroup = BuildToggleGroup(tempSmoothToggle);
        var tempSmoothRow = BuildSettingRow(Localization.Get("TemperatureSmooth"), tempSmoothGroup);
        tempSmoothRow.Enabled = colorTempToggle.Checked;
        colorTempToggle.CheckedChanged += (_, _) => tempSmoothRow.Enabled = colorTempToggle.Checked;
        tempSmoothRow.Dock = DockStyle.Top;
        scroll.Controls.Add(tempSmoothRow);

        colorTempRow.Dock = DockStyle.Top;
        scroll.Controls.Add(colorTempRow);



        var title = new Label
        {
            Text = Localization.Get("SettingsColorTemp"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        // ---- P5：色温页稳定 ID（L0）+ 动作注册（L1）----
        // 色温上下限输入框（Tmp_MinBox / Tmp_MaxBox）单独设值不会提交 —— 需再
        // ui.click:Tmp_RangeConfirmBtn 走「√」才应用，与用户操作路径一致。
        AutomationBridge.Wire(
            (colorTempToggle, "Tmp_EnableToggle"),
            (tempStepCombo, "Tmp_WheelStepCombo"),
            (presetCombo, "Tmp_PresetCombo"),
            (minBox, "Tmp_MinBox"),
            (maxBox, "Tmp_MaxBox"),
            (rangeConfirmBtn, "Tmp_RangeConfirmBtn"),
            (tempSmoothToggle, "Tmp_SmoothToggle"));

        // F19：色温范围输入框（minBox / maxBox）挂"程序性焦点取消全选"
        SuppressProgrammaticSelectAll(page);

        return page;
    }

    private Panel BuildSolarPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);

        var inst = Program.Instance;

        // 前向引用委托：页面控件全部声明后再赋值，避免局部函数前向引用。
        Action? updateSolarState = null;
        Action? updateLocation = null;

        // ---- 总开关 ----
        var solarToggle = new ToggleSwitch
        {
            Checked = inst?.GetSolarAdjustEnabled() ?? false
        };
        solarToggle.ApplyDpiScale(_dpiScale);
        solarToggle.CheckedChanged += (_, _) =>
        {
            // 诊断（只加日志，不改行为）：该 Checked 只可能被 OnClick（鼠标）或
            // OnKeyDown(Space/Enter) 翻转 —— 把触发链写进日志，区分
            // "用户点的" / "键盘触发的" / "程序翻转的"。
            string trig = "?";
            try
            {
            	var st = new StackTrace(1, false);
            	var sb = new System.Text.StringBuilder();
            	int n = Math.Min(5, st.FrameCount);
            	for (int i = 0; i < n; i++)
            	{
            		var m = st.GetFrame(i)?.GetMethod();
            		if (m == null) continue;
            		sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
            		if (i < n - 1) sb.Append(" <- ");
            	}
            	trig = sb.ToString();
            }
            catch { }
            OpLog.Log($"[solartoggle] CheckedChanged -> Checked={solarToggle.Checked} " +
                      $"mouse={Control.MouseButtons} focused={solarToggle.Focused} trigger={trig}");
            inst?.SetSolarAdjustEnabled(solarToggle.Checked);
            updateSolarState?.Invoke();
        };
        var solarGroup = BuildToggleGroup(solarToggle);
        var solarRow = BuildSettingRow(Localization.Get("SolarAdjustEnabled"), solarGroup);


        // ---- 模式下拉：手动 / 物理位置 ----
        var modeCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(90 * _dpiScale)
        };
        modeCombo.ApplyTheme(InputBg, TextMain);
        modeCombo.SetParentBackground(BgInner);
        modeCombo.Items.Add(Localization.Get("SolarModeManual"));
        modeCombo.Items.Add(Localization.Get("SolarModeLocation"));
        modeCombo.SelectedIndex = (inst?.GetSolarManualMode() ?? true) ? 0 : 1;
        modeCombo.SelectedIndexChanged += (_, _) =>
        {
            bool manual = modeCombo.SelectedIndex == 0;
            inst?.SetSolarManualMode(manual);
            updateSolarState?.Invoke();
        };
        var modeRow = BuildSettingRow(Localization.Get("SolarMode"), modeCombo);

        // ---- 手动日出/日落时间输入 ----
        // 两列 HH:mm 各两个数字框 + : 分隔 + 右侧确认按钮。
        // 输入框只接受数字，最长2位；焦点失活（不含确认按钮按下）放弃编辑。
        var sunrisePanel = BuildTimeInputBox(inst?.GetManualSunriseMinutes() ?? 480,
            () => inst?.GetManualSunriseMinutes() ?? 480, v => inst?.SetManualSunriseMinutes(v),
            "Sol_Sunrise");
        var sunsetPanel = BuildTimeInputBox(inst?.GetManualSunsetMinutes() ?? 1080,
            () => inst?.GetManualSunsetMinutes() ?? 1080, v => inst?.SetManualSunsetMinutes(v),
            "Sol_Sunset");
        var sunriseRow = BuildSettingRow(Localization.Get("SolarManualSunrise"), sunrisePanel);
        var sunsetRow = BuildSettingRow(Localization.Get("SolarManualSunset"), sunsetPanel);

        // ---- 物理位置（获取位置按钮 + 坐标显示）----
        var getLocBtn = new RoundedButton
        {
            Text = Localization.Get("SolarGetLocation"),
            Font = UiFont(9f),
            Width = (int)(110 * _dpiScale),
            Height = (int)(28 * _dpiScale),
            TabStop = false
        };
        getLocBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        getLocBtn.SetParentBackground(BgInner);
        getLocBtn.Click += async (_, _) =>
        {
            getLocBtn.Enabled = false;
            try
            {
                var loc = await GeoLocation.GetCurrentAsync();
                inst?.SetSolarLocation(loc.Latitude, loc.Longitude);
                updateLocation?.Invoke();
            }
            catch
            {
                MessageBox.Show(Localization.Get("SolarLocationFailed"),
                    Localization.Get("SolarAdjust"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                getLocBtn.Enabled = true;
            }
            updateLocation?.Invoke();
        };
        var locationLabel = new ThemedLabel
        {
            Text = "",
            Font = UiFont(9f),
            ForeColor = TextSub,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Width = (int)(230 * _dpiScale),
            Height = (int)(20 * _dpiScale)
        };
        updateLocation = () =>
        {
            if (inst?.GetSolarLocationSet() == true)
            {
                locationLabel.Text = string.Format(Localization.Get("SolarLocationGot"),
                    inst.GetSolarLatitude().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                    inst.GetSolarLongitude().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                locationLabel.Text = Localization.Get("SolarLocationHint");
            }
        };
        updateLocation();
        var locationRow = BuildSettingRow(Localization.Get("SolarModeLocation"), locationLabel);
        var getLocRow = BuildSettingRow(Localization.Get("SolarGetLocation"), getLocBtn);

        // ---- 白天色温 / 白天亮度 / 夜晚色温 / 夜晚亮度 滑块 ----
        var minTemp = inst?.GetMinTemperature() ?? GammaController.MIN_TEMPERATURE;
        var maxTemp = inst?.GetMaxTemperature() ?? GammaController.MAX_TEMPERATURE;

        var dayTempSlider = BuildSolarSlider(
            minTemp, maxTemp, 100f, inst?.GetDayTemperature() ?? 6600f,
            v => inst?.SetDayTemperature(v),
            v => string.Format(Localization.Get("SolarTemperatureUnit"), (int)v),
            ThemeManager.IsDark);
        var dayBrightSlider = BuildSolarSlider(
            0f, 1f, 0.01f, inst?.GetDayBrightness() ?? 1.0f,
            v => inst?.SetDayBrightness(v),
            v => string.Format(Localization.Get("SolarBrightnessUnit"), (int)Math.Round(v * 100)),
            ThemeManager.IsDark);
        var nightTempSlider = BuildSolarSlider(
            minTemp, maxTemp, 100f, inst?.GetNightTemperature() ?? 3900f,
            v => inst?.SetNightTemperature(v),
            v => string.Format(Localization.Get("SolarTemperatureUnit"), (int)v),
            ThemeManager.IsDark);
        var nightBrightSlider = BuildSolarSlider(
            0f, 1f, 0.01f, inst?.GetNightBrightness() ?? 0.85f,
            v => inst?.SetNightBrightness(v),
            v => string.Format(Localization.Get("SolarBrightnessUnit"), (int)Math.Round(v * 100)),
            ThemeManager.IsDark);

        var dayTempRow = BuildSettingRow(Localization.Get("SolarDayTemperature"), dayTempSlider);
        var dayBrightRow = BuildSettingRow(Localization.Get("SolarDayBrightness"), dayBrightSlider);
        var nightTempRow = BuildSettingRow(Localization.Get("SolarNightTemperature"), nightTempSlider);
        var nightBrightRow = BuildSettingRow(Localization.Get("SolarNightBrightness"), nightBrightSlider);

        // ---- 过渡时长滑块 ----
        var transitionSlider = BuildSolarSlider(
            0f, 60f, 5f, inst?.GetTransitionMinutes() ?? 0f,
            v => inst?.SetTransitionMinutes((int)v),
            v => string.Format(Localization.Get("SolarTransitionMinutes"), (int)Math.Round(v)),
            ThemeManager.IsDark);
        var transitionRow = BuildSettingRow(Localization.Get("SolarTransition"), transitionSlider);

        // ---- 状态刷新闭包 ----
        bool solarOn() => inst?.GetSolarAdjustEnabled() ?? false;
        bool manualMode() => inst?.GetSolarManualMode() ?? true;
        bool tempEnabled() => inst?.GetColorTemperatureEnabled() ?? false;

        // 2026-09-19（用户 D6）：滑轨被锁定时的**原因提示**。
        // 此前只有"写了没反应"—— 既没说为什么，暂停期滑轨还显示为可用。
        // 现在按 §2「冻结 ≠ 隐身」：**写值入口冻结 ⇒ 滑轨灰化 + 悬停说明原因**，查看值照常显示。
        _solarTip?.Dispose();
        _solarTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };

        updateSolarState = () =>
        {
            bool on = solarOn();
            bool manual = manualMode();
            bool tempOn = tempEnabled();
            MainController? solarCtrl = Program.Instance;
            // 任一暂停源生效（L0 功能停用 / L1 全屏暂停 / L1′ 白名单）⇒ 写值入口冻结。
            // 与 §B0 一致：全屏/白名单的"部分暂停"不算（用各自的 `*Paused()` 全局判据）。
            bool paused = solarCtrl != null &&
                          (solarCtrl.IsDisableActive() || solarCtrl.IsFullscreenPaused() || solarCtrl.IsWhitelistPaused());
            // ⚠️ 2026-09-20：原 `lockPaused` / `lockTempOff`（各一句笼统文案）已弃用，
            //    改为下方按"具体因素"拼接（见 pauseReasons / tempReasons）。
            //    对应 key（SolarLockPaused / SolarLockTempOff）保留在 Localization 里未删。

            modeRow.Enabled = on;
            modeCombo.Enabled = on;
            sunriseRow.Enabled = on && manual;
            sunsetRow.Enabled = on && manual;
            sunrisePanel.Enabled = on && manual;
            sunsetPanel.Enabled = on && manual;
            getLocRow.Enabled = on && !manual;
            locationRow.Enabled = on && !manual;
            // ⚠️ 2026-09-20（用户报「锁定后一点提示也没有，窗口都没有」）：
            //   **只禁用滑轨本身，不再禁用整行**。
            //   原因：`Enabled=false` 的控件**不接收鼠标消息** ⇒ 挂在它上面的 ToolTip
            //   永远不会弹 ⇒ 提示等于不存在。原来禁的是整行（`xxxRow.Enabled`），
            //   连带 Label 一起失效 ⇒ 用户悬停任何位置都看不到原因。
            //   现在只禁滑轨：鼠标落在禁用滑轨上会**穿透到父容器（行）** ⇒
            //   悬停行的任意位置都能看到"为什么被锁" ✓
            dayTempSlider.Enabled = on && tempOn && !paused;
            nightTempSlider.Enabled = on && tempOn && !paused;
            dayBrightSlider.Enabled = on && !paused;
            nightBrightSlider.Enabled = on && !paused;
            transitionRow.Enabled = on;

            // 2026-09-20（用户报）：锁定后**行首文字**不变灰 —— 与滑轨的灰化不一致。
            // 四个选项（白天/夜晚 × 色温/亮度）的标签与滑轨**同步**锁定：
            //   · 色温两行：色温总开关关 或 暂停 ⇒ 灰
            //   · 亮度两行：暂停 ⇒ 灰
            // 过渡时长（transitionRow）不属于这四项，保持原样。
            // ⚠ 传**锁定**语义（与滑轨的 `!paused` 取反），内部用显式 ForeColor 实现。
            SetRowLabelLocked(dayTempRow, !(on && tempOn && !paused));
            SetRowLabelLocked(nightTempRow, !(on && tempOn && !paused));
            SetRowLabelLocked(dayBrightRow, !(on && !paused));
            SetRowLabelLocked(nightBrightRow, !(on && !paused));

            // ------------------------------------------------------------------
            // 2026-09-20（用户需求）：**锁定原因要列全、且尽可能简洁**。
            //   规范示例：白名单 + 功能停用同时生效 ⇒「应用白名单，功能停用生效」。
            // 旧实现把三个暂停源合成一句"调节已暂停"，说不出具体是哪个；
            //   且"暂停 + 色温总开关关闭"同时发生时只显示暂停，**漏掉色温因素**。
            // ⇒ 改为按因素拼接：[因素1，因素2] + 后缀("生效")。
            //   亮度滑轨：只受暂停影响；色温滑轨：暂停 **或** 色温总开关关闭都会锁。
            // ------------------------------------------------------------------
            var pauseReasons = new List<string>();
            if (solarCtrl != null)
            {
                // 顺序固定，保证提示稳定可读
                if (solarCtrl.IsWhitelistPaused()) pauseReasons.Add(Localization.Get("LockReasonWhitelist"));
                if (solarCtrl.IsDisableActive()) pauseReasons.Add(Localization.Get("LockReasonDisable"));
                if (solarCtrl.IsFullscreenPaused()) pauseReasons.Add(Localization.Get("LockReasonFullscreen"));
            }
            string lockSuffix = Localization.Get("LockReasonSuffix");

            string brightHint = pauseReasons.Count > 0
                ? string.Join("，", pauseReasons) + lockSuffix
                : "";

            var tempReasons = new List<string>(pauseReasons);
            if (!tempOn) tempReasons.Add(Localization.Get("LockReasonTempOff"));
            string tempHint = tempReasons.Count > 0
                ? string.Join("，", tempReasons) + lockSuffix
                : "";

            AttachRowHint(_solarTip, dayTempRow, dayTempSlider, tempHint);
            AttachRowHint(_solarTip, nightTempRow, nightTempSlider, tempHint);
            AttachRowHint(_solarTip, dayBrightRow, dayBrightSlider, brightHint);
            AttachRowHint(_solarTip, nightBrightRow, nightBrightSlider, brightHint);

            // 2026-09-20 诊断：用户报"锁定后完全没有提示" ⇒ 先把**判据与拼接结果**打出来，
            // 判定是"本来就没开暂停"还是"判据返回 false"（两者现象一样，必须区分）。
            OpLog.Log($"[solar/hint] on={on} tempOn={tempOn} " +
                      $"wl={solarCtrl?.IsWhitelistPaused()} dis={solarCtrl?.IsDisableActive()} " +
                      $"fs={solarCtrl?.IsFullscreenPaused()} paused={paused} | " +
                      $"bright='{brightHint}' temp='{tempHint}'");
        };

        // Dock layout (reverse z-order: add bottom-most first).
        transitionRow.Dock = DockStyle.Top;
        scroll.Controls.Add(transitionRow);
        nightBrightRow.Dock = DockStyle.Top;
        scroll.Controls.Add(nightBrightRow);
        nightTempRow.Dock = DockStyle.Top;
        scroll.Controls.Add(nightTempRow);
        dayBrightRow.Dock = DockStyle.Top;
        scroll.Controls.Add(dayBrightRow);
        dayTempRow.Dock = DockStyle.Top;
        scroll.Controls.Add(dayTempRow);
        getLocRow.Dock = DockStyle.Top;
        scroll.Controls.Add(getLocRow);
        locationRow.Dock = DockStyle.Top;
        scroll.Controls.Add(locationRow);
        sunsetRow.Dock = DockStyle.Top;
        scroll.Controls.Add(sunsetRow);
        sunriseRow.Dock = DockStyle.Top;
        scroll.Controls.Add(sunriseRow);
        modeRow.Dock = DockStyle.Top;
        scroll.Controls.Add(modeRow);
        solarRow.Dock = DockStyle.Top;
        scroll.Controls.Add(solarRow);

        var title = new Label
        {
            Text = Localization.Get("SolarAdjust"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        // ---- P5：时间调整页稳定 ID（L0）+ 动作注册（L1）----
        // 日出 / 日落两组各 3 个（HourBox / MinuteBox / ConfirmBtn）在
        // BuildTimeInputBox 内按传入前缀自行注册，此处不重复。
        AutomationBridge.Wire(
            (solarToggle, "Sol_EnableToggle"),
            (modeCombo, "Sol_ModeCombo"),
            (dayTempSlider, "Sol_DayTempSlider"),
            (dayBrightSlider, "Sol_DayBrightSlider"),
            (nightTempSlider, "Sol_NightTempSlider"),
            (nightBrightSlider, "Sol_NightBrightSlider"),
            (transitionSlider, "Sol_TransitionSlider"));

        // 「获取物理位置」会发起网络请求，失败时可能弹模态提示框 —— 模态框占住 UI 线程
        // 会让管道 Invoke 一直阻塞。故标为破坏性，需 --allow-destructive 才允许执行。
        AutomationBridge.WireDestructive(
            (getLocBtn, "Sol_GetLocationBtn"));

        // 2026-09-19（D6）：**进入本页时**按当前暂停状态刷新一次 —— 暂停源是动态的
        // （用户可能在别处进了全屏、开了白名单、点了停用），构建期那一次早已过期。
        page.VisibleChanged += (_, _) => { if (page.Visible) updateSolarState?.Invoke(); };

        _refreshSolarState = updateSolarState;
        updateSolarState();
        return page;
    }

    /// <summary>
    /// 手动时间输入：[时][:][分] + 右侧确认按钮。两个数字框各最多 2 位，
    /// 只接受数字；时 0~23、分 0~59；个位数自动补前导 0。
    /// 回车与确认按钮等效；失焦（未按确认）放弃编辑恢复保存值。
    /// </summary>
    private Panel BuildTimeInputBox(int minutes, Func<int> getter, Action<int> setter,
        string automationIdPrefix = "")
    {
        int total = Math.Max(0, minutes);
        var panel = new Panel
        {
            Width = (int)(150 * _dpiScale),
            Height = (int)(26 * _dpiScale),
        };

        var hourBox = new RoundedTextBox
        {
            Width = (int)(30 * _dpiScale),
            Height = (int)(24 * _dpiScale),
            Font = UiFont(9f),
            TextAlign = HorizontalAlignment.Center,
            MaxLength = 2,
            Text = (total / 60).ToString("00")
        };
        hourBox.ApplyTheme(InputBg, TextMain);
        hourBox.SetParentBackground(BgInner);

        var minuteBox = new RoundedTextBox
        {
            Width = (int)(30 * _dpiScale),
            Height = (int)(24 * _dpiScale),
            Font = UiFont(9f),
            TextAlign = HorizontalAlignment.Center,
            MaxLength = 2,
            Text = (total % 60).ToString("00")
        };
        minuteBox.ApplyTheme(InputBg, TextMain);
        minuteBox.SetParentBackground(BgInner);

        var colon = new ThemedLabel
        {
            Text = ":",
            Font = UiFont(10f),
            ForeColor = TextSub,
            AutoSize = false,
            Width = (int)(10 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var confirmBtn = new RoundedButton
        {
            Text = "\u221A",
            Font = UiFont(9f, FontStyle.Bold),
            Width = (int)(24 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TabStop = false
        };
        confirmBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        confirmBtn.SetParentBackground(BgInner);

        // 只接受数字。
        void DigitOnly(RoundedTextBox box) =>
            box.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
        DigitOnly(hourBox);
        DigitOnly(minuteBox);

        // 提交：解析两框；超出范围（时>23、分>59）按非法处理恢复保存值；
        // 合法则补前导 0 并 setter。
        bool commitArmed = false;
        Action commit = () =>
        {
            bool okH = int.TryParse(hourBox.Text, out int h);
            bool okM = int.TryParse(minuteBox.Text, out int m);
            if (!okH || !okM || h > 23 || m > 59)
            {
                // 非法或超范围：恢复为当前保存值（CommitTimeFields 会回写）。
                CommitTimeFields(hourBox, minuteBox, getter, setter, out _);
                return;
            }
            hourBox.Text = h.ToString("00");
            minuteBox.Text = m.ToString("00");
            setter(h * 60 + m);
        };
        hourBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { commit(); e.SuppressKeyPress = true; } };
        minuteBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { commit(); e.SuppressKeyPress = true; } };
        confirmBtn.MouseDown += (_, _) => commitArmed = true;
        confirmBtn.Click += (_, _) => { commitArmed = false; commit(); };
        // 失焦恢复：与色温范围输入框同款 BeginInvoke 延迟判定。
        hourBox.Leave += (_, _) => BeginInvoke((Action)(() => { if (!commitArmed) CommitTimeFields(hourBox, minuteBox, getter, setter, out _); commitArmed = false; }));
        minuteBox.Leave += (_, _) => BeginInvoke((Action)(() => { if (!commitArmed) CommitTimeFields(hourBox, minuteBox, getter, setter, out _); commitArmed = false; }));

        panel.Controls.Add(hourBox);
        panel.Controls.Add(colon);
        panel.Controls.Add(minuteBox);
        panel.Controls.Add(confirmBtn);
        panel.Layout += (_, _) =>
        {
            int gap = (int)(4 * _dpiScale);
            int yBox = (panel.Height - hourBox.Height) / 2;
            int ySmall = (panel.Height - confirmBtn.Height) / 2;
            int x = 0;
            hourBox.Location = new Point(x, yBox); x += hourBox.Width + gap;
            colon.SetBounds(x, yBox, colon.Width, hourBox.Height); x += colon.Width;
            minuteBox.Location = new Point(x, yBox); x += minuteBox.Width + gap * 2;
            confirmBtn.Location = new Point(x, ySmall); x += confirmBtn.Width;
            panel.Width = x;
        };
        // P5：把本组三个控件暴露给自动化接口。ID 前缀由调用方给（日出 / 日落各一组），
        // 故最终 ID 形如 Sol_SunriseHourBox / Sol_SunriseMinuteBox / Sol_SunriseConfirmBtn。
        if (!string.IsNullOrEmpty(automationIdPrefix))
        {
            AutomationBridge.Wire(
                (hourBox, automationIdPrefix + "HourBox"),
                (minuteBox, automationIdPrefix + "MinuteBox"),
                (confirmBtn, automationIdPrefix + "ConfirmBtn"));
        }
        return panel;
    }

    /// <summary>
    /// 从两框文本读取时间；合法则 setter 并回写补零格式，非法则回写保存值。
    /// </summary>
    private void CommitTimeFields(RoundedTextBox hourBox, RoundedTextBox minuteBox,
        Func<int> getter, Action<int> setter, out bool valid)
    {
        if (!int.TryParse(hourBox.Text, out int h) || !int.TryParse(minuteBox.Text, out int m)
            || h < 0 || h > 23 || m < 0 || m > 59)
        {
            valid = false;
            // 非法或超范围：恢复为当前保存值（getter 提供本输入面板对应的时间）。
            int saved = getter();
            hourBox.Text = (saved / 60).ToString("00");
            minuteBox.Text = (saved % 60).ToString("00");
            return;
        }
        valid = true;
        hourBox.Text = h.ToString("00");
        minuteBox.Text = m.ToString("00");
        setter(h * 60 + m);
    }

    private SettingSlider BuildSolarSlider(float min, float max, float step, float initial,
        Action<float> onValue, Func<float, string>? format, bool dark)
    {
        var slider = new SettingSlider
        {
            Width = (int)(240 * _dpiScale),
            Height = (int)(24 * _dpiScale),
            Min = min,
            Max = max,
            Step = step,
            Value = initial,
            ForeColor = TextSub,
            Font = UiFont(11f),
            Format = format
        };
        // 滑轨主题：浅色=蓝色填充，深色=白色填充（与弹窗滑轨一致）；
        // 圆形按钮（拇指）：浅色=浅灰，深色=中灰。
        slider.ApplyTheme(
            Dark ? Color.FromArgb(56, 56, 60) : Color.FromArgb(214, 214, 218),
            Dark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(200, 200, 205),
            Dark ? Color.FromArgb(200, 200, 205) : Color.FromArgb(178, 178, 184),
            Dark ? Color.White : Accent);
        // 拖动实时应用（预览），松手保存（setter 内部已 Save）。
        slider.ValueChanged += v => onValue(v);
        _disableLocked.Add((slider, () => true));
        return slider;
    }

    /// <summary>透明度滑轨（40–100%，整数步进，右侧显示百分比）。拖动实时应用并保存。</summary>
    private SettingSlider BuildOpacitySlider(int initial, Action<float> onValue)
    {
        bool dark = ThemeManager.IsDark;
        var slider = new SettingSlider
        {
            Width = (int)(240 * _dpiScale),
            Height = (int)(24 * _dpiScale),
            Min = 40,
            Max = 100,
            Step = 1,
            Value = initial,
            ForeColor = TextSub,
            Font = UiFont(11f),
            Format = v => $"{(int)Math.Round(v)}%"
        };
        slider.ApplyTheme(
            dark ? Color.FromArgb(56, 56, 60) : Color.FromArgb(214, 214, 218),
            dark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(200, 200, 205),
            dark ? Color.FromArgb(200, 200, 205) : Color.FromArgb(178, 178, 184),
            dark ? Color.White : Accent);
        slider.ValueChanged += v => onValue(v);
        return slider;
    }

    /// <summary>
    /// 禁用模式锁定：滑轨与亮度/色温挡位在禁用期间不可操作；
    /// 解除后按各自原有逻辑恢复（如色温预设跟随色温总开关）。
    /// 状态变化时才动作（幂等），由 _disableUiTimer 每秒轮询。
    /// </summary>
    private void UpdateDisableLock()
    {
        // 2026-09-16：冻结罩判据统一为 IsUiPaused()（三暂停源任一）。
        // 此前只认 IsDisableActive()（仅 L0 功能停用），于是 **全屏暂停 / 白名单暂停**
        // 期间，色温预设、Solar 滑轨、亮度挡位这三个写值入口仍可点动 ——
        // 控件外观会变，但值被 gamma 的 _paused 吞掉，属"写值入口未冻结"，
        // 与用户定下的「冻结 ≠ 隐身」原则不符（该原则要求写值入口一律冻结）。
        // _disableLocked 内不含"功能停用"下拉，故不会锁死解除停用的入口。
        bool locked = Program.Instance?.IsUiPaused() ?? false;
        if (locked != _disableLockActive)
        {
            _disableLockActive = locked;
            foreach (var (ctrl, restore) in _disableLocked)
                ctrl.Enabled = locked ? false : restore();
            // 2026-09-20：暂停状态变化 ⇒ 立刻刷新时间调整页的**锁定观感**
            // （滑轨与行首文字的灰化 + 悬停原因提示）。
            // 此前该页只在"构建 / 切到本页 / 总开关切换 / 模式切换"时刷新 ⇒
            // 用户在**本页停留期间**因托盘菜单停用、白名单命中、全屏进入而进入锁定时，
            // 文字与滑轨保持未锁定的样子（用户 2026-09-20 实测报告）。
            // ⚠ 只在**变化时**触发（不做无条件每秒刷新）：`updateSolarState` 内含
            //   诊断日志，无条件调用会把 ops 日志刷爆。
            // ⚠ 必须放在上面的 foreach **之后** —— `restore()` 一律返回 true，
            //   会把"色温总开关关闭"时本该禁用的色温滑轨强行启用，由本调用纠正回来。
            _refreshSolarState?.Invoke();
        }
        // 轮询兜底：即使事件链路未触发（如时间调整自动变化），
        // 每 1 秒也主动读取当前亮度/色温刷新挡位下拉显示。
        var inst = Program.Instance;
        if (inst == null) return;
        int pct = (int)Math.Round(inst.GetCurrentBrightness() * 100);
        if (pct != _lastSyncBrightnessPct)
        {
            _lastSyncBrightnessPct = pct;
            _refreshLevelSelection?.Invoke();
            _refreshLevelDisplay?.Invoke();
        }
        int k = (int)Math.Round(inst.GetCurrentTemperature());
        if (k != _lastSyncTemperatureK)
        {
            _lastSyncTemperatureK = k;
            _refreshPresetSelection?.Invoke();
            _refreshPresetDisplay?.Invoke();
        }
        // 禁用下拉同步：轮询保证到期/解除后下拉与状态标签实时刷新。
        RefreshDisableCombo();
    }

    /// <summary>
    /// 同步禁用下拉的选中项与状态标签（与右键菜单禁用逻辑一致）。
    /// 当前激活项：关闭(0)/永久(1)/临时时长(2-10)/日出日落(11)。
    /// </summary>
    private void RefreshDisableCombo()
    {
        if (_disableCombo == null) return;
        var inst = Program.Instance;
        if (inst == null) return;
        var until = inst.GetDisableUntil();
        bool active = until != null && until.Value > DateTime.Now;
        bool isSolar = active && inst.IsSolarDisableActive();
        int idx = 0; // 关闭
        if (active)
        {
            var untilVal = until.GetValueOrDefault();
            if (untilVal == DateTime.MaxValue) idx = 1; // 永久
            else if (isSolar) idx = 11;                // 日出/日落
            else
            {
                var rem = inst.GetDisableRemaining();
                if (rem != null && rem.Value > TimeSpan.Zero)
                {
                    double minutes = rem.Value.TotalMinutes;
                    if (minutes <= 1.5) idx = 2;
                    else if (minutes <= 7.5) idx = 3;
                    else if (minutes <= 22.5) idx = 4;
                    else if (minutes <= 45) idx = 5;
                    else if (minutes <= 90) idx = 6;
                    else if (minutes <= 4 * 60) idx = 7;
                    else if (minutes <= 9 * 60) idx = 8;
                    else if (minutes <= 18 * 60) idx = 9;
                    else idx = 10;
                }
                else idx = 10;
            }
        }
        // 第 12 项文本按昼夜切换：白天"到日落"，夜晚"到日出"。
        if (_disableCombo.Items.Count > 11)
        {
            string solarText = Localization.Get(inst.IsDaytimeNow() ? "DisableUntilSunset" : "DisableUntilSunrise");
            if (_disableCombo.Items[11] != solarText)
            {
                _disableCombo.Items[11] = solarText;
                _disableCombo.Invalidate(); // List 直接改元素不触发重绘，手动刷新
            }
        }
        _syncingDisable = true;
        if (_disableCombo.SelectedIndex != idx) _disableCombo.SelectedIndex = idx;
        _syncingDisable = false;

        // 状态标签：激活时显示剩余时间；永久显示"永久"；否则空。
        // 下拉收起文本：停用时显示状态/倒计时（DisplayText 覆盖选中项，展开仍显示原项）。
        if (active)
        {
            var untilVal2 = until.GetValueOrDefault();
            if (untilVal2 == DateTime.MaxValue)
                _disableCombo.DisplayText = Localization.Get("DisablePermanent");
            else if (isSolar)
                _disableCombo.DisplayText = Localization.Get(inst.IsDaytimeNow() ? "DisableUntilSunset" : "DisableUntilSunrise");
            else if (inst.GetDisableRemaining() is TimeSpan rem2 && rem2 > TimeSpan.Zero)
            {
                // 直接显示 HH:MM:SS 倒计时，省去翻译。
                string countdown = string.Format("{0:D2}:{1:D2}:{2:D2}",
                    (int)rem2.TotalHours, rem2.Minutes, rem2.Seconds);
                _disableCombo.DisplayText = string.Format(Localization.Get("DisableActiveStatus"), countdown);
            }
            else _disableCombo.DisplayText = null;
        }
        else _disableCombo.DisplayText = null;
    }


    /// <summary>
    /// P1：托盘常驻写入阶梯出结果后更新开关提示。
    /// 成功 → 清掉提示；阶梯耗尽仍未成功 → 显示 TrayVisibilityNotReady（非模态、不打断）。
    /// 绝不能在拨动开关的瞬间同步读注册表 —— 那时阶梯首拍还没跑，必然误报（已实测踩到）。
    /// </summary>
    private void OnTrayVisLadderFinished(bool success)
    {
        if (IsDisposed || !Visible) return;
        ToggleSwitch? t = _trayVisibleToggle;
        if (t == null || _trayVisTip == null) return;
        if (!t.Checked) { _trayVisTip.SetToolTip(t, ""); return; }
        _trayVisTip.SetToolTip(t, success ? "" : Localization.Get("TrayVisibilityNotReady"));
    }

    private Panel BuildBrightnessPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        // Scrollable container (same pattern as the other pages).
        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);

        // ---- 滚轮步进 (wheel step size) ----
        // Presets: 2/5/10/15/20/30/50/75/100%. Default remains 5%.
        var stepCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        stepCombo.ApplyTheme(InputBg, TextMain);
        stepCombo.SetParentBackground(BgInner); // rounded corners blend into the card
        // Index 0..8 map to 2/5/10/15/20/30/50/75/100%.
        int[] stepPresets = { 2, 5, 10, 15, 20, 30, 50, 75, 100 };
        foreach (int p in stepPresets)
            stepCombo.Items.Add($"{p}%");
        float savedStep = Program.Instance?.GetStepSize() ?? GammaController.DEFAULT_STEP;
        int savedPercent = Math.Max(1, Math.Min(100, (int)Math.Round(savedStep * 100)));
        // Select the nearest preset (default 5% when unset).
        stepCombo.SelectedIndex = Array.FindIndex(stepPresets, p => p >= savedPercent);
        if (stepCombo.SelectedIndex < 0) stepCombo.SelectedIndex = stepPresets.Length - 1;
        stepCombo.SelectedIndexChanged += (_, _) =>
        {
            int idx = stepCombo.SelectedIndex;
            if (idx >= 0 && idx < stepPresets.Length)
                Program.Instance?.SetStepSize(stepPresets[idx] / 100f);
        };
        var stepRow = BuildSettingRow(Localization.Get("StepSize"), stepCombo);

        // ---- 亮度挡位 (fixed brightness levels, dropdown) ----
        // 100/75/50/25/10%, each applies immediately with OSD feedback.
        // Same ThemedComboBox pattern as the other rows so the rounded
        // corners blend with the card in both light and dark themes.
        var levelCombo = new ThemedComboBox
        {
            Font = UiFont(10f),
            Width = (int)(180 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            DropDownHeight = (int)(120 * _dpiScale)
        };
        levelCombo.ApplyTheme(InputBg, TextMain);
        levelCombo.SetParentBackground(BgInner); // rounded corners blend into the card
        float[] levelValues = { 1.0f, 0.75f, 0.5f, 0.25f, 0.1f };
        foreach (float lv in levelValues)
            levelCombo.Items.Add($"{(int)Math.Round(lv * 100)}%");
        levelCombo.SelectedIndex = 0; // 100%
        _disableLocked.Add((levelCombo, () => true));
        // 程序化同步选中时抑制 SelectedIndexChanged，避免把下拉的跟随动作
        // 误当作一次"用户选择"去改变亮度。
        bool syncingLevel = false;
        Action refreshLevelSelection = () =>
        {
            float current = Program.Instance?.GetCurrentBrightness() ?? 1.0f;
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < levelValues.Length; i++)
            {
                float d = Math.Abs(current - levelValues[i]);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            syncingLevel = true;
            try { levelCombo.SelectedIndex = best; }
            finally { syncingLevel = false; }
        };
        // 收起时显示实时亮度值（如 95%、74%），展开列表不变仍为挡位项。
        Action refreshLevelDisplay = () =>
        {
            int pct = (int)Math.Round((Program.Instance?.GetCurrentBrightness() ?? 1.0f) * 100);
            levelCombo.DisplayText = $"{pct}%";
        };
        levelCombo.SelectedIndexChanged += (_, _) =>
        {
            if (syncingLevel) return;
            int idx = levelCombo.SelectedIndex;
            if (idx >= 0 && idx < levelValues.Length)
                Program.Instance?.SetBrightnessLevel(levelValues[idx]);
        };
        // 不再在此处订阅 Program.Instance.BrightnessChanged：构建期订阅在
        // RebuildUi 重建时只 += 永不 -=（泄漏 + 旧闭包引用已 Dispose 控件）。
        // 改由构造器 OnProgramBrightnessChanged 单次挂载 + _refreshLevel* 字段转发。
        refreshLevelSelection();
        refreshLevelDisplay();
        _refreshLevelSelection = refreshLevelSelection;
        _refreshLevelDisplay = refreshLevelDisplay;
        var levelsRow = BuildSettingRow(Localization.Get("BrightnessLevels"), levelCombo);

        // Dock layout runs in reverse z-order: last added docks first (top).
        // Add bottom-most first, top-most last.
        stepRow.Dock = DockStyle.Top;
        scroll.Controls.Add(stepRow);
        levelsRow.Dock = DockStyle.Top;
        scroll.Controls.Add(levelsRow);

        // ---- 亮度平滑 (startup / schedule smooth transition) ----
        var brightSmoothToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetBrightnessSmooth() ?? true
        };
        brightSmoothToggle.ApplyDpiScale(_dpiScale);
        brightSmoothToggle.CheckedChanged += (_, _) => Program.Instance?.SetBrightnessSmooth(brightSmoothToggle.Checked);
        var brightSmoothGroup = BuildToggleGroup(brightSmoothToggle);
        var brightSmoothRow = BuildSettingRow(Localization.Get("BrightnessSmooth"), brightSmoothGroup);
        brightSmoothRow.Dock = DockStyle.Top;
        scroll.Controls.Add(brightSmoothRow);

        var title = new Label
        {
            Text = Localization.Get("SettingsBrightness"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        // ---- P5：亮度页稳定 ID（L0）+ 动作注册（L1）----
        AutomationBridge.Wire(
            (stepCombo, "Bri_WheelStepCombo"),
            (levelCombo, "Bri_LevelCombo"),
            (brightSmoothToggle, "Bri_SmoothToggle"));

        return page;
    }


    private Panel BuildHotkeysPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        // Scrollable container (same pattern as the general page) so the
        // rows stay reachable at high DPI / small windows.
        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);

        // ---- 增加亮度 hotkey row ----
        var incCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyIncreaseBrightness"),
            Program.Instance?.GetIncreaseBrightnessHotKey() ?? "",
            v => Program.Instance?.SetIncreaseBrightnessHotKey(v) == true,
            Program.Instance?.GetIncreaseBrightnessHotKeyEnabled() ?? true,
            v => Program.Instance?.SetIncreaseBrightnessHotKeyEnabled(v),
            actionId: "Hk_IncreaseBrightness");
        incCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incCapture);

        // ---- 降低亮度 hotkey row ----
        var decCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseBrightness"),
            Program.Instance?.GetDecreaseBrightnessHotKey() ?? "",
            v => Program.Instance?.SetDecreaseBrightnessHotKey(v) == true,
            Program.Instance?.GetDecreaseBrightnessHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseBrightnessHotKeyEnabled(v),
            actionId: "Hk_DecreaseBrightness");
        decCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(decCapture);

        // ---- 熄屏 hotkey row ----
        var powerOffCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyPowerOff"),
            Program.Instance?.GetPowerOffHotKey() ?? "",
            v => Program.Instance?.SetPowerOffHotKey(v) == true,
            Program.Instance?.GetPowerOffHotKeyEnabled() ?? true,
            v => Program.Instance?.SetPowerOffHotKeyEnabled(v),
            actionId: "Hk_PowerOff");
        powerOffCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(powerOffCapture);

        // ---- 增加色温 hotkey row ----
        var incTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyIncreaseTemperature"),
            Program.Instance?.GetIncreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetIncreaseTemperatureHotKey(v) == true,
            Program.Instance?.GetIncreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetIncreaseTemperatureHotKeyEnabled(v),
            true,
            actionId: "Hk_IncreaseTemperature");
        incTempCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incTempCapture);

        // ---- 降低色温 hotkey row ----
        var decTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseTemperature"),
            Program.Instance?.GetDecreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetDecreaseTemperatureHotKey(v) == true,
            Program.Instance?.GetDecreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseTemperatureHotKeyEnabled(v),
            true,
            actionId: "Hk_DecreaseTemperature");
        decTempCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(decTempCapture);

        // ---- 一键清除 (clear all hotkeys) ----
        var clearBtn = new RoundedButton
        {
            Text = Localization.Get("ClearAllHotkeys"),
            Font = UiFont(9f),
            Width = (int)(110 * _dpiScale),
            Height = (int)(28 * _dpiScale),
            TabStop = false
        };
        clearBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        clearBtn.SetParentBackground(BgInner);
        clearBtn.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                Localization.Get("ClearAllHotkeysConfirm"),
                Localization.Get("ClearAllHotkeys"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            Program.Instance?.ClearAllHotkeys();
            MessageBox.Show(
                Localization.Get("ClearAllHotkeysDone"),
                Localization.Get("ClearAllHotkeys"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            // Rebuild the page so every capture box shows the placeholder.
            RebuildUi();
        };
        // Added BEFORE the title: Dock=Top stacks in reverse z-order, so the
        // title (added last) sits at the very top, this row right below it.
        var clearRow = BuildSettingRow(Localization.Get("ClearAllHotkeys"), clearBtn);
        clearRow.Dock = DockStyle.Top;
        scroll.Controls.Add(clearRow);

        // ---- 全部快捷键总开关 (master switch: enable/disable ALL hotkeys) ----
        var allHotKeysToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetAllHotKeysEnabled() ?? true
        };
        allHotKeysToggle.ApplyDpiScale(_dpiScale);
        _allHotKeysToggle = allHotKeysToggle;
        allHotKeysToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetAllHotKeysEnabled(allHotKeysToggle.Checked);
        var allHotKeysGroup = BuildToggleGroup(allHotKeysToggle);
        var allHotKeysRow = BuildSettingRow(Localization.Get("AllHotKeysEnabled"), allHotKeysGroup);
        allHotKeysRow.Dock = DockStyle.Top;
        scroll.Controls.Add(allHotKeysRow);

        // Master switch linkage: when ALL hotkeys are disabled, every per-
        // hotkey toggle must read as OFF and be locked (grayed) so the user
        // cannot be fooled into thinking a sub-switch still works. When the
        // master is re-enabled, non-temp rows return to ON (absolute master
        // control) and temp rows return to their saved state (dual-gated).
        bool masterOn = allHotKeysToggle.Checked;
        var hotKeyRows = new[]
        {
            (Row: incCapture, Getter: (Func<bool>)(() => Program.Instance?.GetIncreaseBrightnessHotKeyEnabled() ?? true), IsTemp: false),
            (Row: decCapture, Getter: (Func<bool>)(() => Program.Instance?.GetDecreaseBrightnessHotKeyEnabled() ?? true), IsTemp: false),
            (Row: powerOffCapture, Getter: (Func<bool>)(() => Program.Instance?.GetPowerOffHotKeyEnabled() ?? true), IsTemp: false),
            (Row: incTempCapture, Getter: (Func<bool>)(() => Program.Instance?.GetIncreaseTemperatureHotKeyEnabled() ?? true), IsTemp: true),
            (Row: decTempCapture, Getter: (Func<bool>)(() => Program.Instance?.GetDecreaseTemperatureHotKeyEnabled() ?? true), IsTemp: true)
        };
        _hotKeyToggleRows.Clear();
        foreach (var (row, getter, isTemp) in hotKeyRows)
        {
            var toggle = FindHotKeyToggle(row);
            if (toggle == null) continue;
            _hotKeyToggleRows.Add((row, toggle, isTemp, getter));
            // 初始同步由 SyncHotKeySubToggles 统一处理（构建末尾调用）。
            // 子开关自身 CheckedChanged（CreateHotKeyCaptureRow 内）负责持久化。
        }

        // 主开关翻转：统一走 SyncHotKeySubToggles（同步所有子开关 UI）。
        allHotKeysToggle.CheckedChanged += (_, _) => SyncHotKeySubToggles();
        // The clear-all row is also disabled (grayed) while the master
        // switch is off.
        allHotKeysToggle.CheckedChanged += (_, _) => clearRow.Enabled = allHotKeysToggle.Checked;
        clearRow.Enabled = masterOn;

        // 初始同步：根据主开关 + 色温总开关状态统一设置所有子开关 UI。
        SyncHotKeySubToggles();

        var title = new Label
        {
            Text = Localization.Get("SettingsHotkeys"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        // ---- P5：快捷键页稳定 ID（L0）+ 动作注册（L1）----
        // 5 组 × 4 个（Capture / Toggle / ConfirmBtn / CancelBtn）在
        // CreateHotKeyCaptureRow 内按传入的 actionId 自行注册，此处不重复。
        // Hk_ClearBtn 会弹确认对话框 → 标为破坏性。
        AutomationBridge.Wire(
            (allHotKeysToggle, "Hk_AllToggle"));
        AutomationBridge.WireDestructive(
            (clearBtn, "Hk_ClearBtn"));

        return page;
    }

    /// <summary>
    /// Finds the mini enable/disable ToggleSwitch inside a hotkey row
    /// (the only ToggleSwitch the row contains). The row is a
    /// RoundedCardPanel whose Inner panel holds the actual children, so
    /// we look inside the first child panel. Returns null if absent.
    /// </summary>
    private static ToggleSwitch? FindHotKeyToggle(Panel row)
    {
        foreach (Control c in row.Controls)
        {
            if (c is Panel inner)
            {
                foreach (Control cc in inner.Controls)
                {
                    if (cc is ToggleSwitch ts) return ts;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 统一同步快捷键页所有子开关的 UI 状态。调用时机：
    /// 1) 主开关翻转（BuildHotkeysPage 内订阅）；
    /// 2) 色温总开关翻转（BuildColorTempPage 内订阅，跨页立即生效）；
    /// 3) 快捷键页构建完成后的初始同步。
    /// 规则（用户逻辑，2026-08-20 确认）：
    /// - 非色温子开关：主开关【绝对控制】——主开关开 → 强制开；关 → 强制关；
    ///   主开关开启期间用户可自我关闭（点一下关），但主开关重新开关后拉回开启；
    /// - 色温子开关：受主开关与色温总开关双重约束（生效 = 持久值 && 主开关 &&
    ///   色温总开关，三者"与"）——任一关闭 → 强制关 + 真正锁定；两者都开 →
    ///   解锁并恢复【上锁前状态】（完全自由自我开关）；
    /// - 主开关/色温总开关切换【不修改】子开关持久值（恢复"上锁前状态"的前提）。
    /// 子开关 Checked 变化会触发各自 CheckedChanged → setEnabled 持久化，
    /// 此处程序化设置时由处理器内的守卫（主开关关/色温关分支 return）避免
    /// 污染持久值；解锁分支恢复状态正是期望行为。
    /// </summary>
    private void SyncHotKeySubToggles()
    {
        bool masterOn = Program.Instance?.GetAllHotKeysEnabled() ?? true;
        bool tempEnabled = Program.Instance?.GetColorTemperatureEnabled() ?? false;
        foreach (var (row, toggle, isTemp, getter) in _hotKeyToggleRows)
        {
            // 色温行受色温总开关约束；非色温行不受影响。
            bool locked = !masterOn || (isTemp && !tempEnabled);
            if (locked)
            {
                // 锁定分支：强制关 + 真正禁用（主开关关，或色温行且色温关）。
                toggle.Checked = false;
                toggle.Enabled = false;
                row.Enabled = false;
                continue;
            }
            // 解锁分支（主开关开）：
            // - 非色温行：主开关绝对控制 → 强制开；
            // - 色温行（色温总开关开）：恢复上锁前状态（getter 持久值），自由开关。
            toggle.Enabled = true;
            toggle.Checked = isTemp ? getter() : true;
            row.Enabled = true;
        }
    }

    /// <summary>
    /// 3.6.0 显示器页：独立控制总开关与受控显示器子开关联动。
    /// 总开关关 → 子开关强制关 + 禁用（持久值不变，停用屏冻结由 GammaController 保证）；
    /// 总开关开 → 恢复各子开关持久值并解锁。
    /// 程序化设置 Checked 会触发 CheckedChanged，需确保不会写回持久值——
    /// SetDisplayEnabled 以 toggle.Checked 为准写入，故同步前先置位 _syncingMonitorSub 守卫。
    /// </summary>
    private bool _syncingMonitorSub;
    private void SyncMonitorSubToggles()
    {
        // ⚠ 2026-09-19 修正（用户指出「单屏时独立控制开不了，受控显示器却能随意开关，UI 没同步」）：
        //   我曾在此处加过 "单屏时不锁受控开关"（本意是避免"用户无法停用唯一那块屏"的功能倒退），
        //   但这与总开关的锁定态**自相矛盾**：总开关显示为关、下面的每屏开关却仍可拨动
        //   ⇒ 状态不一致。用户明确要求同步 ⇒ **去掉该例外**，恢复"总开关关 ⇒ 子开关锁"的一致性。
        //   （代价：单屏时不能停用该屏 —— 与"独立控制关闭即统一控制"的语义一致，可接受。）
        bool masterOn = _perMonitorToggle?.Checked ?? false;
        _syncingMonitorSub = true;
        try
        {
            foreach (var (toggle, getter) in _monitorSubToggles)
            {
                if (!masterOn)
                {
                    // 总开关关：强制关 + 禁用（持久值保留，恢复时用 getter 读回）
                    toggle.Checked = false;
                    toggle.Enabled = false;
                }
                else
                {
                    // 总开关开：恢复持久值并解锁
                    toggle.Enabled = true;
                    toggle.Checked = getter();
                }
            }
        }
        finally
        {
            _syncingMonitorSub = false;
        }
    }

    /// <summary>
    /// Builds a single hotkey setting row: label text on the left, a capture
    /// box in the middle, a small enable/disable toggle, and confirm (√) /
    /// cancel (×) buttons on the right. Clicking the box starts recording;
    /// while recording, pressing a combo shows "Ctrl + Shift + Up" style
    /// text; the confirm button commits it to the controller (and re-registers
    /// the global hotkey), the cancel button reverts to the previously saved
    /// value while recording and deletes (unbinds) the hotkey when not
    /// recording. The toggle controls whether the hotkey is registered at all
    /// without unbinding it.
    /// </summary>
    private Panel CreateHotKeyCaptureRow(string labelText, string currentValue, Func<string,bool> commit,
        bool enabled, Action<bool>? setEnabled, bool isTempHotkey = false, string actionId = "")
    {
        // Rounded card frame (same style as the general-page rows).
        var row = new RoundedCardPanel
        {
            Height = (int)(48 * _dpiScale),
            Margin = new Padding(0, (int)(10 * _dpiScale), 0, 0)
        };
        row.ApplyTheme(Bg, BgInner, Border);

        var inner = row.Inner;

        var label = new ThemedLabel
        {
            Text = labelText,
            Font = UiFont(10f),
            AutoSize = false,
            Height = (int)(24 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };

        var capture = new HotKeyCaptureBox
        {
            Width = (int)(160 * _dpiScale),
            Height = (int)(26 * _dpiScale)
        };
        capture.ApplyTheme(InputBg, TextMain);
        capture.SetParentBackground(BgInner); // rounded corners blend into the card inner panel
        capture.SetPlaceholder(Localization.Get("HotkeyInputPlaceholder"));
        capture.HotKey = currentValue;
        // While recording, suspend ALL hotkeys so the user can type a new
        // combo without triggering any existing binding. Resume on commit
        // / cancel (CaptureStateChanged fires on both transitions).
        capture.CaptureStateChanged += (_, _) =>
        {
            if (capture.IsCapturing)
                Program.Instance?.SuspendAllHotKeys();
            else
                Program.Instance?.ResumeAllHotKeys();
        };
        inner.Controls.Add(label);
        inner.Controls.Add(capture);

        // Enable/disable toggle: controls whether the hotkey is registered
        // (active) without unbinding it. Mini size to save horizontal space.
        ToggleSwitch? toggle = null;
        if (setEnabled != null)
        {
            toggle = new ToggleSwitch
            {
                Checked = enabled,
                Width = (int)(24 * _dpiScale),
                Height = (int)(14 * _dpiScale)
            };
            // When the master switch is OFF the sub-toggle is disabled and
            // forced off; never let a click persist an "on" while the master
            // is off (defensive, the control is disabled anyway).
            toggle.CheckedChanged += (_, _) =>
            {
                if (Program.Instance?.GetAllHotKeysEnabled() == false)
                {
                    toggle.Checked = false;
                    row.Enabled = false;
                    return;
                }
                // 色温快捷键行：色温总开关关闭时强制锁定为关，优先级高于
                // "启用全部快捷键"总开关。
                if (isTempHotkey && !(Program.Instance?.GetColorTemperatureEnabled() ?? false))
                {
                    toggle.Checked = false;
                    row.Enabled = false;
                    return;
                }
                setEnabled(toggle.Checked);
                // 子开关关闭只灰化行内操作控件，不禁用整行（否则 toggle 自身
                // 被父链禁用，再也无法重新打开）。
                UpdateRowEnabled();
                row.Enabled = true;
            };
            inner.Controls.Add(toggle);
        }

        // Confirm (√) button: commits the recorded combo.
        var confirmBtn = new RoundedButton
        {
            Text = "\u221A",
            Font = UiFont(9f, FontStyle.Bold),
            Width = (int)(24 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TabStop = false
        };
        confirmBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        confirmBtn.SetParentBackground(BgInner); // corners blend into the card inner panel
        // Focus leaves the capture box before the button's Click fires. Set
        // AutoCancelSuppressed from MouseDown (fires before focus transfer)
        // and GotFocus (backup) so the box's Leave handler does not auto-
        // cancel the recording, which would lose the recorded combo.
        confirmBtn.MouseDown += (_, _) => capture.AutoCancelSuppressed = true;
        confirmBtn.GotFocus += (_, _) => capture.AutoCancelSuppressed = true;
        confirmBtn.Click += (_, _) =>
        {
            string value = capture.CapturedHotKey;
            string previous = capture.SavedValue;
            if (capture.IsCleared)
            {
                value = ""; // cleared = unbind
            }
            else if (string.IsNullOrEmpty(value))
            {
                value = capture.SavedValue; // nothing recorded: keep the old binding
            }
            capture.CommitCapture(value);
            if (!commit(value))
            {
                MessageBox.Show(
                    Localization.Get("HotkeyConflict"),
                    Localization.Get("Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                // Roll back the box to the previous binding (the
                // controller already restored it and re-registered).
                capture.HotKey = previous;
            }
        };
        inner.Controls.Add(confirmBtn);

        // Cancel (×) button: while recording it reverts to the saved value;
        // when not recording it deletes (unbinds) the hotkey. This gives the
        // × button a second role: remove the binding without needing to
        // clear the capture box first.
        var cancelBtn = new RoundedButton
        {
            Text = "\u00D7",
            Font = UiFont(9f, FontStyle.Bold),
            Width = (int)(24 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TabStop = false
        };
        cancelBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        cancelBtn.SetParentBackground(BgInner); // corners blend into the card inner panel
        // Focus leaves the capture box before the button's Click fires. Set
        // AutoCancelSuppressed from MouseDown (fires before focus transfer)
        // and GotFocus (backup) so the box's Leave handler does not auto-
        // cancel the recording, which would make IsCapturing false and turn
        // this click into an accidental unbind.
        cancelBtn.MouseDown += (_, _) => capture.AutoCancelSuppressed = true;
        cancelBtn.GotFocus += (_, _) => capture.AutoCancelSuppressed = true;
        cancelBtn.Click += (_, _) =>
        {
            if (capture.IsCapturing)
            {
                // Recording: cancel the recording, keep the old binding.
                capture.CancelCapture();
            }
            else
            {
                // Not recording: delete (unbind) the hotkey.
                capture.CommitCapture("");
                commit("");
            }
        };
        inner.Controls.Add(cancelBtn);

        // Layout: label left, capture box center-left, toggle + buttons right.
        inner.Layout += (_, _) =>
        {
            int gap = (int)(10 * _dpiScale);

            // Fixed-width label so the capture boxes align across all rows
            // regardless of the label text length (and across languages).
            int labelW = (int)(110 * _dpiScale);
            int labelMaxH = inner.Height - (int)(8 * _dpiScale);
            float labelSize = FitLabelFont(label.Text, label.Font, labelW, labelMaxH);
            if (Math.Abs(labelSize - label.Font.Size) > 0.01f)
            {
                var oldLabelFont = label.Font;
                label.Font = new Font(oldLabelFont.FontFamily, labelSize, oldLabelFont.Style, GraphicsUnit.Pixel);
                oldLabelFont.Dispose();
            }
            int labelH = Math.Min(
                TextRenderer.MeasureText(label.Text, label.Font, new Size(labelW, int.MaxValue),
                    TextFormatFlags.WordBreak).Height,
                labelMaxH);
            label.SetBounds(14, (inner.Height - labelH) / 2, labelW, labelH);
            int btnW = confirmBtn.Width;
            int rightEdge = inner.Width - 14;
            cancelBtn.Location = new Point(rightEdge - btnW, (inner.Height - cancelBtn.Height) / 2);
            confirmBtn.Location = new Point(rightEdge - btnW * 2 - gap, (inner.Height - confirmBtn.Height) / 2);

            // Capture box sits between the label and the buttons, with a
            // fixed left edge so every row's box starts at the same x.
            // Reserve room for the enable toggle when present.
            int captureLeft = 14 + labelW + gap * 2;
            int captureRight = confirmBtn.Left - gap
                - (toggle != null ? toggle.Width + gap : 0);
            int captureW = Math.Max(80, captureRight - captureLeft);
            capture.SetBounds(captureLeft, (inner.Height - capture.Height) / 2, captureW, capture.Height);

            // Enable toggle sits between the capture box and the buttons.
            if (toggle != null)
            {
                int toggleX = capture.Right + gap;
                int toggleY = (inner.Height - toggle.Height) / 2;
                toggle.Location = new Point(toggleX, toggleY);
            }
        };

        // Row reflects the master switch only. The sub-toggle must stay
        // clickable even when this hotkey is switched off, otherwise the
        // row would be disabled and the user could never re-enable it
        // (WinForms blocks child interaction when a parent is disabled).
        // 色温快捷键行额外受色温总开关约束：色温关 → 整行锁定禁用。
        bool masterOn = Program.Instance?.GetAllHotKeysEnabled() ?? true;
        bool tempLocked = isTempHotkey && !(Program.Instance?.GetColorTemperatureEnabled() ?? false);
        row.Enabled = masterOn && !tempLocked;
        if (toggle != null) UpdateRowEnabled();

        // 子开关关闭时只灰化行内操作控件（capture/√/×），保持 toggle 本身
        // 可点击，避免整行禁用导致开关"锁死"无法再次打开。
        void UpdateRowEnabled()
        {
            bool tempLocked = isTempHotkey && !(Program.Instance?.GetColorTemperatureEnabled() ?? false);
            bool masterOn = Program.Instance?.GetAllHotKeysEnabled() ?? true;
            bool on = toggle?.Checked == true && !tempLocked;
            if (tempLocked && toggle != null) toggle.Checked = false;
            // 色温锁定：toggle 本身也禁用（真正锁死，点击无效）；
            // 否则主开关打开时 toggle.Enabled=true 会变成"待命"状态。
            if (toggle != null)
            {
                toggle.Enabled = masterOn && !tempLocked;
                if (tempLocked) { toggle.Checked = false; }
            }
            foreach (Control c in inner.Controls)
            {
                if (c == toggle || c is ThemedLabel) continue;
                c.Enabled = on;
            }
        }

        // P5：把本组四个控件暴露给自动化接口。actionId 由调用方给（5 组各一个，
        // 如 Hk_IncreaseBrightness → Hk_IncreaseBrightnessCapture / …Toggle /
        // …ConfirmBtn / …CancelBtn）。toggle 可能为 null（setEnabled 为空时不创建）。
        if (!string.IsNullOrEmpty(actionId))
        {
            var toWire = new List<(Control, string)> { (capture, actionId + "Capture") };
            if (toggle != null) toWire.Add((toggle, actionId + "Toggle"));
            toWire.Add((confirmBtn, actionId + "ConfirmBtn"));
            toWire.Add((cancelBtn, actionId + "CancelBtn"));
            AutomationBridge.Wire(toWire.ToArray());
        }

        return row;
    }

    /// <summary>
    /// 3.6.0 第 7 页：显示器。独立控制总开关 + 受控显示器折叠菜单
    /// （每屏启用/停用）+ 重命名显示器折叠菜单 + 显示器信息列表。
    /// 变化即时通知 MainController 与弹窗（不依赖设置窗关闭）。
    /// </summary>
    private Panel BuildMonitorsPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);

        var controller = Program.Instance;
        var displayIds = controller?.GetDisplayIds() ?? Array.Empty<string>();

        // ---- 独立控制总开关 ----
        // ⚠ 2026-09-19（用户 D5，3.6.0 遗留问题）：**只有 1 台显示器时本开关没有意义**
        //   （单屏下"独立控制"与"统一控制"行为完全相同），此前却仍可打开 ⇒ 用户判定不符合逻辑。
        //   现在：单屏 ⇒ **关闭 + 锁定 + 悬停提示**。
        //   **不写 settings**（存储值不动）—— 沿用项目「锁定但保留用户偏好」原则，
        //   接回第二块显示器后自动恢复用户原来的选择。
        //   注：重命名显示器 / 显示器信息 / 受控显示器三个折叠菜单**不受影响**（用户明确要求）。
        bool monSingle = displayIds.Count < 2;
        _perMonitorToggle = new ToggleSwitch
        {
            // 单屏时显示为「关」（赋值发生在挂事件之前 ⇒ 不会误写配置）。
            Checked = !monSingle && (controller?.GetPerMonitorEnabled() ?? false)
        };
        _perMonitorToggle.ApplyDpiScale(_dpiScale);
        if (monSingle) _perMonitorToggle.Enabled = false;
        _perMonitorToggle.CheckedChanged += (_, _) =>
        {
            controller?.SetPerMonitorEnabled(_perMonitorToggle.Checked);
            // 总开关切换：同步受控显示器子开关（关→锁死+保持持久值；开→恢复）
            SyncMonitorSubToggles();
            // ⚠ 白名单页的「按显示器生效」也受独立控制门控 —— 必须**立即**刷新它的置灰/勾选态，
            // 否则用户关掉独立控制后回到白名单页，开关还显示为开（实际已不生效），
            // 要重开设置窗口才恢复（用户 2026-09-16 实测报告）。
            ApplyWhitelistLockState();
            // ⚠ 同理（2026-09-19 用户报告）：**通用页**的全屏「按显示器生效」子开关门控
            // 同样依赖本总开关 —— 不刷新的话返回通用页看到它仍是锁定态，要重开设置窗才可用。
            SyncFullscreenPerMonRow();
        };
        var perMonitorToggle = _perMonitorToggle;
        var perMonitorGroup = BuildToggleGroup(perMonitorToggle);
        var perMonitorRow = BuildSettingRow(Localization.Get("MonitorsPerMonitor"), perMonitorGroup);

        // 单屏锁定提示（行 / 标签 / 开关三处都挂 —— 子控件会吞掉父级的悬停）。
        // 每次重建都先释放旧 ToolTip（含原生窗口句柄），否则会逐次泄漏。
        _monitorsTip?.Dispose();
        _monitorsTip = null;
        if (monSingle)
        {
            _monitorsTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };
            string monitorLockHint = Localization.Get("MonSingleDisplayLock");
            _monitorsTip.SetToolTip(perMonitorRow, monitorLockHint);
            Label? perMonitorLabel = FindRowLabel(perMonitorRow);
            if (perMonitorLabel != null) _monitorsTip.SetToolTip(perMonitorLabel, monitorLockHint);
            _monitorsTip.SetToolTip(perMonitorToggle, monitorLockHint);
            OpLog.Log("[monitors/ui] 仅 1 台显示器 ⇒ 独立控制开关关闭锁定 + 提示（D5）");
        }

        // ---- 显示器信息折叠菜单 ----
        var infoHeader = BuildExpandableHeader(Localization.Get("MonitorsInfo"));
        var infoBody = BuildInfoMonitorsBody(displayIds, controller);
        infoHeader.ExpandedChanged += expanded => SetFoldBody(infoBody, expanded, scroll);

        // ---- 重命名显示器折叠菜单 ----
        var renameHeader = BuildExpandableHeader(Localization.Get("MonitorsRename"));
        var renameBody = BuildRenameMonitorsBody(displayIds, controller);
        renameHeader.ExpandedChanged += expanded => SetFoldBody(renameBody, expanded, scroll);

        // ---- 受控显示器折叠菜单 ----
        var controlledHeader = BuildExpandableHeader(Localization.Get("MonitorsControlled"));

        // ---- P5：显示器页稳定 ID（L0）+ 动作注册（L1）----
        // 每屏动态控件（CtrlToggle / RenameBox / RenameSaveBtn）在两个 body 工厂里
        // 按 AutomationBridge.MonitorKey(edidId) 自行注册，此处不重复。
        // 三个折叠头不是控件（ExpandableHeader 包装 RoundedCardPanel），注册其面板，
        // Click 调 Toggle() 与手点等价。
        AutomationBridge.Wire(
            (_perMonitorToggle!, "Mon_PerMonitorToggle"));
        infoHeader.Panel.Name = infoHeader.Panel.AccessibleName = "Mon_HeaderInfo";
        renameHeader.Panel.Name = renameHeader.Panel.AccessibleName = "Mon_HeaderRename";
        controlledHeader.Panel.Name = controlledHeader.Panel.AccessibleName = "Mon_HeaderControlled";
        AutomationBridge.Register(new AutomationAction("Mon_HeaderInfo", "header", Click: () => infoHeader.Toggle()));
        AutomationBridge.Register(new AutomationAction("Mon_HeaderRename", "header", Click: () => renameHeader.Toggle()));
        AutomationBridge.Register(new AutomationAction("Mon_HeaderControlled", "header", Click: () => controlledHeader.Toggle()));
        var controlledBody = BuildControlledMonitorsBody(displayIds, controller);
        controlledHeader.ExpandedChanged += expanded => SetFoldBody(controlledBody, expanded, scroll);

        // 页标题（置于滚动区最顶部）：不显式设 BackColor（保持 Color.Transparent
        // 继承父 _content 背景），主题切换时 _content.BackColor 由 RefreshTheme 更新，
        // 标题随之同步变深/浅；显式 BackColor=Bg 会停在构造时主题色不变。
        // 字号/高度与通用设置/亮度设置等页标题一致（14F Bold / 36*dpi）。
        var pageTitle = new Label
        {
            Text = Localization.Get("MonitorsSettings"),
            Font = UiFont(14f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };

        // ThemeScrollPanel stacks in reverse collection order (last added = top),
        // so add bottom-most content first: info, rename, controlled, master row,
        // and the page title LAST so it sits at the very top.
        scroll.Controls.Add(infoBody);
        scroll.Controls.Add(infoHeader.Panel);
        scroll.Controls.Add(renameBody);
        scroll.Controls.Add(renameHeader.Panel);
        scroll.Controls.Add(controlledBody);
        scroll.Controls.Add(controlledHeader.Panel);
        scroll.Controls.Add(perMonitorRow);
        scroll.Controls.Add(pageTitle);

        if (displayIds.Count == 0)
        {
            var emptyRow = BuildSettingRow(Localization.Get("MonitorsNoDisplays"), new Label
            {
                Text = "-",
                AutoSize = true,
                ForeColor = TextDim
            });
            scroll.Controls.Add(emptyRow);
        }

        // 构建完成后立即同步一次子开关状态（总开关当前值决定子开关启停）
        SyncMonitorSubToggles();
        // F19：重命名显示器输入框挂"程序性焦点取消全选"
        SuppressProgrammaticSelectAll(page);

        return page;
    }

    /// <summary>
    /// 折叠体容器（受控/重命名/信息三个折叠菜单的展开区）。
    /// 只充当行的布局承载区，不呈现"整块卡片"外观：底色用页面背景 Bg
    /// （浅色主题下 Bg==BgInner==白，本就同色不可见；深色下若用 BgInner 会
    /// 与页面背景形成一整块同色矩形）。用独立类型而非裸 Panel，是为了
    /// RefreshTheme 主题刷新时能把它和普通 BgInner 面板区分开，始终保持 Bg，
    /// 避免主题切换后颜色被"Panel→bgInner"规则改回去。
    /// </summary>
    private sealed class FoldBodyPanel : Panel
    {
    }

    /// <summary>
    /// 折叠体展开/收起。此前 body 展开高度按"屏数×(48+10)×dpi"估算，与行
    /// 实际 Dock 布局占用不一致 → 最后一行下方露出与行同色(BgInner)的空白
    /// 矩形（浅色主题下同白不明显，深色下突兀）。
    /// 安全修法（不碰 AutoSize/Dock，避免 1040 AutoSize 与 ThemeScrollPanel 手动
    /// 布局冲突导致的整体崩坏）：展开时先用估算高度让内部 Dock=Top 行完成布局，
    /// 再按各子控件实测布局后的实际底边把 body 收缩到贴合内容——底部不再留
    /// 卡片色空白；body 底色用页面背景 Bg，即使有 1px 级测量偏差也只会露出
    /// 页面底色（等同折叠区间隙），不会再有突兀的同色矩形。
    /// </summary>
    private static void SetFoldBody(Panel body, bool expanded, ThemeScrollPanel scroll)
    {
        if (body == null || body.IsDisposed) return;
        // 原子更新：折叠体尺寸/可见性变更会触发多次自动布局与重绘，消息循环中
        // 可能呈现"中间帧"（内容先上移/下移、其它行文字瞬时错乱再恢复的跳动渲染）。
        // 关闭重绘→完成全部几何变更→一次性重画终态，只让用户看到最终结果。
        // 布局照常执行（不用 SuspendLayout——那会引发整体位移，1427 教训）。
        scroll.BeginUpdate();
        try
        {
            if (expanded)
            {
                // 单段定高（2026-09-03 布局探针实证后的正确实现）：
                // 折叠体内行是 Dock=Top，行高在构建期固定（BuildSettingRow 高
                // =48*dpi，不随布局变化），因此 body 最终高度可直接对各子行
                // Height 求和得到——一次赋高即到位，无"先撑后缩"的中间帧。
                body.Visible = true;
                int used = 0;
                foreach (Control child in body.Controls)
                    used += child.Height;       // Dock 布局下行高固定，直接累计即终高
                body.Height = used;
                body.PerformLayout();           // 行定位到终态高度内
                // 只刷滚动指标（位置已由自动布局更新）：RefreshLayout 的全量重排
                // 是重复渲染来源（布局探针：自动 3 次 + 显式第 4 次）。
                scroll.RefreshMetrics();
            }
            else
            {
                body.Height = 0;
                body.Visible = false;
                // ------------------------------------------------------------------
                // ★ 焦点回收（2026-09-18 用户报「点开折叠菜单光标在闪 / 文字被全选」）
                //   实测（probe_rename_focus.py，外部 GetGUIThreadInfo 观测）：
                //   收起折叠区**不会**把焦点从体内控件移走 —— WinForms 不因
                //   "后代被 Visible=false" 回收焦点，`Form.ActiveControl` 仍指向那个
                //   已隐藏的输入框。后果：
                //     ① 键盘输入继续打进看不见的输入框（可能误改显示器名）；
                //     ② 再展开时看到"光标在闪"；窗口被重新激活时原生 EDIT 按
                //        "程序设置焦点"处理并 **全选**整段文字。
                //   故在隐藏之后主动清掉窗体 ActiveControl（此刻输入框已不可聚焦，
                //   框架不会又把它要回去）。
                // ------------------------------------------------------------------
                if (body.ContainsFocus)
                {
                    var host = body.FindForm();
                    if (host != null) host.ActiveControl = null;
                }
                scroll.RefreshMetrics(); // 折叠后内容变短 → 刷新 _maxScroll 归零，滚动条自动隐藏
            }
        }
        finally
        {
            scroll.EndUpdate(); // 恢复重绘并一次性呈现终态
        }
    }

    private Panel BuildControlledMonitorsBody(IReadOnlyList<string> ids, MainController? controller)
    {
        int rowH = (int)(48 * _dpiScale) + (int)(10 * _dpiScale);
        var body = new FoldBodyPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            BackColor = Bg,
            Height = 0,
            Tag = ids.Count * rowH
        };

        if (ids.Count == 0) return body;

        // 反向添加：FoldBody 内行是 Dock=Top，"后添加者在上"，倒序遍历使
        // ids[0]（系统枚举/主屏优先的第一位）显示在折叠区最顶部，
        // 与弹窗/OSD 逐屏滑轨的"首行 = 列表首项"一致。
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            var id = ids[i];
            string name = controller?.GetDisplaySystemName(id) ?? id;
            var st = controller?.GetDisplayState(id) ?? (1f, GammaController.DEFAULT_TEMPERATURE, true);
            bool enabled = st.Enabled;

            var toggle = new ToggleSwitch
            {
                Checked = enabled
            };
            toggle.ApplyDpiScale(_dpiScale);
            // 注册到子开关列表（总开关联动用）：Getter 读回持久值（单一事实源=settings）
            string capturedId = id;
            _monitorSubToggles.Add((toggle, () => (controller?.GetDisplayState(capturedId) ?? (1f, GammaController.DEFAULT_TEMPERATURE, true)).Enabled));
            toggle.CheckedChanged += (_, _) =>
            {
                if (_syncingMonitorSub) return; // 同步期间不写回持久值
                controller?.SetDisplayEnabled(capturedId, toggle.Checked);
            };
            // P5：受控开关自动化 ID —— Mon_<MonitorKey>_CtrlToggle
            AutomationBridge.Wire((toggle, "Mon_" + AutomationBridge.MonitorKey(id, i) + "_CtrlToggle"));
            var group = BuildToggleGroup(toggle);
            var row = BuildSettingRow(name, group);
            row.Dock = DockStyle.Top;
            body.Controls.Add(row);
        }

        return body;
    }

    /// <summary>
    /// 重命名显示器折叠菜单主体：每屏一行（当前显示名 + 输入框 + 保存按钮）。
    /// </summary>
    private Panel BuildRenameMonitorsBody(IReadOnlyList<string> ids, MainController? controller)
    {
        int rowH = (int)(48 * _dpiScale) + (int)(10 * _dpiScale);
        var body = new FoldBodyPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            BackColor = Bg,
            Height = 0,
            Tag = ids.Count * rowH
        };

        if (ids.Count == 0) return body;

        // 反向添加：使 ids[0]（主屏优先）显示在折叠区最顶行，与弹窗/OSD 行一致。
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            var id = ids[i];
            string currentName = controller?.GetDisplaySystemName(id) ?? id;

            var editBox = new RoundedTextBox
            {
                Width = (int)(140 * _dpiScale),
                Font = UiFont(10f),
                Text = currentName,
                // 与快捷键页 HotKeyCaptureBox 一致：文字水平居中，避免短名称
                // 在宽框里贴左。TextBox 默认 AutoSize=true，实际高度由字体决定
                // （10F≈32px@175%），下方布局按实际高垂直居中，不设 Height。
                TextAlign = HorizontalAlignment.Center,
                // 名称上限 15 字符（防自定义名挤占右侧信息列）
                MaxLength = 15
            };
            editBox.ApplyTheme(InputBg, TextMain);
            editBox.SetParentBackground(BgInner);

            // 失焦回滚：未按确认（Enter/保存钮）就点到别处 → 还原为"最后一次
            // 提交的名字"（与色温范围输入框同语义）。提交成功后基准同步更新。
            string committedName = currentName;
            bool renameArmed = false;
            DateTime lastTooLongWarnUtc = DateTime.MinValue;
            void CommitRename()
            {
                string trimmed = editBox.Text?.Trim() ?? "";
                if (trimmed.Length > 15)
                {
                    MessageBox.Show(Localization.Get("RenameTooLong"), Localization.Get("Error"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    editBox.Text = committedName;
                    return;
                }
                controller?.SetDisplayName(id, trimmed.Length == 0 ? null : trimmed);
                committedName = trimmed.Length == 0 ? currentName : trimmed;
                // 立即生效：弹窗/OSD 行名由 MainController 实时推送；此处再重建
                // 显示器页让"信息/重命名"列表同步显示新名（无需重启）。
                if (!IsDisposed && Visible) BeginInvoke((Action)RebuildUi);
            }
            // 键入到 15 字符上限后再输入字母 → 提示"名称过长"并拦截该字符
            // （限流：1.2s 内只弹一次，避免连按刷屏）。
            editBox.KeyPress += (_, e) =>
            {
                if (char.IsControl(e.KeyChar)) return;
                if (editBox.TextLength >= 15)
                {
                    e.Handled = true;
                    if ((DateTime.UtcNow - lastTooLongWarnUtc).TotalSeconds > 1.2)
                    {
                        lastTooLongWarnUtc = DateTime.UtcNow;
                        MessageBox.Show(Localization.Get("RenameTooLong"), Localization.Get("Error"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };

            var saveBtn = new RoundedButton
            {
                Text = Localization.Get("MonitorsRenameBtn"),
                Width = (int)(56 * _dpiScale),
                Height = (int)(26 * _dpiScale),
                Font = UiFont(9f),
            };
            saveBtn.ApplyTheme(Bg, TextMain, Border,
                ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
                ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));

            // 保存钮 MouseDown 先置"提交中"标志：TextBox 的 Leave 在焦点转移到
            // 按钮时触发、早于 Click，若不加标志，失焦回滚会把刚保存的名字又
            // 还原成旧值（与色温范围输入框的 rangeCommitArmed 同手法）。
            saveBtn.MouseDown += (_, _) => renameArmed = true;
            // P5：Mon_<MonitorKey>_RenameBox / Mon_<MonitorKey>_RenameSaveBtn
            // 注意：RenameSaveBtn 会触发 RebuildUi（重建显示器页）—— 注册会被清掉重做，
            // 所以这次 Invoke 里的 Get 回读的是旧控件，属预期。
            AutomationBridge.Wire(
                (editBox, "Mon_" + AutomationBridge.MonitorKey(id, i) + "_RenameBox"));
            AutomationBridge.WireDestructive(
                (saveBtn, "Mon_" + AutomationBridge.MonitorKey(id, i) + "_RenameSaveBtn"));
            saveBtn.Click += (_, _) =>
            {
                renameArmed = false;
                CommitRename();
            };
            editBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CommitRename(); e.SuppressKeyPress = true; }
            };
            editBox.Leave += (_, _) => BeginInvoke((Action)(() =>
            {
                if (!renameArmed) editBox.Text = committedName;
                renameArmed = false;
            }));

            var rightPanel = new Panel
            {
                BackColor = BgInner,
                AutoSize = false,
                Width = editBox.Width + saveBtn.Width + (int)(8 * _dpiScale),
                Height = Math.Max(editBox.Height, saveBtn.Height)
            };
            rightPanel.Controls.Add(editBox);
            rightPanel.Controls.Add(saveBtn);
            rightPanel.Layout += (_, _) =>
            {
                // 输入框与按钮都按自身实际高在面板内垂直居中：TextBox 有
                // AutoSize（10F 实际 ~32px）而 RoundedButton 无（保持设定高），
                // 两者若都顶对齐，输入框会整体偏上（先前 28px 框按 45px 面板
                // 居中后的偏上观感）。VerticalCenter 奇数差把多出像素放底部。
                editBox.Location = new Point(0, VerticalCenter(rightPanel.Height, editBox.Height));
                saveBtn.Location = new Point(
                    editBox.Width + (int)(8 * _dpiScale),
                    VerticalCenter(rightPanel.Height, saveBtn.Height));
            };

            var row = BuildSettingRow(currentName, rightPanel);
            row.Dock = DockStyle.Top;
            body.Controls.Add(row);
        }

        return body;
    }

    /// <summary>
    /// 显示器信息折叠菜单主体：每屏一行（显示名称 + 当前分辨率/缩放比）。
    /// </summary>
    private Panel BuildInfoMonitorsBody(IReadOnlyList<string> ids, MainController? controller)
    {
        int rowH = (int)(48 * _dpiScale) + (int)(10 * _dpiScale);
        var body = new FoldBodyPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            BackColor = Bg,
            Height = 0,
            Tag = ids.Count * rowH
        };

        if (ids.Count == 0) return body;

        // 每屏物理信息映射：EDID 实例 ID(base) → Monitor（分辨率/缩放）。
        // EDID 解析失败时 EdidId 为空，无法与 key 对齐 → 该行显示占位符。
        var byEdid = new Dictionary<string, Monitor>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Monitor.GetAll())
        {
            if (!string.IsNullOrEmpty(m.EdidId) && !byEdid.ContainsKey(m.EdidId))
                byEdid[m.EdidId] = m;
        }

        // 反向添加：使 ids[0]（主屏优先）显示在信息列表最顶行，与弹窗/OSD 行一致。
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            var id = ids[i];
            // 左列：原厂显示名（不含自定义改名）
            string name = controller?.GetDisplayOriginalName(id) ?? id;
            // 中间留白：显示"改名后的名字"（小字号），无改名则留空
            string? customName = controller?.GetDisplayName(id);

            // 副文本：只显示缩放比与分辨率（不显示 EDID/亮度/色温等内部信息）
            string info;
            if (byEdid.TryGetValue(id, out var mon) && mon != null && mon.PhysicalWidthPx > 0)
            {
                info = $"{mon.PhysicalWidthPx}×{mon.PhysicalHeightPx}   |   {mon.ScalePercent}%";
            }
            else
            {
                info = "—";
            }

            // 右内容区：左=自定义名（小字），右=分辨率/缩放（右对齐）
            var content = new Panel
            {
                Width = (int)(230 * _dpiScale),
                Height = (int)(24 * _dpiScale),
                BackColor = BgInner
            };
            bool showCustom = !string.IsNullOrWhiteSpace(customName)
                              && !string.Equals(customName, name, StringComparison.OrdinalIgnoreCase);
            var customFont = UiFont(8.5f);
            var customLabel = new ThemedLabel
            {
                Text = showCustom ? customName! : "",
                Font = customFont,
                AutoSize = false,
                // 文本宽度实测 + 右间距：固定宽度的 Dock=Left 会自动撑满整行高度，
                // 文字 MiddleLeft 天然垂直居中（避免 AutoSize+Dock 的高度怪异）。
                Width = showCustom
                    ? System.Windows.Forms.TextRenderer.MeasureText(customName!, customFont).Width
                        + (int)(10 * _dpiScale)
                    : 0,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMain
            };
            var infoLabel = new ThemedLabel
            {
                Text = info,
                Font = UiFont(8.5f),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = TextSub
            };
            content.Controls.Add(infoLabel);      // Fill 先加
            content.Controls.Add(customLabel);    // 后加的 Dock.Left 占左并撑满高度

            var row = BuildSettingRow(name, content);
            row.Dock = DockStyle.Top;
            body.Controls.Add(row);
        }

        return body;
    }


    /// <summary>
    /// 折叠菜单头部（仿 Twinkle Tray expandable）：标题 + 右侧 V 形箭头。
    /// 点击切换展开状态。返回头部控件；展开/收起由 ExpandedChanged 事件通知。
    /// </summary>
    private ExpandableHeader BuildExpandableHeader(string title)
    {
        var header = new ExpandableHeader(title, _dpiScale);
        return header;
    }

    /// <summary>
    /// 折叠菜单头部控件：标题 + chevron 箭头，点击切换展开状态。
    /// ExpandedChanged(bool) 事件通知主体面板显隐/高度。
    /// </summary>
    /// <summary>
    /// 折叠菜单头部控件：标题 + chevron 箭头，点击切换展开状态。
    /// ExpandedChanged(bool) 事件通知主体面板显隐/高度。
    /// 内部组合 RoundedCardPanel（其 sealed 不可继承）。
    /// </summary>
    private sealed class ExpandableHeader
    {
        private readonly RoundedCardPanel _panel;
        private readonly Label _label;
        private readonly Label _arrow;
        private bool _expanded;
        private readonly float _dpiScale;

        public event Action<bool>? ExpandedChanged;

        /// <summary>头部卡片控件（加入页面滚动容器）。</summary>
        public RoundedCardPanel Panel => _panel;

        public ExpandableHeader(string title, float dpiScale)
        {
            _dpiScale = dpiScale;
            _panel = new RoundedCardPanel
            {
                Height = (int)(40 * dpiScale),
                Margin = new Padding(0, (int)(6 * dpiScale), 0, 0),
                Cursor = Cursors.Hand
            };
            _panel.ApplyTheme(SettingsForm.Bg, SettingsForm.BgInner, SettingsForm.Border);

            var inner = _panel.Inner;
            _label = new ThemedLabel
            {
                Text = title,
                Font = PxFont(_dpiScale, 10f),
                // B'：本类为**嵌套类**（自持 `_dpiScale`），故直接调静态核心
                // `PxFont(_dpiScale, …)`，而非外层的实例包装 `UiFont(…)`。
                // Tag="dynamic" 保留：仅作文档标记（"由 Layout 按容器自适应"），
                // AttachFontFix 已随 B' 删除，不再有代码读取它。
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMain,
                Cursor = Cursors.Hand
            };
            _arrow = new Label
            {
                Text = "\uE70D",   // chevron down
                Font = PxFont(_dpiScale, "Segoe MDL2 Assets", 10f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = TextSub,
                Cursor = Cursors.Hand
            };
            inner.Controls.Add(_label);
            inner.Controls.Add(_arrow);

            inner.Layout += (_, _) =>
            {
                // 与 BuildSettingRow 同策略：按当前容器宽度/高度用 FitLabelPx
                // 把字号 fit 回合适值（DPI 变更/重建后标题文字由布局按新尺寸决定，
                // 不会与容器错位）。
                int labelW = Math.Max(0, inner.Width - (int)(60 * dpiScale));
                int maxH = Math.Max(14, inner.Height - (int)(6 * dpiScale));
                float size = FitLabelPx(_dpiScale, _label.Text, _label.Font, labelW, maxH);
                if (Math.Abs(size - _label.Font.Size) > 0.01f)
                {
                    var old = _label.Font;
                    _label.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Pixel);
                    old.Dispose();
                }
                _label.SetBounds(14, 0, labelW, inner.Height);
                _arrow.SetBounds(inner.Width - (int)(40 * dpiScale), 0, (int)(36 * dpiScale), inner.Height);
            };

            _panel.Click += (_, _) => Toggle();
            _label.Click += (_, _) => Toggle();
            _arrow.Click += (_, _) => Toggle();
            // Inner 空白区点击也切换（自绘卡片外层 Click 收不到子控件区域的事件）
            inner.Click += (_, _) => Toggle();
        }

        public void Toggle()
        {
            _expanded = !_expanded;
            _arrow.Text = _expanded ? "\uE70E" : "\uE70D";  // chevron up/down
            ExpandedChanged?.Invoke(_expanded);
        }

    }

    // ==================================================================
    //  应用白名单页（设计稿 §16.6）
    // ==================================================================

    private ThemeScrollPanel? _whitelistScroll;

    /// <summary>本次会话扫描到的候选（**会话快照、不持久化** —— 重启后为空，§16.0）。</summary>
    private List<WhitelistCandidate>? _whitelistCandidates;

    /// <summary>当前列表里每行的 (路径, 开关, 文本标签)，用于汇总"已勾选"与切换锁定态。</summary>
    private readonly List<(string Path, ToggleSwitch Toggle, Label? Label)> _whitelistRows = new();

    /// <summary>刷新按钮（总开关关闭时置灰，且切换锁定态时不再重建列表）。</summary>
    private RoundedButton? _whitelistRefreshBtn;
    // 2026-09-16 逐屏白名单子开关（门控不满足时置灰，但保留用户偏好）
    private ToggleSwitch? _whitelistPerMonToggle;
    private bool _whitelistPerMonSilent;
    /// <summary>
    /// 白名单「按显示器生效」子行（2026-09-20）。
    /// 提示文案要从 <see cref="ApplyWhitelistLockState"/> 里刷新，而该行原来是
    /// `BuildAppWhitelistPage` 的**局部变量** ⇒ 跨不了方法，故提为字段。
    /// </summary>
    private Panel? _wlPerMonRow;
    /// <summary>白名单子开关**当前**的悬停提示文案，供只读探针 <c>Gen_WlPerMonHint</c> 回读。</summary>
    private string _wlPerMonHintText = "";

    /// <summary>应用行状态提示（当前可见/已最小化/任务栏托盘/未运行）改为**悬停 tooltip**，
    /// 不再占用行内文字（用户 2026-09-15 要求；也顺带免掉长语言换行问题）。</summary>
    private ToolTip? _whitelistToolTip;

    /// <summary>重建行时抑制"勾选变化→保存"，避免重建过程写脏设置。</summary>
    private bool _whitelistRebuilding;

    /// <summary>
    /// 应用白名单页。总开关语义 = **锁定/解锁**（关闭时列表灰化锁定且白名单不生效）；
    /// 列表 = 会话快照；空白列表打开总开关即**视为刷新**（省掉 LightBulb 那一步）。
    /// 每行复用 <see cref="ToggleSwitch"/>（不新写复选框）。
    /// </summary>
    private Panel BuildAppWhitelistPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var scroll = new ThemeScrollPanel();
        scroll.ApplyTheme(Bg, Track, Thumb, ThumbHover);
        scroll.Dock = DockStyle.Fill;
        page.Controls.Add(scroll);
        _whitelistScroll = scroll;

        // 直接取用**启动期预热扫描**的结果（应用一启动就在后台扫好了）：
        // 否则打开白名单页会先停在"正在扫描"、必须手动点刷新才加载（用户实测）。
        if (_whitelistCandidates == null)
        {
            _whitelistCandidates = Program.Instance?.CachedWhitelistCandidates;
        }

        _whitelistToolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };
        page.Disposed += (_, _) =>
        {
            _whitelistToolTip?.Dispose();
            _whitelistToolTip = null;
        };

        // 整页是新建的（首次构建 / RebuildUi 切语言主题后），签名必须清空，
        // 否则"数据没变"的短路会让新页面一行都不渲染。
        _whitelistRenderSig = "";

        RebuildWhitelistRows();

        // 打开设置窗口时就**提前扫描**（扫描已改为后台线程，不阻塞 UI）。
        // 否则用户切到白名单页时，会先只看到"已勾选项"、一两秒后才补齐全部候选，
        // 看着像"总开关开着却只显示一项"的漏洞（用户实测并担心过）。
        if ((Program.Instance?.GetAppWhitelistEnabled() ?? false) && _whitelistCandidates == null)
        {
            RescanWhitelist();
        }

        // 每次切到本页都校准一次列表（用户担心的"总开关开着却只显示一两项"）：
        // 构造期发起的提前扫描可能还没回来，那时页面已按"仅已勾选项"渲染过；
        // 这里补一次 —— 有候选就按候选重排，没有就再扫一次。
        page.VisibleChanged += (_, _) =>
        {
            if (!page.Visible) return;
            if (!(Program.Instance?.GetAppWhitelistEnabled() ?? false)) return;
            if (_whitelistCandidates == null) RescanWhitelist();
            else RebuildWhitelistRows();
        };

        return page;
    }

    /// <summary>
    /// 重建白名单页内容。注意：<see cref="ThemeScrollPanel"/> 的子控件是**逆序堆叠**
    /// （集合里最后一个 = 最上面），所以先把全部行按视觉顺序建好，再逆序 Add。
    /// </summary>
    private System.Windows.Forms.Timer? _whitelistRebuildTimer;

    /// <summary>
    /// 延迟重排白名单页 —— 等总开关的滑块动画跑完（Bug B，2026-09-17）。
    /// ToggleSwitch 的步进是 `Interval=16ms` + `pos += (target-pos)*0.35` ⇒ 约 10 步、
    /// ≈150ms 才进到位；这里留 260ms（含 DPI 缩放下的重绘余量）。
    /// 重复调用只重置计时 ⇒ 连点开关也只会在最后一次之后重排一次。
    /// </summary>
    private void DeferRebuildWhitelistRows(int delayMs = 260)
    {
        if (_whitelistRebuildTimer == null)
        {
            _whitelistRebuildTimer = new System.Windows.Forms.Timer { Interval = delayMs };
            _whitelistRebuildTimer.Tick += (_, _) =>
            {
                _whitelistRebuildTimer?.Stop();
                _whitelistRebuildTimer?.Dispose();
                _whitelistRebuildTimer = null;
                if (!IsDisposed && !Disposing) RebuildWhitelistRows();
            };
        }
        _whitelistRebuildTimer.Stop();
        _whitelistRebuildTimer.Start();
        OpLog.Log($"[whitelist/ui] 总开关动画保护：延迟 {delayMs}ms 再重排列表" +
                  "（否则会把正在滑动的那枚开关 Dispose 掉）");
    }

    private void RebuildWhitelistRows()
    {
        if (_whitelistScroll == null || _whitelistScroll.IsDisposed) return;
        if (_whitelistRebuilding) return;

        MainController? ctrl = Program.Instance;
        bool masterOn = ctrl?.GetAppWhitelistEnabled() ?? false;
        var saved = new HashSet<string>(ctrl?.GetAppWhitelist() ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // ---- 渲染签名短路：数据没变就**不重建** ----
        // 进/出本页时 VisibleChanged 会调到这里；总开关开着时列表有 ~17 行（≈85 个控件），
        // 每次重建都会把导航切换卡住：导航高亮停在旧页、鼠标拖不动、内容短暂空白。
        // 用户实测"关掉总开关就不卡"正好印证开销来自本页重建。
        string sig = (masterOn ? "1" : "0")
                     + "|" + (_whitelistCandidates == null ? "n" : "s")
                     + "|" + string.Join(";", saved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (_whitelistCandidates != null)
        {
            sig += "|" + string.Join(";", _whitelistCandidates
                .Select(c => c.Path)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        if (sig == _whitelistRenderSig) return;
        _whitelistRenderSig = sig;

        _whitelistRebuilding = true;
        ThemeScrollPanel scroll = _whitelistScroll;
        scroll.BeginUpdate();
        try
        {
            // 行是动态注册的，先清掉这一页的动作（含每次重建的 Wl_Item_*）。
            AutomationBridge.ClearActionsByPrefix("Wl_");

            foreach (Control c in scroll.Controls.Cast<Control>().ToList())
            {
                scroll.Controls.Remove(c);
                c.Dispose();
            }
            _whitelistRows.Clear();

            var ordered = new List<Control>();

            // ---- 行 1：总开关（锁定/解锁），左侧并排【刷新图标按钮】 ----
            // 图标/按钮比例对齐标题栏图钉（图钉：35px 图标 + 80px 按钮 = 四周留白 22px）。
            // 之前是 35px 塞进 49px 按钮 → 四周只剩 7px，视觉上"撑满、很重"（用户反馈突兀）。
            int iconPx = Math.Max(12, (int)(17 * _dpiScale));
            int sidePx = (int)(30 * _dpiScale);
            var refresh = new RoundedButton
            {
                Text = string.Empty,
                AutoSize = false,
                Size = new Size(sidePx, sidePx),
                Image = LoadRefreshIcon(iconPx)
            };
            _whitelistRefreshBtn = refresh;
            // 边框色 = 行底色（即**不画可见边框**），与图钉按钮同款：平时是一枚扁平图标，
            // 只在悬停时给底色。否则纯文本行里会冒出一个"方盒子"，显得与旁边"开"字格格不入。
            refresh.ApplyTheme(BgInner, TextMain, BgInner,
                ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
                ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
            refresh.SetParentBackground(BgInner);
            refresh.Click += (_, _) => RescanWhitelist();

            var master = new ToggleSwitch { Checked = masterOn };
            master.ApplyDpiScale(_dpiScale);
            master.CheckedChanged += (_, _) =>
            {
                MainController? c = Program.Instance;
                if (c == null) return;
                c.SetAppWhitelistEnabled(master.Checked);
                if (master.Checked && _whitelistCandidates == null)
                {
                    RescanWhitelist();     // 空白列表打开总开关 = 刷新
                }
                else
                {
                    // 开/关都要重排列表：关 → 只留已勾选项；开 → 恢复全部候选。
                    // ⚠ 必须**延迟**到滑块动画跑完（Bug B，2026-09-17 用户报「白名单的总开关
                    //   没有做平滑变化的动画，这点参考其他滑动开关」）：
                    //   重排会 Dispose 掉**当前这个开关自己**，而 ToggleSwitch 的滑块动画是
                    //   16ms × ~10 步（≈150ms）的 Timer；原来用 BeginInvoke 在下一轮消息循环
                    //   就重排（WM_TIMER 优先级低于 posted message）⇒ 动画一步都没走就被
                    //   Dispose，滑块「啪」地瞬移到位。其他所有开关（时间调整 / 独立控制 /
                    //   色温总开关 …）都不重建自己 ⇒ 动画完整。
                    //   改为等动画结束再重排：视觉与其他开关一致，列表刷新只晚 ~260ms。
                    //   ⚠ 不要改 `RescanWhitelist` 里的那处 BeginInvoke —— 那是
                    //     后台扫描完成后的回封，与滑块动画无关。
                    DeferRebuildWhitelistRows();
                }
            };

            // [刷新按钮][On/Off 文字][开关]，布局与 BuildToggleGroup 同款（按实测宽度排，不写死坐标），
            // 刷新按钮垂直居中、紧贴 On/Off 文字左侧，不会遮挡它。
            var masterState = new ThemedLabel
            {
                Text = master.Checked ? Localization.Get("On") : Localization.Get("Off"),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Font = UiFont(10f),
                ForeColor = TextMain
            };
            master.CheckedChanged += (_, _) =>
                masterState.Text = master.Checked ? Localization.Get("On") : Localization.Get("Off");

            var masterGroup = new Panel { BackColor = BgInner, AutoSize = false };
            masterGroup.Controls.Add(masterState);
            masterGroup.Controls.Add(master);
            masterGroup.Controls.Add(refresh);
            masterGroup.Layout += (_, _) =>
            {
                int maxTextW = (int)(90 * _dpiScale);
                int textW = Math.Min(
                    TextRenderer.MeasureText(masterState.Text, masterState.Font).Width, maxTextW);
                int groupH = (int)(22 * _dpiScale);
                float size = FitLabelFont(masterState.Text, masterState.Font, textW, groupH);
                if (Math.Abs(size - masterState.Font.Size) > 0.01f)
                {
                    var old = masterState.Font;
                    masterState.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Pixel);
                    old.Dispose();
                }
                int gap = (int)(8 * _dpiScale);
                int h = Math.Max(groupH, refresh.Height);
                masterGroup.Size = new Size(
                    refresh.Width + gap + textW + 10 + master.Width, h);
                int cy = h / 2;
                refresh.Location = new Point(0, cy - refresh.Height / 2);
                masterState.Size = new Size(textW, groupH);
                masterState.Location = new Point(refresh.Width + gap, cy - groupH / 2);
                master.Location = new Point(refresh.Width + gap + textW + 10, cy - master.Height / 2);
            };
            ordered.Add(BuildSettingRow(Localization.Get("WlEnable"), masterGroup));

            // ---- 逐屏生效子开关（2026-09-16 定稿 §7.1）----
            // 门控 = 总开关开 && 独立控制开 && 受控屏>=2；不满足则置灰且不生效，但**保留用户偏好**。
            var perMonToggle = new ToggleSwitch { Checked = Program.Instance?.GetAppWhitelistPerMonitor() ?? false };
            perMonToggle.ApplyDpiScale(_dpiScale);
            perMonToggle.CheckedChanged += (_, _) =>
            {
                if (_whitelistPerMonSilent) return;   // 程序性赋值不写配置（ToggleSwitch 同值早退，异值会发事件）
                Program.Instance?.SetAppWhitelistPerMonitor(perMonToggle.Checked);
                ApplyWhitelistLockState();
            };
            _whitelistPerMonToggle = perMonToggle;
            Panel perMonRow = BuildSettingRow(Localization.Get("WlPerMonitor"), BuildToggleGroup(perMonToggle));
            Label? perMonLabel = FindRowLabel(perMonRow);
            _wlPerMonRow = perMonRow;   // 供 ApplyWhitelistLockState 刷新提示（局部变量跨不了方法）
            string hint = Localization.Get("WlPerMonitorHint");
            _whitelistToolTip?.SetToolTip(perMonRow, hint);
            if (perMonLabel != null) _whitelistToolTip?.SetToolTip(perMonLabel, hint);
            _whitelistToolTip?.SetToolTip(perMonToggle, hint);
            ordered.Add(perMonRow);
            AutomationBridge.Wire((perMonToggle, "Wl_PerMonitorToggle"));
            // 2026-09-20：只读探针 —— ToolTip 文本对外部不可读，把当前文案暴露出来，
            // 测试脚本可断言「门控满足 ⇒ 文案为空」（与 Gen_FsPerMonHint 同款）。
            AutomationBridge.Register(new AutomationAction("Gen_WlPerMonHint", "value",
                Get: () => _wlPerMonHintText));

            // ---- 应用行：候选 ∪ 已勾选（后者标"未运行"）----
            // **即使还没扫描过（候选为空）也要把已勾选项渲染出来**，否则关掉总开关后
            // 会出现"看着像被清空"的观感（用户 2026-09-15 要求：关闭总开关应保留已选项）。
            var byPath = new Dictionary<string, WhitelistCandidate>(StringComparer.OrdinalIgnoreCase);
            // 总开关**关闭**时只保留已勾选项（未启用的一律不显示）；开启时显示全部候选。
            // 之前要重开设置窗口才会收敛，现在由开关事件直接重建（用户 2026-09-15 要求）。
            if (_whitelistCandidates != null && masterOn)
            {
                foreach (WhitelistCandidate cand in _whitelistCandidates) byPath[cand.Path] = cand;
            }
            foreach (string p in saved)
            {
                if (!byPath.ContainsKey(p))
                {
                    byPath[p] = new WhitelistCandidate(p, Path.GetFileNameWithoutExtension(p), "absent");
                }
            }

            if (byPath.Count > 0)
            {
                foreach (WhitelistCandidate cand in byPath.Values
                             .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string stateText = cand.Source switch
                    {
                        AppWhitelistService.SourceMinimized => Localization.Get("WlSourceMinimized"),
                        AppWhitelistService.SourceTray => Localization.Get("WlSourceTray"),
                        "absent" => Localization.Get("WlNotRunning"),
                        _ => Localization.Get("WlSourceVisible"),
                    };
                    var rowToggle = new ToggleSwitch { Checked = saved.Contains(cand.Path) };
                    rowToggle.ApplyDpiScale(_dpiScale);
                    string path = cand.Path;
                    rowToggle.CheckedChanged += (_, _) => OnWhitelistRowToggled();
                    // 行上只显示应用名；状态提示走 **悬停 tooltip**（行/标签/开关三处都挂，
                    // 因为子控件会吞掉父级的悬停，只挂行面板的话指针在文字上时弹不出来）。
                    Panel row = BuildSettingRow(cand.Name, BuildToggleGroup(rowToggle));
                    Label? rowLabel = FindRowLabel(row);
                    _whitelistToolTip?.SetToolTip(row, stateText);
                    if (rowLabel != null) _whitelistToolTip?.SetToolTip(rowLabel, stateText);
                    _whitelistToolTip?.SetToolTip(rowToggle, stateText);
                    _whitelistRows.Add((path, rowToggle, rowLabel));
                    ordered.Add(row);

                    // 稳定 ID：Wl_Item_<exe名>（重名时后者覆盖前者，可接受）
                    AutomationBridge.Wire((rowToggle, "Wl_Item_" + SafeIdSegment(cand.Name)));
                }
            }
            else
            {
                // 未扫描过 → 提示行。总开关**已开**时说明是"扫描还没回来"，
                // 文案要说"正在扫描"，而不是让用户去开总开关。
                Panel emptyRow = BuildSettingRow(
                    Localization.Get(masterOn ? "WlScanning" : "WlEmpty"),
                    new Panel { Size = Size.Empty });
                Label? emptyLabel = FindRowLabel(emptyRow);
                if (emptyLabel != null) emptyLabel.ForeColor = TextDim;
                ordered.Add(emptyRow);
            }

            // 总开关已开、本次会话还没扫到候选、而列表里已有已勾选项时，补一条"正在扫描"提示：
            // 否则用户只看到那几项，会误判成"漏项"（用户实测截图并担心过）。
            if (masterOn && _whitelistCandidates == null && byPath.Count > 0)
            {
                Panel scanRow = BuildSettingRow(Localization.Get("WlScanning"), new Panel { Size = Size.Empty });
                Label? scanLabel = FindRowLabel(scanRow);
                if (scanLabel != null) scanLabel.ForeColor = TextDim;
                ordered.Insert(1, scanRow);   // 紧跟总开关行下方
            }

            // 页标题：作为**列表第一行**放进 scroll（与通用设置等页一致 —— 它们是
            // `scroll.Controls.Add(title)` + Dock=Top），标题随页面一起滚动/隐藏，
            // 而不是固定钉在窗口顶部。ordered[0] 经逆序 Add 后位于视觉最上方。
            var title = new Label
            {
                Text = Localization.Get("AppWhitelist"),
                Font = UiFont(14f, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = (int)(36 * _dpiScale),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMain
            };
            ordered.Insert(0, title);

            // 逆序 Add（ThemeScrollPanel 最后一个 = 最上面）
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                scroll.Controls.Add(ordered[i]);
            }
            scroll.RefreshLayout();

            AutomationBridge.Wire(
                (master, "Wl_EnableToggle"),
                (refresh, "Wl_RefreshBtn"));

            ApplyWhitelistLockState();   // 统一置锁定态（避免每行各写一遍）
        }
        finally
        {
            _whitelistRebuilding = false;
            scroll.EndUpdate();
        }
    }

    /// <summary>
    /// 总开关切换时**只更新锁定态、不重建列表** —— 逐行重建在几十行时会明显卡顿。
    /// 锁定 = 行开关 Enabled=false（ToggleSwitch 自带灰化）+ 标签调暗 + 刷新按钮置灰。
    /// </summary>
    private void ApplyWhitelistLockState()
    {
        bool masterOn = Program.Instance?.GetAppWhitelistEnabled() ?? false;
        if (_whitelistRefreshBtn != null && !_whitelistRefreshBtn.IsDisposed)
        {
            _whitelistRefreshBtn.Enabled = masterOn;
        }
        foreach (var (_, toggle, label) in _whitelistRows)
        {
            if (toggle.IsDisposed) continue;
            toggle.Enabled = masterOn;
            if (label != null && !label.IsDisposed)
            {
                label.ForeColor = masterOn ? TextMain : TextDim;
            }
        }

        // 逐屏子开关（2026-09-16）：门控 = 总开关开 && hostReady(独立控制开 + 受控屏>=2)。
        // 置灰时**显示为关**（因为功能确实不生效），但 settings 里的用户偏好**不动** ——
        // 条件恢复后自动回到用户原来的选择（定稿 §7.1「自动置关 + 保留偏好」）。
        if (_whitelistPerMonToggle != null && !_whitelistPerMonToggle.IsDisposed)
        {
            bool canToggle = masterOn && (Program.Instance?.IsWhitelistPerMonitorHostReady() ?? false);
            bool stored = Program.Instance?.GetAppWhitelistPerMonitor() ?? false;
            _whitelistPerMonToggle.Enabled = canToggle;
            _whitelistPerMonSilent = true;
            try
            {
                _whitelistPerMonToggle.Checked = canToggle && stored;
            }
            finally
            {
                _whitelistPerMonSilent = false;
            }
            // 2026-09-20（用户报）：提示文案原来在建页时设一次就固定 ⇒
            // 门控已满足（开关解锁）时仍显示「显示器独立控制开关没开」。
            // ⇒ 与全屏侧同款修法：**门控满足置空、不满足按具体原因列出**。
            UpdateWlPerMonHint(masterOn, canToggle);
        }
    }

    /// <summary>
    /// 刷新白名单「按显示器生效」子开关的悬停提示（2026-09-20）。
    ///
    /// 规则：**门控满足 ⇒ 提示为空**；不满足 ⇒ 按具体原因拼接（「，」分隔）：
    ///   · 应用白名单总开关没开 ⇒ `WlMasterOffHint`
    ///   · 独立控制没开         ⇒ `WlPerMonitorHint`
    ///   · 受控屏 &lt; 2          ⇒ `MonSingleDisplayLock`
    ///
    /// ⚠️ 与 `UpdateFsPerMonHint`（全屏侧）结构对齐，两者是同一类缺陷的两个实例。
    /// </summary>
    private void UpdateWlPerMonHint(bool masterOn, bool canToggle)
    {
        ThemeScrollPanel? scroll = _whitelistScroll;
        Panel? row = _wlPerMonRow;
        ToggleSwitch? toggle = _whitelistPerMonToggle;
        if (row == null || toggle == null || row.IsDisposed) return;
        // 行已从集合移除（未在页面上）时不必设提示
        if (scroll != null && !scroll.Controls.Contains(row)) return;

        string hint = "";
        if (!canToggle)
        {
            var reasons = new List<string>();
            if (!masterOn) reasons.Add(Localization.Get("WlMasterOffHint"));
            if (!(Program.Instance?.GetPerMonitorEnabled() ?? false))
                reasons.Add(Localization.Get("WlPerMonitorHint"));
            if (!(Program.Instance?.HasTwoControlledDisplays() ?? false))
                reasons.Add(Localization.Get("MonSingleDisplayLock"));
            hint = string.Join("，", reasons);
        }

        _wlPerMonHintText = hint;
        _whitelistToolTip?.SetToolTip(row, hint);
        Label? lbl = FindRowLabel(row);
        if (lbl != null) _whitelistToolTip?.SetToolTip(lbl, hint);
        _whitelistToolTip?.SetToolTip(toggle, hint);
    }

    /// <summary>本次会话是否正在后台扫描（防重入）。</summary>
    private bool _whitelistScanning;

    /// <summary>上一次渲染的"数据签名"（总开关 + 候选集合 + 已勾选集合）。
    /// 相同则跳过重建 —— 避免进/出本页时无谓地重建几十个控件把导航切换卡住。</summary>
    private string _whitelistRenderSig = "";

    /// <summary>
    /// 重新扫描候选并重建列表。**扫描放到后台线程**：`EnumWindows`（本机约 600 个顶层窗口）+
    /// 遍历全部进程 + 读托盘注册表，合计数百毫秒；跑在 UI 线程上会把消息循环堵住，
    /// 表现为"点刷新时鼠标卡一下拖不动"（用户实测）。结果回到 UI 线程再重建行。
    /// </summary>
    private void RescanWhitelist()
    {
        MainController? ctrl = Program.Instance;
        if (ctrl == null || _whitelistScanning) return;
        _whitelistScanning = true;
        OpLog.Log("[whitelist] scan: start (background)");

        System.Threading.Tasks.Task.Run(() =>
        {
            List<WhitelistCandidate> list;
            try
            {
                list = ctrl.GetWhitelistCandidates();
            }
            catch (Exception ex)
            {
                OpLog.LogEx("[whitelist] scan failed", ex);
                list = new List<WhitelistCandidate>();
            }

            // 结果**先无条件存下**：构造期（设置窗句柄还没创建）也不能丢，
            // 否则"打开窗口就提前扫描"会白扫一次、列表停在一行（实测踩到）。
            _whitelistCandidates = list;
            ctrl.SetCachedWhitelistCandidates(list);   // 同步回主控缓存，设置窗重开也能直接取用
            _whitelistScanning = false;
            OpLog.Log($"[whitelist] scan -> {list.Count} candidates");

            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    // ⚠️ 这个回封**与滑块动画有关**（旧注释写"无关"是错的，2026-09-17 组合矩阵实测抓到）：
                    //    典型时序 = 用户「打开白名单总开关」→ 同一事件里启动候选扫描
                    //    → 扫描很快回来（缓存命中 / 进程少，实测 11ms）→ 回封在下一轮消息循环
                    //      立刻 RebuildWhitelistRows → 把**刚开始滑动**的总开关 Dispose 掉
                    //      （实测日志：[toggle/anim] ✗ 动画被 Dispose 打断，只走了 1 步）。
                    //    ⇒ 必须走延后，让重排排在滑块动画（~160ms）之后。
                    BeginInvoke((Action)(() => DeferRebuildWhitelistRows()));
                }
            }
            catch (Exception ex)
            {
                OpLog.LogEx("[whitelist] marshal scan result failed", ex);
            }
        });
    }

    /// <summary>行开关变化 → 把"当前勾选的路径集合"整体写回设置（去重/规范化在 MainController 内做）。</summary>
    private void OnWhitelistRowToggled()
    {
        if (_whitelistRebuilding) return;
        MainController? ctrl = Program.Instance;
        if (ctrl == null) return;
        ctrl.SetAppWhitelist(_whitelistRows.Where(r => r.Toggle.Checked).Select(r => r.Path));
    }

    /// <summary>取出行内的文本标签（<see cref="BuildSettingRow"/> 不返回它，用于调暗/恢复颜色）。</summary>
    /// <summary>
    /// 设置行首标签的"锁定观感"（灰化 / 恢复）。
    ///
    /// 2026-09-20（用户报）：滑轨被锁定时**行首文字仍是未锁定的颜色**。
    ///
    /// ⚠️ 为什么**不**用 `label.Enabled = false` 来实现灰化（第一版就是这么写的，实测失败）：
    ///   `Control.Enabled` 是 WinForms 的**级联**属性 —— getter 逐级向上查父容器。
    ///   于是"改 Enabled 表达灰化"一旦遇到某个祖先容器被禁用，**把 Enabled 设回 true
    ///   也恢复不了颜色**（绘制仍走 `ThemedLabel` 的禁用分支）。
    ///   用户实测现象：「功能关闭停止生效时，文字还是处于锁定灰色状态」——
    ///   探针实测：四个 Label 与正常的"过渡时长"Label **启用状态链完全相同**（全 enabled），
    ///   但绘制色一个是 `(128,128,128)`（DisabledColor）、一个是 `(40,40,40)`（TextMain）。
    ///   ⇒ 颜色必须**显式接管**，不能依赖 Enabled。
    ///
    /// 做法：`GrayWhenDisabled = false` 让 `ThemedLabel` 始终按 `ForeColor` 绘制，
    /// 这里再把 ForeColor 设成 TextDim / TextMain。控件**保持启用** ⇒
    /// 无级联风险、鼠标事件正常、ToolTip 照常弹出。
    /// 颜色在调用时按 `Dark` 现算 ⇒ 主题切换后自动跟随。
    /// </summary>
    private static void SetRowLabelLocked(Panel row, bool locked)
    {
        Label? label = FindRowLabel(row);
        if (label == null) return;
        // 接管配色：不再让 Enabled 的级联决定颜色
        if (label is ThemedLabel themed) themed.GrayWhenDisabled = false;
        Color want = locked ? TextDim : TextMain;
        if (label.ForeColor != want)
        {
            label.ForeColor = want;
            label.Invalidate();
        }
    }

    private static Label? FindRowLabel(Panel row)
    {
        if (row is not RoundedCardPanel card) return null;
        foreach (Control c in card.Inner.Controls)
        {
            if (c is Label label) return label;
        }
        return null;
    }

    /// <summary>
    /// 把提示挂到一**整行**上（行面板 / 文本标签 / 内侧控件三处 —— 子控件会吞掉父级的悬停，
    /// 只挂行面板的话指针落在文字或滑轨上时弹不出来）。`hint` 为空串表示清掉旧提示。
    /// </summary>
    private static void AttachRowHint(ToolTip? tip, Panel row, Control inner, string hint)
    {
        if (tip == null) return;
        tip.SetToolTip(row, hint);
        Label? label = FindRowLabel(row);
        if (label != null) tip.SetToolTip(label, hint);
        tip.SetToolTip(inner, hint);
    }

    /// <summary>
    /// 全屏「按显示器生效」子行的**显示/隐藏 + 门控置灰**（2026-09-19 定稿 §三）。
    ///
    /// ⚠ 为什么必须是**类级方法**（原为 BuildGeneralPage 的局部函数）：
    ///   子开关在**通用页**，而它的门控依赖**显示器页**的「独立控制」总开关。
    ///   用户去显示器页打开独立控制、返回通用页时必须立刻看到子开关解锁 ——
    ///   局部函数跨不了页 ⇒ 原来要重开设置窗才刷新（用户 2026-09-19 实测报告）。
    ///
    /// ⚠ 隐藏**不能**用 `Visible = false`：`ThemeScrollPanel.LayoutContent()` 不检查 Visible，
    ///   照样对所有子控件 SetBounds 并累加 y ⇒ 会留下一段空白。必须真的从集合里增删。
    ///   只增删这一行、不动开关自身 ⇒ `ToggleSwitch` 的滑块动画完整。
    ///
    /// ✅ 纯字段访问；通用页尚未构建时安全 no-op（`_generalScroll == null`）。
    /// </summary>
    private void SyncFullscreenPerMonRow()
    {
        ThemeScrollPanel? scroll = _generalScroll;
        Panel? fullscreenRow = _generalFullscreenRow;
        ToggleSwitch? fullscreenToggle = _generalFullscreenToggle;
        ToggleSwitch? fsPerMonToggle = _fsPerMonToggle;
        Panel? row = _fsPerMonRow;
        if (scroll == null || fullscreenRow == null || fullscreenToggle == null
            || fsPerMonToggle == null || row == null) return;
        if (scroll.IsDisposed || fullscreenRow.IsDisposed) return;

        bool on = fullscreenToggle.Checked;
        bool canToggle = on && (Program.Instance?.IsFullscreenPerMonitorHostReady() ?? false);
        bool stored = Program.Instance?.GetFullscreenPerMonitor() ?? false;

        if (on)
        {
            if (!scroll.Controls.Contains(row))
            {
                scroll.Controls.Add(row);
                // 索引**更小 = 视觉更靠下**（LayoutContent 逆序遍历、从 y=0 往下排）
                // ⇒ 把子行放到 fullscreenRow 当前的位置，fullscreenRow 被挤到 idx+1，
                //   子行正好落在它正下方。
                int idx = scroll.Controls.GetChildIndex(fullscreenRow);
                scroll.Controls.SetChildIndex(row, Math.Max(0, idx));
                scroll.RefreshMetrics();
            }
        }
        else if (scroll.Controls.Contains(row))
        {
            scroll.Controls.Remove(row);
            scroll.RefreshMetrics();
        }

        // 门控置灰（与白名单同款：置灰时**显示为关**，但 settings 里的用户偏好不动，
        // 条件恢复后自动回到用户原来的选择）。
        if (!fsPerMonToggle.IsDisposed)
        {
            fsPerMonToggle.Enabled = canToggle;
            _fsPerMonSilent = true;
            try
            {
                fsPerMonToggle.Checked = canToggle && stored;
            }
            finally
            {
                _fsPerMonSilent = false;
            }
        }
        OpLog.Log($"[fs/ui] 子行同步：全屏暂停={on} 可操作={canToggle} 用户偏好={stored}");

        // 2026-09-20（用户报）：**提示文案必须随门控状态刷新**。
        // 原来只在建页时设一次固定文案「显示器独立控制开关没开」⇒
        // 用户已开「独立控制 + 全屏暂停」（开关**已解锁**）时，鼠标移上去
        // 仍显示「显示器独立控制开关没开」这句错话。
        // ⇒ 门控满足时**清空提示**；不满足时按**具体原因**列出（照抄 Solar 页规范）。
        UpdateFsPerMonHint(on, canToggle);
    }

    /// <summary>
    /// 刷新全屏「按显示器生效」子开关的悬停提示。
    ///
    /// 规则（2026-09-20）：**门控满足 ⇒ 提示为空**（不该有提示）；
    /// 不满足 ⇒ 按**具体原因**拼接，多个原因用「，」分隔：
    ///   · 全屏暂停总开关没开 ⇒ `FsPerMonitorHintNoToggle`
    ///   · 独立控制没开       ⇒ `FsPerMonitorHint`
    ///   · 受控屏 &lt; 2        ⇒ `MonSingleDisplayLock`
    ///
    /// ⚠️ 三个判据必须**分开问**（原 `IsPerMonitorFeatureHostReady()` 是一个整体 bool，
    ///   说不出到底缺哪个）⇒ 为此在 `MainController` 加了 `HasTwoControlledDisplays()`。
    /// ⚠️ 挂在 row / label / toggle **三处**（与建页时一致）—— 子控件会吞掉父级悬停。
    /// </summary>
    private void UpdateFsPerMonHint(bool fullscreenOn, bool canToggle)
    {
        if (_fsPerMonTip == null || _fsPerMonRow == null
            || _fsPerMonToggle == null || _fsPerMonRow.IsDisposed) return;

        string hint = "";
        if (!canToggle)
        {
            var reasons = new List<string>();
            if (!fullscreenOn) reasons.Add(Localization.Get("FsPerMonitorHintNoToggle"));
            if (!(Program.Instance?.GetPerMonitorEnabled() ?? false))
                reasons.Add(Localization.Get("FsPerMonitorHint"));
            if (!(Program.Instance?.HasTwoControlledDisplays() ?? false))
                reasons.Add(Localization.Get("MonSingleDisplayLock"));
            hint = string.Join("，", reasons);
        }

        _fsPerMonHintText = hint;   // 供探针 Gen_FsPerMonHint 回读（同上）
        _fsPerMonTip.SetToolTip(_fsPerMonRow, hint);
        Label? lbl = FindRowLabel(_fsPerMonRow);
        if (lbl != null) _fsPerMonTip.SetToolTip(lbl, hint);
        _fsPerMonTip.SetToolTip(_fsPerMonToggle, hint);
    }

    /// <summary>
    /// 2026-09-19（F19 二修）：递归给树下**所有文本框**挂"程序性焦点取消全选"。
    ///
    /// 为什么改到**控件层**而不是窗体 Deactivate：
    ///   实测 Deactivate 订阅会丢（窗口被 RebuildUi 重建后订阅没了），用户反馈
    ///   "触发全选后再切走，输入框变光标闪烁待输入" ⇒ 焦点压根没被清掉。
    ///
    /// 机制：原生 EDIT 对"**程序设置的焦点**"（窗口激活时自动还给控件）的默认行为
    ///   就是全选整段；而**鼠标点击**获得的焦点不该被干预。
    ///   用 `Control.MouseButtons` 区分：左键没按下 ⇒ 不是用户在点 ⇒ 取消全选。
    /// </summary>
    private static void SuppressProgrammaticSelectAll(Control root)
    {
        if (root is TextBoxBase selfTb)
        {
            selfTb.GotFocus += (_, _) => ClearSelectIfProgrammatic(selfTb);
        }
        foreach (Control c in root.Controls)
        {
            SuppressProgrammaticSelectAll(c);
        }
    }

    private static void ClearSelectIfProgrammatic(TextBoxBase tb)
    {
        try
        {
            // 鼠标正在按 ⇒ 用户自己点的（保留正常行为）；否则是程序把焦点还回来的
            if ((Control.MouseButtons & MouseButtons.Left) != 0) return;
            if (tb.SelectionLength > 0 && tb.SelectionLength == tb.TextLength)
            {
                tb.SelectionLength = 0;
                tb.SelectionStart = tb.TextLength;   // 光标落到末尾，别停在开头
            }
        }
        catch { /* 忽略 */ }
    }

    /// <summary>把应用名转成可用作 ID 的片段（非字母数字归一为 '_'）。</summary>
    private static string SafeIdSegment(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "APP";
    }

    private Panel BuildAboutPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };


        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.1.0";

        var nameLabel = new Label
        {
            Text = "Gamma Brightness Tool",
            Font = UiFont(13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, (int)(20 * _dpiScale)),
            ForeColor = TextMain
        };
        page.Controls.Add(nameLabel);

        var descLabel = new Label
        {
            Text = Localization.Get("AboutDescription"),
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Width = (int)(380 * _dpiScale),
            Location = new Point(0, (int)(50 * _dpiScale)),
            ForeColor = TextDim
        };
        // Height grows with the wrapped text (long translations), and the
        // version/update controls below shift down accordingly.
        descLabel.Height = Math.Max((int)(26 * _dpiScale),
            TextRenderer.MeasureText(descLabel.Text, descLabel.Font,
                new Size(descLabel.Width, int.MaxValue), TextFormatFlags.WordBreak).Height);
        page.Controls.Add(descLabel);

        int versionY = descLabel.Bottom + (int)(8 * _dpiScale);
        var versionLabel = new Label
        {
            Text = $"{Localization.Get("AboutVersion")}: {version}",
            AutoSize = true,
            Location = new Point(0, versionY),
            ForeColor = TextSub
        };
        page.Controls.Add(versionLabel);

        // "Check for Updates" button: opens the GitHub releases page in the
        // default browser. No auto-update logic - just a shortcut.
        const string repoUrl = "https://github.com/miluxiansen/GammaBrightnessTool";
        var checkUpdateBtn = new RoundedButton
        {
            Text = Localization.Get("CheckUpdate"),
            AutoSize = false,
            Size = new Size((int)(120 * _dpiScale), (int)(32 * _dpiScale)),
            Location = new Point(0, versionLabel.Bottom + (int)(10 * _dpiScale))
        };
        checkUpdateBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        checkUpdateBtn.SetParentBackground(Bg); // About page background
        checkUpdateBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(repoUrl) { UseShellExecute = true });
            }
            catch
            {
                // No browser available - silently ignore.
            }
        };
        page.Controls.Add(checkUpdateBtn);

        // "Gitee" button: opens the Gitee mirror page in the default browser.
        const string giteeUrl = "https://gitee.com/mlxs008/gamma-brightness-tool";
        var giteeBtn = new RoundedButton
        {
            Text = Localization.Get("Gitee"),
            AutoSize = false,
            Size = new Size((int)(120 * _dpiScale), (int)(32 * _dpiScale)),
            Location = new Point(checkUpdateBtn.Right + (int)(10 * _dpiScale), checkUpdateBtn.Top)
        };
        giteeBtn.ApplyTheme(BgInner, TextMain, Border,
            ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
            ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));
        giteeBtn.SetParentBackground(Bg); // About page background
        giteeBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(giteeUrl) { UseShellExecute = true });
            }
            catch
            {
                // No browser available - silently ignore.
            }
        };
        page.Controls.Add(giteeBtn);

        // ---- P5：关于页稳定 ID（L0）+ 动作注册（L1）----
        // 两个按钮都会 Process.Start 打开浏览器（外部副作用）→ 标为破坏性。
        AutomationBridge.WireDestructive(
            (checkUpdateBtn, "Abt_CheckUpdateBtn"),
            (giteeBtn, "Abt_GiteeBtn"));

        return page;
    }

    /// <summary>点击导航条目：切到对应页面并刷新高亮。</summary>
    private void SelectNav(int idx)
    {
        if (idx < 0 || idx >= _navItems.Count) return;
        if (_contentPanel == null || _contentPanel.IsDisposed) return;
        _navSelectedIndex = idx;

        Control target = idx switch
        {
            0 => _generalPage,
            1 => _brightnessPage,
            2 => _colorTempPage,
            3 => _solarPage,
            4 => _hotkeysPage,
            5 => _monitorsPage,
            6 => _appWhitelistPage,
            7 => _aboutPage,
            _ => null!
        };
        if (target == null) return;

        // Clicking the already-selected item would clear and re-add the
        // panel, producing a visible flicker. Skip when the target page is
        // already showing.
        if (_contentPanel.Controls.Count == 1 && ReferenceEquals(_contentPanel.Controls[0], target))
        {
            UpdateNavSelection();
            return;
        }

        _contentPanel.Controls.Clear();
        _contentPanel.Controls.Add(target);
        UpdateNavSelection();
    }

    /// <summary>按当前选中/悬停状态刷新全部导航条目的外观（选中底色+左侧强调条由自绘处理）。</summary>
    private void UpdateNavSelection()
    {
        for (int i = 0; i < _navItems.Count; i++)
        {
            bool sel = i == _navSelectedIndex;
            _navItems[i].ForeColor = sel ? TextMain : TextSub;
            _navItems[i].BackColor = sel ? BgNavSelected : BgNav;
        }
    }

    /// <summary>悬停高亮：鼠标所在项用略浅底色，其余回落到选中/普通态。</summary>
    private void UpdateNavHover(int idx, bool hover)
    {
        if (idx < 0 || idx >= _navItems.Count) return;
        if (hover)
        {
            _navItems[idx].BackColor = BgNavSelected;
        }
        else
        {
            _navItems[idx].BackColor = idx == _navSelectedIndex ? BgNavSelected : BgNav;
        }
    }

    /// <summary>主题切换后刷新导航条目配色（Label 导航无滚动条，仅需重绘外观）。</summary>
    private void RefreshNavAppearance()
    {
        _navPanel.BackColor = BgNav;
        for (int i = 0; i < _navItems.Count; i++)
        {
            _navItems[i].BackColor = i == _navSelectedIndex ? BgNavSelected : BgNav;
            _navItems[i].ForeColor = i == _navSelectedIndex ? TextMain : TextSub;
            _navItems[i].Invalidate();
        }
    }

    /// <summary>
    /// 用显式坐标定位全部导航条目（x=0, y=索引×行高, 宽=面板宽, 高=行高）。
    /// 不依赖 Dock/z 序 → 无论 DPI 重建、句柄重建如何改动 Controls 集合，
    /// 条目顺序（通用设置在上、版本信息在下）永不反转。
    /// </summary>
    private void LayoutNavItems(int? itemH = null)
    {
        if (_navPanel == null || _navPanel.IsDisposed) return;
        int h = itemH ?? Math.Max(16, (int)(40 * _dpiScale));
        int panelW = _navPanel.Width;
        for (int i = 0; i < _navItems.Count; i++)
        {
            var it = _navItems[i];
            it.Location = new Point(0, i * h);
            it.Size = new Size(panelW, h);
        }
    }

    // ------------------------------------------------------------------
    // ⛔ B'（2026-09-22）：「字体补丁三套」已全部删除。
    //
    // 原三套补丁（`AttachFontFix` / `KeepFontFixed` / `RebuildSkeletonFonts` 里的
    // 「拿旧 pt 值重建」逻辑）存在的前提是：**字体是 Point 单位**，于是 WinForms
    // 在 DPI 变化时会按 `newDpi/oldDpi` 隐式改 pt（`GetScaledFont` →
    // `SetScaledFont`），而「改 pt × 旧烘焙 DPI」在不同控件类型上并不一致
    // （ComboBox 没有该回调；放大方向整体不生效）⇒ 必然出现"有的字跟、有的字不跟"。
    //
    // B' 起所有 UI 字体一律 `GraphicsUnit.Pixel`（见 `UiFont`），**WinForms 不会再
    // 隐式改动它们** ⇒ 三套补丁既无用、又会与 px 语义冲突（它们比的是 pt 语义的
    // `.Size`），因此：
    //   · `KeepFontFixed(Control, Font)`   —— 已删（原 5520 附近）
    //   · `AttachFontFix(Control)`         —— 已删（原 5538 附近）
    //   · 两处 `FontChanged += ...` 钩子    —— 已删
    //   · `RebuildSkeletonFonts`           —— 保留但重写为「按 `_dpiScale` 重新
    //                                        赋 px 字体」，不再是"拉回 pt"补丁
    //   · `NavFixedFont` / `TitleFixedFont` —— 已删（静态字段拿不到 `_dpiScale`）
    //
    // 今后新增控件**不需要**任何字体补丁：用 `UiFont(...)` 建字体即可。
    // ------------------------------------------------------------------

    // `AttachFontFix(Control)` 已删除（B'，见上方说明）。

    /// <summary>当前显示页（_contentPanel.Controls[0]）的滚动偏移。</summary>
    private int GetCurrentPageScroll()
    {
        if (_contentPanel == null || _contentPanel.Controls.Count == 0) return 0;
        return (_contentPanel.Controls[0] as ThemeScrollPanel)?.ScrollPosition ?? 0;
    }

    /// <summary>设置当前显示页的滚动偏移（用于重建/重启后恢复）。</summary>
    private void SetCurrentPageScroll(int scroll)
    {
        if (_contentPanel == null || _contentPanel.Controls.Count == 0) return;
        if (_contentPanel.Controls[0] is ThemeScrollPanel sp) sp.ScrollPosition = scroll;
    }

    /// <summary>自动重启前把"设置窗开着 + 当前页 + 滚动位置"落盘，供新进程恢复。</summary>
    public static void SaveStateForRestart()
    {
        try
        {
            var inst = _instance;
            if (inst == null || inst.IsDisposed) return;
            int scroll = inst.GetCurrentPageScroll();
            File.WriteAllText(RestartStatePath,
                $"{{\"nav\":{inst._navSelectedIndex},\"scroll\":{scroll}}}");
        }
        catch { /* 保存失败不影响重启 */ }
    }

    /// <summary>
    /// F24-a（2026-09-22）：自动重启**交接期**先把设置窗藏起来。
    ///
    /// 旧进程不再立刻 `Application.Exit()`，而是要持续重申 ramp ≈0.45 s
    /// （见 `Program.StartHandoverKeepAlive`）。这段时间它已经被新进程接管的准备阶段覆盖，
    /// 若旧窗还留在屏幕上，会与新进程弹出的窗口叠在一起闪一下 ⇒ 先隐藏。
    /// ⛔ 只 `Hide()`，不 `Close()`：不触发任何清理，进程随后由新进程 singleton kill。
    /// 尽力而为，例外一律吞掉（在重启流程内，绝不能打断交接）。
    /// </summary>
    public static void HideForRestart()
    {
        try
        {
            var inst = _instance;
            if (inst == null || inst.IsDisposed) return;
            inst.Hide();
        }
        catch { /* 隐藏失败不影响重启 */ }
    }

    /// <summary>进程重启后恢复：读状态文件 → 切回原页 → 恢复滚动 → 删除文件。</summary>
    private void TryRestoreRestartState()
    {
        try
        {
            if (!File.Exists(RestartStatePath)) return;
            string json = File.ReadAllText(RestartStatePath);
            File.Delete(RestartStatePath);
            int nav = -1, scroll = 0;
            // 极简解析（仅两个 int 字段）
            var mNav = System.Text.RegularExpressions.Regex.Match(json, "\"nav\":(-?\\d+)");
            var mScr = System.Text.RegularExpressions.Regex.Match(json, "\"scroll\":(-?\\d+)");
            if (mNav.Success) int.TryParse(mNav.Groups[1].Value, out nav);
            if (mScr.Success) int.TryParse(mScr.Groups[1].Value, out scroll);
            if (nav >= 0 && nav < _navItems.Count)
            {
                SelectNav(nav);
                // 恢复滚动：等页面在消息队列中完成布局（_maxScroll 有效）后再设值。
                if (scroll > 0)
                {
                    int s = scroll;
                    BeginInvoke((Action)(() => SetCurrentPageScroll(s)));
                }
            }
        }
        catch { /* 恢复失败保持默认页 */ }
    }

    private static string RestartStatePath =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GBT_restart_state.json");

    /// <summary>
    /// Program.Instance.BrightnessChanged 单次挂载处理（构造时订阅、关闭时退订）。
    /// 经 _refresh* 字段转发到"当前构建"的页内刷新逻辑，避免持有已 Dispose 的旧控件。
    /// </summary>
    private void OnProgramBrightnessChanged(object? sender, float value)
    {
        if (IsDisposed) return;
        _refreshLevelSelection?.Invoke();
        _refreshLevelDisplay?.Invoke();
    }

    /// <summary>Program.Instance.TemperatureChanged 单次挂载处理（见 OnProgramBrightnessChanged）。</summary>
    private void OnProgramTemperatureChanged(object? sender, float value)
    {
        if (IsDisposed) return;
        _refreshPresetSelection?.Invoke();
        _refreshPresetDisplay?.Invoke();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_disableUiTimer == null)
        {
            _disableUiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _disableUiTimer.Tick += (_, _) => UpdateDisableLock();
            _disableUiTimer.Start();
        }
        EnsureNavNoScroll();
        BeginInvoke((Action)EnsureNavNoScroll);   // Dock 布局定型后再校正一次
        TryRestoreRestartState();                 // 自动重启后恢复原页与滚动
        // 探针（只读）：首帧定格后全量转储一次骨架 + 当前页字体
        DpiProbe("OnShown.form", this);
        DpiProbe("OnShown.nav0", _navItems.Count > 0 ? _navItems[0] : null);
        DpiProbe("OnShown.title", _titleLabel);
        DpiProbe("OnShown.version", _versionLabel);
        if (_contentPanel != null && _contentPanel.Controls.Count > 0)
            DpiDumpTree("OnShown.page", _contentPanel.Controls[0]);
        UpdateDisableLock();
    }

    /// <summary>
    /// 导航为自绘 Label（无 ListBox → 无滚动条）。显式布局下条目永按索引排布；
    /// 仅当 7 项总高超过面板可视高（极小窗口）时压缩行高以全部容纳。
    /// </summary>
    private void EnsureNavNoScroll()
    {
        if (_navPanel == null || _navPanel.IsDisposed) return;
        if (_navItems.Count == 0) return;
        int clientH = _navPanel.ClientSize.Height;
        if (clientH <= 0) return;
        int targetH = Math.Max(16, (int)(40 * _dpiScale));
        if (_navItems.Count * targetH <= clientH)
        {
            LayoutNavItems(targetH);   // 放得下：设计行高，按索引定位
        }
        else
        {
            int fitted = Math.Max(16, (clientH - 4) / _navItems.Count);
            LayoutNavItems(fitted);    // 放不下：压缩行高恰好容纳
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _instance = null;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Safety net: if the window closed while a hotkey box was still
        // recording, the suspended group would otherwise stay disabled
        // until the app restarts.
        Program.Instance?.ResumeAllHotKeys();
        if (Program.Instance != null)
        {
            Program.Instance.BrightnessChanged -= OnProgramBrightnessChanged;
            Program.Instance.TemperatureChanged -= OnProgramTemperatureChanged;
        }
        Localization.LanguageChanged -= OnLanguageChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        if (Program.Instance != null)
        {
            Program.Instance.TrayVisibilityLadderFinished -= OnTrayVisLadderFinished;
        }
        _trayVisibleToggle = null;
        _dpiDebounce?.Stop();
        _dpiDebounce?.Dispose();
        _dpiDebounce = null;
        _disableUiTimer?.Stop();
        _disableUiTimer?.Dispose();
        _disableUiTimer = null;
        _rebuildDebounce?.Dispose();
        _rebuildDebounce = null;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _windowIcon?.Dispose();
        _pinToolTip?.Dispose();
        _pinToolTip = null;
        _selfHealTip?.Dispose();
        _selfHealTip = null;
        _fullscreenTip?.Dispose();
        _fullscreenTip = null;
        // 2026-09-19 新增的三个 ToolTip（全屏逐屏 / 显示器页单屏锁定 / 时间调整页滑轨锁定）
        _fsPerMonTip?.Dispose();
        _fsPerMonTip = null;
        _monitorsTip?.Dispose();
        _monitorsTip = null;
        _solarTip?.Dispose();
        _solarTip = null;
        _trayVisTip?.Dispose();
        _trayVisTip = null;
        _windowIcon = null;

        // P5：摘掉本窗体的动作注册 —— 窗体现在即将 Dispose，留着会让 Win_* / Nav_* /
        // 各页面域的 ID 指向已释放控件（脚本再发 ui.click 会抛 ObjectDisposedException）。
        // 下次 ShowOrActivate() 新建窗体时会重新注册。
        AutomationBridge.ClearActionsByPrefix("Win_");
        AutomationBridge.ClearActionsByPrefix("Nav_");
        foreach (string prefix in PageActionPrefixes)
        {
            AutomationBridge.ClearActionsByPrefix(prefix);
        }

        _instance = null;
        base.OnFormClosed(e);
    }
}
