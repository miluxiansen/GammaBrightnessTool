using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Persistent brightness slider popup shown above the tray icon when the
/// user left-clicks the tray icon. Dismissed by clicking outside.
/// </summary>
public sealed class BrightnessPopup : Form
{
    private readonly Label _label;
    private readonly Panel _sliderHitArea;
    private readonly RoundedButton _powerButton;
    private readonly ToggleSwitch _modeSwitch;   // 亮度/色温 滑动开关
    private readonly RoundedButton _settingsButton;   // 齿轮图标，打开设置窗口
    private PowerTipForm? _powerTip;
    private PowerTipForm? _modeTip;
    private PowerTipForm? _settingsTip;
    private System.Windows.Forms.Timer? _tipHideTimer;
    private const int TipHideDelayMs = 250;
    private const int TipHideDelayShortMs = 150;
    private bool _isDragging;
    // 色温拖动"6600 稍作停留"状态（参数 1340 实测定稿）：
    // 指针跨过 6600 时值锁 6600 约 TempDwellMs，之后 ≤100K/事件追回指针格。
    private long _tempPauseUntilMs;      // Environment.TickCount64 截止时刻，0=无停留
    private bool _tempCatchActive;       // 停留后/离开 6600 后的逐格追赶中
    private System.Windows.Forms.Timer? _tempDwellTimer;   // 松手后播完停留再落定
    private const int TempDwellMs = 140;   // 跨过 6600 的轻顿时长（200/360ms 实测偏重，140ms 定稿）
    private const float TempDwellEarlyExitK = 900f;  // 停留中指针推出 6600 超过此值 → 立即解除，不"拽住不让走"
    private int _currentPercentage = 100;
    private readonly int _baseWidth = 140;   // Slightly wider than OSD

    // The base label font size (computed from DPI) used to detect when
    // FitLabelFont() needs to shrink the label to fit long localized strings.
    private float _labelBaseFontSize;

    /// <summary>
    /// Per-wheel-notch brightness step (0..1) as configured in settings;
    /// used by the wheel handlers here so the popup honors the same
    /// step as the wheel OSD path instead of a fixed 5%.
    /// </summary>
    public float StepSize { get; set; } = 0.05f;
    /// <summary>Per-notch color-temperature step (K), driven by the settings UI (50~3000).</summary>
    public float TemperatureStepSize { get; set; } = GammaController.DEFAULT_TEMPERATURE_STEP;
    /// <summary>Configurable color-temperature range [MinTemperature, MaxTemperature].</summary>
    public float MinTemperature { get; set; } = GammaController.MIN_TEMPERATURE;
    public float MaxTemperature { get; set; } = GammaController.MAX_TEMPERATURE;
    private readonly int _baseHeight = 60;   // Label + slider + power-off button, compact
    private int _lastLayoutDpi;
    /// <summary>上次布局用的物理宽度（跨不同 DPI 显示器弹出时会变化）。</summary>
    private int _lastLayoutWidth;
    // Tracks the physical size the rounded-corner Region was last built for.
    // Do NOT compare against WinForms Width/Height here: after WM_DPICHANGED
    // the framework auto-scales the window (and the Region) before our 200 ms
    // poll runs, so Width/Height already equal the new values and the rebuild
    // guard would be skipped, leaving the framework-scaled (misaligned) region.
    private Size _lastRegionSize;

    public event EventHandler<float>? OnBrightnessChanged;

    /// <summary>
    /// 3.6.0: 多屏模式下某一行（某屏）的亮度/色温变化。Args: (edidId, brightness, temperatureK)。
    /// 单屏模式不使用（走 OnBrightnessChanged/OnTemperatureChanged）。
    /// </summary>
    public event Action<string, float, float>? OnDisplayRowChanged;

    /// <summary>
    /// 独立控制模式下，托盘滚轮请求对所有启用屏做一次等步长偏移。
    /// Args: sign（+1/-1）。由 MainController 决定亮度或色温并回灌各行。
    /// </summary>
    public event Action<int>? OnPerMonitorWheel;


    /// <summary>
    /// Raised when the user changes the color temperature via the slider
    /// (only while in temperature mode). Value is kelvin (1000~10000).
    /// </summary>
    public event EventHandler<float>? OnTemperatureChanged;

    /// <summary>
    /// 弹窗滑块当前控制的模式：true=色温，false=亮度。
    /// </summary>
    public enum SliderMode
    {
        Brightness,
        Temperature
    }

    private SliderMode _mode = SliderMode.Brightness;
    private float _currentTemperatureK = GammaController.DEFAULT_TEMPERATURE;

    /// <summary>
    /// 色温调节总开关。false 时弹窗隐藏模式开关、电源按钮变为全宽圆角按钮、滑块只调亮度。
    /// </summary>
    private bool _temperatureEnabled = false;
    public bool TemperatureEnabled
    {
        get => _temperatureEnabled;
        set
        {
            if (_temperatureEnabled == value) return;
            _temperatureEnabled = value;
            if (IsDisposed) return;
            // 关闭时强制回到亮度模式（滑块只调亮度）
            if (!value && _mode == SliderMode.Temperature)
            {
                _mode = SliderMode.Brightness;
                _modeSwitch.Checked = false;
                _modeSwitch.Mode = ModeIconKind.Brightness;
            }
            ApplyBottomRowLayout();
            UpdateLabelAndSlider();
            PushModeToRows();
        }
    }

    /// <summary>
    /// 从 GammaController 同步最新亮度/色温到弹窗显示（外部路径：快捷键、滚轮等）。
    /// 弹窗不可见时只更新内部状态，下次 ShowAbove 自然读到新值。
    /// </summary>
    public void SyncFromGamma(float brightness, float temperatureK)
    {
        _currentPercentage = (int)Math.Round(brightness * 100);
        _currentTemperatureK = temperatureK;
        if (Visible)
        {
            UpdateLabelAndSlider();
        }
    }


    /// <summary>
    /// Raised when the popup's visibility changes (shown/hidden).
    /// MainController uses this to start/stop the icon-rect polling timer
    /// that keeps the popup anchored to the tray icon.
    /// </summary>
    public event EventHandler? OnShownChanged;

    public BrightnessPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        float dpiScale = DeviceDpi / 96.0f;
        Size = new Size((int)(_baseWidth * dpiScale), (int)(_baseHeight * dpiScale));
        _lastLayoutDpi = DeviceDpi;
        BackColor = ThemeManager.PopupBg;
        ForeColor = ThemeManager.PopupText;
        // 半透明(0.9)不在构造时设置，而是等首次 Show() 且内容已自绘后再施加
        // （见 _opacityApplied / ShowAbove）：Opacity<1 会使窗口从创建起就是
        // 分层(WS_EX_LAYERED)窗口，重启后首次显示时空分层表面会被系统以默认
        // 白底合成首帧 → 深色弹窗"闪一下白"。先不透明地完成首帧绘制、再开启
        // 半透明，分层属性施加在已有内容上，不再有白底首帧。
        // Opacity = 0.9;
        TopMost = true;

        // Repaint with the new palette when the app theme changes while the
        // popup is open (e.g. user switches dark/light in the settings).
        ThemeManager.PopupThemeChanged += OnThemeChanged;

        // Refresh the value label when the UI language changes while the
        // popup is open (e.g. user switches language in the settings).
        Localization.LanguageChanged += OnLanguageChanged;

        ApplyRoundedCorners(8, Width, Height);
        _lastRegionSize = new Size(Width, Height);

        // Layout parameters mirror the wheel OSD (BrightnessOverlay) which the
        // user confirmed looks right: small font (7pt base), generous label
        // height (20px base) and loose spacing. The left-click popup previously
        // used an 8pt font in an 18px label: at 150% DPI that becomes 12pt text
        // in an 18px label, so the glyphs nearly filled the label and their
        // bottom strokes collided with the slider hit area below (fake-
        // transparent panels erase what they cover).
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;

        // Percentage label - same font size as the wheel OSD
        int fontSize = Math.Max(7, (int)(7 * dpiScale));
        _labelBaseFontSize = fontSize;
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

        // The visual bar's Y inside the popup (label bottom + gap).
        int barY = topPadding + labelHeight + gap;

        // Slider hit area (invisible, captures mouse events for dragging).
        // Starts at the label's bottom edge (barY - gap) so it never overlaps
        // the text; combined with the small font the text cannot be covered.
        // OPAQUE (window base color) and fully self-draws the slider (gray
        // track + white fill + thumb circle) in SliderHitArea_Paint. The old
        // design used a separate semi-transparent progress Panel + Dock=Left
        // fill child below this panel; that fake-transparent panel repaints
        // the parent background when its fill width changes, erasing the
        // thumb circle painted on the hit area above it.
        _sliderHitArea = new Panel
        {
            BackColor = ThemeManager.PopupBg,
            Location = new Point(margin, barY - gap),
            // Height: the hit area must end exactly where the bottom row
            // starts (rowY = barY + barHeight + 4dpi). The bar itself is
            // only barHeight tall, so the extra ~4px below it is the drag
            // cushion. It must NOT reach past rowY or it will overlap and
            // occlude the mode switch / power button below.
            Size = new Size(contentWidth, barHeight + (int)(4 * dpiScale)),
            Cursor = Cursors.Hand
        };

        _sliderHitArea.MouseDown += SliderHitArea_MouseDown;
        _sliderHitArea.MouseMove += SliderHitArea_MouseMove;
        _sliderHitArea.MouseUp += SliderHitArea_MouseUp;
        _sliderHitArea.MouseWheel += SliderHitArea_MouseWheel;
        _sliderHitArea.Paint += SliderHitArea_Paint;

        // Bottom row: mode toggle switch (left) + compact rounded power
        // button (right). The slider above keeps its exact position/size;
        // the mode switch only changes what the slider controls.
        int rowY = barY + barHeight + (int)(4 * dpiScale);
        int bottomPadding = (int)(4 * dpiScale);
        int rowHeight = Math.Max(18, ClientSize.Height - rowY - bottomPadding);
        int rowTop = rowY;

