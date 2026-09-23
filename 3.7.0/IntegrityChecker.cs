using System.Text.Json;
using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// 启动时完整性自检：注册表、用户配置、托盘图标可见性。
/// 发现问题时自动静默修复，不打扰用户。
/// 自检的动作/结果写入 OpLog（%TEMP%\GammaBrightnessTool_ops.log）供实测复盘。
/// </summary>
public static class IntegrityChecker
{
    /// <summary>
    /// 执行完整的启动自检流程。
    /// </summary>
    public static void RunCheck()
    {
        OpLog.Log("[IntegrityChecker] self-check start");
        var settings = SettingsManager.Load();

        CheckStartupRegistry(settings);
        CheckSettingsFile();
        CheckTrayIconVisibility();
        OpLog.Log("[IntegrityChecker] self-check done");
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
                // 安装器代写识别（2026-09-14 用户报告：安装包勾选"开机自启"，装完
                // 进软件开关却是关的）。根因：安装包的 startup Task 直接写 Run 键，
                // 而保留的 settings.json 里 StartupEnabled=false（卸载重装场景）→
                // 走到这里被判为"不一致"，以设置为准把安装器刚写的 Run 键删掉。
                // 若 Run 值指向的正是当前 exe，则视为安装器意图，**采纳**到设置
                // （一次性），而不是删掉。
                if (actual
                    && settings.StartupEnabled == false
                    && StartupManager.TryGetStartupCommandLine(out string authored)
                    && string.Equals(authored,
                        StartupManager.BuildCommandLine(Application.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    settings.StartupEnabled = true;
                    SettingsManager.Save(settings);
                    OpLog.Log("[IntegrityChecker] startup: adopted installer-authored Run entry");
                    return;
                }

                // 设置文件与注册表不一致：以设置文件（用户意图）为准，修复注册表
                StartupManager.SetStartup(settings.StartupEnabled.Value);
                return;
            }

            // 开关状态一致、且为「开」时，再校验一次 Run 值指向的 exe 是否还在。
            // IsStartupEnabled() 只判断值是否存在、不比对路径 —— 若该值指向的 exe
            // 已被删除或搬走（绿色版换目录、旧版本目录被清、装卸路径变更），
            // 开关看起来仍是「开」的，实际开机静默失败且用户完全无感知。
            if (settings.StartupEnabled == true)
            {
                RepairDanglingStartupPath();
            }
        }
        catch (Exception ex)
        {
            OpLog.Log($"[IntegrityChecker] Startup registry check failed: {ex}");
        }
    }

