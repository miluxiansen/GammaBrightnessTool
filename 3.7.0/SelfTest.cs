using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GammaBrightnessTool;

/// <summary>
/// 自动化自测套件（--selftest 入口触发）。
///
/// 安全边界（严格遵守，避免任何波及系统/用户的动作）：
///  * 不修改系统设置（分辨率/缩放/睡眠/主题注册表等一概不动）；
///  * 不 Kill/不启动任何进程（含同族实例，互斥被占即放弃并提示）；
///  * 不写显示器 gamma、不开弹窗/OSD、不碰 TrayNotify 注册表；
///  * 仅操作本应用配置目录（含损坏自愈测试：先备份、结束恢复原字节）；
///  * 主题/弹窗主题仅改内存态并循环恢复原值（触发事件给托盘图标刷图标，无持久影响）。
///
/// 覆盖项：T06 片段(主题循环 GDI 稳定性)、T11(Tooltip 全语言格式与长度)、
/// R02 静态(显示器枚举/EDID 唯一性)、配置存取/损坏自愈(M5 相关)、自检自证。
/// 结果同时写 %TEMP%\GammaBrightnessTool_ops.log 与 %TEMP%\GammaBrightnessTool_selftest.txt。
/// </summary>
public static class SelfTest
{
    private const uint GR_GDIOBJECTS = 0;
    private const uint GR_USEROBJECTS = 1;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    private static readonly List<string> Report = new();
    private static int _pass;
    private static int _fail;

