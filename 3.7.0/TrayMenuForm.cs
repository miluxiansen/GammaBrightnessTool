namespace GammaBrightnessTool;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

/// <summary>
/// Self-drawn tray context menu window, replacing the native Win32 popup
/// menu so the whole surface (background, hover highlight, check marks,
/// separators) follows the app theme with full control over rendering.
/// </summary>
internal sealed class TrayMenuForm : Form
{
    public event EventHandler? OnSettingsRequested;
    public event EventHandler? OnUninstallRequested;
    public event EventHandler? OnRestartRequested;
    public event EventHandler? OnExitRequested;

    private enum EntryKind { Item, Submenu, Separator }

    internal enum SubmenuKind { Language, Disable, BrightnessLevels }

    // ---- “禁用”子菜单依赖注入（由 MainController 提供）----
    // 是否启用太阳调度（日出/日落选项可选的依据）。
    public Func<bool>? IsSolarEnabled;
    // 当前禁用剩余时间（null=未禁用或永久禁用）。
    public Func<TimeSpan?>? GetDisableRemaining;
    // 禁用到期时间（null=未禁用；DateTime.MaxValue=永久禁用）。
    public Func<DateTime?>? GetDisableUntil;
    // 当前是否处于日出/日落禁用模式。
    public Func<bool>? IsSolarDisableActive;
    // 当前是否白天（白天→显示“日落”，夜晚→显示“日出”）。
    public Func<bool>? IsDaytime;
    // 请求禁用：null=永久；TimeSpan.Zero=解除；时长=临时禁用；
    // 特殊值 -1 秒=日出/日落（到期时间由 MainController 计算）。
    public Action<TimeSpan?>? OnDisableRequested;

    private sealed class Entry
    {
        public EntryKind Kind;
        public string? Text;
        public SubmenuKind Sub;
        public Action? Action;
        /// <summary>P5/B1：稳定自动化 ID（Tray_*）。Separator 与未命名条目为 null。</summary>
        public string? Id;
    }

    private readonly List<Entry> _entries = new();
    private int _hoverIndex = -1;
    private int _itemH;
    private int _sepH;

    private TraySubMenu? _subMenu;

    // Close-on-outside-click: global low-level mouse hook.
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;

