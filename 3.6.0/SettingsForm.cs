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
    // 导航栏/标题栏/版本标签的固定字体（Point 单位）。DPI 变化时 WinForms 会
    // 缩放显式设置的 Point 字体（GetScaledFont），导致这几个区域文字随 DPI
    // 变大/变小；FontChanged 处理器把字体拉回这些固定实例，与选项内容一致。
    private static readonly Font NavFixedFont = new Font("Segoe UI", 10F);
    private static readonly Font TitleFixedFont = new Font("Segoe UI", 9F);
    private Panel _generalPage;
    private Panel _brightnessPage;
    private Panel _colorTempPage;
    private Panel _solarPage;
    private Panel _hotkeysPage;
    private Panel _aboutPage;
    private Panel _monitorsPage;   // 3.6.0 第 7 页：显示器
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
    /// <summary>最近一次自动重开的时间（防 DisplaySettingsChanged 广播多次导致连环重启）。</summary>
    private static DateTime _lastRelaunchUtc = DateTime.MinValue;

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
    private ToolTip? _pinToolTip;                // pin tooltip (localized, kept alive)
    private ToolTip? _selfHealTip;               // gamma self-heal row tooltip
    private ToolTip? _fullscreenTip;             // fullscreen pause row tooltip
    private Icon? _windowIcon;                   // taskbar button icon (borderless)
    private static SettingsForm? _instance;

    /// <summary>设置窗当前是否打开（进程自动重启时据此决定是否带 --show-settings 恢复）。</summary>
    public static bool IsOpen => _instance != null && !_instance.IsDisposed && _instance.Visible;

    /// <summary>
    /// Shows the single settings window (or activates it if already open).
    /// Non-modal: the tray stays fully usable while it is open.
    /// </summary>
    public static void ShowOrActivate()
    {
        if (_instance == null || _instance.IsDisposed)
        {
            _instance = new SettingsForm();
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
        }
        else
        {
            if (_instance.WindowState == FormWindowState.Minimized)
            {
                _instance.WindowState = FormWindowState.Normal;
            }
            _instance.Activate();
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
        // 置顶 (always-on-top) is user-controlled from the 通用设置 page so
        // it can be toggled for testing without covering other windows.
        TopMost = Program.Instance?.GetTopMost() ?? false;
        // Manual DPI scaling: AutoScaleMode.Dpi only scales fonts and
        // auto-layout, NOT fixed sizes (ToggleSwitch 44x22, row heights...).
        // At 175% the fixed-height switch stayed 22px while the label font
        // grew, so the state text (开/关) was clipped at the bottom and the
        // 14pt title overflowed into the row below. We scale every fixed
        // dimension ourselves with _dpiScale and use scaled fonts.
        _dpiScale = DeviceDpi / 96f;
        // Classic 400px height: extra settings rows scroll inside the slim
        // ThemeScrollPanel instead of growing the window.
        ClientSize = new Size((int)(560 * _dpiScale), (int)(400 * _dpiScale) + _titleBarH);
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9F);

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
        string[] navKeys =
        {
            "SettingsGeneral", "SettingsBrightness", "SettingsColorTemp",
            "SolarAdjust", "SettingsHotkeys", "SettingsMonitors", "SettingsAbout"
        };
        int itemH = (int)(40 * _dpiScale);
        for (int i = 0; i < navKeys.Length; i++)
        {
            int idx = i; // 闭包捕获
            var item = new Label
            {
                // 不用 Dock.Top：其布局依赖 Controls 集合 z 序，DPI/重建触发布局
                // 时 z 序被改动会把导航顺序反转（版本跑到最顶）。改用显式坐标，
                // 由 LayoutNavItems() 统一按索引定位，顺序永不依赖 z 序。
                Location = new Point(0, i * itemH),
                Size = new Size(_navPanel.Width, itemH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Text = Localization.Get(navKeys[idx]),
                Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
            ForeColor = TextMain,
            BackColor = BgNav,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        // Position inside the sidebar footer area (nav width x bottom strip).
        // ClientSize now includes the self-drawn title bar height, so the
        // version tag anchors to the bottom of the whole client area (below
        // the nav list, which fills down to the content panel bottom).
        _versionLabel.Location = new Point((int)(8 * _dpiScale), ClientSize.Height - (int)(26 * _dpiScale));
        _versionLabel.FontChanged += (_, _) => KeepFontFixed(_versionLabel, NavFixedFont);
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
        AttachFontFix(this); // 递归修复全部控件字体，防 DPI 缩放

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
            Font = new Font("Segoe UI", 9F),
            ForeColor = TextMain,
            BackColor = BgNav,
            Dock = DockStyle.Fill,
            Padding = new Padding((int)(12 * _dpiScale), 0, 0, 0)
        };
        _titleBar.Controls.Add(_titleLabel);
        var titleLabel = _titleLabel; // 刚赋值非 null
        titleLabel.FontChanged += (_, _) => KeepFontFixed(titleLabel, TitleFixedFont);

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
            Font = new Font("Segoe UI", 10F),
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
        foreach (var page in new[] { _generalPage, _brightnessPage, _hotkeysPage, _aboutPage, _colorTempPage, _solarPage, _monitorsPage })
        {
            if (page == null) continue;
            page.BackColor = Bg;  // page root uses the page background
            foreach (Control child in page.Controls)
                RefreshTheme(child, Bg, BgInner, Border,
                             TextMain, Track, Thumb, ThumbHover, InputBg);
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

    /// <summary>
    /// 运行中 DPI 变化 → 销毁当前实例并按新 DPI 重建同状态窗口（等效"关闭重开"）。
    /// 保留当前页与窗口位置/置顶状态，尽量无缝。
    /// </summary>
    private void RelaunchAfterDpiChange()
    {
        // 防连环重启：DisplaySettingsChanged 在改缩放后会广播多次，且新窗构造时
        // 若事件再次到达会二次重启。距上次成功重开 1.5s 内直接忽略。
        if ((DateTime.UtcNow - _lastRelaunchUtc).TotalMilliseconds < 1500) return;
        if (IsDisposed || _instance != this || !Visible) return;
        DpiTrace("Relaunch: begin");
        try
        {
            int nav = _navSelectedIndex;
            var loc = Location;
            bool pin = TopMost;
            _instance = null;            // Close 前先摘除单例引用（OnFormClosing 也会置 null）
            Close();                     // 触发 OnFormClosed：解绑事件/停 timer/清 _instance
            var f = new SettingsForm();  // 按当前(新) DPI 全新构造（新实例，不受 this 已关闭影响）
            // 关键：new 时句柄未创建，ctor 里 DeviceDpi 可能读到旧值（如 168），
            // 导致新窗仍按旧 DPI 布局（窗口 980px、文字 23px）。这里按变更后的目标
            // DPI 强制重排一次：有 DpiChanged 参数用参数（PMv2 路径）；SystemAware
            // 下由 DisplaySettingsChanged 触发、_pendingDpi 未设置 → 用当前 DeviceDpi
            // （此时系统 DPI 已更新，返回新值）。
            int target = _pendingDpi > 0 ? _pendingDpi : DeviceDpi;
            f.RelayoutForCurrentDpi(target);
            f.Location = loc;
            f.TopMost = pin;
            // 同 ShowOrActivate：透明首帧防"系统模式底色"闪现后再置不透明。
            f.Opacity = 0;
            f.Show();
            f.Update();
            f.Opacity = 1.0;
            if (nav >= 0 && nav < f._navItems.Count) { f._navSelectedIndex = nav; f.SelectNav(nav); }
            f.EnsureNavNoScroll();       // Show 后布局已定型，再校正一次行高
            _instance = f;
            _lastRelaunchUtc = DateTime.UtcNow;
            DpiTrace($"Relaunch: done (new instance shown, dpiScale={f._dpiScale:0.###})");
        }
        catch (Exception ex)
        {
            DpiTrace("Relaunch EXCEPTION: " + ex);
            // 重开失败不崩溃：保持可手动重开
            System.Diagnostics.Debug.WriteLine("RelaunchAfterDpiChange: " + ex);
        }
    }

    /// <summary>DPI 诊断（仅写 %TEMP%，不改任何行为；用于确认 DpiChanged/重开是否真的触发）。</summary>
    private static void DpiTrace(string msg)
    {
        try
        {
            var p = Path.Combine(Path.GetTempPath(), "GBT_dpi.log");
            File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 把骨架控件（导航/版本标签/标题栏/窗体）的 Font 全部换成同名 size 的新实例。
    /// KeepFontFixed / AttachFontFix 只在字号(size/unit)被改时才拉回，同名新实例放行，
    /// 因此赋值即时生效——新实例的 GDI 句柄在首次绘制时按当前 DPI 生成。
    /// 注意：这里只换新实例、**绝不主动 Dispose 旧实例**——WinForms 在控件句柄创建
    /// （OnHandleCreated → SetWindowFont → ToHfont）时会用到控件当前的 Font，若该
    /// Font 已被 Dispose 会抛 "Parameter is not valid" 崩溃（Relayout 在 Show 之前
    /// 执行、句柄延迟创建时极易踩中）。旧字体对象交给 GC 释放，代价远小于崩溃。
    /// </summary>
    private void RebuildSkeletonFonts()
    {
        Font Apply(Control c, Font next)
        {
            c.Font = next;
            return next;
        }

        var navFont = new Font("Segoe UI", NavFixedFont.SizeInPoints);
        _navPanel.Font = navFont;
        for (int i = 0; i < _navItems.Count; i++) _navItems[i].Font = navFont;
        Apply(_versionLabel, navFont);
        if (_titleLabel != null)
            Apply(_titleLabel, new Font("Segoe UI", TitleFixedFont.SizeInPoints));
        var captionFont = new Font("Segoe UI", 10F);
        if (_btnPin != null) Apply(_btnPin, captionFont);
        if (_btnMin != null) Apply(_btnMin, captionFont);
        if (_btnClose != null) Apply(_btnClose, captionFont);
        Font = new Font("Segoe UI", 9F);
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

        // Detach current pages, then rebuild everything with the new language.
        _contentPanel.Controls.Clear();
        string[] navKeys =
        {
            "SettingsGeneral", "SettingsBrightness", "SettingsColorTemp",
            "SolarAdjust", "SettingsHotkeys", "SettingsMonitors", "SettingsAbout"
        };
        for (int i = 0; i < _navItems.Count && i < navKeys.Length; i++)
        {
            _navItems[i].Text = Localization.Get(navKeys[i]);
        }

        _generalPage?.Dispose();
        _brightnessPage?.Dispose();
        _colorTempPage?.Dispose();
        _solarPage?.Dispose();
        _hotkeysPage?.Dispose();
        _aboutPage?.Dispose();
        _monitorsPage?.Dispose();   // 3.6.0 第 7 页

        _generalPage = BuildGeneralPage();
        _brightnessPage = BuildBrightnessPage();
        _colorTempPage = BuildColorTempPage();
        _solarPage = BuildSolarPage();
        _hotkeysPage = BuildHotkeysPage();
        _aboutPage = BuildAboutPage();
        _monitorsPage = BuildMonitorsPage();   // 3.6.0 第 7 页

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
        AttachFontFix(this); // 页面重建后重新挂载字体修复
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
        // ---- Setting row 1: 开机自启 (startup with Windows) ----
        // All children use Dock=Top so every row's width exactly matches the
        // page width at any DPI. A fixed-width row would overflow the page
        // (the content area is only ~372px wide at 100%, ~558px at 150% DPI)
        // and Windows clips HWND children to the parent's client area,
        // cutting off the right border.
        var startupToggle = new ToggleSwitch
        {
            Checked = StartupManager.IsStartupEnabled()
        };
        startupToggle.ApplyDpiScale(_dpiScale);
        startupToggle.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupManager.SetStartup(startupToggle.Checked);
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
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
            Checked = Program.Instance?.GetPauseInFullscreenEnabled() ?? true
        };
        fullscreenToggle.ApplyDpiScale(_dpiScale);
        fullscreenToggle.CheckedChanged += (_, _) =>
            Program.Instance?.SetPauseInFullscreenEnabled(fullscreenToggle.Checked);
        var fullscreenGroup = BuildToggleGroup(fullscreenToggle);
        var fullscreenRow = BuildSettingRow(Localization.Get("PauseInFullscreen"), fullscreenGroup);
        _fullscreenTip?.Dispose();
        _fullscreenTip = new ToolTip();
        _fullscreenTip.SetToolTip(fullscreenRow, Localization.Get("PauseInFullscreenHint"));

        // ---- Setting row 8c: 导入/导出设置（一行两按钮）----
        var exportBtn = new RoundedButton
        {
            Text = Localization.Get("ExportSettings"),
            Font = new Font("Segoe UI", 9F),
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
            Font = new Font("Segoe UI", 9F),
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
            Font = new Font("Segoe UI", 9F),
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

        selfHealRow.Dock = DockStyle.Top;
        scroll.Controls.Add(selfHealRow);

        invertRow.Dock = DockStyle.Top;
        scroll.Controls.Add(invertRow);

        wheelRow.Dock = DockStyle.Top;
        scroll.Controls.Add(wheelRow);

        // 禁用行：浮窗主题正下方（Dock 逆序，popupTheme 之前 add 则显示在其下方）。
        disableRow.Dock = DockStyle.Top;
        scroll.Controls.Add(disableRow);

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
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

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
    /// <summary>
    /// Shrinks a label's font (down to 6pt) so its text fits within the
    /// given width/height. Long translations (German, Russian, French, ...)
    /// wrap across lines and the font shrinks only when the wrapped text
    /// would exceed the available height. Returns the chosen font size.
    /// </summary>
    private static float FitLabelFont(string text, Font font, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return 10f;
        // Always measure from the base size, never from the current
        // (possibly already-shrunk) font, so a label can grow back.
        float size = 10f;
        while (size > 6f)
        {
            using var probe = new Font(font.FontFamily, size);
            var sz = TextRenderer.MeasureText(text, probe, new Size(maxWidth, int.MaxValue),
                TextFormatFlags.WordBreak);
            if (sz.Height <= maxHeight) return size;
            size -= 0.5f;
        }
        return 6f;
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
            Tag = "dynamic",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 10F),
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
                stateLabel.Font = new Font(old.FontFamily, size);
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
            Font = new Font("Segoe UI", 10F),
            Tag = "dynamic",
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
                label.Font = new Font(old.FontFamily, size);
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 9F),
            TextAlign = HorizontalAlignment.Center,
            Text = ((int)(Program.Instance?.GetMinTemperature() ?? GammaController.MIN_TEMPERATURE)).ToString()
        };
        minBox.ApplyTheme(InputBg, TextMain);
        minBox.SetParentBackground(BgInner);
        var maxBox = new RoundedTextBox
        {
            Width = (int)(48 * _dpiScale),
            Height = (int)(26 * _dpiScale),
            Font = new Font("Segoe UI", 9F),
            TextAlign = HorizontalAlignment.Center,
            Text = ((int)(Program.Instance?.GetMaxTemperature() ?? GammaController.MAX_TEMPERATURE)).ToString()
        };
        maxBox.ApplyTheme(InputBg, TextMain);
        maxBox.SetParentBackground(BgInner);
        var tildeLabel = new ThemedLabel
        {
            Text = "~",
            Font = new Font("Segoe UI", 10F),
            ForeColor = TextSub,
            AutoSize = false,
            Height = (int)(20 * _dpiScale),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var rangeConfirmBtn = new RoundedButton
        {
            Text = "\u221A",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
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
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

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
            inst?.SetSolarAdjustEnabled(solarToggle.Checked);
            updateSolarState?.Invoke();
        };
        var solarGroup = BuildToggleGroup(solarToggle);
        var solarRow = BuildSettingRow(Localization.Get("SolarAdjustEnabled"), solarGroup);


        // ---- 模式下拉：手动 / 物理位置 ----
        var modeCombo = new ThemedComboBox
        {
            Font = new Font("Segoe UI", 10F),
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
        var sunrisePanel = BuildTimeInputBox(inst?.GetManualSunriseMinutes() ?? 440,
            () => inst?.GetManualSunriseMinutes() ?? 440, v => inst?.SetManualSunriseMinutes(v));
        var sunsetPanel = BuildTimeInputBox(inst?.GetManualSunsetMinutes() ?? 990,
            () => inst?.GetManualSunsetMinutes() ?? 990, v => inst?.SetManualSunsetMinutes(v));
        var sunriseRow = BuildSettingRow(Localization.Get("SolarManualSunrise"), sunrisePanel);
        var sunsetRow = BuildSettingRow(Localization.Get("SolarManualSunset"), sunsetPanel);

        // ---- 物理位置（获取位置按钮 + 坐标显示）----
        var getLocBtn = new RoundedButton
        {
            Text = Localization.Get("SolarGetLocation"),
            Font = new Font("Segoe UI", 9F),
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
            Font = new Font("Segoe UI", 9F),
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

        updateSolarState = () =>
        {
            bool on = solarOn();
            bool manual = manualMode();
            bool tempOn = tempEnabled();
            modeRow.Enabled = on;
            modeCombo.Enabled = on;
            sunriseRow.Enabled = on && manual;
            sunsetRow.Enabled = on && manual;
            sunrisePanel.Enabled = on && manual;
            sunsetPanel.Enabled = on && manual;
            getLocRow.Enabled = on && !manual;
            locationRow.Enabled = on && !manual;
            // 色温总开关关闭时两色温滑块禁用（只调亮度）。
            dayTempRow.Enabled = on && tempOn;
            nightTempRow.Enabled = on && tempOn;
            dayBrightRow.Enabled = on;
            nightBrightRow.Enabled = on;
            transitionRow.Enabled = on;
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
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

        _refreshSolarState = updateSolarState;
        updateSolarState();
        return page;
    }

    /// <summary>
    /// 手动时间输入：[时][:][分] + 右侧确认按钮。两个数字框各最多 2 位，
    /// 只接受数字；时 0~23、分 0~59；个位数自动补前导 0。
    /// 回车与确认按钮等效；失焦（未按确认）放弃编辑恢复保存值。
    /// </summary>
    private Panel BuildTimeInputBox(int minutes, Func<int> getter, Action<int> setter)
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
            Font = new Font("Segoe UI", 9F),
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
            Font = new Font("Segoe UI", 9F),
            TextAlign = HorizontalAlignment.Center,
            MaxLength = 2,
            Text = (total % 60).ToString("00")
        };
        minuteBox.ApplyTheme(InputBg, TextMain);
        minuteBox.SetParentBackground(BgInner);

        var colon = new ThemedLabel
        {
            Text = ":",
            Font = new Font("Segoe UI", 10F),
            ForeColor = TextSub,
            AutoSize = false,
            Width = (int)(10 * _dpiScale),
            Height = (int)(20 * _dpiScale),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var confirmBtn = new RoundedButton
        {
            Text = "\u221A",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
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
            Font = new Font("Segoe UI", 8F),
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
            Font = new Font("Segoe UI", 8F),
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
        bool locked = Program.Instance?.IsDisableActive() ?? false;
        if (locked != _disableLockActive)
        {
            _disableLockActive = locked;
            foreach (var (ctrl, restore) in _disableLocked)
                ctrl.Enabled = locked ? false : restore();
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

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
            v => Program.Instance?.SetIncreaseBrightnessHotKeyEnabled(v));
        incCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incCapture);

        // ---- 降低亮度 hotkey row ----
        var decCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseBrightness"),
            Program.Instance?.GetDecreaseBrightnessHotKey() ?? "",
            v => Program.Instance?.SetDecreaseBrightnessHotKey(v) == true,
            Program.Instance?.GetDecreaseBrightnessHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseBrightnessHotKeyEnabled(v));
        decCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(decCapture);

        // ---- 熄屏 hotkey row ----
        var powerOffCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyPowerOff"),
            Program.Instance?.GetPowerOffHotKey() ?? "",
            v => Program.Instance?.SetPowerOffHotKey(v) == true,
            Program.Instance?.GetPowerOffHotKeyEnabled() ?? true,
            v => Program.Instance?.SetPowerOffHotKeyEnabled(v));
        powerOffCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(powerOffCapture);

        // ---- 增加色温 hotkey row ----
        var incTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyIncreaseTemperature"),
            Program.Instance?.GetIncreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetIncreaseTemperatureHotKey(v) == true,
            Program.Instance?.GetIncreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetIncreaseTemperatureHotKeyEnabled(v),
            true);
        incTempCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incTempCapture);

        // ---- 降低色温 hotkey row ----
        var decTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseTemperature"),
            Program.Instance?.GetDecreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetDecreaseTemperatureHotKey(v) == true,
            Program.Instance?.GetDecreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseTemperatureHotKeyEnabled(v),
            true);
        decTempCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(decTempCapture);

        // ---- 一键清除 (clear all hotkeys) ----
        var clearBtn = new RoundedButton
        {
            Text = Localization.Get("ClearAllHotkeys"),
            Font = new Font("Segoe UI", 9F),
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
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = (int)(36 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        scroll.Controls.Add(title);

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
        bool enabled, Action<bool>? setEnabled, bool isTempHotkey = false)
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
            Tag = "dynamic",
            Font = new Font("Segoe UI", 10F),
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
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
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
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
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
                label.Font = new Font(oldLabelFont.FontFamily, labelSize);
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

        return row;
    }

    private Panel BuildPlaceholderPage(string text)
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextDim,
            Font = new Font("Segoe UI", 10F)
        };
        page.Controls.Add(label);

        return page;
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
        _perMonitorToggle = new ToggleSwitch
        {
            Checked = controller?.GetPerMonitorEnabled() ?? false
        };
        _perMonitorToggle.ApplyDpiScale(_dpiScale);
        _perMonitorToggle.CheckedChanged += (_, _) =>
        {
            controller?.SetPerMonitorEnabled(_perMonitorToggle.Checked);
            // 总开关切换：同步受控显示器子开关（关→锁死+保持持久值；开→恢复）
            SyncMonitorSubToggles();
        };
        var perMonitorToggle = _perMonitorToggle;
        var perMonitorGroup = BuildToggleGroup(perMonitorToggle);
        var perMonitorRow = BuildSettingRow(Localization.Get("MonitorsPerMonitor"), perMonitorGroup);

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
        var controlledBody = BuildControlledMonitorsBody(displayIds, controller);
        controlledHeader.ExpandedChanged += expanded => SetFoldBody(controlledBody, expanded, scroll);

        // 页标题（置于滚动区最顶部）：不显式设 BackColor（保持 Color.Transparent
        // 继承父 _content 背景），主题切换时 _content.BackColor 由 RefreshTheme 更新，
        // 标题随之同步变深/浅；显式 BackColor=Bg 会停在构造时主题色不变。
        // 字号/高度与通用设置/亮度设置等页标题一致（14F Bold / 36*dpi）。
        var pageTitle = new Label
        {
            Text = Localization.Get("MonitorsSettings"),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
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
                Font = new Font("Segoe UI", 10F),
                Text = currentName,
                // 与快捷键页 HotKeyCaptureBox 一致：文字水平居中，避免短名称
                // 在宽框里贴左。TextBox 默认 AutoSize=true，实际高度由字体决定
                // （10F≈32px@175%），下方布局按实际高垂直居中，不设 Height。
                TextAlign = HorizontalAlignment.Center
            };
            editBox.ApplyTheme(InputBg, TextMain);
            editBox.SetParentBackground(BgInner);

            var saveBtn = new RoundedButton
            {
                Text = Localization.Get("MonitorsRenameBtn"),
                Width = (int)(56 * _dpiScale),
                Height = (int)(26 * _dpiScale),
                Font = new Font("Segoe UI", 9F),
            };
            saveBtn.ApplyTheme(Bg, TextMain, Border,
                ThemeManager.IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251),
                ThemeManager.IsDark ? Color.FromArgb(57, 57, 66) : Color.FromArgb(192, 208, 228));

            saveBtn.Click += (_, _) =>
            {
                controller?.SetDisplayName(id, editBox.Text);
            };

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
            string name = controller?.GetDisplaySystemName(id) ?? id;

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

            var infoLabel = new ThemedLabel
            {
                Text = info,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = TextSub,
                Width = (int)(230 * _dpiScale)
            };

            var row = BuildSettingRow(name, infoLabel);
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
                Font = new Font("Segoe UI", 10F),
                // dynamic：与设置行标题同一自适应策略。DPI 变化时 WinForms 会把
                // 显式 Point 字体 GetScaledFont 缩放；设置行（Tag=dynamic）靠
                // Layout 里 FitLabelFont 拉回，而这里若不标 dynamic 会被
                // AttachFontFix 焊死，导致折叠标题与行文字在 DPI 变更后不一致。
                Tag = "dynamic",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMain,
                Cursor = Cursors.Hand
            };
            _arrow = new Label
            {
                Text = "\uE70D",   // chevron down
                Font = new Font("Segoe MDL2 Assets", 10F),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = TextSub,
                Cursor = Cursors.Hand
            };
            inner.Controls.Add(_label);
            inner.Controls.Add(_arrow);

            inner.Layout += (_, _) =>
            {
                // 与 BuildSettingRow 同策略：按当前容器宽度/高度用 FitLabelFont
                // 把字号 fit 回合适值（DPI 变更/重建后无论 WinForms 怎么缩放字体，
                // 标题文字都由布局按新尺寸决定，不会与容器错位）。
                int labelW = Math.Max(0, inner.Width - (int)(60 * dpiScale));
                int maxH = Math.Max(14, inner.Height - (int)(6 * dpiScale));
                float size = FitLabelFont(_label.Text, _label.Font, labelW, maxH);
                if (Math.Abs(size - _label.Font.Size) > 0.01f)
                {
                    var old = _label.Font;
                    _label.Font = new Font(old.FontFamily, size);
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

        public void SetExpanded(bool expanded)
        {
            if (_expanded == expanded) return;
            _expanded = expanded;
            _arrow.Text = _expanded ? "\uE70E" : "\uE70D";
            ExpandedChanged?.Invoke(_expanded);
        }
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
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
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
            6 => _aboutPage,
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

    /// <summary>
    /// DPI 变化时 WinForms 会把显式设置的 Point 字体按 newDpi/oldDpi 缩放
    /// （WM_DPICHANGED_BEFOREPARENT → GetScaledFont → SetScaledFont），导航栏
    /// 10pt→12.5pt、标题栏 9pt→11.25pt @125%。选项内容文字因 Layout 里的
    /// FitLabelFont 会被拉回固定字号，而导航栏/标题栏没有该机制，所以这里用
    /// FontChanged 事件在缩放触发时立即把字体重置回固定 Point 值。
    /// </summary>
    private static void KeepFontFixed(Control control, Font fixedFont)
    {
        var current = control.Font;
        if (current.Size == fixedFont.Size && current.Unit == fixedFont.Unit) return;
        control.Font = fixedFont;
    }

    /// <summary>
    /// 递归给窗体上所有控件挂 FontChanged 修复：DPI 变化时 WinForms 会把
    /// 显式设置的 Point 字体缩放（GetScaledFont），这里在缩放触发
    /// OnFontChanged 的瞬间把字体拉回构造时的设计字体，覆盖按钮、页面
    /// 标题、次要文字等一切未单独处理的控件。Tag="dynamic" 的控件
    /// （FitLabelFont 动态管理字体的行标题等）跳过，避免覆盖主动缩小；
    /// 已挂载的控件（Tag="fontfix"）跳过，防重复挂载。
    /// </summary>
    private void AttachFontFix(Control c)
    {
        // dynamic（FitLabelFont 主动缩字的行标题等）也挂载：处理器只拉回"被
        // GetScaledFont 放大超过设计字号"的字体，缩小放行 → 不影响 FitLabelFont。
        if (c.Tag is string ts && ts == "fontfix") return;
        // 记录"名义字体"而非 Font 对象引用：DPI 重建（RebuildSkeletonFonts）会把
        // 旧 Font 实例 Dispose，若此闭包仍引用旧实例并在 FontChanged 里赋回，
        // Control.set_Font → ToHfont(已释放字体) 会抛 "Parameter is not valid" 崩溃
        // （栈见 WmDpiChangedBeforeParent → SetScaledFont → OnFontChanged）。
        var target = c.Font;
        string familyName = target.FontFamily.Name;
        float targetSize = target.Size;
        FontStyle targetStyle = target.Style;
        GraphicsUnit targetUnit = target.Unit;
        c.FontChanged += (_, _) =>
        {
            if (c.IsDisposed) return;
            if (c.Disposing) return;
            var f = c.Font;
            if (f == null || f.Unit != targetUnit) return;
            // 只把"被 WinForms GetScaledFont 放大超过设计字号"的字体拉回名义值。
            // （DPI 切换时显式字体被按 newDpi/oldDpi 缩放，方向/比例错误时会把
            //   10F 放大成 ~14F，绘制后物理尺寸仍等于原 DPI → "文字不随窗口缩放"。）
            // 小于名义值的（FitLabelFont 主动缩字）放行，不干扰自适应。
            // 每次用新实例赋回（只引用名义值，不引用旧 Font 对象）。
            if (f.Size > targetSize + 0.01f)
            {
                c.Font = new Font(familyName, targetSize, targetStyle, targetUnit);
            }
        };
        c.Tag = "fontfix";
        foreach (Control child in c.Controls) AttachFontFix(child);
    }

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
        _windowIcon = null;
        _instance = null;
        base.OnFormClosed(e);
    }
}
