using System.Diagnostics;

namespace GammaBrightnessTool;

/// <summary>
/// Main application controller that coordinates all components.
/// </summary>
public sealed class MainController : IDisposable
{
    private TrayIconManager? _trayIcon;
    private GlobalMouseHook? _mouseHook;
    private GammaController? _gamma;
    private BrightnessOverlay? _overlay;
    private BrightnessPopup? _popup;
    private AppSettings? _settings;
    // Per-slot registration results from the last RegisterHotkeys() run,
    // keyed by slot name. Used to detect OS-level conflicts when a hotkey
    // binding changes. Unbound slots are not present here.
    private readonly Dictionary<string, bool> _hotKeyRegistration = new();
    // While the user is recording a hotkey in the settings window,
    // ALL hotkeys are temporarily unregistered so the new combo can
    // be typed without triggering any existing binding.
    private bool _hotKeysSuspended;
    // Polls the tray icon rect while the popup is open so the popup follows
    // the icon across DPI changes / taskbar moves without depending on
    // WM_DPICHANGED delivery to a hidden message window (which Windows does
    // not send to hidden windows, and CheckMouseLeave only runs while the
    // cursor is over the icon). This is the same "always use fresh
    // coordinates" philosophy as the wheel OSD path.
    private System.Windows.Forms.Timer? _popupAnchorTimer;
    private static readonly TimeSpan PopupAnchorInterval = TimeSpan.FromMilliseconds(200);
    // 时间调整调度器（按日出日落自动调节色温/亮度）。
    private SolarScheduler? _solarScheduler;

    public void Initialize(bool silent, bool showSettingsOnStart = false)
    {
        // 1. Run startup integrity check (registry, settings, tray icon visibility)
        IntegrityChecker.RunCheck();

        // 2. Load settings (fresh load after check, auto-creates default if not exists)
        _settings = SettingsManager.Load();
        // 过渡时长上限 60 分钟（旧版本设置可能残留 180）。
        _settings.TransitionMinutes = Math.Clamp(_settings.TransitionMinutes, 0, 60);
        // Apply saved language. Language.System (the default) follows the
        // system UI language, falling back to English when unsupported.
        Localization.Setting = _settings.Language;
        Localization.Current = Localization.Resolve(_settings.Language).Effective;
        // Apply the saved theme (System follows the Windows app theme).
        ThemeManager.Apply(_settings.Theme);
        // Popup theme is independent of the main UI theme.
        ThemeManager.ApplyPopupTheme(_settings.PopupTheme);

        // 2. Create tray icon first (visible immediately)
        _trayIcon = new TrayIconManager();
        _trayIcon.Initialize();
        _trayIcon.OnUninstallRequested += OnUninstallRequested;
        _trayIcon.OnSettingsRequested += OnSettingsRequested;
        _trayIcon.OnLeftClickRequested += OnLeftClickRequested;
        _trayIcon.OnContextMenuOpening += OnContextMenuOpening;
        _trayIcon.OnTrayDpiChanged += OnTrayDpiChanged;
        _trayIcon.OnIconRectChanged += OnIconRectChanged;

        // 3. Initialize gamma controller
        _gamma = new GammaController();
        _gamma.Initialize();
        _gamma.StepSize = _settings.StepSize; // apply saved step size
        _gamma.TemperatureStepSize = _settings.TemperatureStepSize; // apply saved temperature step
        _gamma.MinTemperature = _settings.MinTemperature; // apply saved temperature range
        _gamma.MaxTemperature = _settings.MaxTemperature;
        ApplyStartupGamma(); // 平滑/瞬时应用保存的亮度与色温

        // 4. Create brightness overlay
        _overlay = new BrightnessOverlay();
        _overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;

        // 5. Install mouse hook
        _mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
        {
            // Runtime-resolved so the settings UI changes apply immediately.
            IsInvertedScroll = () => _settings?.InvertScroll ?? false,
            IsOverlayEnabled = () => _settings?.ShowOverlay ?? true,
            IsWheelEnabled = () => _settings?.WheelEnabled ?? true,
            IsColorTemperatureEnabled = () => _settings?.ColorTemperatureEnabled ?? false,
            OnUserAdjustment = OnManualAdjustment
        };
        _mouseHook.Install();

        // 5b. Create left-click brightness popup (docked above tray icon)
        _popup = new BrightnessPopup();
        _popup.StepSize = _settings.StepSize; // popup wheel uses the same step as the OSD path
        _popup.TemperatureStepSize = _settings.TemperatureStepSize; // temperature wheel step (K)
        _popup.MinTemperature = _settings.MinTemperature; // temperature slider range
        _popup.MaxTemperature = _settings.MaxTemperature;
        _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled; // master switch drives popup layout
        _popup.OnBrightnessChanged += OnPopupBrightnessChanged;
        _popup.OnTemperatureChanged += OnPopupTemperatureChanged;
        _popup.OnShownChanged += OnPopupShownChanged;
        _mouseHook.SetPopup(_popup);

        // 6. Update tray tooltip with current brightness
        _trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);

        // 7. Register saved hotkeys (brightness up/down)
        RegisterHotkeys();

        // 7b. 启动时间调整调度器（若总开关开启且未被手动接管）。
        _solarScheduler = new SolarScheduler(_gamma, _settings!);
        if (_settings!.SolarAdjustEnabled && !_settings.SolarManuallyOverridden)
        {
            _solarScheduler.Start();
        }

