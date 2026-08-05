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

        // Apply saved language
        Localization.Current = _settings.Language;

        // 2. Create tray icon first (visible immediately)
        _trayIcon = new TrayIconManager();
        _trayIcon.Initialize();
        _trayIcon.OnBrightnessSelected += OnBrightnessSelected;
        _trayIcon.OnLanguageChanged += OnLanguageChanged;
        _trayIcon.OnUninstallRequested += OnUninstallRequested;
        _trayIcon.OnLeftClickRequested += OnLeftClickRequested;
        _trayIcon.OnContextMenuOpening += OnContextMenuOpening;
        _trayIcon.OnTrayDpiChanged += OnTrayDpiChanged;
        _trayIcon.OnIconRectChanged += OnIconRectChanged;

        // 3. Initialize gamma controller
        _gamma = new GammaController();
        _gamma.Initialize();
        _gamma.SetBrightness(_settings.LastBrightness); // Load last brightness from settings

        // 4. Create brightness overlay
        _overlay = new BrightnessOverlay();
        _overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;

        // 5. Install mouse hook
        _mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay);
        _mouseHook.Install();

        // 5b. Create left-click brightness popup (docked above tray icon)
        _popup = new BrightnessPopup();
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
        // when the shell temporarily loses the icon) 鈥?same robustness as
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
        // OnIconRectChanged only when the icon actually moved 鈥?so the
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
        // 鈥?with a stationary icon it re-applies the same size/position and
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

        // 3. Build uninstall batch script (green version: no install dir)
        var exePath = Application.ExecutablePath;
        var appName = Path.GetFileName(exePath);
        var batchPath = Path.Combine(Path.GetTempPath(), $"uninstall_{appName}.bat");

        var batchContent = $@"
@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v IconStreams /f 2>nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v PastIconsStream /f 2>nul
rmdir /s /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GammaBrightnessTool")}"" 2>nul
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
            Localization.Current = lang;
            _settings.Language = lang;
            SettingsManager.Save(_settings);
            _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f);
            // Refresh tray icon tooltip text with new language
            _trayIcon?.RefreshIcon();
        }
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