    /// <summary>
    /// P4-a：自启 Run 键指向的 exe 已不存在时，用当前 exe 重写该值。
    ///
    /// 这是补齐「一致性」判据的逻辑漏洞 —— 原判据只覆盖开/关一维，漏了有效性：
    /// Run 值存在但指向死路径时，实际状态是「自启无效」，与用户意图「自启有效」
    /// 本就不一致，只是旧判据看不见。
    ///
    /// **有意收窄、不做的事**（见 3.7.0 设计稿 P4-a 取舍）：
    ///   * Run 值指向「存在但不同」的 exe（可能是用户刻意保留的另一份/绿色版）
    ///     → 不动。那属于 P4-b「路径纠偏」，会覆盖用户可能的刻意设定，本轮不做；
    ///   * 路径形态无法用于存在性判断（含环境变量、通配符、非盘符绝对路径）
    ///     → 不动，避免 File.Exists 误判；
    ///   * 所在盘未就绪（映射盘断连、可移动盘拔出）→ 不动，避免误判为「已删除」。
    ///
    /// 写入走静默路径（不弹框），失败只记日志，绝不阻断启动 —— 遵守 §3.4 fail-safe 定档。
    /// 调用时机在 MainController 加载设置**之前**，此刻不存在未落盘的内存改动，
    /// 故 TrySetStartupSilent 内部额外的 Load/Save 往返是安全的。
    /// </summary>
    private static void RepairDanglingStartupPath()
    {
        if (!StartupManager.TryGetStartupCommandLine(out string registered))
        {
            // 值为空 / 读不到：与 actual=true 矛盾，形态不明，不冒险动
            return;
        }

        if (!StartupManager.TryExtractExePath(registered, out string registeredExe))
        {
            OpLog.Log($"[IntegrityChecker] startup value not parseable, skip repair: [{registered}]");
            return;
        }

        if (!StartupManager.IsSafeForExistenceCheck(registeredExe))
        {
            // 含 %VAR% / 通配符 / 非盘符绝对路径 / 盘未就绪 → 无法可靠判断，跳过
            OpLog.Log($"[IntegrityChecker] startup path not probeable, skip repair: [{registeredExe}]");
            return;
        }

        if (File.Exists(registeredExe))
        {
            // P4-b′（2026-09-16 用户拍板）：路径**存在**，但可能不是"用户最后用过的那份"。
            // 按「最后运行时间为主、版本号为辅」仲裁一次 —— 见 KnownInstalls.PickPreferredExe。
            //
            // 这是对 §14.4「P4-b 不做路径纠偏」的**有意识变更**（原决策理由是"可能是用户
            // 刻意保留的另一份"）。用户 2026-09-16 定的语义是"谁最后用就归谁"，
            // 并配套要求「设置页手动拨开关立即生效、不被仲裁覆盖」——
            // 手动路径走 StartupManager.SetStartup()（无条件写当前 exe），两者互不干扰。
            //
            // ⚠️ 前置：本函数只在 StartupEnabled == true 时被调用（见 CheckStartupRegistry），
            //    所以这里不可能"偷偷打开自启"，符合设计定稿 §2.4 的第 ① 条。
            string? preferred = KnownInstalls.PickPreferredExe(registeredExe);
            if (preferred != null)
            {
                string target = StartupManager.BuildCommandLine(preferred);
                OpLog.Log($"[IntegrityChecker] startup target stale: registered=[{registeredExe}] " +
                          $"-> rewriting to [{target}]（按最后运行时间仲裁）");
                if (StartupManager.TrySetStartupSilent(true))
                {
                    OpLog.Log("[IntegrityChecker] startup target updated (arbitration)");
                }
            }
            return;
        }

        string expected = StartupManager.BuildCommandLine(Application.ExecutablePath);
        OpLog.Log($"[IntegrityChecker] dangling startup path detected: " +
                  $"registered=[{registered}] missing=[{registeredExe}] -> rewriting to [{expected}]");

        if (StartupManager.TrySetStartupSilent(true))
        {
            OpLog.Log("[IntegrityChecker] dangling startup path repaired");
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

            // ------------------------------------------------------------------
            // P0-4 修复（2026-09-23 代码审查）：校验矩阵补全。此前只校验上面 4 个字段，
            // 其余数值字段的越界损坏值（手编 settings.json / 半截写入等）会原样通过。
            // 说明：下游多数读取点有自己的 Clamp（SolarScheduler.Tick / SetTemperature 等），
            // 本校验的价值 = 保证**落盘值恒在硬件范围内**，不再依赖散落各处的下游钳制；
            // 其中 TemperatureStepSize 是下游完全未设防的一个（<=0 会让热键步进失灵、
            // <0 会反转方向）。
            // ⚠ JSON 反序列化本身不接受 NaN/Infinity（System.Text.Json 直接抛错 ⇒
            //   走下方"损坏重建"分支），故这里无需做 IsFinite 特判，与上方既有判据同风格。
            // ------------------------------------------------------------------
            if (settings.LastTemperature < GammaController.MIN_TEMPERATURE ||
                settings.LastTemperature > GammaController.MAX_TEMPERATURE)
            {
                settings.LastTemperature = GammaController.DEFAULT_TEMPERATURE;
                needFix = true;
            }

            if (settings.TemperatureStepSize < GammaController.MIN_TEMPERATURE_STEP ||
                settings.TemperatureStepSize > GammaController.MAX_TEMPERATURE_STEP)
            {
                settings.TemperatureStepSize = GammaController.DEFAULT_TEMPERATURE_STEP;
                needFix = true;
            }

            if (settings.DayTemperature < GammaController.MIN_TEMPERATURE ||
                settings.DayTemperature > GammaController.MAX_TEMPERATURE)
            {
                settings.DayTemperature = 6600f;
                needFix = true;
            }

            if (settings.NightTemperature < GammaController.MIN_TEMPERATURE ||
                settings.NightTemperature > GammaController.MAX_TEMPERATURE)
            {
                settings.NightTemperature = 3900f;
                needFix = true;
            }

            if (settings.DayBrightness < 0f || settings.DayBrightness > 1f)
            {
                settings.DayBrightness = 1f;
                needFix = true;
            }

            if (settings.NightBrightness < 0f || settings.NightBrightness > 1f)
            {
                settings.NightBrightness = 0.85f;
                needFix = true;
            }

            // 逐屏状态（key = EDID）：损坏条目夹回硬件范围，**不删键**（保留 Enabled 标记）。
            // 注意此前的恢复路径（RestoreSavedDisplayStates → InitializeDisplayState）读入时
            // 已有 Clamp，这里校验的是"落盘数据本身"的自洽。
            if (settings.MonitorStates.Count > 0)
            {
                foreach (string key in settings.MonitorStates.Keys.ToList())
                {
                    MonitorState ms = settings.MonitorStates[key];
                    float b = Math.Clamp(ms.Brightness, 0f, 1f);
                    float t = Math.Clamp(ms.Temperature,
                        GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
                    if (b != ms.Brightness || t != ms.Temperature)
                    {
                        settings.MonitorStates[key] = new MonitorState
                        {
                            Enabled = ms.Enabled,
                            Brightness = b,
                            Temperature = t
                        };
                        needFix = true;
                    }
                }
            }

            if (needFix)
            {
                SettingsManager.Save(settings);
                OpLog.Log("[IntegrityChecker] Fixed invalid settings values");
            }
        }
        catch (Exception ex)
        {
            OpLog.Log($"[IntegrityChecker] Settings file corrupted: {ex.Message}");

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
            OpLog.Log("[IntegrityChecker] Recreated default settings");
        }
    }

    #endregion

    #region 3. 托盘图标可见性检查

    /// <summary>
    /// 托盘图标可见性检查 —— <b>纯只读诊断，不写不删</b>。
    ///
    /// 历史沿革（2026-09-13 收口）：本方法原有两处系统级写入 ——
    ///   ① 键缺失时 `CreateSubKey` 自建 TrayNotify；
    ///   ② `IconStreams` / `PastIconsStream` 数据短于 20 字节时删除该值。
    /// 二者均已移除，理由：
    ///   * TrayNotify 是 explorer 自己的键，自检无权创建；
    ///   * 这两个流是<b>全系统共享</b>的旧式托盘历史缓存，删除会重置<b>所有软件</b>
    ///     的托盘图标自定义。项目在别处已明确不碰它 —— PerformUninstall 绿色版分支
    ///     注释（MainController.cs）与 Setup.iss 卸载侧注释都写明「刻意不删、副作用
    ///     大于收益」。自检里保留这条路会让项目内部策略自相矛盾，故一并收口。
    ///
    /// Win11 25H2 实测：本机 IconStreams / PastIconsStream / EnableAutoTray 均 absent
    /// （旧机制已废），本方法在该平台上只记录存在性。
    ///
    /// 注意：不得因「GUID 不在缓存中」就整体删除这两个值 —— 图标记录由 Shell 在
    /// 本程序以 NIF_GUID 注册时自动补建，无需自检越权清理。
    /// </summary>
    private static void CheckTrayIconVisibility()
    {
        const string trayNotifyKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify";

        try
        {
            // 只读打开。键不存在就当作「该平台不使用旧式托盘缓存」，直接返回；
            // 绝不 CreateSubKey —— TrayNotify 归 explorer 所有。
            using var key = Registry.CurrentUser.OpenSubKey(trayNotifyKey, writable: false);
            if (key == null)
            {
                OpLog.Log("[IntegrityChecker] TrayNotify key absent (read-only check, no write)");
                return;
            }

            // 只记录两个旧式流的现状，仅供诊断；存在与否都不动它。
            var iconStreams = key.GetValue("IconStreams");
            var pastIconsStream = key.GetValue("PastIconsStream");

            string iconDesc = iconStreams is byte[] a ? $"byte[{a.Length}]" : "<absent>";
            string pastDesc = pastIconsStream is byte[] b ? $"byte[{b.Length}]" : "<absent>";
            OpLog.Log($"[IntegrityChecker] TrayNotify(ro): IconStreams={iconDesc} PastIconsStream={pastDesc}");
        }
        catch (Exception ex)
        {
            OpLog.Log($"[IntegrityChecker] Tray icon visibility check failed: {ex}");
        }
    }

    #endregion
}
