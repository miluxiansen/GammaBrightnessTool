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
    /// <summary>左键弹窗（BrightnessPopup）不透明度（%）。默认 90 = 沿用原固定值。</summary>
    public int PopupOpacityPercent { get; set; } = 90;
    /// <summary>OSD 浮窗（BrightnessOverlay）不透明度（%）。默认 70 = 沿用原固定值。</summary>
    public int OverlayOpacityPercent { get; set; } = 70;
    public Language Language { get; set; } = Language.System;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>
    /// 浮窗主题（两个浮窗独立于软件主题），System/Dark/Light。
    /// </summary>
    public ThemeMode PopupTheme { get; set; } = ThemeMode.System;
    /// <summary>
    /// 开机自启状态。**构造默认保持 null = "首次运行未初始化，以注册表实况为准"**。
    ///
    /// ⚠️ 2026-09-18 结论：**这一项「新装」与「恢复出厂」必须不对称**（有意为之）。
    ///   · 新装 → `null`：用户/安装器在安装包里勾了「开机自动启动」⇒ Setup.iss 已把 Run 值
    ///     写好 ⇒ 首次启动必须**无条件采纳注册表实况**，让开关显示为「开」。
    ///     这条路径（IntegrityChecker `StartupEnabled == null` 分支）**只读不写**，绝不删键。
    ///   · 恢复出厂 → `false`：用户明确要"关"。
    ///
    /// ⛔ 曾经把构造默认也写成 `false`，那会走「设置与注册表不一致 → 以设置为准修复注册表」
    ///   分支：只有在 `TryGetStartupCommandLine()` 的 Run 值**逐字等于**当前 exe 的
    ///   `BuildCommandLine()` 时才"采纳安装器意图"，**否则 `SetStartup(false)` 直接删掉 Run 键**。
    ///   于是「装完却先运行了另一份 exe（绿色版 / 换过目录 / 便携版）」会把安装器刚写的自启
    ///   **静默删除**。故构造默认必须是 `null`。
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
    /// 默认 **关闭** —— 与"恢复出厂设置"的期望结果一致（用户 2026-09-18 给的
    /// 恢复出厂清单：「快捷键总开关→关」）。此前构造默认 = true，导致
    /// "新装"与"恢复出厂"结果不同；热键**绑定本身**不在恢复出厂范围内
    /// （热键页有单独的"恢复默认"按钮）。
    /// ⚠ 兜底 getter 仍按 `?? true` 处理"配置读不到"的情形（热键 fail-open，
    /// 与 `settings == null || AllHotKeysEnabled` 的既有语义一致）。
    /// </summary>
    public bool AllHotKeysEnabled { get; set; } = false;

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

    /// <summary>手动日出时刻（从当天 0 点起的分钟数）。默认 08:00 = 480。</summary>
    public int ManualSunriseMinutes { get; set; } = 480;

    /// <summary>手动日落时刻（从当天 0 点起的分钟数）。默认 18:00 = 1080。</summary>
    public int ManualSunsetMinutes { get; set; } = 1080;

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

    /// <summary>手动接管标志：时间调整运行中用户手动调亮度/色温后置 true，
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
    /// 让应用显示原生色彩；退出全屏后自动恢复。默认关闭（用户明确要求，
    /// 避免升级/新装后无感知地改变画面行为）。
    /// 检测方式：监听前台窗口变化，判断窗口是否覆盖整个工作区。
    /// </summary>
    /// <remarks>
    /// ⚠️ 2026-09-18：此默认值必须与 `MainController.ResetSettings()`、
    /// `GetPauseInFullscreenEnabled()` 兜底、`SettingsForm` 兜底三处保持一致。
    /// 此前只有本处是 `false`、其余三处是 `true` ⇒ "新装"与"恢复出厂"结果不同；
    /// 且该开关一旦生效会让调节**静默失效**（`if (_paused) return;`），
    /// 属高风险默认值 ⇒ 已统一为 `false`。
    /// </remarks>
    public bool PauseInFullscreenEnabled { get; set; } = false;

    /// <summary>
    /// 禁用到期时间：右键菜单"禁用"后 gamma 暂停调节，画面保持原生色彩，
    /// 直到此时间自动恢复。null = 未禁用；过去的时间 = 已到期（启动/检查时
    /// 自动清除并恢复）。持久化以便重启软件后仍保持禁用状态。
    /// </summary>
    public DateTime? DisableUntil { get; set; }

    // ---- 3.6.0: 多显示器独立控制 ----

    /// <summary>
    /// 多显示器独立控制总开关。默认关闭：所有显示器统一调节（与旧版一致）。
    /// 开启后每台显示器独立保存亮度/色温，弹窗按屏显示独立滑轨。
    /// </summary>
    public bool PerMonitorEnabled { get; set; } = false;

    /// <summary>
    /// 每台显示器的状态，key = EDID 实例 ID（base 形式，如
    /// MONITOR\SAC2466\{4d36e96e-...}）。仅 PerMonitorEnabled 时使用。
    /// 缺省项回退到全局 LastBrightness/LastTemperature。
    /// </summary>
    public Dictionary<string, MonitorState> MonitorStates { get; set; } = new();

    /// <summary>
    /// 显示器自定义名称，key = EDID 实例 ID（base 形式）。
    /// 空/缺省 = 使用系统显示名（Generic PnP Monitor 等）。
    /// </summary>
    public Dictionary<string, string> MonitorNames { get; set; } = new();

    /// <summary>
    /// 托盘图标常驻显示（对应 Windows「其他系统托盘图标」里本软件的开关）。
    /// true = 写入 NotifyIconSettings\...\IsPromoted = 1；false = 写入 0。默认 true（§八）。
    /// P2″：系统「隐藏的图标菜单」总开关明确为关时，本项被强制视为 true，但<b>不改存储值</b>
    /// —— 总开关恢复开启后用户原偏好自动生效。
    /// </summary>
    public bool KeepTrayIconVisible { get; set; } = true;

    // ---- 应用白名单（设计稿 §16）----

    /// <summary>
    /// 应用白名单总开关。语义是**锁定/解锁**（不是"功能全关"）：
    /// false = 列表灰化锁定且白名单不生效；true = 可勾选且生效。默认 false。
    /// </summary>
    public bool AppWhitelistEnabled { get; set; } = false;

    /// <summary>
    /// 白名单应用（**规范化 exe 全路径**，判等 OrdinalIgnoreCase，与 LightBulb 一致）。
    /// 只存勾选结果；候选列表本身是会话快照、不持久化（§16.0）。
    /// </summary>
    public List<string> AppWhitelist { get; set; } = new();

    /// <summary>
    /// 逐屏白名单（2026-09-16 第三轮定稿）：开启后按"白名单应用窗口**完全落入**的屏幕"
    /// 决定暂停哪些屏。门控：独立控制开 && 受控屏 >= 2（条件不满足时自动置关但保留本值）。
    /// **追加字段**，旧版本读到会忽略（遵守"字段只能追加"红线）。
    /// </summary>
    public bool AppWhitelistPerMonitor { get; set; } = false;

    /// <summary>
    /// 该机驱动**是否需要**「每通道 ramp 峰值 ≥ 32768」下限保护（启动时探测一次并落盘）。
    /// null = 尚未探测（下次启动会探测，探测会短暂闪一下屏）。
    /// 探测结果只与"本机驱动"有关 ⇒ 换驱动后可用自动化动作 `Biz_ProbeRampFloor` 重探。
    /// </summary>
    public bool? RampFloorNeeded { get; set; }

    /// <summary>
    /// 全屏自动暂停「按显示器生效」（2026-09-19 定稿）：开启后只暂停**全屏窗口所在的那块显示器**，
    /// 其余显示器保持设定值 —— 修的是「副屏全屏把主屏色彩也一并抹掉」的既有缺陷。
    ///
    /// 门控（与「逐屏白名单」**完全同构**，用户 2026-09-19 拍板）：
    ///   全屏自动暂停开 &amp;&amp; 独立控制开 &amp;&amp; 受控屏 ≥ 2；
    ///   不满足 ⇒ 功能锁定（UI 置灰）且不生效，但**保留本值**，条件恢复后自动回到用户选择。
    ///
    /// ⚠️ 默认 `false`：门控要求用户先开「独立控制」，而 <see cref="PerMonitorEnabled"/> 构造默认
    /// 同样是 `false` ⇒ 本开关开箱即灰（与 <see cref="AppWhitelistPerMonitor"/> 一致）。
    /// 三来源（本构造 / `MainController.ResetSettings()` / `GetFullscreenPerMonitor()` 兜底）必须一致（§Y）。
    /// **追加字段**，旧版本读到会忽略（遵守"字段只能追加"红线）。
    /// </summary>
    public bool FullscreenPerMonitor { get; set; } = false;
}

