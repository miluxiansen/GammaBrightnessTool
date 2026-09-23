using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// 启动时完整性自检：注册表、用户配置、托盘图标可见性。
/// 发现问题时自动静默修复，不打扰用户。
/// </summary>
public static class IntegrityChecker
{
    /// <summary>
    /// 执行完整的启动自检流程。
    /// </summary>
    public static void RunCheck()
    {
        var settings = SettingsManager.Load();

        CheckStartupRegistry(settings);
        CheckSettingsFile();
        CheckTrayIconVisibility();
    }

    #region 1. 开机自启注册表检查

    private static void CheckStartupRegistry(AppSettings settings)
    {
        try
        {
            bool actual = StartupManager.IsStartupEnabled();

            if (settings.StartupEnabled == null)
            {
                // 首次运行（或旧版本升级）：以注册表实际状态为准同步到设置文件
                settings.StartupEnabled = actual;
                SettingsManager.Save(settings);
                return;
            }

            if (settings.StartupEnabled != actual)
            {
                // 设置文件与注册表不一致：以设置文件（用户意图）为准，修复注册表
                StartupManager.SetStartup(settings.StartupEnabled.Value);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IntegrityChecker] Startup registry check failed: {ex}");
        }
    }

    #endregion

    #region 2. 用户配置文件检查

    private static void CheckSettingsFile()
    {
        string path = SettingsManager.SettingsFilePath;

        // 2.1 文件不存在：由 SettingsManager.Load() 自动创建，无需额外处理
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);

            if (settings == null)
            {
                throw new Exception("Deserialized null settings");
            }

            // 2.2 校验关键字段有效性
            bool needFix = false;

            if (settings.LastBrightness < GammaController.MIN_BRIGHTNESS ||
                settings.LastBrightness > GammaController.MAX_BRIGHTNESS)
            {
                settings.LastBrightness = 1.0f;
                needFix = true;
            }

            if (settings.StepSize <= 0 || settings.StepSize > 0.5f)
            {
                settings.StepSize = GammaController.DEFAULT_STEP;
                needFix = true;
            }

            if (settings.OverlayDurationMs < 500 || settings.OverlayDurationMs > 10000)
            {
                settings.OverlayDurationMs = 1500;
                needFix = true;
            }

            // 色温范围：值必须在硬件范围内且 Min < Max，否则回退默认。
            if (settings.MinTemperature < GammaController.MIN_TEMPERATURE ||
                settings.MinTemperature > GammaController.MAX_TEMPERATURE ||
                settings.MaxTemperature < GammaController.MIN_TEMPERATURE ||
                settings.MaxTemperature > GammaController.MAX_TEMPERATURE ||
                settings.MinTemperature >= settings.MaxTemperature)
            {
                settings.MinTemperature = GammaController.MIN_TEMPERATURE;
                settings.MaxTemperature = GammaController.MAX_TEMPERATURE;
                needFix = true;
            }

            if (needFix)
            {
                SettingsManager.Save(settings);
                Debug.WriteLine("[IntegrityChecker] Fixed invalid settings values");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IntegrityChecker] Settings file corrupted: {ex.Message}");

            // 2.3 配置文件损坏：备份旧文件，重建默认配置
            try
            {
                string backupPath = path + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backupPath);
            }
            catch
            {
                /* 备份失败也继续重建 */
            }

            var fresh = new AppSettings();
            SettingsManager.Save(fresh);
            Debug.WriteLine("[IntegrityChecker] Recreated default settings");
        }
    }

    #endregion

    #region 3. 托盘图标可见性检查

    private static void CheckTrayIconVisibility()
    {
        const string trayNotifyKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify";

        try
        {
            // 3.1 确保 TrayNotify 键存在
            using var key = Registry.CurrentUser.OpenSubKey(trayNotifyKey, writable: true);
            if (key == null)
            {
                Registry.CurrentUser.CreateSubKey(trayNotifyKey);
                Debug.WriteLine("[IntegrityChecker] Created TrayNotify registry key");
                return;
            }

            // 3.2 检查 IconStreams 是否损坏或缺失我们的 GUID
            var iconStreams = key.GetValue("IconStreams");
            if (iconStreams is byte[] iconData)
            {
                if (iconData.Length < 20)
                {
                    // 数据明显损坏，删除重建
                    DeleteTrayStreams(key);
                    Debug.WriteLine("[IntegrityChecker] Deleted corrupted IconStreams");
                }
                else if (!ContainsGuid(iconData, TrayIconManager.IconGuid))
                {
                    // GUID 不在缓存中，删除让 Windows 重建，确保图标出现在隐藏图标菜单列表
                    DeleteTrayStreams(key);
                    Debug.WriteLine($"[IntegrityChecker] Deleted IconStreams to re-register GUID: {TrayIconManager.IconGuid}");
                }
            }

            // 3.3 检查 PastIconsStream
            var pastIconsStream = key.GetValue("PastIconsStream");
            if (pastIconsStream is byte[] pastData && pastData.Length < 20)
            {
                key.DeleteValue("PastIconsStream", throwOnMissingValue: false);
                Debug.WriteLine("[IntegrityChecker] Deleted corrupted PastIconsStream");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IntegrityChecker] Tray icon visibility check failed: {ex}");
        }
    }

    private static void DeleteTrayStreams(RegistryKey key)
    {
        key.DeleteValue("IconStreams", throwOnMissingValue: false);
        key.DeleteValue("PastIconsStream", throwOnMissingValue: false);
    }

    /// <summary>
    /// 在 IconStreams 二进制数据中搜索指定的 GUID 字节序列。
    /// </summary>
    private static bool ContainsGuid(byte[] data, Guid guid)
    {
        byte[] guidBytes = guid.ToByteArray();
        int limit = data.Length - guidBytes.Length;

        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < guidBytes.Length; j++)
            {
                if (data[i + j] != guidBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    #endregion
}
