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
    private AppSettings? _settings;

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

        // 6. Update tray tooltip with current brightness
        _trayIcon.UpdateTooltip(_gamma.CurrentBrightness);
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
            Localization.Get("UninstallConfirm"),
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

        _mouseHook?.Dispose();
        _gamma?.Dispose();
        _overlay?.Dispose();
        _trayIcon?.Dispose();
    }
}
