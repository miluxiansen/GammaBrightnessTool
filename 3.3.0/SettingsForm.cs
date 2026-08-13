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
    private readonly ListBox _navList;
    private readonly Panel _contentPanel;
    private readonly Label _versionLabel;
    private Panel _generalPage;
    private Panel _hotkeysPage;
    private Panel _aboutPage;
    private Panel _colorTempPage;

    // Debounce: LanguageChanged fires twice per language switch
    // (Setting= then Current=), so coalesce them into one RebuildUi.
    private System.Windows.Forms.Timer? _rebuildDebounce;

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
    private readonly float _dpiScale;

    private static SettingsForm? _instance;

    /// <summary>
    /// Shows the single settings window (or activates it if already open).
    /// Non-modal: the tray stays fully usable while it is open.
    /// </summary>
    public static void ShowOrActivate()
    {
        if (_instance == null || _instance.IsDisposed)
        {
            _instance = new SettingsForm();
            _instance.Show();
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
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true; // allow minimizing so it never blocks other apps
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
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
        ClientSize = new Size((int)(560 * _dpiScale), (int)(400 * _dpiScale));
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

        _navList = new ListBox
        {
            Dock = DockStyle.Left,
            Width = (int)(140 * _dpiScale),
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = (int)(40 * _dpiScale),
            Font = new Font("Segoe UI", 10F),
            BackColor = BgNav
        };
        _navList.Items.Add(Localization.Get("SettingsGeneral"));
        _navList.Items.Add(Localization.Get("SettingsColorTemp"));
        _navList.Items.Add(Localization.Get("SettingsHotkeys"));
        _navList.Items.Add(Localization.Get("SettingsAbout"));
        _navList.SelectedIndexChanged += OnNavSelected;
        _navList.DrawItem += OnNavDrawItem;
        Controls.Add(_navList);


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
        _versionLabel.Location = new Point((int)(8 * _dpiScale), ClientSize.Height - (int)(26 * _dpiScale));
        Controls.Add(_versionLabel);
        _versionLabel.BringToFront(); // Keep it above the Fill content panel
        // Build the three pages
        _generalPage = BuildGeneralPage();
        _hotkeysPage = BuildHotkeysPage();
        _aboutPage = BuildAboutPage();
        _colorTempPage = BuildColorTempPage();
        _contentPanel.Controls.Add(_generalPage);

        // Default to the first page
        _navList.SelectedIndex = 0;

        // Rebuild all UI text when the language changes (from this combo or
        // the tray menu), so the window itself updates immediately instead
        // of only after reopening.
        Localization.LanguageChanged += OnLanguageChanged;

        // Rebuild the whole window when the theme changes so every control
        // (backgrounds, text, borders, combos, navigation) repaints with
        // the new palette.
        ThemeManager.ThemeChanged += OnThemeChanged;

        // First-show repair: OwnerDraw combo boxes can paint once with
        // un-laid-out bounds while the window is still appearing (their
        // SelectedIndex is set during page construction, before the control
        // has a real size/position). That first paint can leave a garbled
        // box until any repaint. Force one clean refresh after the window
        // is actually shown so the first visible frame is always correct.
        Shown += (_, _) =>
        {
            foreach (var combo in FindAllThemedCombos(this))
            {
                combo.Invalidate();
            }
        };
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
        ApplyTitleBarTheme();
    }

    /// <summary>Applies (or clears) DWM immersive dark mode on the title bar
    /// so the window caption follows the app theme. Called on handle creation
    /// and again on theme change.</summary>
    private void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated) return;
        int dark = ThemeManager.IsDark ? 1 : 0;
        // Windows 10 2004+ uses attr 19; Windows 11 uses 20. Try both.
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_10, ref dark, sizeof(int));
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTitleBarTheme();
        // Theme switch no longer rebuilds the control tree (which caused
        // a visible nav delay and replayed the toggle slide animation).
        // Just repaint every control with the new palette directly.
        if (IsHandleCreated)
        {
            // The theme combo's SelectedIndexChanged is on the call stack;
            // defer the refresh so the combo finishes updating first.
            BeginInvoke(RefreshAllThemes);
        }
        else
        {
            RefreshAllThemes();
        }
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
        _navList.BackColor = BgNav;
        _navList.Invalidate();  // force owner-drawn repaint
        if (_versionLabel != null)
        {
            _versionLabel.BackColor = BgNav;
            _versionLabel.ForeColor = TextMain;
        }

        // Refresh every page: first the page panel itself (its BackColor is
        // set at build time and must follow the theme), then its subtree.
        foreach (var page in new[] { _generalPage, _hotkeysPage, _aboutPage, _colorTempPage })
        {
            if (page == null) continue;
            page.BackColor = Bg;  // page root uses the page background
            foreach (Control child in page.Controls)
                RefreshTheme(child, Bg, BgInner, Border,
                             TextMain, Track, Thumb, ThumbHover, InputBg);
        }
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
                count++;
            }
            else if (c is RoundedTextBox rtb)
            {
                rtb.ApplyTheme(inputBg, textMain);
                // The field's rounded corners blend into the card inner panel.
                rtb.SetParentBackground(bgInner);
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
                lbl.ForeColor = textMain;
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

    private void RebuildUi()
    {
        if (IsDisposed) return;
        int navIndex = _navList.SelectedIndex;

        // Apply the current theme to the form shell itself as well (the
        // pages rebuild with the new palette below; the form background
        // would otherwise stay in the old theme).
        BackColor = Bg;
        _contentPanel.BackColor = Bg;
        _navList.BackColor = BgNav;
        _navList.Invalidate();

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
        _navList.Items.Clear();
        _navList.Items.Add(Localization.Get("SettingsGeneral"));
        _navList.Items.Add(Localization.Get("SettingsColorTemp"));
        _navList.Items.Add(Localization.Get("SettingsHotkeys"));
        _navList.Items.Add(Localization.Get("SettingsAbout"));

        _generalPage?.Dispose();
        _hotkeysPage?.Dispose();
        _aboutPage?.Dispose();
        _colorTempPage?.Dispose();

        _generalPage = BuildGeneralPage();
        _hotkeysPage = BuildHotkeysPage();
        _aboutPage = BuildAboutPage();
        _colorTempPage = BuildColorTempPage();

        Text = Localization.Get("SettingsTitle");

        if (navIndex < 0) navIndex = 0;
        _navList.SelectedIndex = navIndex; // triggers OnNavSelected -> adds page
        _versionLabel?.BringToFront(); // RebuildUi recreates pages; keep version tag on top
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
            var theme = (ThemeMode)popupThemeCombo.SelectedIndex;
            Program.Instance?.SetPopupTheme(theme);
        };
        var popupThemeRow = BuildSettingRow(Localization.Get("PopupTheme"), popupThemeCombo);

        // ---- Setting row 5: 滚轮步进 (wheel step size) ----
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

        // ---- Setting row 7: 设置窗口置顶 (settings window always-on-top) ----
        var topMostToggle = new ToggleSwitch
        {
            Checked = Program.Instance?.GetTopMost() ?? false
        };
        topMostToggle.ApplyDpiScale(_dpiScale);
        topMostToggle.CheckedChanged += (_, _) =>
        {
            Program.Instance?.SetTopMost(topMostToggle.Checked);
            TopMost = topMostToggle.Checked; // apply to this window immediately
        };
        var topMostGroup = BuildToggleGroup(topMostToggle);
        var topMostRow = BuildSettingRow(Localization.Get("SettingsTopMost"), topMostGroup);

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

        topMostRow.Dock = DockStyle.Top;
        scroll.Controls.Add(topMostRow);

        overlayRow.Dock = DockStyle.Top;
        scroll.Controls.Add(overlayRow);

        invertRow.Dock = DockStyle.Top;
        scroll.Controls.Add(invertRow);

        wheelRow.Dock = DockStyle.Top;
        scroll.Controls.Add(wheelRow);


        stepRow.Dock = DockStyle.Top;
        scroll.Controls.Add(stepRow);

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
    /// the inner panel's Layout event using the real control sizes.
    /// </summary>
    /// <summary>
    /// Builds a right-side group containing a state label (开/关) and a
    /// ToggleSwitch, laid out live in Layout with the current DPI font so
    /// wider glyphs never overlap the switch. Shared by the startup,
    /// inverted-wheel and OSD rows.
    /// </summary>
    private Panel BuildToggleGroup(ToggleSwitch toggle)
    {
        var stateLabel = new Label
        {
            Text = toggle.Checked ? Localization.Get("On") : Localization.Get("Off"),
            AutoSize = false,
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
            int textW = TextRenderer.MeasureText(stateLabel.Text, stateLabel.Font).Width;
            int groupH = (int)(22 * _dpiScale);
            group.Size = new Size(textW + 10 + toggle.Width, groupH);
            stateLabel.Size = new Size(textW, groupH);
            stateLabel.Location = new Point(0, 0);
            toggle.Location = new Point(textW + 10, (group.Height - toggle.Height) / 2);
        };
        return group;
    }

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

        var label = new Label
        {
            Text = labelText,
            Font = new Font("Segoe UI", 10F),
            AutoSize = true,
            ForeColor = TextMain
        };

        inner.Controls.Add(label);
        inner.Controls.Add(rightControl);

        inner.Layout += (_, _) =>
        {
            // Center both vertically using their real (autosized) heights.
            label.Location = new Point(14, (inner.Height - label.Height) / 2);
            rightControl.Location = new Point(
                inner.Width - rightControl.Width - 14,
                (inner.Height - rightControl.Height) / 2);
        };

        return outer;
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
        };

        var colorTempGroup = BuildToggleGroup(colorTempToggle);
        var colorTempRow = BuildSettingRow(Localization.Get("ColorTemperatureEnabled"), colorTempGroup);
        var tempStepRow = BuildSettingRow(Localization.Get("TemperatureStepSize"), tempStepCombo);

        tempStepRow.Dock = DockStyle.Top;
        scroll.Controls.Add(tempStepRow);
        colorTempRow.Dock = DockStyle.Top;
        scroll.Controls.Add(colorTempRow);

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
            v => Program.Instance?.SetIncreaseBrightnessHotKey(v),
            Program.Instance?.GetIncreaseBrightnessHotKeyEnabled() ?? true,
            v => Program.Instance?.SetIncreaseBrightnessHotKeyEnabled(v));
        incCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incCapture);

        // ---- 降低亮度 hotkey row ----
        var decCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseBrightness"),
            Program.Instance?.GetDecreaseBrightnessHotKey() ?? "",
            v => Program.Instance?.SetDecreaseBrightnessHotKey(v),
            Program.Instance?.GetDecreaseBrightnessHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseBrightnessHotKeyEnabled(v));
        decCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(decCapture);

        // ---- 熄屏 hotkey row ----
        var powerOffCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyPowerOff"),
            Program.Instance?.GetPowerOffHotKey() ?? "",
            v => Program.Instance?.SetPowerOffHotKey(v),
            Program.Instance?.GetPowerOffHotKeyEnabled() ?? true,
            v => Program.Instance?.SetPowerOffHotKeyEnabled(v));
        powerOffCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(powerOffCapture);

        // ---- 增加色温 hotkey row ----
        var incTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyIncreaseTemperature"),
            Program.Instance?.GetIncreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetIncreaseTemperatureHotKey(v),
            Program.Instance?.GetIncreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetIncreaseTemperatureHotKeyEnabled(v));
        incTempCapture.Dock = DockStyle.Top;
        scroll.Controls.Add(incTempCapture);

        // ---- 降低色温 hotkey row ----
        var decTempCapture = CreateHotKeyCaptureRow(Localization.Get("HotkeyDecreaseTemperature"),
            Program.Instance?.GetDecreaseTemperatureHotKey() ?? "",
            v => Program.Instance?.SetDecreaseTemperatureHotKey(v),
            Program.Instance?.GetDecreaseTemperatureHotKeyEnabled() ?? true,
            v => Program.Instance?.SetDecreaseTemperatureHotKeyEnabled(v));
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
    private Panel CreateHotKeyCaptureRow(string labelText, string currentValue, Action<string> commit,
        bool enabled, Action<bool>? setEnabled = null)
    {
        // Rounded card frame (same style as the general-page rows).
        var row = new RoundedCardPanel
        {
            Height = (int)(48 * _dpiScale),
            Margin = new Padding(0, (int)(10 * _dpiScale), 0, 0)
        };
        row.ApplyTheme(Bg, BgInner, Border);

        var inner = row.Inner;

        var label = new Label
        {
            Text = labelText,
            Font = new Font("Segoe UI", 10F),
            AutoSize = false,
            Height = (int)(24 * _dpiScale),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMain
        };
        inner.Controls.Add(label);

        var capture = new HotKeyCaptureBox
        {
            Width = (int)(160 * _dpiScale),
            Height = (int)(26 * _dpiScale)
        };
        capture.ApplyTheme(InputBg, TextMain);
        capture.SetParentBackground(BgInner); // rounded corners blend into the card inner panel
        capture.SetPlaceholder(Localization.Get("HotkeyInputPlaceholder"));
        capture.HotKey = currentValue;
        inner.Controls.Add(capture);

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
            if (capture.IsCleared)
            {
                value = ""; // cleared = unbind
            }
            else if (string.IsNullOrEmpty(value))
            {
                value = capture.SavedValue; // nothing recorded: keep the old binding
            }
            capture.CommitCapture(value);
            commit(value);
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
            toggle.CheckedChanged += (_, _) => setEnabled(toggle.Checked);
            inner.Controls.Add(toggle);
        }

        // Layout: label left, capture box center-left, toggle + buttons right.
        inner.Layout += (_, _) =>
        {
            int gap = (int)(10 * _dpiScale);

            // Fixed-width label so the capture boxes align across all rows
            // regardless of the label text length (and across languages).
            int labelW = (int)(110 * _dpiScale);
            label.SetBounds(14, (inner.Height - label.Height) / 2, labelW, label.Height);

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
            AutoSize = true,
            Location = new Point(0, (int)(50 * _dpiScale)),
            ForeColor = TextDim
        };
        page.Controls.Add(descLabel);

        var versionLabel = new Label
        {
            Text = $"{Localization.Get("AboutVersion")}: {version}",
            AutoSize = true,
            Location = new Point(0, (int)(82 * _dpiScale)),
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
            Location = new Point(0, (int)(110 * _dpiScale))
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
        return page;
    }

    private void OnNavSelected(object? sender, EventArgs e)
    {
        _contentPanel.Controls.Clear();

        switch (_navList.SelectedIndex)
        {
            case 0: _contentPanel.Controls.Add(_generalPage); break;
            case 1: _contentPanel.Controls.Add(_colorTempPage); break;
            case 2: _contentPanel.Controls.Add(_hotkeysPage); break;
            case 3: _contentPanel.Controls.Add(_aboutPage); break;
        }
    }

    private void OnNavDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        // Background
        using var bg = new SolidBrush(selected ? BgNavSelected : BgNav);
        e.Graphics.FillRectangle(bg, e.Bounds);

        // Left accent bar for the selected item
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        // Text
        var text = _navList.Items[e.Index]?.ToString() ?? "";
        var color = selected ? TextMain : TextSub;
        using var textBrush = new SolidBrush(color);
        var textRect = new Rectangle(e.Bounds.Left + 12, e.Bounds.Top, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, e.Font, textRect, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // NOTE: no focus rectangle is drawn. The focus dotted border is
        // rendered in the system highlight color, which shows as an ugly
        // red dashed outline on the dark theme. Selection is already
        // clearly indicated by the lighter background + accent bar, so the
        // focus ring adds nothing.
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _instance = null;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        _rebuildDebounce?.Dispose();
        _rebuildDebounce = null;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _instance = null;
        base.OnFormClosed(e);
    }
}