        // 1) Mode toggle switch: 亮度 ⇄ 色温 (sliding switch, no text).
        // Sized like the settings form's 44x22 so the mode icon fits on
        // the knob. The knob carries the ACTIVE mode's icon (sun for
        // brightness, gradient ring for temperature), so the switch
        _modeSwitch = new ToggleSwitch
        {
            Checked = false,   // false = brightness mode (default)
            TabStop = false,
            ShowKnobIcon = true,   // draw the mode glyph on the knob
            UsePopupTheme = true  // 弹窗开关跟随弹窗主题
        };
        _modeSwitch.Mode = ModeIconKind.Brightness;
        _modeSwitch.CheckedChanged += ModeSwitch_CheckedChanged;
        _modeSwitch.MouseEnter += (s, e) => ShowModeTip();
        _modeSwitch.MouseLeave += (s, e) => HideModeTip();

        _powerButton = new RoundedButton
        {
            Text = string.Empty,   // icon drawn via Image (language-independent)
            Cursor = Cursors.Hand,
            TabStop = false,
            FlatStyle = FlatStyle.Flat
        };
        _powerButton.ApplyTheme(ThemeManager.PopupBtnBg, ThemeManager.PopupText,
            ThemeManager.PopupBtnBorder, ThemeManager.PopupBtnHover, ThemeManager.PopupBtnDown);
        _powerButton.SetParentBackground(ThemeManager.PopupBg);
        _powerButton.Click += (s, e) => PowerOffDisplay();
        _powerButton.MouseEnter += (s, e) => ShowPowerTip();
        _powerButton.MouseLeave += (s, e) => HidePowerTip();

        _settingsButton = new RoundedButton
        {
            Text = string.Empty,   // gear icon via Image
            Cursor = Cursors.Hand,
            TabStop = false,
            FlatStyle = FlatStyle.Flat
        };
        _settingsButton.ApplyTheme(ThemeManager.PopupBtnBg, ThemeManager.PopupText,
            ThemeManager.PopupBtnBorder, ThemeManager.PopupBtnHover, ThemeManager.PopupBtnDown);
        _settingsButton.SetParentBackground(ThemeManager.PopupBg);
        _settingsButton.Click += (s, e) => SettingsForm.ShowOrActivate();
        _settingsButton.MouseEnter += (s, e) => ShowSettingsTip();
        _settingsButton.MouseLeave += (s, e) => HideSettingsTip();

        Controls.Add(_label);
        Controls.Add(_sliderHitArea);
        Controls.Add(_modeSwitch);
        Controls.Add(_powerButton);
        Controls.Add(_settingsButton);

        // Bottom row layout is computed by ApplyBottomRowLayout(),
        // which switches between the compact (switch + small power)
        // and full-width (power button only) arrangements.
        ApplyBottomRowLayout();