    // Close-on-app-switch: poll the foreground window; if it changes from
    // the one active when the menu opened (Alt+Tab, clicking another app),
    // close. The no-activate menu never becomes foreground itself.
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 100 };
    private IntPtr _initialForeground = IntPtr.Zero;

    // Layout paddings (logical pixels; text is laid out with the system menu
    // font which the OS already DPI-scales).
    private const int LeftPad = 14;
    private const int RightPad = 24; // reserve room for the submenu arrow

    // Theme palette (matches the original owner-drawn native menu).
    private static bool Dark => ThemeManager.IsDark;
    private static Color MenuBg => Dark ? Color.FromArgb(30, 30, 30) : Color.White;
    private static Color MenuHover => Dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251);
    private static Color MenuText => Dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);
    private static Color MenuTextSelected => Dark ? Color.White : Color.FromArgb(20, 20, 20);
    private static Color MenuBorder => Dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);
    private static Color SeparatorColor => Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);

    public TrayMenuForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BuildEntries();

        _foregroundTimer.Tick += (_, _) =>
        {
            if (!Visible || IsDisposed)
            {
                _foregroundTimer.Stop();
                return;
            }
            IntPtr fg = GetForegroundWindow();
            if (fg != _initialForeground && fg != Handle)
            {
                CloseMenu();
            }
        };
    }

    private void BuildEntries()
    {
        _entries.Clear();
        // 亮度挡位放最上（语言上方）：与设置页"亮度挡位"同值同行为
        // （不弹 OSD、按平滑开关走过渡），当前亮度落在哪个挡位就勾选哪个。
        _entries.Add(new Entry { Kind = EntryKind.Submenu, Id = "Tray_BrightnessLevels", Text = Localization.Get("BrightnessLevels"), Sub = SubmenuKind.BrightnessLevels });
        _entries.Add(new Entry { Kind = EntryKind.Submenu, Id = "Tray_Language", Text = Localization.Get("Language"), Sub = SubmenuKind.Language });
        _entries.Add(new Entry { Kind = EntryKind.Submenu, Id = "Tray_Disable", Text = Localization.Get("DisableMenu"), Sub = SubmenuKind.Disable });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Id = "Tray_Settings", Text = Localization.Get("Settings"), Action = () => OnSettingsRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Id = "Tray_Restart", Text = Localization.Get("RestartApp"), Action = () => OnRestartRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Id = "Tray_Uninstall", Text = Localization.Get("Uninstall"), Action = () => OnUninstallRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Id = "Tray_Exit", Text = Localization.Get("Exit"), Action = () => OnExitRequested?.Invoke(this, EventArgs.Empty) });
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    /// <summary>Size the menu and show it with its top-left at the given point,
    /// clamped into the working area. Also installs the outside-click hook and
    /// the foreground watcher.</summary>
    /// <summary>
    /// P5/B1：把主菜单条目注册进自动化注册表。
    ///
    /// 托盘菜单是<b>自绘窗体</b>，在 Windows UIA 树里完全不存在 —— 本注册表是
    /// 从外部驱动它的<b>唯一</b>通道。
    ///
    /// Submenu 条目只登记（Kind=submenu，Click 无效），其叶子项（语言 / 停用时长 /
    /// 亮度挡位）在各自子菜单构建时另行注册。
    /// 破坏性：Tray_Restart / Tray_Uninstall / Tray_Exit 都会结束本进程。
    /// </summary>
    public void RegisterAutomation()
    {
        foreach (Entry e in _entries)
        {
            if (string.IsNullOrEmpty(e.Id)) continue;
            if (e.Kind == EntryKind.Submenu)
            {
                AutomationBridge.Register(new AutomationAction(e.Id!, "submenu"));
                continue;
            }
            if (e.Action == null) continue;
            bool destructive = e.Id is "Tray_Restart" or "Tray_Uninstall" or "Tray_Exit";
            AutomationBridge.Register(new AutomationAction(e.Id!, "item",
                Click: e.Action,
                Destructive: destructive));
        }
    }

    public void ShowAt(Point screenPt)
    {
        // Row height follows the system menu font height (already DPI-scaled
        // by the OS), matching the original owner-drawn native menu and
        // avoiding any manual DPI multiplication (double-scaling bug).
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        _sepH = Math.Max(6, _itemH / 3);
        BackColor = MenuBg;

        // Rebuild so language labels and the current-language check mark
        // reflect the live selection each time the menu opens.
        BuildEntries();

        // P5/B1：菜单打开即注册子菜单叶子项（子菜单靠 hover 展开，自动化无法 hover）。
        RegisterSubmenuActions();

        var sz = ComputeSize();
        var screen = Screen.FromPoint(screenPt);
        var wa = screen.WorkingArea;
        // Keep the menu glued to the cursor like the native TrackPopupMenu:
        // top-left at the cursor, flipping up/left when it would cross the
        // working-area edge (never move it to a detached position).
        int x = screenPt.X;
        int y = screenPt.Y;
        if (x + sz.Width > wa.Right) x = screenPt.X - sz.Width;
        if (y + sz.Height > wa.Bottom) y = screenPt.Y - sz.Height;
        if (x < wa.Left) x = wa.Left;
        if (y < wa.Top) y = wa.Top;

        // Bring to topmost so the menu stays above other windows, matching
        // the native TrackPopupMenu behavior (which creates a topmost
        // popup). WS_EX_NOACTIVATE keeps it from stealing focus.
        SetWindowPos(Handle, HWND_TOPMOST, x, y, sz.Width, sz.Height, SWP_NOACTIVATE);
        Show();
        // Re-assert position + topmost after Show: the shell may have
        // re-arranged a brand-new window while it was being created.
        SetWindowPos(Handle, HWND_TOPMOST, x, y, sz.Width, sz.Height, SWP_NOACTIVATE);

        // Clip the window to a rounded rect so the four corners are truly
        // transparent (DWM corner rounding alone leaves square backdrop
        // corners on some systems/configurations).
        Region?.Dispose();
        using (var rp = ThemedComboBox.RoundedRect(new Rectangle(0, 0, sz.Width, sz.Height), 8))
            Region = new Region(rp);

        InstallMouseHook();
        _initialForeground = GetForegroundWindow();
        _foregroundTimer.Start();

        // Highlight the entry under the cursor (if the menu opened with the
        // cursor already on an item) without expanding any submenu — the
        // user must move the mouse to expand, matching the native menu.
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || !Visible) return;
            var p = PointToClient(Cursor.Position);
            if (ClientRectangle.Contains(p))
            {
                _hoverIndex = HitIndex(p.Y);
                Invalidate();
            }
        }));
    }

    private Size ComputeSize()
    {
        int maxW = 0;
        foreach (var e in _entries)
        {
            if (e.Text == null) continue;
            int w = TextRenderer.MeasureText(e.Text, SystemFonts.MenuFont!).Width;
            if (w > maxW) maxW = w;
        }
        int width = maxW + LeftPad + RightPad;
        int height = 2;
        foreach (var e in _entries)
        {
            height += e.Kind == EntryKind.Separator ? _sepH : _itemH;
        }
        return new Size(width, height);
    }

    private int HitIndex(int y)
    {
        int ty = 1;
        for (int i = 0; i < _entries.Count; i++)
        {
            int h = _entries[i].Kind == EntryKind.Separator ? _sepH : _itemH;
            if (y >= ty && y < ty + h && _entries[i].Kind != EntryKind.Separator) return i;
            ty += h;
        }
        return -1;
    }

    private int ItemTop(int index)
    {
        int ty = 1;
        for (int i = 0; i < index && i < _entries.Count; i++)
        {
            ty += _entries[i].Kind == EntryKind.Separator ? _sepH : _itemH;
        }
        return ty;
    }

    private void UpdateHover(int idx)
    {
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
        Invalidate();

        var entry = (idx >= 0 && idx < _entries.Count) ? _entries[idx] : null;
        if (entry != null && entry.Kind == EntryKind.Submenu)
        {
            ShowSubmenuFor(idx);
        }
        else
        {
            CloseSubMenu();
        }
    }

    private void ShowSubmenuFor(int entryIndex)
    {
        var entry = _entries[entryIndex];
        if (_subMenu != null && _subMenu.Kind == entry.Sub) return;

        CloseSubMenu();

        var sub = new TraySubMenu(entry.Sub);
        sub.OnItemActivated += (_, _) => BeginInvoke(CloseMenu);
        BuildSubmenuItems(entry.Sub, sub);

        var size = sub.ComputeSize();
        int y = PointToScreen(new Point(0, ItemTop(entryIndex))).Y;
        int x = Right - 2;
        var work = Screen.FromPoint(new Point(x, y)).WorkingArea;
        if (x + size.Width > work.Right)
            x = Left - size.Width + 2;
        // Keep the submenu reachable from the parent item: shift it up so its
        // bottom hugs the working area instead of flipping entirely above the
        // parent (which breaks the hover path from the parent item into it).
        if (y + size.Height > work.Bottom)
            y = work.Bottom - size.Height;
        if (y < work.Top)
            y = work.Top;
        sub.ShowAt(new Point(x, y), size);
        _subMenu = sub;
    }

    /// <summary>
    /// 构建“亮度挡位”子菜单：100% / 75% / 50% / 25% / 10%。
    /// 与设置页“亮度挡位”完全同值同行为（走 SetBrightnessLevel：
    /// 不弹 OSD、按“亮度平滑”开关走过渡/直切）；当前亮度最接近的挡位打勾。
    /// </summary>
    private void BuildBrightnessLevelSubMenu(TraySubMenu sub)
    {
        float[] levels = { 1.0f, 0.75f, 0.5f, 0.25f, 0.1f };
        float current = Program.Instance?.GetCurrentBrightness() ?? 1.0f;
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < levels.Length; i++)
        {
            float d = Math.Abs(current - levels[i]);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        for (int i = 0; i < levels.Length; i++)
        {
            int idx = i; // 闭包捕获
            float level = levels[i];
            sub.AddChecked($"{(int)Math.Round(level * 100)}%", i == best,
                () => Program.Instance?.SetBrightnessLevel(level),
                "Tray_Level_" + (int)Math.Round(level * 100));
        }
    }

    /// <summary>
    /// 构建“禁用”子菜单：永久 / 1分钟 … 一天 / 日出或日落。
    /// 日出/日落选项仅在太阳调度启用时可选；当前激活项打勾。
    /// </summary>
    private void BuildDisableSubMenu(TraySubMenu sub)
    {
        var remaining = GetDisableRemaining?.Invoke();
        var until = GetDisableUntil?.Invoke();
        bool active = until != null && until.Value > DateTime.Now;
        DateTime untilVal = until.GetValueOrDefault();

        // 关闭：解除当前禁用；未禁用时置灰不可点。
        sub.AddChecked(Localization.Get("DisableOff"), false, () => OnDisableRequested?.Invoke(TimeSpan.Zero), "Tray_Disable_Off");
        if (!active) sub.DisableFirstItem();

        // 永久：激活且到期时间为 MaxValue 时勾选。
        sub.AddChecked(Localization.Get("DisablePermanent"), active && untilVal == DateTime.MaxValue, () => OnDisableRequested?.Invoke(null), "Tray_Disable_Permanent");
        bool tempActive = active && untilVal != DateTime.MaxValue;
        // 仅勾选与剩余时间最接近的档位，避免容差导致多个档位同时打勾。
        int checkedIdx = -1;
        if (tempActive && remaining != null && remaining.Value > TimeSpan.Zero)
        {
            TimeSpan[] presets =
            {
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
                TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(5), TimeSpan.FromHours(12), TimeSpan.FromDays(1)
            };
            double best = double.MaxValue;
            for (int i = 0; i < presets.Length; i++)
            {
                double d = (remaining.Value - presets[i]).Duration().TotalMinutes;
                if (d < best) { best = d; checkedIdx = i; }
            }
        }
        sub.AddChecked(Localization.Get("Disable1Min"), checkedIdx == 0, () => OnDisableRequested?.Invoke(TimeSpan.FromMinutes(1)), "Tray_Disable_1Min");
        sub.AddChecked(Localization.Get("Disable5Min"), checkedIdx == 1, () => OnDisableRequested?.Invoke(TimeSpan.FromMinutes(5)), "Tray_Disable_5Min");
        sub.AddChecked(Localization.Get("Disable15Min"), checkedIdx == 2, () => OnDisableRequested?.Invoke(TimeSpan.FromMinutes(15)), "Tray_Disable_15Min");
        sub.AddChecked(Localization.Get("Disable30Min"), checkedIdx == 3, () => OnDisableRequested?.Invoke(TimeSpan.FromMinutes(30)), "Tray_Disable_30Min");
        sub.AddChecked(Localization.Get("Disable1Hour"), checkedIdx == 4, () => OnDisableRequested?.Invoke(TimeSpan.FromHours(1)), "Tray_Disable_1Hour");
        sub.AddChecked(Localization.Get("Disable3Hours"), checkedIdx == 5, () => OnDisableRequested?.Invoke(TimeSpan.FromHours(3)), "Tray_Disable_3Hours");
        sub.AddChecked(Localization.Get("Disable5Hours"), checkedIdx == 6, () => OnDisableRequested?.Invoke(TimeSpan.FromHours(5)), "Tray_Disable_5Hours");
        sub.AddChecked(Localization.Get("Disable12Hours"), checkedIdx == 7, () => OnDisableRequested?.Invoke(TimeSpan.FromHours(12)), "Tray_Disable_12Hours");
        sub.AddChecked(Localization.Get("Disable1Day"), checkedIdx == 8, () => OnDisableRequested?.Invoke(TimeSpan.FromDays(1)), "Tray_Disable_1Day");

        // 日出/日落：仅太阳调度启用时可选；
        // 白天→“到日落”，夜晚→“到日出”。
        bool solarEnabled = IsSolarEnabled?.Invoke() ?? false;
        bool isDay = IsDaytime?.Invoke() ?? true;
        string solarLabel = Localization.Get(isDay ? "DisableUntilSunset" : "DisableUntilSunrise");
        bool solarActive = (IsSolarDisableActive?.Invoke() ?? false) && active;
        sub.AddChecked(solarLabel, solarActive, () =>
        {
            if (!solarEnabled) return; // 未启用时不可选
            OnDisableRequested?.Invoke(TimeSpan.FromSeconds(-1)); // 特殊值：日出/日落
        }, "Tray_Disable_Solar");
        if (!solarEnabled) sub.DisableLastItem();
    }

    /// <summary>
    /// 构建指定子菜单的全部叶子项。**显示路径与自动化注册路径共用此方法** ——
    /// 这样自动化读到的勾选状态与动作跟用户手点走的是同一份逻辑，不会分叉。
    /// </summary>
    private void BuildSubmenuItems(SubmenuKind kind, TraySubMenu sub)
    {
        if (kind == SubmenuKind.Disable)
        {
            BuildDisableSubMenu(sub);
            return;
        }
        if (kind == SubmenuKind.BrightnessLevels)
        {
            BuildBrightnessLevelSubMenu(sub);
            return;
        }
        var cur = Localization.Setting;
        sub.AddChecked(Localization.Get("LangSystem"), cur == Language.System, () => Program.Instance?.ChangeLanguage(Language.System), "Tray_Lang_System");
        sub.AddChecked(Localization.Get("LangSC"), cur == Language.SimplifiedChinese, () => Program.Instance?.ChangeLanguage(Language.SimplifiedChinese), "Tray_Lang_SC");
        sub.AddChecked(Localization.Get("LangTC"), cur == Language.TraditionalChinese, () => Program.Instance?.ChangeLanguage(Language.TraditionalChinese), "Tray_Lang_TC");
        sub.AddChecked(Localization.Get("LangEN"), cur == Language.English, () => Program.Instance?.ChangeLanguage(Language.English), "Tray_Lang_EN");
        sub.AddChecked(Localization.Get("LangJA"), cur == Language.Japanese, () => Program.Instance?.ChangeLanguage(Language.Japanese), "Tray_Lang_JA");
        sub.AddChecked(Localization.Get("LangKO"), cur == Language.Korean, () => Program.Instance?.ChangeLanguage(Language.Korean), "Tray_Lang_KO");
        sub.AddChecked(Localization.Get("LangDE"), cur == Language.German, () => Program.Instance?.ChangeLanguage(Language.German), "Tray_Lang_DE");
        sub.AddChecked(Localization.Get("LangFR"), cur == Language.French, () => Program.Instance?.ChangeLanguage(Language.French), "Tray_Lang_FR");
        sub.AddChecked(Localization.Get("LangES"), cur == Language.Spanish, () => Program.Instance?.ChangeLanguage(Language.Spanish), "Tray_Lang_ES");
        sub.AddChecked(Localization.Get("LangRU"), cur == Language.Russian, () => Program.Instance?.ChangeLanguage(Language.Russian), "Tray_Lang_RU");
    }

    /// <summary>
    /// P5/B1：注册三个子菜单的全部叶子项（Tray_Level_* / Tray_Lang_* / Tray_Disable_*）。
    ///
    /// 为什么在主菜单**打开时**注册、而不是子菜单展开时：
    /// 子菜单是 hover 触发的自绘窗体，外部自动化产生不了 hover 事件；
    /// 若等展开才注册，这些 ID 将永远不可达（2026-09-16 接口盘点发现）。
    /// 叶子项的 Click 只捕获 Program.Instance / OnDisableRequested，不依赖
    /// 子菜单窗体存活，因此可在菜单打开期间常驻，关闭时按前缀清理。
    /// </summary>
    private void RegisterSubmenuActions()
    {
        ClearSubmenuActions();
        SubmenuKind[] kinds = { SubmenuKind.BrightnessLevels, SubmenuKind.Language, SubmenuKind.Disable };
        foreach (SubmenuKind kind in kinds)
        {
            var probe = new TraySubMenu(kind);
            try
            {
                BuildSubmenuItems(kind, probe);
                foreach (var (id, _checked, _disabled, action) in probe.AutomationItems())
                {
                    // 灰显项（Action == null，如未启用 Solar 时的"日出/日落"、
                    // 未停用时的"关闭"）照样登记，但 Click 为空 → 外部会得到
                    // "不可点击"的干净错误，而不是静默无效果。
                    AutomationBridge.Register(new AutomationAction(id, "item", Click: action));
                }
            }
            finally
            {
                probe.Dispose();
            }
        }
    }

    /// <summary>
    /// 注销三个子菜单的叶子项 ID。
    /// 菜单关闭（用户点外部 / 切前台 / 自动化关闭）时必须调用 —— 否则
    /// Tray_Level_* 等 ID 会残留并指向已失效的闭包（2026-09-16 接口盘点发现
    /// HideMenuForAutomation 原先只 Hide 不清理）。
    /// </summary>
    internal static void ClearSubmenuActions()
    {
        AutomationBridge.ClearActionsByPrefix("Tray_Level_");
        AutomationBridge.ClearActionsByPrefix("Tray_Lang_");
        AutomationBridge.ClearActionsByPrefix("Tray_Disable_");
    }

    private void CloseSubMenu()
    {
        _subMenu?.Dispose();
        _subMenu = null;
    }

    private void CloseMenu()
    {
        _foregroundTimer.Stop();
        UninstallMouseHook();
        CloseSubMenu();
        ClearSubmenuActions();
        if (!IsDisposed)
            Hide();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = MouseHookCallback;
            _mouseHook = NativeMethods.SetWindowsHookEx(14, _mouseProc, NativeMethods.GetModuleHandle(null), 0u);
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == 0x0201 || msg == 0x0204) // WM_LBUTTONDOWN | WM_RBUTTONDOWN
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.x, info.pt.y);
                bool inMain = Bounds.Contains(pt);
                bool inSub = _subMenu != null && _subMenu.Visible && _subMenu.Bounds.Contains(pt);
                if (!inMain && !inSub)
                    BeginInvoke(CloseMenu);
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdateHover(HitIndex(e.Y));
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int num = HitIndex(e.Y);
            if (num >= 0 && num < _entries.Count && _entries[num].Kind == EntryKind.Item)
            {
                Action? action = _entries[num].Action;
                CloseMenu();
                action?.Invoke();
            }
        }
        base.OnMouseUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(MenuBg))
        using (var path = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            g.FillPath(brush, path);

        int y = 1;
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Kind == EntryKind.Separator)
            {
                int lineY = y + _sepH / 2;
                using var sepPen = new Pen(SeparatorColor);
                g.DrawLine(sepPen, 8, lineY, Width - 8, lineY);
                y += _sepH;
                continue;
            }

            var rect = new Rectangle(1, y, Width - 2, _itemH);
            bool hover = i == _hoverIndex;
            if (hover)
            {
                using var hoverBrush = new SolidBrush(MenuHover);
                g.FillRectangle(hoverBrush, rect);
            }
            var foreColor = hover ? MenuTextSelected : MenuText;
            TextRenderer.DrawText(g, entry.Text ?? "", SystemFonts.MenuFont,
                new Rectangle(LeftPad, y, Width - LeftPad - RightPad, _itemH), foreColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            if (entry.Kind == EntryKind.Submenu)
                TextRenderer.DrawText(g, "\u25B8", SystemFonts.MenuFont,
                    new Rectangle(Width - RightPad + 4, y, 20, _itemH), foreColor,
                    TextFormatFlags.VerticalCenter);
            y += _itemH;
        }

        using var pen = new Pen(MenuBorder);
        using var borderPath = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        g.DrawPath(pen, borderPath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _foregroundTimer.Dispose();
            UninstallMouseHook();
            CloseSubMenu();
        }
        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// <summary>