        // 8. Auto-show settings window for testing automation (--show-settings)
        if (showSettingsOnStart)
        {
            // Defer until the message loop is running so the window is
            // shown after the tray is fully initialized (calling Show()
            // synchronously before Application.Run() can leave the window
            // created-but-invisible).
            System.Windows.Forms.Application.Idle += OnIdleShowSettings;
        }
    }

    // ==================== 亮度/色温平滑 ====================

    /// <summary>启动渐变定时器与状态。</summary>
    private System.Windows.Forms.Timer? _smoothTimer;
    private DateTime _smoothStartTime;
    private float _smoothStartBright, _smoothTargetBright;
    private float _smoothStartTemp, _smoothTargetTemp;
    private bool _smoothBrightActive, _smoothTempActive;
    private const int SmoothDurationMs = 1200;
    private const int SmoothTickMs = 30;

    /// <summary>亮度平滑开关（默认开）。</summary>
    public bool GetBrightnessSmooth() => _settings?.BrightnessSmooth ?? true;

    /// <summary>色温平滑开关（默认开）。</summary>
    public bool GetTemperatureSmooth() => _settings?.TemperatureSmooth ?? true;

    /// <summary>设置亮度平滑开关。</summary>
    public void SetBrightnessSmooth(bool enabled)
    {
        if (_settings == null) return;
        _settings.BrightnessSmooth = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>设置色温平滑开关。</summary>
    public void SetTemperatureSmooth(bool enabled)
    {
        if (_settings == null) return;
        _settings.TemperatureSmooth = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>
    /// 启动时应用保存的亮度/色温。对应平滑开关开启且时间调整调度未运行
    /// 时，从屏幕当前实际值做约 1.2 秒的 ease-out 渐变；否则直接设置。
    /// 时间调整运行时由其自身的过渡时长规则平滑，这里直接设值。
    /// </summary>
    private void ApplyStartupGamma()
    {
        if (_settings == null || _gamma == null) return;
        float targetBright = _settings.LastBrightness;
        float targetTemp = _settings.ColorTemperatureEnabled
            ? _settings.LastTemperature
            : GammaController.DEFAULT_TEMPERATURE;

        // 时间调整调度运行时由其自身的过渡时长规则平滑，不启用本平滑。
        bool solarRuns = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
        StartSmoothTransition(targetBright, targetTemp,
            _settings.BrightnessSmooth && !solarRuns,
            _settings.TemperatureSmooth && !solarRuns);
    }

    /// <summary>
    /// 通用平滑过渡：把当前 gamma 值平滑过渡到目标值（ease-out cubic，
    /// 约 SmoothDurationMs）。smoothBright/smoothTemp 为 false 的通道瞬时
    /// 到位。时间调整调度运行中不应调用（调度由 SolarScheduler 自己平滑）。
    /// 供启动恢复、亮度挡位/色温挡位切换等大跨度跳变共用。
    /// </summary>
    private void StartSmoothTransition(float targetBright, float targetTemp, bool smoothBright, bool smoothTemp)
    {
        if (_gamma == null) return;
        if (!smoothBright && !smoothTemp)
        {
            _gamma.SetBrightness(targetBright);
            _gamma.SetTemperature(targetTemp);
            return;
        }
        // 非平滑通道瞬时到位，避免停留在旧值。
        if (!smoothBright) _gamma.SetBrightness(targetBright);
        if (!smoothTemp) _gamma.SetTemperature(targetTemp);

        _smoothStartBright = _gamma.ReadCurrentBrightness();
        _smoothStartTemp = _gamma.ReadCurrentTemperature();
        _smoothTargetBright = targetBright;
        _smoothTargetTemp = targetTemp;
        _smoothBrightActive = smoothBright;
        _smoothTempActive = smoothTemp;
        _smoothStartTime = DateTime.Now;
        // 动画进行中再次调用（连续切挡位）时复用 timer，参数直接覆盖。
        if (_smoothTimer == null)
        {
            _smoothTimer = new System.Windows.Forms.Timer { Interval = SmoothTickMs };
            _smoothTimer.Tick += OnSmoothTick;
        }
        _smoothTimer.Start();
    }

    private void OnSmoothTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _smoothStartTime).TotalMilliseconds / SmoothDurationMs;
        if (t >= 1.0) t = 1.0;
        double ease = 1.0 - Math.Pow(1.0 - t, 3.0); // ease-out cubic
        if (_smoothBrightActive)
            _gamma?.SetBrightness((float)(_smoothStartBright + (_smoothTargetBright - _smoothStartBright) * ease));
        if (_smoothTempActive)
            _gamma?.SetTemperature((float)(_smoothStartTemp + (_smoothTargetTemp - _smoothStartTemp) * ease));
        if (t >= 1.0)
        {
            _smoothTimer?.Stop();
            _smoothTimer?.Dispose();
            _smoothTimer = null;
            // 动画结束：托盘提示显示最终值。
            _trayIcon?.UpdateTooltip(
                _gamma?.CurrentBrightness ?? 1f,
                _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE,
                _settings?.ColorTemperatureEnabled ?? false);
        }
    }
    private void OnIdleShowSettings(object? sender, EventArgs e)
    {
        System.Windows.Forms.Application.Idle -= OnIdleShowSettings;
        OnSettingsRequested(this, EventArgs.Empty);
    }

    private void OnLeftClickRequested(object? sender, EventArgs e)
    {
        // Dismiss the wheel OSD first so popup and OSD never overlap
        _overlay?.Hide();

        #if DEBUG
        PopupDebug.Log("OnLeftClickRequested: BEGIN");
        #endif

        // Live icon rect: no cache, and self-heals (triggers icon recovery
        // when the shell temporarily loses the icon) — same robustness as
        // the wheel path's IsMouseOverIconNow.
        var iconRect = _trayIcon?.GetIconRectLive();
        if (iconRect.HasValue)
        {
            #if DEBUG
            PopupDebug.Log($"OnLeftClickRequested: iconRect={iconRect.Value}");
            #endif
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, iconRect.Value);
        }
        else
        {
            // Tray rect unavailable (shell hiccup, explorer restart, icon
            // temporarily lost). Fall back to a default position above the
            // taskbar of the screen under the cursor so the left-click is
            // never a silent no-op. The anchor timer will re-anchor the
            // popup to the real icon rect as soon as the icon recovers.
            var cursorPos = Cursor.Position;
            var screen = Screen.FromPoint(cursorPos);
            var wa = screen.WorkingArea;
            var fallbackRect = new Rectangle(
                wa.Left + (wa.Width - 120) / 2,
                wa.Bottom - 40,
                120,
                40);
            #if DEBUG
            PopupDebug.Log($"OnLeftClickRequested: FALLBACK cursor={cursorPos} screen={screen.Bounds} wa={wa} fallbackRect={fallbackRect}");
            #endif
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, fallbackRect);
        }
    }

    private void OnContextMenuOpening(object? sender, EventArgs e)
    {
        // Dismiss popup + OSD so the context menu is the only visible surface
        _popup?.Dismiss();
        _overlay?.Hide();
    }

    private void OnTrayDpiChanged(object? sender, EventArgs e)
    {
        #if DEBUG
        PopupDebug.Log($"OnTrayDpiChanged: IsShown={_popup?.IsShown}");
        #endif

        // The tray icon moved to a new physical position after a DPI change.
        // If the left-click popup is open, re-anchor it above the icon's new
        // rect so it follows instead of staying at the stale position.
        if (_popup != null && _popup.IsShown)
        {
            var iconRect = _trayIcon?.GetIconRectLive();
            if (iconRect.HasValue)
            {
                _popup.ReanchorTo(iconRect.Value);
            }
        }
    }

    private void OnIconRectChanged(object? sender, EventArgs e)
    {
        // The icon moved (DPI change, taskbar relocation). Re-anchor the
        // popup above its new rect. Driven by the polling timer via
        // PollIconRect while the popup is open.
        if (_popup != null && _popup.IsShown)
        {
            var iconRect = _trayIcon?.GetIconRectLive();
            if (iconRect.HasValue)
            {
                _popup.ReanchorTo(iconRect.Value);
            }
        }
    }

    private void OnPopupShownChanged(object? sender, EventArgs e)
    {
        #if DEBUG
        PopupDebug.Log($"OnPopupShownChanged: IsShown={_popup?.IsShown}");
        #endif

        // Start polling the icon rect while the popup is visible, stop when
        // it closes. The timer drives PollIconRect, which raises
        // OnIconRectChanged only when the icon actually moved — so the
        // popup follows the icon across DPI changes without any window
        // churn when the icon is stationary.
        if (_popup != null && _popup.IsShown)
        {
            if (_popupAnchorTimer == null)
            {
                _popupAnchorTimer = new System.Windows.Forms.Timer
                {
                    Interval = (int)PopupAnchorInterval.TotalMilliseconds
                };
                _popupAnchorTimer.Tick += OnPopupAnchorTick;
            }
            _popupAnchorTimer.Start();
        }
        else
        {
            _popupAnchorTimer?.Stop();
        }
    }

    private void OnPopupAnchorTick(object? sender, EventArgs e)
    {
        if (_popup == null || !_popup.IsShown) return;

        // Re-anchor unconditionally on every tick: the popup must follow
        // the icon even when the icon's PHYSICAL rect is unchanged (DPI
        // change resizes the popup via WinForms auto-scaling, which can
        // misplace it; the icon itself stays put). ReanchorTo is idempotent
        // — with a stationary icon it re-applies the same size/position and
        // is cheap (SetWindowPos to the same location). PollIconRect still
        // drives OnIconRectChanged for icon-move cases, but the tick no
        // longer depends on it.
        var iconRect = _trayIcon?.GetIconRectLive();
        if (iconRect.HasValue)
        {
            _popup.ReanchorTo(iconRect.Value);
        }
    }

    /// <summary>
    /// 更新托盘提示。色温调节关闭时只显示亮度（不带色温值）。
    /// </summary>
    private void UpdateTrayTooltip(float? brightness = null, float? temperatureK = null)
    {
        float b = brightness ?? _gamma?.CurrentBrightness ?? 1.0f;
        float t = temperatureK ?? _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;
        bool showTemp = _settings?.ColorTemperatureEnabled ?? false;
        _trayIcon?.UpdateTooltip(b, t, showTemp);
    }

    private void OnPopupBrightnessChanged(object? sender, float brightness)
    {
        OnManualAdjustment();
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        SaveSettings();
    }

    /// <summary>
    /// 左键弹窗色温滑块回调：应用色温并保存。
    /// </summary>
    private void OnPopupTemperatureChanged(object? sender, float kelvin)
    {
        OnManualAdjustment();
        _gamma?.SetTemperature(kelvin);
        SaveSettings();
        TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
    }

    private void OnOverlayBrightnessChanged(object? sender, float brightness)
    {
        // 与弹窗/热键路径一致：OSD 滑块拖拽也属手动调节，需暂停时间调整调度。
        OnManualAdjustment();
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        SaveSettings();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        // Non-modal settings window; the tray stays fully usable while it is open.
        SettingsForm.ShowOrActivate();
    }

    private void OnUninstallRequested(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            Localization.Get("UninstallPrompt"),
            Localization.Get("UninstallTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            PerformUninstall();
        }
    }

    private void PerformUninstall()
    {
        // 1. Remove startup registry entry
        StartupManager.SetStartup(false);

        // 2. Release mutex so uninstaller can run
        Program.ReleaseMutex();

        var exePath = Application.ExecutablePath;
        var appName = Path.GetFileName(exePath);
        var appDir = Path.GetDirectoryName(exePath) ?? "";

        // 3a. Installed version: hand off to the Inno Setup uninstaller (unins000.exe).
        // It removes the app directory (including itself), desktop/start-menu shortcuts
        // and registry values recorded at install time — which a self-cleanup batch cannot do.
        var uninsExe = Path.Combine(appDir, "unins000.exe");
        if (File.Exists(uninsExe))
        {
            var uninsPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = uninsExe,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(uninsPsi);
            Application.Exit();
            return;
        }

        // 3b. Green version: self-cleanup via batch script
        var batchPath = Path.Combine(Path.GetTempPath(), $"uninstall_{appName}.bat");

        var batchContent = $@"
@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v IconStreams /f 2>nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v PastIconsStream /f 2>nul
rmdir /s /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GammaBrightnessTool")}"" 2>nul
del /f /q ""{Path.Combine(appDir, "settings.json")}"" 2>nul
del /f /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Gamma Brightness Tool.lnk")}"" 2>nul
del /f /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GammaBrightnessTool.lnk")}"" 2>nul
del /f /q ""{exePath}"" 2>nul
del /f /q ""{batchPath}"" 2>nul
";

        File.WriteAllText(batchPath, batchContent);

        // 4. Launch uninstaller and exit
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batchPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi);

        Application.Exit();
    }
    private void OnLanguageChanged(object? sender, Language lang)
    {
        if (_settings != null)
        {
            // lang is the user's raw choice (may be Language.System). Resolve
            // it to a concrete effective language; when the system language
            // is not supported, fall back to English and tell the user.
            var (effective, supported) = Localization.Resolve(lang);

            Localization.Setting = lang;
            Localization.Current = effective;
            _settings.Language = lang;
            SettingsManager.Save(_settings);
            // Refresh the tooltip text with the new language (NIM_MODIFY only — no icon re-registration needed, the sun glyph does not depend on language).
            _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);

            if (lang == Language.System && !supported)
            {
                MessageBox.Show(
                    Localization.Get("SystemLanguageUnsupported"),
                    Localization.Get("Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
    }

    /// <summary>
    /// Switches the UI language from the settings window. Keeps the in-memory
    /// settings instance in sync (so later saves don't overwrite the choice)
    /// and refreshes the tray tooltip immediately.
    /// </summary>
    public void ChangeLanguage(Language lang)
    {
        OnLanguageChanged(null, lang);
    }
    /// <summary>
    /// Returns the theme mode chosen in settings (System/Dark/Light).
    /// </summary>
    public ThemeMode GetTheme()
    {
        return _settings?.Theme ?? ThemeMode.System;
    }

    /// <summary>
    /// Stores the theme choice. Keeps the in-memory settings in sync,
    /// persists it, and applies it immediately so open windows (settings
    /// form, tray menu) repaint in the new theme without a restart.
    /// </summary>
    public void SetTheme(ThemeMode theme)
    {
        if (_settings == null) return;
        _settings.Theme = theme;
        SettingsManager.Save(_settings);
        ThemeManager.Apply(theme);
    }

    /// <summary>
    /// Returns the popup theme mode chosen in settings (System/Dark/Light),
    /// independent of the main UI theme.
    /// </summary>
    public ThemeMode GetPopupTheme()
    {
        return _settings?.PopupTheme ?? ThemeMode.System;
    }

    /// <summary>
    /// Stores the popup theme choice. Keeps the in-memory settings in sync,
    /// persists it, and applies it immediately so the floating popups
    /// repaint in the new theme without a restart.
    /// </summary>
    public void SetPopupTheme(ThemeMode theme)
    {
        if (_settings == null) return;
        _settings.PopupTheme = theme;
        SettingsManager.Save(_settings);
        ThemeManager.ApplyPopupTheme(theme);
    }

    /// <summary>Returns the per-wheel-notch brightness step (0..1).</summary>
    public float GetStepSize() => _settings?.StepSize ?? GammaController.DEFAULT_STEP;

    /// <summary>Stores the step size and applies it to the gamma controller.</summary>
    public void SetStepSize(float step)
    {
        if (_settings == null) return;
        _settings.StepSize = Math.Clamp(step, 0.01f, 1.0f);
        if (_gamma != null) _gamma.StepSize = _settings.StepSize;
        if (_popup != null) _popup.StepSize = _settings.StepSize;
        SettingsManager.Save(_settings);
    }


    /// <summary>Applies a fixed brightness level (0..1) with OSD feedback,
    /// matching the tray menu brightness submenu behavior.</summary>
    public void SetBrightnessLevel(float brightness)
    {
        if (_gamma == null) return;
        OnManualAdjustment();
        bool solarRuns = _settings != null && _settings.SolarAdjustEnabled && _settings.SolarManuallyOverridden != true;
        if (_settings?.BrightnessSmooth == true && !solarRuns)
            StartSmoothTransition(brightness, _gamma.CurrentTemperature, true, false);
        else
            _gamma.SetBrightness(brightness);
        _overlay?.Show(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
        SaveSettings();
    }

    /// <summary>Returns whether the wheel direction is inverted.</summary>
    public bool GetInvertScroll() => _settings?.InvertScroll ?? false;
    /// <summary>Stores the inverted-wheel preference.</summary>
    public void SetInvertScroll(bool invert)
    {
        if (_settings == null) return;
        _settings.InvertScroll = invert;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns whether the wheel brightness adjustment is enabled.</summary>
    public bool GetWheelEnabled() => _settings?.WheelEnabled ?? true;

    /// <summary>Stores the wheel brightness master switch.</summary>
    public void SetWheelEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.WheelEnabled = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns the per-wheel-notch color temperature step (K, default 100).</summary>
    public float GetTemperatureStepSize() => _settings?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP;

    /// <summary>Returns whether color temperature adjustment is enabled.</summary>
    public bool GetColorTemperatureEnabled() => _settings?.ColorTemperatureEnabled ?? false;

    /// <summary>Stores the color temperature master switch and updates the popup layout.</summary>
    public void SetColorTemperatureEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.ColorTemperatureEnabled = enabled;
        if (enabled)
        {
            // 开启色温：恢复上次保存的色温值（LastTemperature 由 SaveSettings 维护）
            _gamma?.SetTemperature(_settings.LastTemperature);
        }
        else
        {
            // 关闭色温：恢复中性白 6600K。LastTemperature 保持原值不变，
            // 下次开启色温时自动恢复用户上次设置的色温。
            _gamma?.SetTemperature(GammaController.DEFAULT_TEMPERATURE);
        }
        SettingsManager.Save(_settings);
        if (_popup != null) _popup.TemperatureEnabled = enabled; // update popup layout immediately
        // Update tray tooltip (hide temperature when disabled)
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, enabled);
        // 色温总开关影响时间调整的色温输出：重算一次让调度器对齐。
        ApplySolarScheduler();
    }

    /// <summary>Stores the per-wheel-notch color temperature step (K) and syncs it everywhere.</summary>
    public void SetTemperatureStepSize(float stepK)
    {
        if (_settings == null) return;
        _settings.TemperatureStepSize = Math.Clamp(stepK, GammaController.MIN_TEMPERATURE_STEP, GammaController.MAX_TEMPERATURE_STEP);
        SettingsManager.Save(_settings);
        if (_gamma != null) _gamma.TemperatureStepSize = _settings.TemperatureStepSize;
        if (_popup != null) _popup.TemperatureStepSize = _settings.TemperatureStepSize;
    }

    /// <summary>Returns the lower bound of the configurable color temperature range (K).</summary>
    public float GetMinTemperature() => _settings?.MinTemperature ?? GammaController.MIN_TEMPERATURE;

    /// <summary>Returns the upper bound of the configurable color temperature range (K).</summary>
    public float GetMaxTemperature() => _settings?.MaxTemperature ?? GammaController.MAX_TEMPERATURE;

    /// <summary>Stores the color temperature range [min, max] (K) and syncs it everywhere.</summary>
    public void SetTemperatureRange(float minK, float maxK)
    {
        if (_settings == null) return;
        minK = Math.Clamp(minK, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        maxK = Math.Clamp(maxK, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        if (minK >= maxK) return; // invalid range: ignore
        _settings.MinTemperature = minK;
        _settings.MaxTemperature = maxK;
        SettingsManager.Save(_settings);
        if (_gamma != null) { _gamma.MinTemperature = minK; _gamma.MaxTemperature = maxK; }
        if (_popup != null) { _popup.MinTemperature = minK; _popup.MaxTemperature = maxK; }
        // Clamp the current temperature into the new range so the display
        // never sits outside what the user configured.
        if (_gamma != null && _settings.ColorTemperatureEnabled)
            _gamma.SetTemperature(_gamma.CurrentTemperature);
    }

    /// <summary>
    /// Sets the color temperature directly (preset quick buttons).
    /// Ignored while color temperature is disabled; the preset buttons are
    /// disabled (grayed) by the settings page in that case anyway.
    /// </summary>
    public void SetColorTemperature(float kelvin)
    {
        if (_settings?.ColorTemperatureEnabled != true) return;
        OnManualAdjustment();
        bool solarRuns = _settings.SolarAdjustEnabled && _settings.SolarManuallyOverridden != true;
        if (_settings.TemperatureSmooth && !solarRuns)
            StartSmoothTransition(_gamma?.CurrentBrightness ?? 1f, kelvin, false, true);
        else
            _gamma?.SetTemperature(kelvin);
        SaveSettings();
        if (_gamma != null && _popup != null)
            _popup.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f,
            _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, true);
        TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
    }
    /// <summary>Returns the current color temperature in kelvin (read-only).</summary>
    public float GetCurrentTemperature() => _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;

    /// <summary>Raised whenever the color temperature changes (popup slider,
    /// preset buttons, wheel, hotkeys). Used by the settings page to keep
    /// the preset button highlight in sync.</summary>
    public event EventHandler<float>? TemperatureChanged;


    // ==================== 时间调整（日出日落自动调节） ====================

    /// <summary>返回时间调整总开关。</summary>
    public bool GetSolarAdjustEnabled() => _settings?.SolarAdjustEnabled ?? false;

    /// <summary>返回模式（true = 手动，false = 物理位置）。</summary>
    public bool GetSolarManualMode() => _settings?.SolarManualMode ?? true;

    /// <summary>手动日出时刻（分钟）。</summary>
    public int GetManualSunriseMinutes() => _settings?.ManualSunriseMinutes ?? 440;

    /// <summary>手动日落时刻（分钟）。</summary>
    public int GetManualSunsetMinutes() => _settings?.ManualSunsetMinutes ?? 990;

    /// <summary>物理位置纬度。</summary>
    public double GetSolarLatitude() => _settings?.SolarLatitude ?? 39.9042;

    /// <summary>物理位置经度。</summary>
    public double GetSolarLongitude() => _settings?.SolarLongitude ?? 116.4074;

    /// <summary>是否已成功获取过物理位置。</summary>
    public bool GetSolarLocationSet() => _settings?.SolarLocationSet ?? false;

    /// <summary>白天目标色温（K）。</summary>
    public float GetDayTemperature() => _settings?.DayTemperature ?? 6600f;

    /// <summary>白天目标亮度（0~1）。</summary>
    public float GetDayBrightness() => _settings?.DayBrightness ?? 1.0f;

    /// <summary>夜晚目标色温（K）。</summary>
    public float GetNightTemperature() => _settings?.NightTemperature ?? 3900f;

    /// <summary>夜晚目标亮度（0~1）。</summary>
    public float GetNightBrightness() => _settings?.NightBrightness ?? 0.85f;

    /// <summary>过渡时长（分钟）。</summary>
    public int GetTransitionMinutes() => _settings?.TransitionMinutes ?? 0;

    /// <summary>手动接管标志。</summary>
    public bool GetSolarManuallyOverridden() => _settings?.SolarManuallyOverridden ?? false;

    /// <summary>
    /// 设置时间调整总开关。开启时清除手动接管标志并（重）启动调度；
    /// 关闭时停止调度。
    /// </summary>
    public void SetSolarAdjustEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.SolarAdjustEnabled = enabled;
        if (enabled)
        {
            // 重新开启 = 重新生效：清除手动接管标志，从当前值平滑过渡到目标。
            _settings.SolarManuallyOverridden = false;
        }
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        if (!enabled)
        {
            // 关闭自动调节：亮度/色温恢复为未执行自动调节时的手动值。
            float targetBright = _settings.LastBrightness;
            float targetTemp = _settings.ColorTemperatureEnabled
                ? _settings.LastTemperature
                : GammaController.DEFAULT_TEMPERATURE;
            _gamma?.SetBrightness(targetBright);
            _gamma?.SetTemperature(targetTemp);
        }
    }

    /// <summary>设置模式（true = 手动，false = 物理位置），并立即重算。</summary>
    public void SetSolarManualMode(bool manual)
    {
        if (_settings == null) return;
        _settings.SolarManualMode = manual;
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>手动日出（分钟）。</summary>
    public void SetManualSunriseMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.ManualSunriseMinutes = Math.Clamp(minutes, 0, 1439);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>手动日落（分钟）。</summary>
    public void SetManualSunsetMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.ManualSunsetMinutes = Math.Clamp(minutes, 0, 1439);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>物理位置（纬度/经度）。</summary>
    public void SetSolarLocation(double latitude, double longitude)
    {
        if (_settings == null) return;
        _settings.SolarLatitude = latitude;
        _settings.SolarLongitude = longitude;
        _settings.SolarLocationSet = true;
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>白天目标色温（K）。</summary>
    public void SetDayTemperature(float kelvin)
    {
        if (_settings == null) return;
        _settings.DayTemperature = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>白天目标亮度（0~1）。</summary>
    public void SetDayBrightness(float brightness)
    {
        if (_settings == null) return;
        _settings.DayBrightness = Math.Clamp(brightness, 0f, 1f);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>夜晚目标色温（K）。</summary>
    public void SetNightTemperature(float kelvin)
    {
        if (_settings == null) return;
        _settings.NightTemperature = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>夜晚目标亮度（0~1）。</summary>
    public void SetNightBrightness(float brightness)
    {
        if (_settings == null) return;
        _settings.NightBrightness = Math.Clamp(brightness, 0f, 1f);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>过渡时长（分钟）。</summary>
    public void SetTransitionMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.TransitionMinutes = Math.Clamp(minutes, 0, 60);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>
    /// 目标值滑块拖动时调用：更新目标值但不打断调度，且当前立即跟随。
    /// 由设置页在滑块 ValueChanged/ValueCommitted 中调用 setter 后触发。
    /// </summary>
    public void RefreshSolarNow()
    {
        if (_settings?.SolarAdjustEnabled != true) return;
        _solarScheduler?.ApplyNowInstant();
    }

    /// <summary>
    /// 根据当前设置启停调度器（总开关 + 手动接管标志决定）。
    /// </summary>
    private void ApplySolarScheduler()
    {
        if (_solarScheduler == null || _gamma == null) return;
        if (_settings?.SolarAdjustEnabled == true && _settings.SolarManuallyOverridden != true)
        {
            if (!_solarScheduler.IsRunning) _solarScheduler.Start();
            else _solarScheduler.Tick();
        }
        else
        {
            _solarScheduler.Stop();
        }
    }


    /// <summary>Returns whether the wheel OSD overlay is shown.</summary>
    public bool GetShowOverlay() => _settings?.ShowOverlay ?? true;

    /// <summary>Returns whether the settings window should be always-on-top.</summary>
    public bool GetTopMost() => _settings?.SettingsTopMost ?? false;

    /// <summary>Stores the settings-window always-on-top preference.</summary>
    public void SetTopMost(bool topMost)
    {
        if (_settings == null) return;
        _settings.SettingsTopMost = topMost;
        SettingsManager.Save(_settings);
    }

    /// <summary>
    /// Resets all settings to their defaults and applies the changes
    /// immediately: language/theme re-applied, step size restored, brightness
    /// back to 100%. Hotkey bindings are intentionally NOT touched here (the
    /// hotkeys page has its own "clear all" action).
    /// </summary>
    public void ResetSettings()
    {
        if (_settings == null) return;

        _settings.LastBrightness = 1.0f;
        _settings.LastTemperature = GammaController.DEFAULT_TEMPERATURE;
        _settings.StepSize = 0.05f;
        _settings.WheelEnabled = true;
        _settings.SettingsTopMost = false;
        _settings.InvertScroll = false;
        _settings.ShowOverlay = true;
        _settings.OverlayDurationMs = 1500;
        _settings.Language = Language.System;
        _settings.Theme = ThemeMode.System;
        _settings.PopupTheme = ThemeMode.System;
        _settings.ColorTemperatureEnabled = false;
        _settings.TemperatureStepSize = GammaController.DEFAULT_TEMPERATURE_STEP;
        _settings.MinTemperature = GammaController.MIN_TEMPERATURE;
        _settings.MaxTemperature = GammaController.MAX_TEMPERATURE;
        _settings.AllHotKeysEnabled = true;
        _settings.StartupEnabled = null; // first-run: follow the registry
        // 时间调整：重置为默认（关、手动模式、默认目标值、默认过渡）。
        _settings.SolarAdjustEnabled = false;
        _settings.SolarManualMode = true;
        _settings.ManualSunriseMinutes = 440;
        _settings.ManualSunsetMinutes = 990;
        _settings.SolarLatitude = 39.9042;
        _settings.SolarLongitude = 116.4074;
        _settings.SolarLocationSet = false;
        _settings.DayTemperature = 6600f;
        _settings.DayBrightness = 1.0f;
        _settings.NightTemperature = 3900f;
        _settings.NightBrightness = 0.85f;
        _settings.TransitionMinutes = 0;
        _settings.SolarManuallyOverridden = false;
        SettingsManager.Save(_settings);

        // Apply language + themes immediately (same as startup).
        Localization.Setting = _settings.Language;
        Localization.Current = Localization.Resolve(_settings.Language).Effective;
        ThemeManager.Apply(_settings.Theme);
        ThemeManager.ApplyPopupTheme(_settings.PopupTheme);

        // Restore step size everywhere.
        if (_gamma != null) _gamma.StepSize = _settings.StepSize;
        if (_popup != null) _popup.StepSize = _settings.StepSize;
        if (_gamma != null) _gamma.TemperatureStepSize = _settings.TemperatureStepSize;
        if (_popup != null) _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
        // Restore temperature range everywhere.
        if (_gamma != null) { _gamma.MinTemperature = _settings.MinTemperature; _gamma.MaxTemperature = _settings.MaxTemperature; }
        if (_popup != null) { _popup.MinTemperature = _settings.MinTemperature; _popup.MaxTemperature = _settings.MaxTemperature; }

        // Re-register hotkeys (bindings unchanged; this just refreshes
        // whatever is currently bound).
        RegisterHotkeys();

        // Back to full brightness (and neutral color temperature).
        _gamma?.SetBrightness(1.0f);
        _gamma?.SetTemperature(GammaController.DEFAULT_TEMPERATURE);
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);

        // 时间调整已重置为关闭，停止调度器。
        ApplySolarScheduler();
    }

    /// <summary>
    /// Clears ALL hotkey bindings (increase/decrease brightness and power
    /// off) and re-registers (i.e. unregisters) them. Used by the "clear
    /// all" button on the hotkeys page.
    /// </summary>
    public void ClearAllHotkeys()
    {
        if (_settings == null) return;
        _settings.IncreaseBrightnessHotKey = "";
        _settings.DecreaseBrightnessHotKey = "";
        _settings.PowerOffHotKey = "";
        _settings.IncreaseTemperatureHotKey = "";
        _settings.DecreaseTemperatureHotKey = "";
        // Keep the enable switches on (they only gate an existing binding).
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Stores the OSD visibility preference.</summary>
    public void SetShowOverlay(bool show)
    {
        if (_settings == null) return;
        _settings.ShowOverlay = show;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns the current hotkey for increasing brightness ("" if not bound).</summary>
    public string GetIncreaseBrightnessHotKey() => _settings?.IncreaseBrightnessHotKey ?? "";

    /// <summary>Returns whether the increase-brightness hotkey is enabled.</summary>
    public bool GetIncreaseBrightnessHotKeyEnabled() => _settings?.IncreaseBrightnessHotKeyEnabled ?? true;

    /// <summary>Sets whether the increase-brightness hotkey is enabled (re-registers).</summary>
    public void SetIncreaseBrightnessHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseBrightnessHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Returns the current hotkey for decreasing brightness ("" if not bound).</summary>
    public string GetDecreaseBrightnessHotKey() => _settings?.DecreaseBrightnessHotKey ?? "";

    /// <summary>Returns whether the decrease-brightness hotkey is enabled.</summary>
    public bool GetDecreaseBrightnessHotKeyEnabled() => _settings?.DecreaseBrightnessHotKeyEnabled ?? true;

    /// <summary>Sets whether the decrease-brightness hotkey is enabled (re-registers).</summary>
    public void SetDecreaseBrightnessHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseBrightnessHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for increasing brightness and registers it globally.</summary>
    public bool SetIncreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.IncreaseBrightnessHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.IncreaseBrightnessHotKey = newValue;
        return CommitHotKey("IncBrightness", newValue, previous,
            v => _settings.IncreaseBrightnessHotKey = v,
            _settings.IncreaseBrightnessHotKeyEnabled);
    }

    /// <summary>Sets the hotkey for decreasing brightness and registers it globally.</summary>
    public bool SetDecreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.DecreaseBrightnessHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.DecreaseBrightnessHotKey = newValue;
        return CommitHotKey("DecBrightness", newValue, previous,
            v => _settings.DecreaseBrightnessHotKey = v,
            _settings.DecreaseBrightnessHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for turning off the display ("" if not bound).</summary>
    public string GetPowerOffHotKey() => _settings?.PowerOffHotKey ?? "";

    /// <summary>Returns whether the power-off hotkey is enabled.</summary>
    public bool GetPowerOffHotKeyEnabled() => _settings?.PowerOffHotKeyEnabled ?? true;

    /// <summary>Sets whether the power-off hotkey is enabled (re-registers).</summary>
    public void SetPowerOffHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.PowerOffHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for turning off the display and registers it globally.</summary>
    public bool SetPowerOffHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.PowerOffHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.PowerOffHotKey = newValue;
        return CommitHotKey("PowerOff", newValue, previous,
            v => _settings.PowerOffHotKey = v,
            _settings.PowerOffHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for increasing color temperature ("" if not bound).</summary>
    public string GetIncreaseTemperatureHotKey() => _settings?.IncreaseTemperatureHotKey ?? "";

    /// <summary>Returns whether the increase-temperature hotkey is enabled.</summary>
    public bool GetIncreaseTemperatureHotKeyEnabled() => _settings?.IncreaseTemperatureHotKeyEnabled ?? true;

    /// <summary>Sets whether the increase-temperature hotkey is enabled (re-registers).</summary>
    public void SetIncreaseTemperatureHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseTemperatureHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for increasing color temperature and registers it globally.</summary>
    public bool SetIncreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.IncreaseTemperatureHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.IncreaseTemperatureHotKey = newValue;
        return CommitHotKey("IncTemperature", newValue, previous,
            v => _settings.IncreaseTemperatureHotKey = v,
            _settings.IncreaseTemperatureHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for decreasing color temperature ("" if not bound).</summary>
    public string GetDecreaseTemperatureHotKey() => _settings?.DecreaseTemperatureHotKey ?? "";

    /// <summary>Returns whether the decrease-temperature hotkey is enabled.</summary>
    public bool GetDecreaseTemperatureHotKeyEnabled() => _settings?.DecreaseTemperatureHotKeyEnabled ?? true;

    /// <summary>Sets whether the decrease-temperature hotkey is enabled (re-registers).</summary>
    public void SetDecreaseTemperatureHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseTemperatureHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Returns whether ALL hotkeys are enabled (master switch).</summary>
    public bool GetAllHotKeysEnabled() => _settings?.AllHotKeysEnabled ?? true;

    /// <summary>
    /// Sets the master switch: when false, no hotkey is registered regardless
    /// of its individual enabled flag. Re-registers on change.
    /// </summary>
    public void SetAllHotKeysEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.AllHotKeysEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for decreasing color temperature and registers it globally.</summary>
    public bool SetDecreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.DecreaseTemperatureHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey))
            return false;
        _settings.DecreaseTemperatureHotKey = newValue;
        return CommitHotKey("DecTemperature", newValue, previous,
            v => _settings.DecreaseTemperatureHotKey = v,
            _settings.DecreaseTemperatureHotKeyEnabled);
    }

    /// <summary>
    /// True when the non-empty combo equals one of the other slots current
    /// bindings (case-insensitive). Used to reject binding the same combo
    /// to two actions.
    /// </summary>
    private static bool IsTakenByAnother(string hotkey, params string[] others)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        foreach (var o in others)
        {
            if (!string.IsNullOrWhiteSpace(o) &&
                string.Equals(o, hotkey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a bound slot is actually registered after the last
    /// RegisterHotkeys() run. Unbound slots always count as active.
    /// </summary>
    private bool HotKeyActive(string slot, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return _hotKeyRegistration.TryGetValue(slot, out bool ok) && ok;
    }

    /// <summary>
    /// Saves and re-registers, then verifies the new binding is active. On
    /// failure restores the previous binding, re-saves, re-registers and
    /// returns false. Used by the hotkey setters to roll back conflicts.
    /// </summary>
    private bool CommitHotKey(string slot, string newValue, string previous, Action<string> apply, bool enabled)
    {
        SettingsManager.Save(_settings!);
        RegisterHotkeys();
        if (!enabled || _settings?.AllHotKeysEnabled == false || HotKeyActive(slot, newValue)) return true;
        apply(previous);
        SettingsManager.Save(_settings!);
        RegisterHotkeys();
        return false;
    }

    /// <summary>
    /// Suspends ALL hotkeys while the user is recording a new combo in
    /// the settings window, so the new binding can be typed without
    /// triggering any existing hotkey. Re-registration happens on
    /// resume.
    /// </summary>
    public void SuspendAllHotKeys()
    {
        if (_hotKeysSuspended) return;
        _hotKeysSuspended = true;
        RegisterHotkeys();
    }

    /// <summary>
    /// Clears the suspension and re-registers all hotkeys. Safe to call
    /// even when nothing is suspended.
    /// </summary>
    public void ResumeAllHotKeys()
    {
        if (!_hotKeysSuspended) return;
        _hotKeysSuspended = false;
        RegisterHotkeys();
    }

    /// <summary>True while hotkeys are suspended (a recording is active).</summary>
    private bool HotKeysSuspended => _hotKeysSuspended;

    /// <summary>
    /// Registers all enabled hotkeys. When a hotkey recording is
    /// active (HotKeysSuspended), nothing is registered so the user
    /// can type the new combo without triggering existing bindings.
    /// </summary>
    private void RegisterHotkeys()
    {
        var hks = _trayIcon?.HotKeyService;
        if (hks == null) return;

        // Unregister all first (hotkeys may conflict with each other when changed)
        hks.UnregisterAll();
        _hotKeyRegistration.Clear();

        // Increase brightness hotkey
        var inc = _settings?.IncreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(inc) && _settings?.IncreaseBrightnessHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["IncBrightness"] = hks.TryRegister(inc, () =>
            {
                // 与滚轮同源：弹窗打开时直接调弹窗（不唤出 OSD），关闭时走 gamma+OSD
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(1);
                }
                else
                {
                    _gamma?.AdjustBrightness(_gamma?.StepSize ?? GammaController.DEFAULT_STEP);
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            }, out _);
        }

        // Decrease brightness hotkey
        var dec = _settings?.DecreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(dec) && _settings?.DecreaseBrightnessHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["DecBrightness"] = hks.TryRegister(dec, () =>
            {
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustBrightness(-(_gamma?.StepSize ?? GammaController.DEFAULT_STEP));
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            }, out _);
        }

        // Power off display hotkey
        var powerOff = _settings?.PowerOffHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(powerOff) && _settings?.PowerOffHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["PowerOff"] = hks.TryRegister(powerOff, () =>
            {
                // Broadcast SC_MONITORPOWER with lParam=2 (full power off).
                // Same code path as the popup's power button.
                NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                    new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(2));
            }, out _);
        }

        // Increase temperature hotkey (ignored while color temperature is disabled)
        var incTemp = _settings?.IncreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(incTemp) && _settings?.IncreaseTemperatureHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["IncTemperature"] = hks.TryRegister(incTemp, () =>
            {
                if (_settings?.ColorTemperatureEnabled != true) return; // 色温关闭时忽略
                // 与滚轮同源：弹窗打开且处于色温模式时直接调弹窗；否则走 gamma
                if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
                {
                    _popup.AdjustByWheel(1);
                }
                else
                {
                    _gamma?.AdjustTemperature(_gamma?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP);
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            }, out _);
        }

        // Decrease temperature hotkey (ignored while color temperature is disabled)
        var decTemp = _settings?.DecreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(decTemp) && _settings?.DecreaseTemperatureHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["DecTemperature"] = hks.TryRegister(decTemp, () =>
            {
                if (_settings?.ColorTemperatureEnabled != true) return; // 色温关闭时忽略
                if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustTemperature(-(_gamma?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP));
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            }, out _);
        }
    }

    /// <summary>
    /// 用户手动调亮度/色温时调用：若时间调整调度正在运行，则停止调度并
    /// 持久化手动接管标志（重启软件也保持），直到关闭再开启总开关才恢复。
    /// </summary>
    private void OnManualAdjustment()
    {
        if (_settings == null || _solarScheduler == null) return;
        if (!_solarScheduler.IsRunning) return;
        _solarScheduler.Stop();
        _settings.SolarManuallyOverridden = true;
        SettingsManager.Save(_settings);
    }

    private void SaveSettings()
    {
        if (_settings != null && _gamma != null)
        {
            _settings.LastBrightness = _gamma.CurrentBrightness;
            // 色温关闭时保留 LastTemperature（此时 gamma 已是 6600K 中性），
            // 以便下次开启色温时恢复用户上次设置的色温值。
            if (_settings.ColorTemperatureEnabled)
                _settings.LastTemperature = _gamma.CurrentTemperature;
            SettingsManager.Save(_settings);
        }
    }

    public void Dispose()
    {
        SaveSettings();

        _solarScheduler?.Dispose();
        _solarScheduler = null;

        _popupAnchorTimer?.Stop();
        _popupAnchorTimer?.Dispose();
        _popupAnchorTimer = null;

        _mouseHook?.Dispose();
        _popup?.Dispose();
        _gamma?.Dispose();
        _overlay?.Dispose();
        _trayIcon?.Dispose();

        _smoothTimer?.Stop();
        _smoothTimer?.Dispose();
        _smoothTimer = null;
    }
}