    public static int RunAll()
    {
        OpLog.Log("[selftest] ================= suite start =================");
        Report.Clear();
        _pass = 0; _fail = 0;
        string reportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GammaBrightnessTool_selftest.txt");

        try
        {
            OpLog.Log($"[selftest] pid={Environment.ProcessId} gdiBase={GetGdiCount()} userBase={GetUserCount()}");
            MonitorEnumeration();
            SettingsRoundTrip();
            TooltipAllLanguages();
            ThemeCycleGdi();
            ConfigSelfHealIntegrity();
        }
        catch (Exception ex)
        {
            Fail("suite", "未预期异常: " + ex.Message + " @ " + ex.StackTrace?.Split('\n')[0]);
            OpLog.LogEx("[selftest] suite exception", ex);
        }
        finally
        {
            // 确保恢复用户主题（即便中途异常）
            try { RestoreTheme(Program.Instance); } catch { /* ignore */ }
        }

        // 汇总
        Report.Add("");
        Report.Add($"== 结果: {_pass} 通过 / {_fail} 失败 ==");
        OpLog.Log($"[selftest] suite finished: {_pass} pass / {_fail} fail -> {reportPath}");
        try
        {
            System.IO.File.WriteAllText(reportPath, string.Join(Environment.NewLine, Report) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { /* 写报告失败不影响退出码 */ }
        return _fail;
    }

    // ------------------------------------------------------------------

    private static void MonitorEnumeration()
    {
        Info("显示器枚举：无 gamma 写入、只读查询后释放设备上下文");
        try
        {
            var monitors = Monitor.GetAll();
            var ids = new List<string>();
            int emptyEdid = 0;
            foreach (var m in monitors)
            {
                ids.Add(m.EdidId);
                if (string.IsNullOrEmpty(m.EdidId)) emptyEdid++;
                Info($"  monitor: EdidId='{m.EdidId}'");
                m.Dispose();
            }
            Check("monitor.count>=1", monitors.Count >= 1, $"count={monitors.Count}");
            Check("monitor.edid.unique", ids.Distinct().Count() == ids.Count,
                $"unique={ids.Distinct().Count()}/total={ids.Count}");
            Check("monitor.edid.nonEmpty", emptyEdid == 0, $"emptyEdid={emptyEdid}");
        }
        catch (Exception ex)
        {
            Fail("monitor.enum", ex.Message);
        }
    }

    private static void SettingsRoundTrip()
    {
        Info("配置往返 + 损坏自愈（先备份、测后恢复原字节，不破坏用户配置）");
        string path = SettingsManager.SettingsFilePath;
        string? original = null;
        try
        {
            if (System.IO.File.Exists(path))
                original = System.IO.File.ReadAllText(path);

            var a = SettingsManager.Load();                 // 正常读
            string jsonA = System.Text.Json.JsonSerializer.Serialize(a);
            SettingsManager.Save(a);                         // 原子写
            var b = SettingsManager.Load();                  // 再读
            string jsonB = System.Text.Json.JsonSerializer.Serialize(b);
            Check("settings.roundtrip", jsonA == jsonB, $"lenA={jsonA.Length} lenB={jsonB.Length}");

            // 损坏文件 → Load 不得抛异常，返回非空默认
            System.IO.File.WriteAllText(path, "{ this is not valid json ,,, }");
            Exception? loadEx = null;
            AppSettings? def = null;
            try { def = SettingsManager.Load(); }
            catch (Exception ex) { loadEx = ex; }
            Check("settings.corruptLoadNoThrow", loadEx == null && def != null,
                loadEx == null ? "Load returned default (non-null)" : "Load threw: " + loadEx.Message);
        }
        catch (Exception ex)
        {
            Fail("settings.roundtrip", ex.Message);
        }
        finally
        {
            // 恢复原始字节（不调用 Save，避免改动任何值）
            try
            {
                if (original != null) System.IO.File.WriteAllText(path, original);
                else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                Info("  配置原文件已恢复");
            }
            catch (Exception ex) { Fail("settings.restore", ex.Message); }
        }
    }

    private static void TooltipAllLanguages()
    {
        Info("Tooltip 全语言：格式不抛异常、长度<=127（M8 截断与 Unicode）");
        var langs = new[]
        {
            Language.SimplifiedChinese, Language.TraditionalChinese,
            Language.English, Language.Japanese,
            Language.Korean, Language.German,
            Language.French, Language.Spanish,
            Language.Russian
        };
        int total = 0, bad = 0;
        foreach (var lang in langs)
        {
            foreach (var key in new[] { "TrayTooltip", "TrayTooltipBrightnessOnly" })
            {
                total++;
                try
                {
                    string text = key == "TrayTooltip"
                        ? Localization.Get(lang, key, 85, 5500)
                        : Localization.Get(lang, key, 85);
                    bool okLen = text.Length <= 127;
                    bool okFmt = !text.Contains(key);            // 命中了翻译而非回退键名
                    bool okVal = text.Contains("85");
                    if (!okLen || !okFmt || !okVal) bad++;
                    if (lang == Language.SimplifiedChinese && key == "TrayTooltip")
                        Info($"  简中样张: \"{text}\" ({text.Length} 字符)");
                }
                catch (Exception ex)
                {
                    bad++;
                    Info($"  {lang}/{key} 抛异常: {ex.Message}");
                }
            }
        }
        Check("tooltip.allLanguages", bad == 0, $"{total - bad}/{total} OK");
    }

    private static void ThemeCycleGdi()
    {
        Info("主题循环 ×20 + GDI 稳定性（恢复原主题；不落盘）");
        var inst = Program.Instance;
        if (inst == null) { Fail("theme.cycle", "Program.Instance 为 null"); return; }
        try
        {
            var origTheme = inst.GetTheme();
            var origPopup = inst.GetPopupTheme();
            uint g0 = GetGdiCount();
            uint u0 = GetUserCount();
            for (int i = 0; i < 20; i++)
            {
                ThemeManager.Apply(i % 2 == 0 ? ThemeMode.Dark : ThemeMode.Light);
                ThemeManager.ApplyPopupTheme(i % 2 == 0 ? ThemeMode.Light : ThemeMode.Dark);
            }
            RestoreTheme(inst);   // 立即还原，避免异常路径下停留
            uint g1 = GetGdiCount();
            uint u1 = GetUserCount();
            // 宽阈值：事件给托盘刷图标属于有界分配；>256 视为增长异常
            Check("theme.cycle.gdiDelta", g1 - g0 <= 256, $"GDI {g0}->{g1} (+{g1 - g0})");
            Check("theme.cycle.userDelta", u1 - u0 <= 256, $"USER {u0}->{u1} (+{u1 - u0})");
        }
        catch (Exception ex)
        {
            Fail("theme.cycle", ex.Message);
        }
    }

    private static void ConfigSelfHealIntegrity()
    {
        Info("IntegrityChecker 启动自检（无异常动作应零输出）");
        try
        {
            IntegrityChecker.RunCheck();
            Check("selfheal.runCheck", true, "completed without throwing");
        }
        catch (Exception ex)
        {
            Fail("selfheal.runCheck", ex.Message);
        }
    }

    // ------------------------------------------------------------------

    private static void RestoreTheme(MainController? inst)
    {
        if (inst == null) return;
        try { ThemeManager.Apply(inst.GetTheme()); } catch { }
        try { ThemeManager.ApplyPopupTheme(inst.GetPopupTheme()); } catch { }
    }

    private static uint GetGdiCount() => GetGuiResources(Process.GetCurrentProcess().Handle, GR_GDIOBJECTS);
    private static uint GetUserCount() => GetGuiResources(Process.GetCurrentProcess().Handle, GR_USEROBJECTS);

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Info($"[PASS] {name}: {detail}"); }
        else { _fail++; Info($"[FAIL] {name}: {detail}"); OpLog.Log($"[selftest] FAIL {name}: {detail}"); }
    }

    private static void Fail(string name, string detail) => Check(name, false, detail);

    private static void Info(string line)
    {
        Report.Add(line);
        OpLog.Log("[selftest] " + line);
    }
}
