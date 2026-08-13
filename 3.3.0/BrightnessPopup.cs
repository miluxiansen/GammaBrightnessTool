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
    private readonly int _baseHeight = 60;   // Label + slider + power-off button, compact
    private int _lastLayoutDpi;
    // Tracks the physical size the rounded-corner Region was last built for.
    // Do NOT compare against WinForms Width/Height here: after WM_DPICHANGED
    // the framework auto-scales the window (and the Region) before our 200 ms
    // poll runs, so Width/Height already equal the new values and the rebuild
    // guard would be skipped, leaving the framework-scaled (misaligned) region.
    private Size _lastRegionSize;

    public event EventHandler<float>? OnBrightnessChanged;

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
        Opacity = 0.9;  // More opaque than OSD for a "panel" feel
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
        float dpiScale = (dpi > 0 ? dpi : DeviceDpi) / 96.0f;
        int margin = (int)(6 * dpiScale);
        int topPadding = (int)(2 * dpiScale);
        int labelHeight = (int)(20 * dpiScale);
        int gap = (int)(2 * dpiScale);
        int barHeight = Math.Max(3, (int)(4 * dpiScale));
        int clientWidth = ClientSize.Width;
        int contentWidth = clientWidth - margin * 2;
        int barY = topPadding + labelHeight + gap;
        int rowY = barY + barHeight + (int)(4 * dpiScale);
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
        _label.ForeColor = ThemeManager.PopupText;
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
        // If the tip is visible, refresh its text for the new mode.
        if (_modeTip != null && _modeTip.Visible)
        {
            _modeTip.SetText(Localization.Get(_mode == SliderMode.Temperature ? "TemperatureMode" : "BrightnessMode"));
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
            if (Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f)
                _label.Text = $"{(int)_currentTemperatureK}K{Localization.Get("DefaultSuffix")}";
            else
                _label.Text = $"{_currentTemperatureK:0}K";
        }
        else
        {
            _label.Text = $"{_currentPercentage}%";
        }
        FitLabelFont();
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
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
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
    }

    private void SliderHitArea_MouseWheel(object? sender, MouseEventArgs e)
    {
        int delta = Math.Sign(e.Delta);
        if (_mode == SliderMode.Temperature)
        {
            float newTemp = _currentTemperatureK + delta * TemperatureStepSize;
            newTemp = Math.Clamp(newTemp, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
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

        int sign = Math.Sign(wheelDelta);
        if (_mode == SliderMode.Temperature)
        {
            float newTemp = _currentTemperatureK + sign * TemperatureStepSize;
            newTemp = Math.Clamp(newTemp, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
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
            float newTemp = GammaController.MIN_TEMPERATURE +
                ratio * (GammaController.MAX_TEMPERATURE - GammaController.MIN_TEMPERATURE);
            newTemp = (float)Math.Round(newTemp / GammaController.TEMPERATURE_STEP) * GammaController.TEMPERATURE_STEP;
            newTemp = Math.Clamp(newTemp, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
            UpdateTemperature(newTemp);
        }
        else
        {
            int percentage = (int)Math.Round(ratio * 100);
            percentage = Math.Max(0, Math.Min(100, percentage));
            UpdateBrightness(percentage);
        }
    }
    private void UpdateTemperature(float kelvin)
    {
        float snapped = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        if (Math.Abs(snapped - _currentTemperatureK) < 0.5f) return;

        _currentTemperatureK = snapped;
        if (Math.Abs(_currentTemperatureK - GammaController.DEFAULT_TEMPERATURE) < 0.5f)
                _label.Text = $"{(int)_currentTemperatureK}K{Localization.Get("DefaultSuffix")}";
        else
            _label.Text = $"{_currentTemperatureK:0}K";
        _sliderHitArea.Invalidate();

        OnTemperatureChanged?.Invoke(this, _currentTemperatureK);
    }

    private void UpdateBrightness(int percentage)
    {
        if (percentage == _currentPercentage) return;

        _currentPercentage = percentage;
        _label.Text = $"{percentage}%";
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
        if (dpi == _lastLayoutDpi) return;
        _lastLayoutDpi = dpi;
        #if DEBUG
        PopupDebug.Log($"ApplyLayoutForCurrentDpi: dpi={dpi}");
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
            ? (_currentTemperatureK - GammaController.MIN_TEMPERATURE) / (GammaController.MAX_TEMPERATURE - GammaController.MIN_TEMPERATURE)
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
        int physH = (int)(_baseHeight * dpiScale);

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
        using var stream = asm.GetManifestResourceStream(name);
        return stream == null ? null : new Bitmap(stream);
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

    private void ApplyRoundedCorners(int radius, int widthPx, int heightPx)
    {
        var path = new GraphicsPath();
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
        Region?.Dispose();
        Region = new Region(path);
    }
}