/// The hover-expanded submenu of the tray menu (language).
/// Mirrors the owner-draw look of the parent menu.
/// </summary>
internal sealed class TraySubMenu : Form
{
    private sealed class Item
    {
        public string Text = "";
        public bool Checked;
        public bool Disabled;
        public Action? Action;
        /// <summary>P5/B1：稳定自动化 ID（Tray_Level_* / Tray_Lang_* / Tray_Disable_*）。</summary>
        public string? Id;
    }

    private readonly List<Item> _items = new();
    private int _hoverIndex = -1;
    private int _itemH;
    private int _scrollPos;
    private int _maxScroll;
    private bool _draggingThumb;
    private int _dragOffsetY;
    private const int MaxVisibleItems = 4;

    private const int LeftPad = 14;
    private const int RightPad = 14;
    private const int CheckPad = 20;

    private readonly TrayMenuForm.SubmenuKind _kind;

    public TrayMenuForm.SubmenuKind Kind => _kind;

    private static bool Dark => ThemeManager.IsDark;

    private static Color MenuBg => Dark ? Color.FromArgb(30, 30, 30) : Color.White;

    private static Color MenuHover => Dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251);

    private static Color MenuText => Dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);

    private static Color MenuTextSelected => Dark ? Color.White : Color.FromArgb(20, 20, 20);

    private static Color MenuBorder => Dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
            return cp;
        }
    }

    public event EventHandler? OnItemActivated;

    internal TraySubMenu(TrayMenuForm.SubmenuKind kind)
    {
        _kind = kind;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void Add(string text, Action action, string? id = null)
    {
        _items.Add(new Item { Text = text, Action = action, Id = id });
    }

    public void AddChecked(string text, bool isChecked, Action action, string? id = null)
    {
        _items.Add(new Item { Text = text, Checked = isChecked, Action = action, Id = id });
    }

    /// <summary>
    /// P5/B1：导出带自动化 ID 的条目（分隔项 / 未命名项自动略过）。
    /// 供 <see cref="TrayMenuForm"/> 在菜单打开时注册 —— 与真实菜单共用同一份
    /// 构建逻辑，故自动化读到的勾选状态与动作跟用户手点完全一致。
    /// </summary>
    internal IReadOnlyList<(string Id, bool Checked, bool Disabled, Action? Action)> AutomationItems()
    {
        var list = new List<(string, bool, bool, Action?)>();
        foreach (var it in _items)
        {
            if (string.IsNullOrEmpty(it.Id)) continue;
            list.Add((it.Id!, it.Checked, it.Disabled, it.Action));
        }
        return list;
    }

    /// <summary>把最后一项标记为不可用（灰显、点击无效）。</summary>
    public void DisableLastItem()
    {
        if (_items.Count == 0) return;
        _items[^1].Action = null;
        _items[^1].Disabled = true;
    }

    /// <summary>Marks the first item as disabled (grayed, click does nothing).</summary>
    public void DisableFirstItem()
    {
        if (_items.Count == 0) return;
        _items[0].Action = null;
        _items[0].Disabled = true;
    }

    public Size ComputeSize()
    {
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        int maxW = 0;
        foreach (var item in _items)
            maxW = Math.Max(maxW, TextRenderer.MeasureText(item.Text, SystemFonts.MenuFont).Width);
        int checkRoom = 0;
        foreach (var it in _items) { if (it.Checked) { checkRoom = CheckPad; break; } }
        int width = maxW + LeftPad + RightPad + checkRoom;
        int total = _items.Count;
        int visible = Math.Min(total, MaxVisibleItems);
        int height = 2 + visible * _itemH;
        _maxScroll = Math.Max(0, total * _itemH - (visible * _itemH));
        _scrollPos = Math.Clamp(_scrollPos, 0, _maxScroll);
        return new Size(width, height);
    }

    public void ShowAt(Point screenPt, Size size)
    {
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        BackColor = MenuBg;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, screenPt.X, screenPt.Y, size.Width, size.Height, 16u);
        Show();
        // Double call: the first positions it, the second re-asserts the Z order.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, screenPt.X, screenPt.Y, size.Width, size.Height, 16u);
        Region?.Dispose();
        using (var rp = ThemedComboBox.RoundedRect(new Rectangle(0, 0, size.Width, size.Height), 8))
            Region = new Region(rp);
    }

    private int HitIndex(int y)
    {
        int idx = _scrollPos / _itemH + (y - 1) / _itemH;
        if (idx < 0 || idx >= _items.Count) return -1;
        return idx;
    }

    private void PaintScrollbar(Graphics g, bool dark)
    {
        var tr = GetThumbRect();
        var thumbColor = dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(190, 190, 190);
        using var b = new SolidBrush(thumbColor);
        using var path = ThemedComboBox.RoundedRect(tr, 2);
        g.FillPath(b, path);
    }

    private Rectangle GetThumbRect()
    {
        int sbW = 6;
        int x = Width - sbW - 3;
        int trackH = Height - 8;
        int totalH = _items.Count * _itemH;
        int visibleH = Height - 4;
        int thumbH = Math.Max(24, (int)(trackH * (double)visibleH / totalH));
        double ratio = _maxScroll > 0 ? (double)_scrollPos / _maxScroll : 0;
        int thumbY = 4 + (int)(ratio * (trackH - thumbH));
        return new Rectangle(x, thumbY, sbW, thumbH);
    }

    private bool InScrollbarArea(int x) => x > Width - 12;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int delta = e.Delta > 0 ? -3 * _itemH : 3 * _itemH;
        _scrollPos = Math.Clamp(_scrollPos + delta, 0, _maxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingThumb && _maxScroll > 0)
        {
            var tr = GetThumbRect();
            int trackH = Height - 8;
            int maxTravel = trackH - tr.Height;
            double ratio = maxTravel > 0 ? (double)(e.Y - _dragOffsetY - 4) / maxTravel : 0;
            _scrollPos = (int)Math.Round(Math.Clamp(ratio, 0, 1) * _maxScroll);
            Invalidate();
        }
        else
        {
            int idx = HitIndex(e.Y);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Invalidate();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_maxScroll > 0 && InScrollbarArea(e.X))
            {
                var tr = GetThumbRect();
                if (e.Y >= tr.Top && e.Y <= tr.Bottom)
                {
                    // Start dragging the thumb.
                    _draggingThumb = true;
                    _dragOffsetY = e.Y - tr.Top;
                }
                else
                {
                    // Click on the gutter: page up/down.
                    int page = Height - 4;
                    _scrollPos = Math.Clamp(_scrollPos + (e.Y < tr.Top ? -page : page), 0, _maxScroll);
                    Invalidate();
                }
            }
            else
            {
                // Fire the item action on mouse DOWN (not up). The parent menu's
                // outside-click hook can close the submenu on the press itself
                // (its Bounds check races with the WM_LBUTTONUP dispatch), which
                // would otherwise swallow the click. Invoking on down guarantees
                // the language switch runs before any deferred CloseMenu.
                int idx = HitIndex(e.Y);
                if (idx >= 0)
                {
                    try
                    {
                        _items[idx].Action?.Invoke();
                    }
                    finally
                    {
                        OnItemActivated?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _draggingThumb = false;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(MenuBg))
        using (var path = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            g.FillPath(brush, path);

        bool dark = ThemeManager.IsDark;
        // 子菜单含勾选项时预留勾选列（语言/禁用子菜单均有）。
        bool hasCheck = false;
        foreach (var it in _items) { if (it.Checked) { hasCheck = true; break; } }
        int textLeft = LeftPad + (hasCheck ? CheckPad : 0);

        int first = _scrollPos / _itemH;
        int visible = Math.Max(0, (Height - 2) / _itemH);
        int end = Math.Min(_items.Count, first + visible);
        int y = 1;
        for (int i = first; i < end; i++)
        {
            var item = _items[i];
            var rect = new Rectangle(1, y, Width - 2, _itemH);
            bool hover = i == _hoverIndex;
            if (hover && !item.Disabled)
            {
                using var hoverBrush = new SolidBrush(MenuHover);
                g.FillRectangle(hoverBrush, rect);
            }
            var foreColor = item.Disabled ? (dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(150, 150, 150)) : (hover ? MenuTextSelected : MenuText);
            if (hasCheck && item.Checked)
                TextRenderer.DrawText(g, "\u2713", SystemFonts.MenuFont,
                    new Rectangle(16, y, CheckPad, _itemH), foreColor,
                    TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, item.Text, SystemFonts.MenuFont,
                new Rectangle(textLeft, y, Width - textLeft - RightPad, _itemH), foreColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            y += _itemH;
        }

        if (_maxScroll > 0) PaintScrollbar(g, dark);

        using var pen = new Pen(MenuBorder);
        using var borderPath = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        g.DrawPath(pen, borderPath);
    }
}