/// <summary>
/// 单台显示器的独立状态（3.6.0 多显示器独立控制）。
/// </summary>
public sealed class MonitorState
{
    /// <summary>是否受控（停用的显示器冻结当前值，不受任何调节影响）。默认 true。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>该显示器 UI 亮度 0..1。</summary>
    public float Brightness { get; set; } = 1.0f;
    /// <summary>该显示器色温（K）。</summary>
    public float Temperature { get; set; } = GammaController.DEFAULT_TEMPERATURE;
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
        // 统一使用 %APPDATA%\GammaBrightnessTool 存放配置：
        // - 安装版与绿色版行为一致，覆盖安装/更换目录都不影响配置；
        // - 卸载时由安装程序保留（见 Setup.iss [UninstallDelete]：明确不删 {userappdata}\GammaBrightnessTool）；
        //   配置仅在"恢复出厂设置"或手动删除时清除，故卸载/重装后设置不会重置；
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
                OpLog.Log($"[settings] loaded main: {SettingsPath} ({json.Length} B)");
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
                    OpLog.Log($"[settings] migrated from exe dir: {altPath} -> {SettingsPath}");
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    OpLog.LogEx("[settings] migrate failed", ex);
                }
            }

            // Auto-create default settings if not exists
            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[settings] load failed", ex);
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
            // 原子写入：先写同目录临时文件再整体 Move 覆盖。直接 WriteAllText
            // 覆盖在写入中途崩溃/断电会把 settings.json 截断成非法 JSON，
            // 下次启动 IntegrityChecker 只能重建默认配置（用户设置全丢）。
            // 同卷 File.Move(overwrite) 是重命名级原子操作，临时文件即使残留
            // 也不会损坏正式配置。
            string tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
            OpLog.LogThrottled("settings.save", $"[settings] saved ({json.Length} B) -> {SettingsPath}");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(SettingsPath + ".tmp")) File.Delete(SettingsPath + ".tmp");
            }
            catch
            {
                // 清理失败不掩盖原始错误
            }
            OpLog.LogEx("[settings] save failed", ex);
        }
    }
}
