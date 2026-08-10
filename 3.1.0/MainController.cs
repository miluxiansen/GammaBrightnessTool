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

    public void Initialize(bool silent)
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
        _gamma.SetBrightness(_settings.LastBrightness); // Load last brightness from settings

        // 4. Create brightness overlay
        _overlay = new BrightnessOverlay();
        _overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;

        // 5. Install mouse hook
        _mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
        {
            // Runtime-resolved so the settings UI changes apply immediately.
            IsInvertedScroll = () => _settings?.InvertScroll ?? false,
            IsOverlayEnabled = () => _settings?.ShowOverlay ?? true
        };
        _mouseHook.Install();

        // 5b. Create left-click brightness popup (docked above tray icon)
        _popup = new BrightnessPopup();
        _popup.StepSize = _settings.StepSize; // popup wheel uses the same step as the OSD path
        _popup.OnBrightnessChanged += OnPopupBrightnessChanged;
        _popup.OnShownChanged += OnPopupShownChanged;
        _mouseHook.SetPopup(_popup);

        // 6. Update tray tooltip with current brightness
        _trayIcon.UpdateTooltip(_gamma.CurrentBrightness);
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
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, iconRect.Value);
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
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, fallbackRect);
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

    private void OnPopupBrightnessChanged(object? sender, float brightness)
    {
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness);
        SaveSettings();
    }

    private void OnBrightnessSelected(object? sender, float brightness)
    {
        _gamma?.SetBrightness(brightness);
        _overlay?.Show(brightness);
        _trayIcon?.UpdateTooltip(brightness);
        SaveSettings();
    }

    public void OnBrightnessChanged()
    {
        if (_gamma != null)
        {
            _trayIcon?.UpdateTooltip(_gamma.CurrentBrightness);
            SaveSettings();
        }
    }

    private void OnOverlayBrightnessChanged(object? sender, float brightness)
    {
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness);
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
            _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f);
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
        _settings.StepSize = Math.Clamp(step, 0.01f, 0.5f);
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

    /// <summary>Returns whether the wheel OSD overlay is shown.</summary>
    public bool GetShowOverlay() => _settings?.ShowOverlay ?? true;

    /// <summary>Stores the OSD visibility preference.</summary>
    public void SetShowOverlay(bool show)
    {
        if (_settings == null) return;
        _settings.ShowOverlay = show;
        SettingsManager.Save(_settings);
    }


    private void SaveSettings()
    {
        if (_settings != null && _gamma != null)
        {
            _settings.LastBrightness = _gamma.CurrentBrightness;
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
