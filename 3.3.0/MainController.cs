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

    // Polls the tray icon rect while the popup is open so the popup follows
    // the icon across DPI changes / taskbar moves without depending on
    // WM_DPICHANGED delivery to a hidden message window (which Windows does
    // not send to hidden windows, and CheckMouseLeave only runs while the
    // cursor is over the icon). This is the same "always use fresh
    // coordinates" philosophy as the wheel OSD path.
    private System.Windows.Forms.Timer? _popupAnchorTimer;
    private static readonly TimeSpan PopupAnchorInterval = TimeSpan.FromMilliseconds(200);

    public void Initialize(bool silent, bool showSettingsOnStart = false)
    {
        // 1. Run startup integrity check (registry, settings, tray icon visibility)
        IntegrityChecker.RunCheck();

        // 2. Load settings (fresh load after check, auto-creates default if not exists)
        _settings = SettingsManager.Load();
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
        _trayIcon.OnBrightnessSelected += OnBrightnessSelected;
        _trayIcon.OnLanguageChanged += OnLanguageChanged;
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
        _gamma.SetBrightness(_settings.LastBrightness); // Load last brightness from settings
        if (_settings.ColorTemperatureEnabled)
            _gamma.SetTemperature(_settings.LastTemperature); // Load last color temperature from settings
        else
            _gamma.SetTemperature(GammaController.DEFAULT_TEMPERATURE); // color temp disabled: neutral white

        // 4. Create brightness overlay
        _overlay = new BrightnessOverlay();
        _overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;

        // 5. Install mouse hook
        _mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
        {
            // Runtime-resolved so the settings UI changes apply immediately.
            IsInvertedScroll = () => _settings?.InvertScroll ?? false,
            IsOverlayEnabled = () => _settings?.ShowOverlay ?? true,
            IsWheelEnabled = () => _settings?.WheelEnabled ?? true
        };
        _mouseHook.Install();

        // 5b. Create left-click brightness popup (docked above tray icon)
        _popup = new BrightnessPopup();
        _popup.StepSize = _settings.StepSize; // popup wheel uses the same step as the OSD path
        _popup.TemperatureStepSize = _settings.TemperatureStepSize; // temperature wheel step (K)
        _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled; // master switch drives popup layout
        _popup.OnBrightnessChanged += OnPopupBrightnessChanged;
        _popup.OnTemperatureChanged += OnPopupTemperatureChanged;
        _popup.OnShownChanged += OnPopupShownChanged;
        _mouseHook.SetPopup(_popup);

        // 6. Update tray tooltip with current brightness
        _trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);

        // 7. Register saved hotkeys (brightness up/down)
        RegisterHotkeys();

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
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        SaveSettings();
    }

    /// <summary>
    /// 左键弹窗色温滑块回调：应用色温并保存。
    /// </summary>
    private void OnPopupTemperatureChanged(object? sender, float kelvin)
    {
        _gamma?.SetTemperature(kelvin);
        SaveSettings();
    }

    private void OnBrightnessSelected(object? sender, float brightness)
    {
        _gamma?.SetBrightness(brightness);
        _overlay?.Show(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        SaveSettings();
    }

    public void OnBrightnessChanged()
    {
        if (_gamma != null)
        {
        _trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
            SaveSettings();
        }
    }

    private void OnOverlayBrightnessChanged(object? sender, float brightness)
    {
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
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
            // Refresh tray icon tooltip text with new language
            _trayIcon?.RefreshIcon();

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
        _settings.StartupEnabled = null; // first-run: follow the registry
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

        // Re-register hotkeys (bindings unchanged; this just refreshes
        // whatever is currently bound).
        RegisterHotkeys();

        // Back to full brightness (and neutral color temperature).
        _gamma?.SetBrightness(1.0f);
        _gamma?.SetTemperature(GammaController.DEFAULT_TEMPERATURE);
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
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
    public void SetIncreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseBrightnessHotKey = hotkey ?? "";
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for decreasing brightness and registers it globally.</summary>
    public void SetDecreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseBrightnessHotKey = hotkey ?? "";
        SettingsManager.Save(_settings);
        RegisterHotkeys();
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
    public void SetPowerOffHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.PowerOffHotKey = hotkey ?? "";
        SettingsManager.Save(_settings);
        RegisterHotkeys();
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
    public void SetIncreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseTemperatureHotKey = hotkey ?? "";
        SettingsManager.Save(_settings);
        RegisterHotkeys();
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

    /// <summary>Sets the hotkey for decreasing color temperature and registers it globally.</summary>
    public void SetDecreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseTemperatureHotKey = hotkey ?? "";
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        var hks = _trayIcon?.HotKeyService;
        if (hks == null) return;

        // Unregister all first (hotkeys may conflict with each other when changed)
        hks.UnregisterAll();

        // Increase brightness hotkey
        var inc = _settings?.IncreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(inc) && _settings?.IncreaseBrightnessHotKeyEnabled != false)
        {
            hks.Register(inc, () =>
            {
                // 与滚轮同源：弹窗打开时直接调弹窗（不唤出 OSD），关闭时走 gamma+OSD
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(1);
                }
                else
                {
                    _gamma?.AdjustBrightness(_gamma?.StepSize ?? GammaController.DEFAULT_STEP);
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            });
        }

        // Decrease brightness hotkey
        var dec = _settings?.DecreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(dec) && _settings?.DecreaseBrightnessHotKeyEnabled != false)
        {
            hks.Register(dec, () =>
            {
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustBrightness(-(_gamma?.StepSize ?? GammaController.DEFAULT_STEP));
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            });
        }

        // Power off display hotkey
        var powerOff = _settings?.PowerOffHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(powerOff) && _settings?.PowerOffHotKeyEnabled != false)
        {
            hks.Register(powerOff, () =>
            {
                // Broadcast SC_MONITORPOWER with lParam=2 (full power off).
                // Same code path as the popup's power button.
                NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                    new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(2));
            });
        }

        // Increase temperature hotkey (ignored while color temperature is disabled)
        var incTemp = _settings?.IncreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(incTemp) && _settings?.IncreaseTemperatureHotKeyEnabled != false)
        {
            hks.Register(incTemp, () =>
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
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            });
        }

        // Decrease temperature hotkey (ignored while color temperature is disabled)
        var decTemp = _settings?.DecreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(decTemp) && _settings?.DecreaseTemperatureHotKeyEnabled != false)
        {
            hks.Register(decTemp, () =>
            {
                if (_settings?.ColorTemperatureEnabled != true) return; // 色温关闭时忽略
                if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustTemperature(-(_gamma?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP));
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
            });
        }
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

        _popupAnchorTimer?.Stop();
        _popupAnchorTimer?.Dispose();
        _popupAnchorTimer = null;

        _mouseHook?.Dispose();
        _popup?.Dispose();
        _gamma?.Dispose();
        _overlay?.Dispose();
        _trayIcon?.Dispose();
    }
}
