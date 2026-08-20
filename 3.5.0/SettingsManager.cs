using System.Diagnostics;
using System.Text.Json;

namespace GammaBrightnessTool;

/// <summary>
/// Application settings with JSON persistence.
/// </summary>
public class AppSettings
{
    public float LastBrightness { get; set; } = 1.0f;
    /// <summary>
    /// 上次使用的色温（K）。6600K 为中性白，小于为暖色、大于为冷色。
    /// </summary>
    public float LastTemperature { get; set; } = GammaController.DEFAULT_TEMPERATURE;
    /// <summary>
    /// 色温调节总开关：false 时弹窗只调亮度、托盘提示只显示亮度。默认关闭。
    /// </summary>
    public bool ColorTemperatureEnabled { get; set; } = false;
    /// <summary>
    /// 色温滚轮步进值（K）。仅在色温调节开启时使用，独立于亮度步进。
    /// 范围 50~3000K，默认 100K。
    /// </summary>
    public float TemperatureStepSize { get; set; } = GammaController.DEFAULT_TEMPERATURE_STEP;
    /// <summary>
    /// 色温可调范围下限（K）。默认 3300K，可收窄（如 4000K）。
    /// </summary>
    public float MinTemperature { get; set; } = GammaController.MIN_TEMPERATURE;
    /// <summary>
    /// 色温可调范围上限（K）。默认 10000K，可收窄（如 8000K）。
    /// </summary>
    public float MaxTemperature { get; set; } = GammaController.MAX_TEMPERATURE;
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

    /// <summary>
    /// 增加色温的快捷键，格式 "Ctrl+Shift+PageUp" 或空字符串（未绑定）。
    /// 步进值由 TemperatureStepSize 控制；色温调节关闭时即使已绑定也忽略。
    /// </summary>
    public string IncreaseTemperatureHotKey { get; set; } = "";

    /// <summary>
    /// 增加色温快捷键是否生效（开关关闭时即使已绑定也不注册）。
    /// </summary>
    public bool IncreaseTemperatureHotKeyEnabled { get; set; } = true;

    /// <summary>
    /// 降低色温的快捷键，格式 "Ctrl+Shift+PageDown" 或空字符串（未绑定）。
    /// </summary>
    public string DecreaseTemperatureHotKey { get; set; } = "";

    /// <summary>
    /// 降低色温快捷键是否生效（开关关闭时即使已绑定也不注册）。
    /// </summary>
    public bool DecreaseTemperatureHotKeyEnabled { get; set; } = true;

    /// <summary>
    /// 快捷键总开关：false 时所有快捷键全部禁用（即使单项开关开启也不注册）。
    /// </summary>
    public bool AllHotKeysEnabled { get; set; } = true;

    // ---- 时间调整（按日出日落自动调节色温/亮度） ----

    /// <summary>
    /// 时间调整总开关。默认关闭。
    /// </summary>
    public bool SolarAdjustEnabled { get; set; } = false;

    /// <summary>
    /// 模式：true = 手动日出日落时间；false = 物理位置（经纬度 + 太阳时算法）。
    /// 默认手动。
    /// </summary>
    public bool SolarManualMode { get; set; } = true;

    /// <summary>手动日出时刻（从当天 0 点起的分钟数）。默认 07:20 = 440。</summary>
    public int ManualSunriseMinutes { get; set; } = 440;

    /// <summary>手动日落时刻（从当天 0 点起的分钟数）。默认 16:30 = 990。</summary>
    public int ManualSunsetMinutes { get; set; } = 990;

    /// <summary>物理位置纬度（十进制度，北正）。默认北京。</summary>
    public double SolarLatitude { get; set; } = 39.9042;

    /// <summary>物理位置经度（十进制度，东正）。默认北京。</summary>
    public double SolarLongitude { get; set; } = 116.4074;

    /// <summary>是否已成功获取过物理位置（决定 UI 是否显示已定位状态）。</summary>
    public bool SolarLocationSet { get; set; } = false;

