using System.Diagnostics;
using System.Text.Json;

namespace GammaBrightnessTool;

/// <summary>
/// Application settings with JSON persistence.
/// </summary>
public class AppSettings
{
    public float LastBrightness { get; set; } = 1.0f;
    public float StepSize { get; set; } = 0.05f;
    /// <summary>
    /// 滚轮调节总开关：false 时托盘滚轮不调节亮度（热键仍生效）。
    /// </summary>
    public bool WheelEnabled { get; set; } = true;
    /// <summary>
    /// 设置窗口置顶（always-on-top）。默认 false，避免遮挡其他窗口；
    /// 需要时可在通用设置页打开（方便测试/参照）。
    /// </summary>
    public bool SettingsTopMost { get; set; } = false;
    public bool InvertScroll { get; set; } = false;
    public bool ShowOverlay { get; set; } = true;
    public int OverlayDurationMs { get; set; } = 1500;
    public Language Language { get; set; } = Language.System;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>
    /// 浮窗主题（两个浮窗独立于软件主题），System/Dark/Light。
    /// </summary>
    public ThemeMode PopupTheme { get; set; } = ThemeMode.System;
    /// <summary>
    /// 开机自启状态，null 表示首次运行未初始化，以注册表实际状态为准同步。
    /// </summary>
    public bool? StartupEnabled { get; set; }

    /// <summary>
    /// 增加亮度的快捷键，格式 "Ctrl+Shift+Up" 或空字符串（未绑定）。
    /// </summary>
    public string IncreaseBrightnessHotKey { get; set; } = "";

    /// <summary>
    /// 增加亮度快捷键是否生效（开关关闭时即使已绑定也不注册）。
    /// </summary>
    public bool IncreaseBrightnessHotKeyEnabled { get; set; } = true;

    /// <summary>
    /// 降低亮度的快捷键，格式 "Ctrl+Shift+Down" 或空字符串（未绑定）。
    /// </summary>
    public string DecreaseBrightnessHotKey { get; set; } = "";

    /// <summary>
    /// 降低亮度快捷键是否生效（开关关闭时即使已绑定也不注册）。
    /// </summary>
    public bool DecreaseBrightnessHotKeyEnabled { get; set; } = true;

    /// <summary>
    /// 熄屏的快捷键，格式 "Ctrl+Shift+O" 或空字符串（未绑定）。
    /// </summary>
    public string PowerOffHotKey { get; set; } = "";

    /// <summary>
    /// 熄屏快捷键是否生效（开关关闭时即使已绑定也不注册）。
    /// </summary>
    public bool PowerOffHotKeyEnabled { get; set; } = true;
}

/// <summary>
/// Manages loading and saving of application settings.
/// </summary>
public static class SettingsManager
{
    private static readonly string SettingsDirectory;
    private static readonly string SettingsPath;

    public static string AppDataDirectory => SettingsDirectory;
    public static string SettingsFilePath => SettingsPath;

    public static bool IsPortableMode { get; }

    static SettingsManager()
    {
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";

        // 检测绿色模式：exe 目录可写，且不在系统 Program Files 中
        bool usePortable = false;
        try
        {
            string testFile = Path.Combine(exeDir, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            bool inProgramFiles = (!string.IsNullOrEmpty(pf) && exeDir.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(pf86) && exeDir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));

            usePortable = !inProgramFiles;
        }
        catch { }

        IsPortableMode = usePortable;

        if (usePortable)
        {
            SettingsDirectory = exeDir;
            SettingsPath = Path.Combine(exeDir, "settings.json");
        }
        else
        {
            SettingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GammaBrightnessTool");
            SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                // Auto-create default settings if not exists
                var defaultSettings = new AppSettings();
                Save(defaultSettings);
                return defaultSettings;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load settings: {ex}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex}");
        }
    }
}