        // Click-outside dismissal is handled by the global mouse hook
        // (GlobalMouseHook checks whether the click landed outside this
        // window and calls Dismiss). Deactivate is NOT used because the
        // popup never takes focus (ShowWithoutActivation = true) and
        // relying on focus loss is unreliable.
    }

    /// <summary>
    /// True while the popup is visible on screen.
    /// </summary>
    /// <summary>
    /// 计算并应用底部行布局。色温开启：模式开关（左）+ 小圆角电源按钮（右）；
    /// 色温关闭：无模式开关，电源按钮占满整行（全宽圆角）。
    /// </summary>
    private void ApplyBottomRowLayout(int dpi = 0)
    {
        if (IsDisposed) return;
        // 未显式指定时优先用图标所在显示器的真实 DPI。DeviceDpi 在隐藏窗口上是
        // 陈旧值，多显示器不同缩放时会让底部行与滑轨行按不同比例计算而错位。
        int layoutDpi = dpi > 0 ? dpi : (_lastLayoutDpi > 0 ? _lastLayoutDpi : DeviceDpi);
        float dpiScale = layoutDpi / 96.0f;
        int margin = (int)(6 * dpiScale);
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;
        // 底部行顶端必须落在全部滑轨行之下。原先此处硬编码单行几何
        // （标签 + 间隙 + 滑轨 + 4px），多屏模式下底部按钮被抬高、高度又被
        // ClientSize.Height 拉长到覆盖整段滑轨区 → 按钮渲染错乱。
        int rowY = GetBottomRowTop(dpiScale);
        int bottomPadding = (int)(4 * dpiScale);
        int rowHeight = Math.Max(18, ClientSize.Height - rowY - bottomPadding);
        int rowTop = rowY;


        if (_temperatureEnabled)
        {
            // 色温开启：模式开关（左）+ 设置按钮（中）+ 电源按钮（右）
            int iconSize = Math.Max(14, (int)(16 * dpiScale));
            int modeSwitchW = (int)(44 * dpiScale);
            int modeSwitchH = (int)(22 * dpiScale);
            int modeRowH = Math.Max(modeSwitchH, iconSize);
            int modeY = rowTop + Math.Max(0, (rowHeight - modeRowH) / 2);
            _modeSwitch.Location = new Point(margin, modeY + (modeRowH - modeSwitchH) / 2);
            _modeSwitch.Size = new Size(modeSwitchW, modeSwitchH);
            _modeSwitch.Visible = true;

            int powerSize = Math.Max(18, rowHeight);
            int powerX = margin + contentWidth - powerSize;
            int powerY = rowTop + Math.Max(0, (rowHeight - powerSize) / 2);
            // 设置按钮紧贴模式开关右侧；两按钮宽度均分空隙，填充中间空间
            int gapBetween = Math.Max(2, (int)(4 * dpiScale));
            int settingsX = margin + modeSwitchW + gapBetween;
            int settingsY = powerY;
            int minGap = Math.Max(2, (int)(2 * dpiScale));
            int freeSpace = powerX - (settingsX + powerSize);
            int grow = Math.Max(0, (freeSpace - minGap) / 2);
            int settingsW = powerSize + grow;
            int powerW = powerSize + grow;
            int powerX2 = margin + contentWidth - powerW;
            _settingsButton.Location = new Point(settingsX, settingsY);
            _settingsButton.Size = new Size(settingsW, powerSize);
            _settingsButton.CornerRadius = Math.Max(4, (int)(5 * dpiScale));
            _settingsButton.Visible = true;
            SetGearIcon(_settingsButton, (int)(24 * dpiScale));
            _powerButton.Location = new Point(powerX2, powerY);
            _powerButton.Size = new Size(powerW, powerSize);
            _powerButton.CornerRadius = Math.Max(4, (int)(5 * dpiScale));
            _powerButton.Image?.Dispose();
            _powerButton.Image = CreatePowerIcon((int)(18 * dpiScale), (int)(18 * dpiScale));
        }
        else
        {
            // 色温关闭：设置按钮（左半）+ 电源按钮（右半）
            _modeSwitch.Visible = false;
            int halfW = (contentWidth - (int)(4 * dpiScale)) / 2;
            int btnH = rowHeight;

            _settingsButton.Location = new Point(margin, rowTop);
            _settingsButton.Size = new Size(halfW, btnH);
            _settingsButton.CornerRadius = Math.Max(6, (int)(8 * dpiScale));
            _settingsButton.Visible = true;
            SetGearIcon(_settingsButton, (int)(24 * dpiScale));

            _powerButton.Location = new Point(margin + halfW + (int)(4 * dpiScale), rowTop);
            _powerButton.Size = new Size(halfW, btnH);
            _powerButton.CornerRadius = Math.Max(6, (int)(8 * dpiScale));
            _powerButton.Image?.Dispose();
            _powerButton.Image = CreatePowerIcon((int)(18 * dpiScale), (int)(18 * dpiScale));
        }
    }

    public bool IsShown => Visible;

    /// <summary>当前滑块是否处于色温模式（供快捷键与滚轮同源判断）。</summary>
    public bool IsTemperatureMode => _mode == SliderMode.Temperature;

    /// <summary>禁用模式（右键菜单"禁用"）下滑块不可拖动；由 MainController 注入。</summary>
    public Func<bool>? IsDisableActive { get; set; }

    private void ShowPowerTip()
    {
        _tipHideTimer?.Stop();
        if (_powerTip == null)
        {
            _powerTip = new PowerTipForm();
            // The tip is a pure indicator: it must NOT keep itself alive
            // while hovered, or it would sit over the power button / mode
            // switch and block clicks. It hides as soon as the mouse leaves
            // the source control (see ScheduleTipHide).
        }

        // Re-apply the localized text on every show so the tip follows a
        // language switch made while the popup instance already existed.
        _powerTip.SetText(Localization.Get("PowerOffDisplayTip"));

        GetWindowRect(_powerButton.Handle, out var rc);
        _powerTip.ShowNear(new Rectangle(rc.Left, rc.Top, rc.Width, rc.Height), this);
    }

    private void ScheduleTipHide(int delayMs)
    {
        if (_tipHideTimer == null)
        {
            _tipHideTimer = new System.Windows.Forms.Timer();
            _tipHideTimer.Tick += (s, e) =>
            {
                _tipHideTimer.Stop();
                HidePowerTip();
                HideModeTip();
                HideSettingsTip();
            };
        }
        _tipHideTimer.Interval = delayMs;
        _tipHideTimer.Start();
    }

    private void HidePowerTip()
    {
        _tipHideTimer?.Stop();
        if (_powerTip != null && _powerTip.Visible)
        {
            _powerTip.Hide();
        }
    }

    private void HideModeTip()
    {
        if (_modeTip != null && _modeTip.Visible)
        {
            _modeTip.Hide();
        }
    }

    private void HideSettingsTip()
    {
        if (_settingsTip != null && _settingsTip.Visible)
        {
            _settingsTip.Hide();
        }
    }

    /// <summary>
    /// Repaints the popup with the current theme palette. Called when the
    /// app theme changes while the popup is open; all colors are derived
    /// from ThemeManager at paint/assign time, so this just re-assigns the
    /// control-level colors and invalidates the self-drawn slider.
    /// </summary>
    /// <summary>
    /// Refreshes the value label when the UI language changes while the
    /// popup is open. The slider position/value itself is language-independent.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnLanguageChanged(sender, e)));
            return;
        }
        UpdateLabelAndSlider();
        if (_modeTip != null && _modeTip.Visible)
        {
            _modeTip.SetText(Localization.Get(_mode == SliderMode.Temperature ? "TemperatureMode" : "BrightnessMode"));
        }
    }

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
        // 色温模式且停在 6600K 时标签保持默认色高亮
        bool tempAtDefault = _mode == SliderMode.Temperature &&
            Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
        _label.ForeColor = tempAtDefault ? DefaultTempHighlight : ThemeManager.PopupText;
        _sliderHitArea.BackColor = ThemeManager.PopupBg;
        _sliderHitArea.Invalidate();

        // Mode switch: follows the popup theme now (UsePopupTheme) -
        // repaint so the track recolors; knob icon cache keys on theme too.
        _modeSwitch.Invalidate();
        // Power button is now a RoundedButton: re-apply its theme palette.
        _powerButton.ApplyTheme(ThemeManager.PopupBtnBg, ThemeManager.PopupText,
            ThemeManager.PopupBtnBorder, ThemeManager.PopupBtnHover, ThemeManager.PopupBtnDown);
        _powerButton.SetParentBackground(ThemeManager.PopupBg);
        _powerButton.Image?.Dispose();
        _powerButton.Image = CreatePowerIcon((int)(18 * DeviceDpi / 96.0f), (int)(18 * DeviceDpi / 96.0f));

        // Settings button: re-apply its palette AND reload the gear
        // icon so it flips black/white with the popup theme.
        _settingsButton.ApplyTheme(ThemeManager.PopupBtnBg, ThemeManager.PopupText,
            ThemeManager.PopupBtnBorder, ThemeManager.PopupBtnHover, ThemeManager.PopupBtnDown);
        _settingsButton.SetParentBackground(ThemeManager.PopupBg);
            int gearSize = (int)(24 * DeviceDpi / 96.0f);
        SetGearIcon(_settingsButton, gearSize);
    }

    /// <summary>
    /// Applies the light-mode 1px border around the power button so its
    /// outline stays visible against the light popup background (dark mode
    /// keeps the borderless look).
    /// </summary>
    [Obsolete("Power button is now a RoundedButton; border is handled by ApplyTheme.")]
    private void ApplyPowerButtonBorder()
    {
        // Kept for reference; no longer used.
    }

    /// <summary>
    /// 模式开关切换：亮度 ⇄ 色温。切换时滑块 thumb 跳到新模式当前值位置，
    /// 标签文本同步切换（百分比 ⇄ 色温K）。
    /// </summary>
    private void ModeSwitch_CheckedChanged(object? sender, EventArgs e)
    {
        _mode = _modeSwitch.Checked ? SliderMode.Temperature : SliderMode.Brightness;
        _modeSwitch.Mode = _mode == SliderMode.Temperature ? ModeIconKind.Temperature : ModeIconKind.Brightness;
        UpdateLabelAndSlider();
        PushModeToRows();
        // If the tip is visible, refresh its text for the new mode.
        if (_modeTip != null && _modeTip.Visible)
        {
            _modeTip.SetText(Localization.Get(_mode == SliderMode.Temperature ? "TemperatureMode" : "BrightnessMode"));
        }
    }

    /// <summary>
    /// 把当前模式（亮度/色温）与温度范围下发给每一屏滑轨行，使行与底部切换开关联动。
    /// 色温模式下每行滑轨显示/调节该屏自己的色温（值文本改显 K）。
    /// </summary>
    private void PushModeToRows()
    {
        if (!_perMonitorEnabled || _rowControls.Count == 0) return;
        bool temp = _mode == SliderMode.Temperature;
        foreach (var row in _rowControls)
        {
            row.SetTemperatureContext(temp, MinTemperature, MaxTemperature, TemperatureStepSize);
        }
    }

    /// <summary>
    /// Shows the mode-switch tooltip ("亮度" / "色温") anchored below the
    /// switch, mirroring the power-button tip behavior.
    /// </summary>
    private void ShowModeTip()
    {
        _tipHideTimer?.Stop();
        if (_modeTip == null)
        {
            _modeTip = new PowerTipForm();
            // Same policy as the power tip: hide as soon as the mouse
            // leaves the switch, never keep the tip alive on hover.
        }

        _modeTip.SetText(Localization.Get(_mode == SliderMode.Temperature ? "TemperatureMode" : "BrightnessMode"));
        GetWindowRect(_modeSwitch.Handle, out var rc);
        _modeTip.ShowNear(new Rectangle(rc.Left, rc.Top, rc.Width, rc.Height), this);
    }

    private void ShowSettingsTip()
    {
        _tipHideTimer?.Stop();
        if (_settingsTip == null)
        {
            _settingsTip = new PowerTipForm();
        }
        _settingsTip.SetText(Localization.Get("Settings"));
        GetWindowRect(_settingsButton.Handle, out var rc);
        _settingsTip.ShowNear(new Rectangle(rc.Left, rc.Top, rc.Width, rc.Height), this);
    }

    /// <summary>
    /// 刷新标签与滑块绘制以匹配当前模式。
    /// </summary>
    private void UpdateLabelAndSlider()
    {
        if (_mode == SliderMode.Temperature)
        {
            RefreshTempLabel();
        }
        else
        {
            _label.Text = $"{_currentPercentage}%";
            _label.ForeColor = ThemeManager.PopupText;
            FitLabelFont();
        }
        _sliderHitArea.Invalidate();
    }

    /// <summary>
    /// Shrinks the label font (down to a 6pt floor) when the current text
    /// does not fit the label width, so long localized strings (e.g. French
    /// "Temp. couleur 10000K") stay fully visible at high DPI. Restores the
    /// base size whenever the text fits again.
    /// </summary>
    private void FitLabelFont()
    {
        if (_labelBaseFontSize <= 0) return;
        if (IsDisposed) return;

        float target = _labelBaseFontSize;
        while (target > 6.0f)
        {
            // Measure through a probe Label so we get the exact same
            // PreferredSize semantics as the real control (TextRenderer +
            // internal padding), which is what actually clips on screen.
            using var probe = new Label
            {
                Font = new Font(_label.Font.FontFamily.Name, target, _label.Font.Style),
                AutoSize = false,
                Text = _label.Text,
            };
            var need = probe.PreferredSize.Width;
            if (need <= _label.Width) break;
            target -= 0.5f;
        }

        if (Math.Abs(_label.Font.SizeInPoints - target) > 0.01f)
        {
            // 先保存旧字体引用再释放，避免访问已释放的 Font 对象
            var oldFont = _label.Font;
            string family = oldFont?.FontFamily.Name ?? "Segoe UI";
            var style = oldFont?.Style ?? FontStyle.Regular;
            oldFont?.Dispose();
            _label.Font = new Font(family, target, style);
        }
    }

    private void SliderHitArea_MouseDown(object? sender, MouseEventArgs e)
    {
        if (IsDisableActive?.Invoke() == true) return;
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            // 捕获鼠标：命中区只有约 8px 高，不捕获的话指针略一移出即收不到
            // MouseMove/MouseUp，拖动会"脱节"（松手事件也丢失，拖后值不落定）。
            _sliderHitArea.Capture = true;
            // 取消上一松手遗留的"播完停留再落定"定时器
            _tempDwellTimer?.Dispose();
            _tempDwellTimer = null;
            UpdateValueFromMouse(e.X);
        }
    }

    private void SliderHitArea_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var sliderPos = _sliderHitArea.PointToClient(Cursor.Position);
            UpdateValueFromMouse(sliderPos.X);
        }
    }

    private void SliderHitArea_MouseUp(object? sender, MouseEventArgs e)
    {
        _isDragging = false;
        if (_sliderHitArea.Capture) _sliderHitArea.Capture = false;
        long now = Environment.TickCount64;
        if (_mode == SliderMode.Temperature && now < _tempPauseUntilMs)
        {
            // 快速扫过时停留尚未播完就松手：不截断。让 6600 的停顿播完
            // （用一次性 Timer），再落定到指针所在格——保证任何拖速下经过
            // 6600 都完整可见那一下停留。
            _tempDwellTimer?.Dispose();
            var t = new System.Windows.Forms.Timer { Interval = Math.Max(16, (int)(_tempPauseUntilMs - now)) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                t.Dispose();
                if (ReferenceEquals(_tempDwellTimer, t)) _tempDwellTimer = null;
                FinalizeTempToPointer();
            };
            _tempDwellTimer = t;
            t.Start();
        }
        else
        {
            FinalizeTempToPointer();
        }
    }

    /// <summary>松手落定：结束停留/追赶并把温度值归位到指针所在 100K 格。
    /// 例外：松手时值正停在 6600（用户在此松手意图即停在 6600），保持 6600，
    /// 不跳到指针格（否则刚吸附到 6600 一松手却变 6400/6800 等周围值）。</summary>
    private void FinalizeTempToPointer()
    {
        _tempPauseUntilMs = 0;
        _tempCatchActive = false;
        if (_mode != SliderMode.Temperature) return;
        if (Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f) return;   // 停在 6600 → 保持
        var p = _sliderHitArea.PointToClient(Cursor.Position);
        float ratio = Math.Max(0f, Math.Min(1f, (float)p.X / _sliderHitArea.Width));
        float final = Math.Clamp(Round100K(MinTemperature + ratio * (MaxTemperature - MinTemperature)), MinTemperature, MaxTemperature);
        if (Math.Abs(final - _currentTemperatureK) >= 0.5f) UpdateTemperature(final);
    }

    private void SliderHitArea_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (IsDisableActive?.Invoke() == true) return;
        int delta = Math.Sign(e.Delta);
        if (_mode == SliderMode.Temperature)
        {
            float newTemp = _currentTemperatureK + delta * TemperatureStepSize;
            newTemp = Math.Clamp(newTemp, MinTemperature, MaxTemperature);
            UpdateTemperature(newTemp);
        }
        else
        {
            int newPercentage = Math.Max(0, Math.Min(100, _currentPercentage + delta * Math.Max(1, (int)Math.Round(StepSize * 100))));
            UpdateBrightness(newPercentage);
        }
    }

    /// <summary>
    /// Adjusts the slider value by a wheel delta, used when the wheel is
    /// scrolled over the tray icon while this popup is open: the popup
    /// stays open and its slider/value move instead of showing the wheel OSD.
    /// Follows the current mode (brightness % or temperature K).
    /// </summary>
    public void AdjustByWheel(int wheelDelta)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<int>(AdjustByWheel), wheelDelta);
            return;
        }
        if (IsDisableActive?.Invoke() == true) return;

        int sign = Math.Sign(wheelDelta);

        // 独立控制（per-monitor）模式：滑轨按屏独立，托盘滚轮应作用于所有启用屏
        // （各屏基于自己当前值偏移），并把新值回灌到各屏行控件。这里交给 MainController
        // 统一处理（改 gamma 后重建行），而不是调整被隐藏的单条主滑块。
        if (_perMonitorEnabled)
        {
            OnPerMonitorWheel?.Invoke(sign);
            return;
        }

        if (_mode == SliderMode.Temperature)
        {
            float newTemp = _currentTemperatureK + sign * TemperatureStepSize;
            newTemp = Math.Clamp(newTemp, MinTemperature, MaxTemperature);
            UpdateTemperature(newTemp);
        }
        else
        {
            int newPercentage = Math.Max(0, Math.Min(100, _currentPercentage + sign * Math.Max(1, (int)Math.Round(StepSize * 100))));
            UpdateBrightness(newPercentage);
        }
    }
    private void UpdateValueFromMouse(int mouseX)
    {
        float ratio = Math.Max(0f, Math.Min(1f, (float)mouseX / _sliderHitArea.Width));
        if (_mode == SliderMode.Temperature)
        {
            float newTemp = MinTemperature +
                ratio * (MaxTemperature - MinTemperature);
            // 6600"稍作停留"实现（两处滑轨一致）：值跟随 100K 网格；每当指针
            // 进入 6600 格或一步跨过 6600（任意拖速），值轻顿 TempDwellMs(200ms)；
            // 停留中指针若明显推出（>±900K）立即解除，不"拽住不让走"；结束后
            // ≤100K/事件逐格追回指针格，无大跳档。
            long now = Environment.TickCount64;
            float grid = Round100K(newTemp);
            bool curAtNeutral = Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
            // 触发"稍作停留"的两种情形（覆盖任意拖速）：
            //  1) 进入 6600 格：本次目标即 6600（慢速拖时指针先进入 ±50 格，
            //     值先落 6600，根本不会产生"跨过 6600 精确点"的事件 → 必须按
            //     目标格触发，否则慢速永不触发）；
            //  2) 一步跨过 6600：快速拖时事件直接跨过精确点。
            bool enteringNeutralCell = !curAtNeutral && Math.Abs(grid - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
            bool crossingOverNeutral =
                !curAtNeutral &&
                ((_currentTemperatureK < GammaController.DEFAULT_TEMPERATURE && newTemp >= GammaController.DEFAULT_TEMPERATURE) ||
                 (_currentTemperatureK > GammaController.DEFAULT_TEMPERATURE && newTemp <= GammaController.DEFAULT_TEMPERATURE));
            if (enteringNeutralCell || crossingOverNeutral)
            {
                _tempPauseUntilMs = now + TempDwellMs;
                _tempCatchActive = false;
            }

            float snapped;
            bool pausedNow = now < _tempPauseUntilMs;
            if (pausedNow && Math.Abs(newTemp - GammaController.DEFAULT_TEMPERATURE) > TempDwellEarlyExitK)
            {
                // 指针已明显推出 6600（快速硬推）：立即解除停留，不产生"拽住"感
                _tempPauseUntilMs = 0;
                pausedNow = false;
            }
            if (pausedNow)
            {
                snapped = GammaController.DEFAULT_TEMPERATURE;   // 轻顿一拍
            }
            else if (curAtNeutral && Math.Abs(grid - GammaController.DEFAULT_TEMPERATURE) >= 1f)
            {
                // 离开 6600（含停留结束 / 本就按在 6600 上后拖离）：只先走相邻一格
                snapped = grid > GammaController.DEFAULT_TEMPERATURE
                    ? Math.Min(grid, GammaController.DEFAULT_TEMPERATURE + GammaController.TEMPERATURE_STEP)
                    : Math.Max(grid, GammaController.DEFAULT_TEMPERATURE - GammaController.TEMPERATURE_STEP);
                _tempCatchActive = Math.Abs(snapped - grid) >= 1f;
                if (!_tempCatchActive) _tempPauseUntilMs = 0;
            }
            else if (_tempCatchActive)
            {
                // 逐格追赶指针格（≤100K/事件），追平后恢复直接跟随
                if (Math.Abs(grid - _currentTemperatureK) < 1f)
                {
                    snapped = grid;
                    _tempCatchActive = false;
                    _tempPauseUntilMs = 0;
                }
                else
                {
                    snapped = grid > _currentTemperatureK
                        ? Math.Min(grid, _currentTemperatureK + GammaController.TEMPERATURE_STEP)
                        : Math.Max(grid, _currentTemperatureK - GammaController.TEMPERATURE_STEP);
                    if (Math.Abs(snapped - grid) < 1f)
                    {
                        _tempCatchActive = false;
                        _tempPauseUntilMs = 0;
                    }
                }
            }
            else
            {
                snapped = grid;
                _tempPauseUntilMs = 0;
            }
            newTemp = Math.Clamp(snapped, MinTemperature, MaxTemperature);
            UpdateTemperature(newTemp);
        }
        else
        {
            int percentage = (int)Math.Round(ratio * 100);
            percentage = Math.Max(0, Math.Min(100, percentage));
            UpdateBrightness(percentage);
        }
    }
    /// <summary>色温拖动取值：四舍五入到最近 100K 格（6550→6600 边界语义）。</summary>
    private static float Round100K(float kelvin)
        => (float)(Math.Round(kelvin / GammaController.TEMPERATURE_STEP, MidpointRounding.AwayFromZero) * GammaController.TEMPERATURE_STEP);

    /// <summary>6600K（默认色温）在标签/行值上的强调色，与弹窗开关的强调蓝一致。</summary>
    private static readonly Color DefaultTempHighlight = Color.FromArgb(0, 120, 215);

    /// <summary>
    /// 刷新温度主标签：值=6600K 时文字着强调蓝（用户指定，不加"(默认)"后缀），
    /// 离开 6600 恢复 PopupText。蓝色 + 值停留 360ms 共同构成"经过默认点"的提示。
    /// </summary>
    private void RefreshTempLabel()
    {
        bool neutral = Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
        _label.Text = neutral
            ? $"{(int)_currentTemperatureK}K"
            : $"{_currentTemperatureK:0}K";
        _label.ForeColor = neutral ? DefaultTempHighlight : ThemeManager.PopupText;
        FitLabelFont();
    }

    private void UpdateTemperature(float kelvin)
    {
        float snapped = Math.Clamp(kelvin, MinTemperature, MaxTemperature);
        if (Math.Abs(snapped - _currentTemperatureK) < 0.5f) return;

        _currentTemperatureK = snapped;
        RefreshTempLabel();
        _sliderHitArea.Invalidate();

        OnTemperatureChanged?.Invoke(this, _currentTemperatureK);
    }

    private void UpdateBrightness(int percentage)
    {
        if (percentage == _currentPercentage) return;

        _currentPercentage = percentage;
        _label.Text = $"{percentage}%";
        _label.ForeColor = ThemeManager.PopupText;   // 离开色温模式恢复普通文字色
        FitLabelFont();

        // The slider is fully self-drawn by SliderHitArea_Paint; just
        // invalidate so the fill and thumb redraw at the new level.
        _sliderHitArea.Invalidate();

        OnBrightnessChanged?.Invoke(this, percentage / 100f);
    }

    /// <summary>
    /// Rebuilds the popup's internal control layout (label, slider hit
    /// area, power button) for the current DeviceDpi, and refreshes the
    /// cached font. Called when the popup's DPI changes while on screen
    /// (WM_DPICHANGED) so the controls scale together with the window.
    /// Without this the controls keep their original (constructor) layout
    /// while WinForms auto-scales the window size 鈥?the classic "wrong
    /// after DPI change, correct after restart" symptom.
    /// </summary>
    /// <param name="dpi">The REAL DPI of the icon's monitor, queried live
    /// (see <see cref="GetIconMonitorDpi"/>). DeviceDpi is stale while the
    /// hidden popup receives no WM_DPICHANGED.</param>
    /// <param name="widthPx">Target physical client width for this DPI, so
    /// the layout matches the size PositionAbove will apply. Using the
    /// stale ClientSize would misplace controls after a DPI change.</param>
    private void ApplyLayoutForCurrentDpi(int dpi, int widthPx)
    {
        // 宽度也要参与判断：DPI 未变但跨到另一台（同缩放）显示器时窗口宽度不变，
        // 而 DPI 与宽度任一变化都必须重算布局，否则行控件与窗口尺寸脱节。
        bool dpiChanged = dpi != _lastLayoutDpi;
        if (!dpiChanged && widthPx == _lastLayoutWidth) return;
        _lastLayoutDpi = dpi;
        _lastLayoutWidth = widthPx;
        #if DEBUG
        PopupDebug.Log($"ApplyLayoutForCurrentDpi: dpi={dpi} width={widthPx}");
        #endif

        float dpiScale = dpi / 96.0f;
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = widthPx;
        int contentWidth = clientWidth - margin * 2;
        int barY = topPadding + labelHeight + gap;

        _label.Location = new Point(margin, topPadding);
        _label.Size = new Size(contentWidth, labelHeight);
        int fontSize = Math.Max(7, (int)(7 * dpiScale));
        _labelBaseFontSize = fontSize;
        if (_label.Font.SizeInPoints != fontSize)
        {
            _label.Font?.Dispose();
            _label.Font = new Font("Segoe UI", fontSize, FontStyle.Bold);
        }

        _sliderHitArea.Location = new Point(margin, barY - gap);
        _sliderHitArea.Size = new Size(contentWidth, barHeight + (int)(4 * dpiScale));
        _sliderHitArea.Invalidate();

        // 多屏模式：DPI 变化会改变行高，需要重建行控件；仅宽度变化（同 DPI
        // 跨显示器弹出）则同步行宽，避免行控件溢出窗口或留下空白。
        if (_perMonitorEnabled)
        {
            if (dpiChanged)
                ApplyDisplayRows();
            else
                foreach (var row in _rowControls) row.Width = contentWidth;
        }

        // Bottom row layout is handled by ApplyBottomRowLayout()
        ApplyBottomRowLayout(dpi);
        FitLabelFont();
    }

    /// <summary>
    /// Shows the popup anchored above the given tray icon rect (screen coords).
    /// </summary>
    public void ShowAbove(Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<Rectangle>(ShowAbove), iconRect);
            return;
        }

        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ShowAbove: iconRect={iconRect} dpi={dpi}");
        #endif
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
        if (!Visible)
        {
            Show();
            // 首次显示（本进程内第一次）立即同步完成首帧自绘：Opacity<1 的分层
            // 窗口在内容尚未绘制时会被系统以默认白底合成首帧 → 深色弹窗"闪一下
            // 白色"。Show() 之后马上 Update() 让整树 WM_PAINT 在返回消息循环前
            // 同步完成，首帧即主题底色，不再闪现白色。
            Update();
            ApplyOpacityAfterFirstPaint();
            OnShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Shows the popup anchored above the given tray icon rect, refreshing
    /// the percentage AND temperature displays first.
    /// </summary>
    public void ShowAbove(float brightness, float temperatureK, Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<float, float, Rectangle>(ShowAbove), brightness, temperatureK, iconRect);
            return;
        }

        _currentPercentage = (int)Math.Round(brightness * 100);
        _currentTemperatureK = temperatureK;
        UpdateLabelAndSlider();

        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ShowAbove: iconRect={iconRect} dpi={dpi}");
        #endif
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
        if (!Visible)
        {
            Show();
            // 首次显示立即同步首帧自绘，避免分层窗口（Opacity<1）以默认白底
            // 合成首帧导致深色弹窗闪现白色。
            Update();
            ApplyOpacityAfterFirstPaint();
            OnShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Re-anchors the popup above a NEW tray icon rect (screen coords).
    /// Used when the tray icon moves (DPI change) while the popup is open:
    /// the popup follows the icon to its new position. Also re-applies the
    /// physical size for the new DPI so the popup doesn't stay at the old
    /// scale. Only repositions/resizes; the brightness value is unchanged.
    /// </summary>
    public void ReanchorTo(Rectangle iconRect)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<Rectangle>(ReanchorTo), iconRect);
            return;
        }

        if (!Visible) return;

        // GetIconMonitorDpi queries the icon's monitor live, so this works
        // even when the hidden popup hasn't received WM_DPICHANGED.
        int dpi = GetIconMonitorDpi(iconRect);
        #if DEBUG
        PopupDebug.Log($"ReanchorTo: iconRect={iconRect} dpi={dpi}");
        #endif

        // PositionAbove sets both size and position using the live DPI,
        // then the internal layout is rebuilt for that same size.
        var size = PositionAbove(iconRect);
        ApplyLayoutForCurrentDpi(dpi, size.Width);
    }

    /// <summary>
    /// Draws the whole slider: gray track, white fill and the thumb circle.
    /// Everything is drawn on this opaque panel in one pass so nothing can
    /// be painted over the circle afterwards (a transparent panel would let
    /// the track below repaint on top of it when the fill shrinks).
    /// </summary>
    private void SliderHitArea_Paint(object? sender, PaintEventArgs e)
    {
        // Use the last applied layout DPI (updated live from the icon's
        // monitor) instead of DeviceDpi, which is stale while the hidden
        // popup receives no WM_DPICHANGED.
        float dpiScale = (_lastLayoutDpi > 0 ? _lastLayoutDpi : DeviceDpi) / 96.0f;
        // Recompute layout values (same formulas as the constructor).
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int barY = topPadding + labelHeight + gap;
        // The visual bar sits at barY inside the popup; relative to the hit
        // area (whose top is barY - gap) that is exactly `gap`.
        int barTop = barY - _sliderHitArea.Top;
        int barW = _sliderHitArea.Width;
        // Fill ratio follows the current mode: brightness % (0..1) or
        // temperature position within [MIN, MAX].
        float fillRatio = _mode == SliderMode.Temperature
            ? (_currentTemperatureK - MinTemperature) / (MaxTemperature - MinTemperature)
            : _currentPercentage / 100.0f;
        fillRatio = Math.Max(0f, Math.Min(1f, fillRatio));
        int fillWidth = Math.Max(1, (int)(barW * fillRatio));

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int radius = Math.Max(3, (int)(4 * dpiScale));
        int cx = Math.Min(fillWidth, barW - radius);
        cx = Math.Max(cx, radius);
        int cy = barTop + barHeight / 2;

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

        // Fill color follows the mode:
        //  - Brightness: theme fill (blue in light, white in dark).
        //  - Temperature: the color the selected kelvin actually produces
        //    (warm orange -> neutral white -> cool blue). Dragging the
        //    slider visibly changes the fill color, so it reads as "you
        //    are adjusting color temperature" at a glance.
        Color fillColor = _mode == SliderMode.Temperature
            ? ColorTemperature.FromKelvin(_currentTemperatureK)
            : ThemeManager.PopupFill;
        var fillRect = new Rectangle(0, barTop, fillWidth, barHeight);
        using (var fill = new SolidBrush(fillColor))
        using (var fillPath = RoundedRect(fillRect, trackRadius))
        {
            g.FillPath(fill, fillPath);
        }

        // Thumb circle at the fill edge. In temperature mode the fill is
        // a dynamic warm/cool color, so the thumb switches to white with a
        // subtle gray outline for contrast on any kelvin; brightness mode
        // keeps the theme thumb (blue in light, white in dark).
        bool tempMode = _mode == SliderMode.Temperature;
        Color thumbColor = tempMode ? Color.White : ThemeManager.PopupThumb;
        Color thumbOutline = tempMode ? Color.FromArgb(90, 90, 90) : ThemeManager.PopupThumbOutline;
        using (var brush = new SolidBrush(thumbColor))
        using (var pen = new Pen(thumbOutline, 1f))
        {
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
        }

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

    /// <summary>
    /// Positions the popup above the tray icon using SetWindowPos with
    /// PHYSICAL coordinates (Shell_NotifyIconGetRect returns physical pixels).
    /// WinForms Location/Screen use LOGICAL coordinates; mixing them would
    /// misplace the popup on scaled (e.g. 175%) displays.
    /// </summary>
    private Size PositionAbove(Rectangle iconRect)
    {
        // Real DPI of the monitor that hosts the icon (NOT DeviceDpi, which
        // is stale while the hidden popup receives no WM_DPICHANGED).
        int dpi = GetIconMonitorDpi(iconRect);
        float dpiScale = dpi / 96.0f;
        int physW = (int)(_baseWidth * dpiScale);
        // 多屏模式按行数计算高度（与 ApplyDisplayRows 共用 LayoutHeightForCurrentMode），
        // 否则 SetWindowPos 会把多行窗口压回 _baseHeight，导致行控件溢出、底部按钮错位。
        // 注意：LayoutHeightForCurrentMode 返回的已经是物理像素，这里不可再乘 dpiScale，
        // 否则多屏模式下窗口高度被放大 ds 倍（行控件按 ds 布局，窗口按 ds² 撑开，比例失衡）。
        int physH = LayoutHeightForCurrentMode(dpiScale);

        // Physical working area of the icon's monitor (NOT Screen.WorkingArea,
        // whose cache goes stale after a DPI change while the app runs).
        var working = GetIconMonitorWorkArea(iconRect);

        #if DEBUG
        PopupDebug.Log($"PositionAbove: iconRect={iconRect} dpi={dpi} size={physW}x{physH} working={working}");
        #endif

        int popupX = iconRect.Left + (iconRect.Width - physW) / 2;
        int popupY = iconRect.Top - physH - 6; // 6px gap above the icon

        // Clamp horizontally to the physical working area
        popupX = Math.Max(working.Left + 2, Math.Min(popupX, working.Right - physW - 2));
        if (popupY < working.Top)
        {
            // Not enough space above: place below the icon instead
            popupY = iconRect.Bottom + 6;
        }
        popupY = Math.Min(popupY, working.Bottom - physH - 2);

        #if DEBUG
        PopupDebug.Log($"PositionAbove: final=({popupX},{popupY})");
        #endif

        SetWindowPos(Handle, IntPtr.Zero, popupX, popupY, physW, physH,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        // NOTE: Location is intentionally NOT set here; SetWindowPos handles
        // physical placement. Assigning Location would re-enter WinForms'
        // logical-coordinate scaling and double-shift the window.

        // The window region (rounded corners) is tied to the window size;
        // after SetWindowPos resizes the window, re-apply it with the NEW
        // physical size or the rounded corners end up misaligned (only the
        // top-left keeps its radius, the other corners become square).
        // Skip when the size is unchanged (this runs every 200 ms while the
        // popup is open) to avoid churning GDI regions. NOTE: track the size
        // ourselves (_lastRegionSize) instead of comparing Width/Height —
        // WinForms auto-scales the window AND the Region on WM_DPICHANGED
        // before the poll runs, so Width/Height already match physW/physH and
        // a Width/Height comparison would wrongly skip the rebuild, leaving
        // the framework-scaled (misaligned) rounded corners.
        if (physW != _lastRegionSize.Width || physH != _lastRegionSize.Height)
        {
            ApplyRoundedCorners(8, physW, physH);
            _lastRegionSize = new Size(physW, physH);
        }

        return new Size(physW, physH);
    }

    /// <summary>
    /// Physical working area of the monitor containing the icon rect.
    /// Uses MonitorFromRect + GetMonitorInfo (rcWork) directly 鈥?this is
    /// ALWAYS physical pixels regardless of the app's cached Screen list,
    /// which goes stale after a DPI change (Screen.WorkingArea can report
    /// a mixed/old value once the scaling changes while the app runs).
    /// </summary>
    private Rectangle GetIconMonitorWorkArea(Rectangle iconRect)
    {
        var r = new NativeMethods.RECT
        {
            Left = iconRect.Left,
            Top = iconRect.Top,
            Right = iconRect.Right,
            Bottom = iconRect.Bottom
        };
        IntPtr hMon = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref mi))
        {
            return new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        }
        // Fallback: the cached Screen (best effort; stale after DPI change)
        var screen = Screen.FromRectangle(iconRect);
        return screen.WorkingArea;
    }

    /// <summary>
    /// Real DPI of the monitor containing the icon rect, queried live via
    /// GetDpiForMonitor. The form's DeviceDpi is NOT reliable here: while
    /// the popup is hidden it receives no WM_DPICHANGED, so DeviceDpi stays
    /// at the value from creation even after the user changes the display
    /// scaling 鈥?which is exactly the bug seen in the field (popup sized
    /// for 175% while the display is now 150%).
    /// </summary>
    private int GetIconMonitorDpi(Rectangle iconRect)
    {
        var r = new NativeMethods.RECT
        {
            Left = iconRect.Left,
            Top = iconRect.Top,
            Right = iconRect.Right,
            Bottom = iconRect.Bottom
        };
        IntPtr hMon = NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero)
        {
            if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            {
                return (int)dpiX;
            }
        }
        // Fallback: window DPI (may be stale if hidden; better than nothing)
        return DeviceDpi > 0 ? DeviceDpi : 96;
    }

    /// <summary>
    /// Closes the popup.
    /// </summary>
    public void Dismiss()
    {
        if (InvokeRequired)
        {
            Invoke(new Action(Dismiss));
            return;
        }

        HidePowerTip();
        HideModeTip();
        HideSettingsTip();
        if (Visible)
        {
            base.Hide();
            OnShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Turns off the display (鎭睆) by broadcasting SC_MONITORPOWER.
    /// lParam=2 means full power off; moving the mouse or pressing a key
    /// wakes the display again (standard Windows behavior).
    /// </summary>
    private void PowerOffDisplay()
    {
        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
    }

    /// <summary>
    /// Creates a "monitor + stand" icon bitmap (white vector lines) for the
    /// power-off button, so the button never depends on localized text.
    /// Rendered at DPI-scaled pixel size.
    /// </summary>
    private Bitmap CreatePowerIcon(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float penW = Math.Max(1.2f, w / 14f);
            using var pen = new Pen(ThemeManager.PopupBtnIcon, penW)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };

            // Monitor body + stand, vertically centered inside the bitmap
            // so the icon sits balanced in the button.
            float iconH = h * 0.47f;
            float iconW = iconH / 0.72f;
            float standH = iconH * (0.35f + 0.12f);   // neck + feet
            float totalH = iconH + standH;
            float top = (h - totalH) / 2f;
            float cx = w / 2f;
            float sx = cx - iconW / 2f;
            float sy = top;

            // Rounded monitor body.
            float r = penW * 2f;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(sx, sy, r, r, 180, 90);
                path.AddArc(sx + iconW - r, sy, r, r, 270, 90);
                path.AddArc(sx + iconW - r, sy + iconH - r, r, r, 0, 90);
                path.AddArc(sx, sy + iconH - r, r, r, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }

            // 45-degree slash crossing the WHOLE icon (screen + stand),
            // from the icon's top-left to bottom-right, to clearly convey
            // "power off". Use a square region centered on the whole icon
            // so the angle stays exactly 45 degrees.
            float iconCx = sx + iconW / 2f;
            float iconCy = top + totalH / 2f;
            float sq = Math.Min(iconW, totalH) * 0.92f;
            g.DrawLine(pen, iconCx - sq / 2f, iconCy - sq / 2f, iconCx + sq / 2f, iconCy + sq / 2f);

            // Stand: neck + feet.
            float neckTop = sy + iconH;
            float neckBottom = neckTop + iconH * 0.35f;
            float feetY = neckBottom + iconH * 0.12f;
            g.DrawLine(pen, cx, neckTop, cx, neckBottom);
            g.DrawLine(pen, cx - iconW * 0.24f, feetY, cx + iconW * 0.24f, feetY);
        }
        return bmp;
    }

    /// <summary>
    /// Loads the gear (settings) art as a theme-appropriate bitmap and
    /// assigns it to the settings button. The gear series ships as
    /// black/white per-size pre-rendered PNGs; the white set is used
    /// on dark popups, the black set on light popups.
    private void SetGearIcon(RoundedButton btn, int pixelSize)
    {
        string suffix = ThemeManager.PopupIsDark
            ? $"gear-white-{PickSizeFrame(pixelSize)}.png"
            : $"gear-black-{PickSizeFrame(pixelSize)}.png";
        var img = LoadEmbeddedPng(suffix);
        if (img == null) return;
        // Downscale the chosen frame to the exact target size so any dpi
        // value (not just the discrete frame sizes) renders correctly.
        if (img.Width != pixelSize || img.Height != pixelSize)
        {
            var scaled = new Bitmap(pixelSize, pixelSize);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(img, 0, 0, pixelSize, pixelSize);
            }
            img.Dispose();
            img = scaled;
        }
        btn.Image?.Dispose();
        btn.Image = img;
    }

    /// <summary>
    /// Picks the gear frame to render for the given pixel size: the
    /// smallest frame that is AT LEAST the target size (downscaling
    /// keeps details crisp; upscaling blurs).
    /// </summary>
    private static int PickSizeFrame(int pixelSize)
    {
        int[] frames = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (int f in frames)
        {
            if (f >= pixelSize) return f;
        }
        return frames[frames.Length - 1];
    }

    private static Bitmap? LoadEmbeddedPng(string suffix)
    {
        var asm = typeof(BrightnessPopup).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        // Bitmap(Stream) 要求流在 Image 存续期内保持打开（文档契约）；资源流在
        // 方法结束即被释放，故先在流内拷贝一份独立像素副本再返回。
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var src = new Bitmap(stream);
        return new Bitmap(src);
    }

    protected override void WndProc(ref Message m)
    {
        // 0x02E0 = WM_DPICHANGED. Log whether the popup receives it at all
        // (hidden forms are not sent WM_DPICHANGED; visible ones are).
        if (m.Msg == 0x02E0)
        {
            int newDpi = (m.WParam.ToInt32() >> 16);
            #if DEBUG
            PopupDebug.Log($"BrightnessPopup WndProc: WM_DPICHANGED newDpi={newDpi}");
            #endif
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.PopupThemeChanged -= OnThemeChanged;
            Localization.LanguageChanged -= OnLanguageChanged;
            _tipHideTimer?.Dispose();
            _tempDwellTimer?.Stop();
            _tempDwellTimer?.Dispose();
            _tempDwellTimer = null;
            _powerTip?.Dispose();
            _modeTip?.Dispose();
            _settingsTip?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Tool window: no taskbar entry, never activates; the global
            // mouse hook owns click-outside dismissal.
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            // Soft drop shadow drawn by DWM outside the window, so the
            // popup keeps a visible outline against a light desktop.
            cp.ClassStyle |= 0x00020000;  // CS_DROPSHADOW
            return cp;
        }
    }

    // 半透明开启时机（深色弹窗首开闪白的修复）：构造函数不设 Opacity（保持
    // 不透明），首次 Show()+Update() 以不透明方式完成首帧自绘后才置 Opacity=0.9。
    // 原因：Opacity<1 使窗口从创建起即为分层窗口(WS_EX_LAYERED)，重启后首次
    // 显示时空的分层表面会被系统以默认白底合成首帧 → 整窗闪白一次；把半透明
    // 施加在已有深色内容上即可消除该白帧（1143 验证通过）。
    private bool _opacityApplied;

    // 用户可调透明度（%）：通用设置页滑轨写入。默认 90（原固定值）。
    // 首帧仍按"不透明绘制完成后再开启半透明"（见构造函数注释/1143 修复），
    // 因此这里只保存目标值，真正生效在 ApplyOpacityAfterFirstPaint()；
    // 若首帧已应用（_opacityApplied），修改时立即生效。
    private int _opacityPercent = 90;
    public int OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            _opacityPercent = Math.Clamp(value, 40, 100);
            if (_opacityApplied)
            {
                Opacity = _opacityPercent >= 100 ? 1.0 : _opacityPercent / 100.0;
            }
        }
    }

    private void ApplyOpacityAfterFirstPaint()
    {
        if (_opacityApplied) return;
        _opacityApplied = true;
        // 首帧不透明自绘完成后按用户设置开启半透明（见构造函数注释/1143 修复）。
        Opacity = _opacityPercent >= 100 ? 1.0 : _opacityPercent / 100.0;
    }

    private void ApplyRoundedCorners(int radius, int widthPx, int heightPx)
    {
        // Region(Region) copies the geometry so the path can be disposed here.
        using (var path = new GraphicsPath())
        {
            int diameter = radius * 2;
            var rect = new Rectangle(0, 0, widthPx, heightPx);

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            // Dispose the previous region (rounded corners are re-applied on
            // every resize/DPI change; leaking a Region per call would exhaust
            // GDI handles over time).
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }
    }
    // ================= 3.6.0: 多显示器独立行 =================

    /// <summary>
    /// 每屏一行的数据（由 MainController 注入）。
    /// </summary>
    public sealed record DisplayRowData(string EdidId, string Name, float Brightness, float Temperature, bool Enabled);

    private readonly List<DisplayRowData> _displayRows = new();
    private readonly List<PopupDisplayRow> _rowControls = new();
    private bool _perMonitorEnabled;

    /// <summary>
    /// 是否处于多显示器独立控制模式。true 时弹窗按屏显示多行滑轨，
    /// 单屏模式（默认）保持原有单条滑轨。
    /// </summary>
    public bool PerMonitorEnabled
    {
        get => _perMonitorEnabled;
        set
        {
            if (_perMonitorEnabled == value) return;
            _perMonitorEnabled = value;
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    ApplyDisplayRows();
                    UpdateLabelAndSlider();
                }));
                return;
            }
            ApplyDisplayRows();
            UpdateLabelAndSlider();
        }
    }

    /// <summary>
    /// 设置当前显示器行数据并重建行控件（热插拔/名称变化时由 MainController 调用）。
    /// </summary>
    public void SetDisplays(IReadOnlyList<DisplayRowData> rows)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<IReadOnlyList<DisplayRowData>>(SetDisplays), rows);
            return;
        }
        _displayRows.Clear();
        _displayRows.AddRange(rows);
        ApplyDisplayRows();
        UpdateLabelAndSlider();
    }

    /// <summary>
    /// 刷新行名称（重命名显示器后调用，不重建滑轨状态）。
    /// </summary>
    public void RefreshDisplayNames()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshDisplayNames));
            return;
        }
        for (int i = 0; i < _rowControls.Count && i < _displayRows.Count; i++)
        {
            _rowControls[i].SetName(_displayRows[i].Name);
        }
    }

    /// <summary>
    /// 多行模式下同步某一屏的值到显示（单屏模式由 SyncFromGamma 处理）。
    /// </summary>
    public void SyncDisplayRow(string edidId, float brightness, float temperature, bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SyncDisplayRow(edidId, brightness, temperature, enabled)));
            return;
        }
        var row = _rowControls.FirstOrDefault(r => r.EdidId == edidId);
        if (row != null)
        {
            row.SetValues(brightness, temperature, enabled);
            row.Invalidate();
        }
        // 同时更新内部数据（供下次 ShowAbove 使用）
        for (int i = 0; i < _displayRows.Count; i++)
        {
            if (_displayRows[i].EdidId == edidId)
            {
                _displayRows[i] = _displayRows[i] with
                {
                    Brightness = brightness,
                    Temperature = temperature,
                    Enabled = enabled
                };
                break;
            }
        }
    }

    /// <summary>
    /// 重建行控件集合：PerMonitorEnabled 且有多屏时显示行；否则清空（单屏模式用主滑块）。
    /// </summary>
    /// <summary>
    /// 重建行控件集合：PerMonitorEnabled 时按屏显示多行滑轨；否则清空行
    /// 并恢复单条滑轨布局。窗口尺寸由 <see cref="LayoutHeightForCurrentMode"/> 统一计算。
    /// </summary>
    private void ApplyDisplayRows()
    {
        foreach (var row in _rowControls)
        {
            Controls.Remove(row);
            row.Dispose();
        }
        _rowControls.Clear();

        if (!_perMonitorEnabled)
        {
            // 单屏模式：恢复主滑块/标签可见，窗口还原基础高度并重建底部行布局。
            _label.Visible = true;
            _sliderHitArea.Visible = true;
            int dpi = _lastLayoutDpi > 0 ? _lastLayoutDpi : DeviceDpi;
            float ds = dpi / 96.0f;
            ClientSize = new Size((int)(_baseWidth * ds), (int)(_baseHeight * ds));
            ApplyBottomRowLayout(dpi);
            return;
        }

        // 多屏模式：隐藏主滑块，显示每屏行
        _label.Visible = false;
        _sliderHitArea.Visible = false;

        // 用图标所在显示器的真实 DPI：DeviceDpi 在隐藏窗口上是陈旧值，
        // 多显示器不同缩放时会导致行高/行宽按错误的比例计算。
        float dpiScale = (_lastLayoutDpi > 0 ? _lastLayoutDpi : DeviceDpi) / 96.0f;
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;
        int rowH = GetDisplayRowHeight(dpiScale);

        int y = topPadding;
        for (int i = 0; i < _displayRows.Count; i++)
        {
            var data = _displayRows[i];
            var row = new PopupDisplayRow(data.EdidId, data.Name, contentWidth, rowH, dpiScale);
            row.Location = new Point(margin, y);
            row.SetValues(data.Brightness, data.Temperature, data.Enabled);
            string rowEdid = data.EdidId;
            row.OnBrightnessChanged += (_, v) =>
            {
                OnDisplayRowChanged?.Invoke(rowEdid, v, row.Temperature);
            };
            row.OnTemperatureChanged += (_, v) =>
            {
                OnDisplayRowChanged?.Invoke(rowEdid, row.Brightness, v);
            };
            Controls.Add(row);
            _rowControls.Add(row);
            y += rowH + gap;
        }

        // 把当前模式（亮度/色温）与温度范围下发给新建行，使行初始即处于正确模式。
        PushModeToRows();

        // 按行数调整窗体高度（与 PositionAbove 的 LayoutHeightForCurrentMode 同公式），
        // 使底部行位于所有滑轨行之下。
        int newHeight = LayoutHeightForCurrentMode(dpiScale);
        ClientSize = new Size(ClientSize.Width, newHeight);

        // 底部行重新定位到新高度
        ApplyBottomRowLayout();
    }

    /// <summary>
    /// 按当前模式计算弹窗总高度，单位 = 物理像素（已按 dpiScale 缩放）。
    /// 单屏模式 = _baseHeight × dpiScale；多屏模式 = 行区 + 底部行。
    /// PositionAbove（SetWindowPos 尺寸权威）与 ApplyDisplayRows（ClientSize）
    /// 共用此公式，保证窗口实际尺寸与行控件布局完全一致。
    /// 两个分支的单位必须统一：多屏分支的各分量（GetBottomRowTop / 22 / 4）
    /// 本身已乘过 dpiScale，单屏分支的 _baseHeight 是设计基准值，必须在此乘。
    /// </summary>
    private int LayoutHeightForCurrentMode(float dpiScale)
    {
        if (!_perMonitorEnabled || _displayRows.Count == 0)
            return (int)(_baseHeight * dpiScale);

        return GetBottomRowTop(dpiScale) + (int)(22 * dpiScale) + (int)(4 * dpiScale);
    }

    /// <summary>
    /// 单条滑轨行的高度（物理像素）：标签行 + 间隙 + 滑轨 + 行内下留白。
    /// 行控件创建（ApplyDisplayRows）与窗口高度计算共用同一公式，
    /// 避免两处各写一份而漂移。
    /// </summary>
    private int GetDisplayRowHeight(float dpiScale)
    {
int labelHeight = (int)(20 * dpiScale);
    int gap = (int)(2 * dpiScale);
    int barHeight = Math.Max(3, (int)(4 * dpiScale));
    // 16 缩放倍数的额外留白 → 文字→滑轨有充裕净空（175% 下≈21px），
    // 同时承接滑轨 thumb 半径 4 与行内下边距。
    return labelHeight + gap + barHeight + (int)(16 * dpiScale);
    }

    /// <summary>
    /// 底部按钮行的顶端 Y（逻辑像素）。单屏模式沿用原有的单行几何；
    /// 多屏（独立控制）模式按实际行数累加，保证底部行落在所有滑轨行之下。
    /// </summary>
    private int GetBottomRowTop(float dpiScale)
    {
        int topPadding = (int)(2 * dpiScale);
        if (!_perMonitorEnabled || _displayRows.Count == 0)
        {
            // 单屏：标签 + 间隙 + 滑轨 + 4px 间距（3.5.0 起的既有布局，保持不变）
            int labelHeight = (int)(20 * dpiScale);
            int gap = (int)(2 * dpiScale);
            int barHeight = Math.Max(3, (int)(4 * dpiScale));
            return topPadding + labelHeight + gap + barHeight + (int)(4 * dpiScale);
        }

        int gap2 = (int)(2 * dpiScale);
        int rowH = GetDisplayRowHeight(dpiScale);
        int y = topPadding;
        for (int i = 0; i < _displayRows.Count; i++) y += rowH + gap2;
        return y;
    }

    /// <summary>
    /// 单屏一行的自绘滑轨控件（3.6.0 多显示器模式）。
    /// 名称在滑轨左上方、值（百分比/色温）在右上方、同字号、超长省略号截断。
    /// </summary>
    private sealed class PopupDisplayRow : Control
    {
        public string EdidId { get; }
        /// <summary>当前行亮度（0..1），供事件回调读取。</summary>
        public float Brightness => _brightness;
        /// <summary>当前行色温（K），供事件回调读取。</summary>
        public float Temperature => _temperature;
        /// <summary>当前行是否启用。</summary>
        public bool IsEnabled => _enabled;

        private string _name;
        private float _brightness = 1f;
        private float _temperature = GammaController.DEFAULT_TEMPERATURE;
        private bool _enabled = true;
        private readonly float _dpiScale;
        private bool _dragging;
        private long _tempPauseUntilMs;   // 与主滑块同语义：6600 稍作停留
        private bool _tempCatchActive;    // 停留/离开后的逐格追赶
        private System.Windows.Forms.Timer? _tempDwellTimer;   // 松手后播完停留再落定
        /// <summary>本行当前控制的模式（与底部切换开关同步）。色温模式下调色温。</summary>
        private bool _temperatureMode;
        private float _minTemp = GammaController.MIN_TEMPERATURE;
        private float _maxTemp = GammaController.MAX_TEMPERATURE;
        private float _tempStep = GammaController.DEFAULT_TEMPERATURE_STEP;

        public event EventHandler<float>? OnBrightnessChanged;
        public event EventHandler<float>? OnTemperatureChanged;

        public PopupDisplayRow(string edidId, string name, int width, int height, float dpiScale)
        {
            EdidId = edidId;
            _name = name;
            _dpiScale = dpiScale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(width, height);
            Cursor = Cursors.Hand;
        }

        public void SetName(string name) { _name = name; Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 行被替换（ApplyDisplayRows）/窗体关闭时停掉"松手播完停留"定时器：
                // 否则 Tick 仍会在已释放的行上触发 FinalizeTempToPointer，访问
                // 已释放控件 → ObjectDisposedException。
                _tempDwellTimer?.Stop();
                _tempDwellTimer?.Dispose();
                _tempDwellTimer = null;
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 同步本行滑轨的模式与温度范围。色温模式开启后行滑轨显示/调节色温，
        /// 否则恒为亮度。范围/步进与弹窗（MainController 注入）保持一致。
        /// </summary>
        public void SetTemperatureContext(bool temperatureMode, float minTemp, float maxTemp, float tempStep)
        {
            _temperatureMode = temperatureMode;
            _minTemp = minTemp;
            _maxTemp = maxTemp;
            _tempStep = tempStep;
            Invalidate();
        }

        public void SetValues(float brightness, float temperature, bool enabled)
        {
            _brightness = Math.Clamp(brightness, 0f, 1f);
            _temperature = temperature;
            _enabled = enabled;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _tempDwellTimer?.Dispose();
                _tempDwellTimer = null;
                UpdateFromMouse(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                UpdateFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            long now = Environment.TickCount64;
            if (_temperatureMode && now < _tempPauseUntilMs)
            {
                // 6600 停留尚未播完就松手：不截断，播完再落定到指针格
                _tempDwellTimer?.Dispose();
                var t = new System.Windows.Forms.Timer { Interval = Math.Max(16, (int)(_tempPauseUntilMs - now)) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    t.Dispose();
                    if (ReferenceEquals(_tempDwellTimer, t)) _tempDwellTimer = null;
                    FinalizeRowTemp();
                };
                _tempDwellTimer = t;
                t.Start();
            }
            else
            {
                FinalizeRowTemp();
            }
        }

        private void FinalizeRowTemp()
        {
            _tempPauseUntilMs = 0;
            _tempCatchActive = false;
            if (!_temperatureMode) return;
            // 松手时停在 6600 → 保持 6600，不跳到指针格（避免一松手变 6400/6800）
            if (Math.Abs(_temperature - GammaController.DEFAULT_TEMPERATURE) < 0.5f) return;
            var p = PointToClient(Cursor.Position);
            float ratio = Math.Max(0f, Math.Min(1f, (float)p.X / Width));
            float final = Math.Clamp(Round100K(_minTemp + ratio * (_maxTemp - _minTemp)), _minTemp, _maxTemp);
            if (Math.Abs(final - _temperature) >= 0.5f)
            {
                _temperature = final;
                OnTemperatureChanged?.Invoke(this, _temperature);
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_enabled) return;
            int sign = Math.Sign(e.Delta);
            if (_temperatureMode)
            {
                float newT = Math.Clamp(_temperature + sign * _tempStep, _minTemp, _maxTemp);
                newT = (float)Math.Round(newT);
                if (newT == _temperature) return;
                _temperature = newT;
                OnTemperatureChanged?.Invoke(this, _temperature);
            }
            else
            {
                // 亮度 ±5%（与主滑块同语义）
                _brightness = Math.Clamp(_brightness + sign * 0.05f, 0f, 1f);
                OnBrightnessChanged?.Invoke(this, _brightness);
            }
            Invalidate();
        }

        private void UpdateFromMouse(int x)
        {
            if (!_enabled) return;
            float ratio = Math.Max(0f, Math.Min(1f, (float)x / Width));
            if (_temperatureMode)
            {
                float newTemp = _minTemp + ratio * (_maxTemp - _minTemp);
                // 与主滑块同语义：6600"轻顿 200ms"，推出 >±900K 立即解除，不粘手。
                long now = Environment.TickCount64;
                float grid = Round100K(newTemp);
                bool curAtNeutral = Math.Abs(_temperature - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
                // 触发停留：进入 6600 格（慢速）或一步跨过 6600（快速）二者任一
                bool enteringNeutralCell = !curAtNeutral && Math.Abs(grid - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
                bool crossingOverNeutral =
                    !curAtNeutral &&
                    ((_temperature < GammaController.DEFAULT_TEMPERATURE && newTemp >= GammaController.DEFAULT_TEMPERATURE) ||
                     (_temperature > GammaController.DEFAULT_TEMPERATURE && newTemp <= GammaController.DEFAULT_TEMPERATURE));
                if (enteringNeutralCell || crossingOverNeutral)
                {
                    _tempPauseUntilMs = now + TempDwellMs;
                    _tempCatchActive = false;
                }

                float snapped;
                bool pausedNow = now < _tempPauseUntilMs;
                if (pausedNow && Math.Abs(newTemp - GammaController.DEFAULT_TEMPERATURE) > TempDwellEarlyExitK)
                {
                    // 指针已明显推出 6600（快速硬推）：立即解除停留，不产生"拽住"感
                    _tempPauseUntilMs = 0;
                    pausedNow = false;
                }
                if (pausedNow)
                {
                    snapped = GammaController.DEFAULT_TEMPERATURE;
                }
                else if (curAtNeutral && Math.Abs(grid - GammaController.DEFAULT_TEMPERATURE) >= 1f)
                {
                    snapped = grid > GammaController.DEFAULT_TEMPERATURE
                        ? Math.Min(grid, GammaController.DEFAULT_TEMPERATURE + GammaController.TEMPERATURE_STEP)
                        : Math.Max(grid, GammaController.DEFAULT_TEMPERATURE - GammaController.TEMPERATURE_STEP);
                    _tempCatchActive = Math.Abs(snapped - grid) >= 1f;
                    if (!_tempCatchActive) _tempPauseUntilMs = 0;
                }
                else if (_tempCatchActive)
                {
                    if (Math.Abs(grid - _temperature) < 1f)
                    {
                        snapped = grid;
                        _tempCatchActive = false;
                        _tempPauseUntilMs = 0;
                    }
                    else
                    {
                        snapped = grid > _temperature
                            ? Math.Min(grid, _temperature + GammaController.TEMPERATURE_STEP)
                            : Math.Max(grid, _temperature - GammaController.TEMPERATURE_STEP);
                        if (Math.Abs(snapped - grid) < 1f)
                        {
                            _tempCatchActive = false;
                            _tempPauseUntilMs = 0;
                        }
                    }
                }
                else
                {
                    snapped = grid;
                    _tempPauseUntilMs = 0;
                }
                newTemp = Math.Clamp(snapped, _minTemp, _maxTemp);
                if (newTemp == _temperature) return;
                _temperature = newTemp;
                OnTemperatureChanged?.Invoke(this, _temperature);
            }
            else
            {
                _brightness = Math.Clamp((float)Math.Round(ratio * 100) / 100f, 0f, 1f);
                OnBrightnessChanged?.Invoke(this, _brightness);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(ThemeManager.PopupBg);

            int margin = (int)(4 * _dpiScale);
            int labelH = (int)(16 * _dpiScale);
            int barH = Math.Max(3, (int)(4 * _dpiScale));
            // 轨道上缘 = 文字区下边界 + 12ds 净空（175% 下约 21px）。
            int barY = labelH + (int)(12 * _dpiScale);
            int barW = Width - margin * 2;
            int fontSize = Math.Max(7, (int)(7 * _dpiScale));

            // ---- 先画轨道/填充/拇指 ----
            int trackRadius = Math.Max(1, barH / 2);
            var trackRect = new Rectangle(margin, barY, barW, barH);
            using (var track = new SolidBrush(ThemeManager.PopupTrack))
            using (var trackPath = RoundedRect(trackRect, trackRadius))
            {
                g.FillPath(track, trackPath);
            }

            if (_enabled)
            {
                // 填充比例：亮度用 brightness，色温用 (K-Min)/(Max-Min)（与主滑块同映射）
                float level = _temperatureMode
                    ? Math.Max(0f, Math.Min(1f, (_temperature - _minTemp) / (_maxTemp - _minTemp)))
                    : _brightness;
                int fillW = Math.Max(1, (int)(barW * level));
                var fillRect = new Rectangle(margin, barY, fillW, barH);
                using (var fill = new SolidBrush(ThemeManager.PopupFill))
                using (var fillPath = RoundedRect(fillRect, trackRadius))
                {
                    g.FillPath(fill, fillPath);
                }

                int radius = Math.Max(3, (int)(4 * _dpiScale));
                int cx = Math.Min(margin + fillW, margin + barW - radius);
                cx = Math.Max(cx, margin + radius);
                int cy = barY + barH / 2;
                using (var brush = new SolidBrush(ThemeManager.PopupThumb))
                using (var pen = new Pen(ThemeManager.PopupThumbOutline, 1f))
                {
                    g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
                    g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
                }
            }

            // ---- 再画名称与值文字：文字永远在轨道之上 ----
            // 注意：TextRenderer(DrawText) 是 GDI 绘制，不裁边且字形可能超出
            // 传入矩形。此前把文字框高写成 labelH(16ds) 并先画文字再画轨道，
            // 高 DPI 下 38px 行高的字形向下溢出到轨道区域，随后轨道画上去把
            // 文字下半截盖掉 → 看起来像"轨道/空白区域遮挡文字"。修复：
            // (a) 文字框给足整段"轨道上缘"的高度，让垂直居中不再溢出到轨道；
            // (b) 轨道先画、文字后画，任何残留重叠都保证文字在最上层可读。
            int textZoneH = Math.Max(labelH, barY - (int)(2 * _dpiScale));

            // Name label (top-left), ellipsis-truncated
            using (var nameFont = new Font("Segoe UI", fontSize, FontStyle.Regular))
            {
                var nameRect = new Rectangle(margin, 0, barW / 2, textZoneH);
                TextRenderer.DrawText(g, _name, nameFont, nameRect,
                    _enabled ? ThemeManager.PopupText : ThemeManager.PopupTrack,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            // Value label (top-right)。色温模式显示 K 值，亮度模式显示百分比。
            bool valueAtDefaultTemp = _temperatureMode &&
                Math.Abs(_temperature - GammaController.DEFAULT_TEMPERATURE) < 0.5f;
            string valueText = !_enabled
                ? Localization.Get("DisplayDisabled")
                : _temperatureMode
                    ? (valueAtDefaultTemp
                        ? $"{(int)_temperature}K" : $"{_temperature:0}K")
                    : $"{_brightness * 100:0}%";
            Color valueColor = valueAtDefaultTemp ? DefaultTempHighlight : ThemeManager.PopupText;
            using (var valueFont = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                var valueRect = new Rectangle(Width - margin - barW / 2, 0, barW / 2, textZoneH);
                TextRenderer.DrawText(g, valueText, valueFont, valueRect, valueColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