    /// <summary>白天目标色温（K）。默认 6600K（中性白）。</summary>
    public float DayTemperature { get; set; } = 6600f;

    /// <summary>白天目标亮度（0~1）。默认 1.0。</summary>
    public float DayBrightness { get; set; } = 1.0f;

    /// <summary>夜晚目标色温（K）。默认 3900K（暖）。</summary>
    public float NightTemperature { get; set; } = 3900f;

    /// <summary>夜晚目标亮度（0~1）。默认 0.85。</summary>
    public float NightBrightness { get; set; } = 0.85f;

    /// <summary>日出/日落过渡时长（分钟）。0 = 瞬时切换。默认 0。</summary>
    public int TransitionMinutes { get; set; } = 0;

    /// <summary>
    /// 手动接管标志：时间调整运行中用户手动调亮度/色温后置 true，
    /// 调度暂停并持久化；重启软件也保持，直到关闭再开启总开关才清除。
    /// </summary>
    public bool SolarManuallyOverridden { get; set; } = false;

    /// <summary>
    /// 亮度平滑：软件启动时（时间调整未运行时）平滑过渡到保存的亮度值，
    /// 时间调整调度变化时也按此开关决定平滑/瞬时。默认开启。
    /// </summary>
    public bool BrightnessSmooth { get; set; } = true;

    /// <summary>
    /// 色温平滑：软件启动时（时间调整未运行时）平滑过渡到保存的色温值，
    /// 时间调整调度变化时也按此开关决定平滑/瞬时。默认开启。
    /// </summary>
    public bool TemperatureSmooth { get; set; } = true;

    /// <summary>
    /// Gamma 自愈：系统睡眠唤醒或显示器热插拔/分辨率变化后，自动重新
    /// 应用 gamma（重建显示器列表并重放当前亮度/色温）。默认开启。
    /// 睡眠唤醒后部分显卡驱动会重置 gamma ramp，热插拔后显示器列表
    /// 失效，开启此选项可自动恢复，无需重启软件。
    /// </summary>
    public bool GammaSelfHealEnabled { get; set; } = true;

    /// <summary>
    /// 全屏自动暂停：检测到全屏应用（游戏/视频）时暂停 gamma 调节，
    /// 让应用显示原生色彩；退出全屏后自动恢复。默认开启。
    /// 检测方式：监听前台窗口变化，判断窗口是否覆盖整个工作区。
    /// </summary>
    public bool PauseInFullscreenEnabled { get; set; } = true;

    /// <summary>
    /// 禁用到期时间：右键菜单"禁用"后 gamma 暂停调节，画面保持原生色彩，
    /// 直到此时间自动恢复。null = 未禁用；过去的时间 = 已到期（启动/检查时
    /// 自动清除并恢复）。持久化以便重启软件后仍保持禁用状态。
    /// </summary>
    public DateTime? DisableUntil { get; set; }
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


    static SettingsManager()
    {
        // 统一使用 %APPDATA%\\GammaBrightnessTool 存放配置：
        // - 安装版与绿色版行为一致，覆盖安装/更换目录都不影响配置；
        // - 卸载时才由安装程序一并删除（见 Setup.iss [UninstallDelete]）；
        // - 旧版绿色版配置（exe 旁 settings.json）在 Load 时自动迁移。
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GammaBrightnessTool");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
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

            // 主位置无配置：尝试从 exe 目录迁移旧绿色版配置（覆盖升级 /
            // 统一 AppData 之前的版本，设置残留在 exe 旁 settings.json）。
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
            string altPath = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, "settings.json") : "";
            if (!string.IsNullOrEmpty(altPath)
                && !string.Equals(altPath, SettingsPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(altPath))
            {
                try
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    File.Copy(altPath, SettingsPath, overwrite: true);
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to migrate settings: {ex}");
                }
            }

            // Auto-create default settings if not exists
            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
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
