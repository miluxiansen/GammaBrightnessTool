using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// 「哪几份装过、各自最后什么时候跑过」的登记表（L2/L3，2026-09-16 用户拍板）。
///
/// 解决的问题：绿色版与安装版可以共存（§14），此时 Run 键只能指向一份。
/// 用户定的判据是「**运行时间为主、版本号为辅**」：
///   · 关机前跑的是老版本 → 重启后自启打开的也是老版本；
///   · 两份**都没运行过**（无运行时间）→ 比版本号，取高的；
///   · 同版本（内测绿色版高频出现）→ 仍靠运行时间区分。
///
/// ⚠️ 为什么用**独立文件**而不是 settings.json 的新字段（2026-09-16 实测结论）：
///   `SettingsManager` 的 `Load()` 反序列化到 `AppSettings`（未知字段不进对象），
///   `Save()` 只序列化该类型的属性，且**没有 `[JsonExtensionData]`**
///   ⇒ 任何未知字段在"启动 + 退出"一轮之后就**被抹掉**（已实测：注入假字段 → 消失）。
///   若把登记表放进 settings.json，**降级运行一次旧版本就会清空它**。
///   放独立文件则旧版本根本不碰，降级安全。
///
/// 安全边界：
///   * 只读写 `%APPDATA%\GammaBrightnessTool\known_installs.json`，不碰系统文件；
///   * 判断"路径是否还在"**必须**经 <see cref="StartupManager.IsSafeForExistenceCheck"/>，
///     否则映射盘 / U 盘未就绪时会被误判成"已删除"而误清记录；
///   * 任何异常都吞掉并记 OpLog（fail-safe，绝不影响程序启动）。
/// </summary>
public static class KnownInstalls
{
    /// <summary>登记表结构版本。将来若改结构，靠它做迁移。</summary>
    private const int SchemaVersion = 1;

    /// <summary>登记表上限（按最后运行时间倒序保留），防止测试期反复换目录导致无限增长。</summary>
    private const int MaxEntries = 10;

    /// <summary>
    /// Inno 的 AppId。⚠️ **必须与 `Setup.iss` 的 `AppId` 保持一致**
    /// （本值自 1.0.0 起从未变过；改它会让"上次安装目录"与卸载键定位双双失效）。
    /// 安装版的位置从卸载键读 —— 这也是"不新增第 4 份真相来源"的做法：
    /// 安装版位置有两处权威来源（卸载键 / 实际文件），绿色版位置才需要本表登记。
    /// </summary>
    private const string InnoAppId = "{B8E3A5C1-2D4F-4A6B-9C8E-1F3A5B7D9E2C}";

    private const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId + "_is1";

    /// <summary>
    /// Inno 自己记录"上次安装到哪"的值名（**无尾斜杠**）。
    /// ⚠️ 不是 `InstallLocation`（那个有尾斜杠，是给"程序和功能"显示的展示值）——
    /// 实测确认 `UsePreviousAppDir` 读的是本值，手写逻辑应与 Inno 原生保持一致。
    /// </summary>
    private const string InnoAppPathValue = "Inno Setup: App Path";

    private const string KindPortable = "portable";
    private const string KindInstalled = "installed";

    /// <summary>登记表里的一条：某个 exe 路径 + 它的版本 + 最后一次运行时刻。</summary>
    public sealed class InstallEntry
    {
        /// <summary>exe 完整路径（比较用 OrdinalIgnoreCase）。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 版本号，取自 <see cref="FileVersionInfo.ProductVersion"/>。
        /// ⚠️ **不解析文件名** —— 绿色版文件名带构建时间戳
        /// （`GammaBrightnessTool_3.7.0_20260916_1536.exe`），解析会得到"伪版本"。
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>最后一次运行的 UTC 时刻（ISO 8601 往返格式）；空 = 从没运行过。</summary>
        public string LastRunUtc { get; set; } = string.Empty;

        /// <summary><see cref="KindPortable"/> 或 <see cref="KindInstalled"/>（仅便于诊断）。</summary>
        public string Kind { get; set; } = KindPortable;

        /// <summary>解析 <see cref="LastRunUtc"/>；解析不出返回 <see cref="DateTime.MinValue"/>。</summary>
        internal DateTime LastRunValue =>
            DateTime.TryParse(LastRunUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime v)
                ? v.ToUniversalTime() : DateTime.MinValue;

        /// <summary>解析 <see cref="Version"/>；解析不出返回 0.0（视作最旧）。</summary>
        internal Version VersionValue =>
            System.Version.TryParse(Version, out Version? v) ? v : new Version(0, 0);
    }

    private sealed class InstallTable
    {
        public int SchemaVersion { get; set; } = KnownInstalls.SchemaVersion;
        public List<InstallEntry> Entries { get; set; } = new();
    }

    /// <summary>登记表文件路径（与 settings.json 同目录，便于统一备份/卸载策略）。</summary>
    public static string FilePath => Path.Combine(SettingsManager.AppDataDirectory, "known_installs.json");

    private static readonly object Gate = new();

    // ==================================================================
    // 登记
    // ==================================================================

    /// <summary>
    /// 登记"当前正在运行的这一份"并顺手做一次垃圾回收。
    /// **必须在自启仲裁之前调用** —— 仲裁靠"谁的 LastRunUtc 更新"来定胜负，
    /// 先登记自己才使"最后运行的那份"具有最新时间戳。
    ///
    /// 幂等：同一路径已存在则只刷新时间/版本，不新增条目。
    /// 任何异常都不外抛（fail-safe）。
    /// </summary>
    public static void RegisterSelf()
    {
        try
        {
            lock (Gate)
            {
                string self = Application.ExecutablePath;
                if (string.IsNullOrEmpty(self)) return;

                var table = LoadTable();
                PruneMissing(table);                 // 先清理已消失的
                UpsertSelf(table, self);             // 再登记自己
                TrimToLimit(table);
                SaveTable(table);

                OpLog.LogThrottled("knowninstalls.register",
                    $"[knowninstalls] 登记 {Path.GetFileName(self)} v{ReadVersion(self)} " +
                    $"{DetectKind(self)}；表内共 {table.Entries.Count} 条");
            }
        }
        catch (Exception ex)
        {
            OpLog.Log("[knowninstalls] RegisterSelf 失败（已忽略）: " + ex.Message);
        }
    }

    /// <summary>
    /// 只刷新当前这一份的时间戳（正常退出时调用，让"最后运行时刻"更准）。
    /// </summary>
    public static void TouchSelf()
    {
        try
        {
            lock (Gate)
            {
                string self = Application.ExecutablePath;
                if (string.IsNullOrEmpty(self)) return;

                var table = LoadTable();
                var hit = Find(table, self);
                if (hit == null) return;              // 没登记过就不在这里补（启动时已登记）
                hit.LastRunUtc = DateTime.UtcNow.ToString("o");
                SaveTable(table);
            }
        }
        catch (Exception ex)
        {
            OpLog.Log("[knowninstalls] TouchSelf 失败（已忽略）: " + ex.Message);
        }
    }

    // ==================================================================
    // 仲裁（L3）
    // ==================================================================

    /// <summary>
    /// 决定"Run 键应该指向哪一份"。
    ///
    /// 调用前提（由调用方保证）：<paramref name="registeredExe"/> 已通过
    /// <see cref="StartupManager.IsSafeForExistenceCheck"/> 且 `File.Exists` 为 true，
    /// 即"Run 指向的 exe 确实存在"。
    ///
    /// 返回：
    ///   * 非 null = 应把 Run 键重写为该路径（本函数返回**当前 exe**）；
    ///   * null     = **不动**（已指向自己 / 对方来源不明 / 自己更旧要让位）。
    ///
    /// 判定顺序（与设计定稿 §2.4 一致）：
    ///   ③ Run 指向的 exe 存在 → 进入本函数（不存在的由 P4-a 悬空修复处理）
    ///   ④ 对方**不在候选集**里 → 不动（可能是用户手工配置的副本，不冒犯）
    ///   ⑤ 当前这一份排序**高于**对方（最后运行时间为主、版本为辅）→ 重写为当前
    ///   ⑥ 否则（当前更旧）→ 让位，不动
    /// </summary>
    public static string? PickPreferredExe(string registeredExe)
    {
        try
        {
            string self = Application.ExecutablePath;
            if (string.IsNullOrEmpty(self)) return null;
            if (string.Equals(self, registeredExe, StringComparison.OrdinalIgnoreCase))
            {
                return null;                          // 已经指向自己 → 无需写
            }

            var table = LoadTable();
            var candidates = BuildCandidates(table);

            var selfEntry = FindIn(candidates, self);
            var regEntry = FindIn(candidates, registeredExe);

            if (regEntry == null)
            {
                // ④ Run 指向的 exe 存在，但它既不在登记表、也不是卸载键那份
                //    ⇒ 来源不明（可能是用户手工复制/手工配置），不冒犯
                OpLog.Log($"[knowninstalls] 仲裁：Run 指向的 [{registeredExe}] 不在候选集 -> 不动");
                return null;
            }

            if (selfEntry == null)
            {
                // 理论上不会发生（启动时已 RegisterSelf）；保守不动
                OpLog.Log("[knowninstalls] 仲裁：当前 exe 不在登记表（异常）-> 不动");
                return null;
            }

            int cmp = CompareRank(selfEntry, regEntry);
            OpLog.Log($"[knowninstalls] 仲裁：self(LastRun={selfEntry.LastRunUtc} v{selfEntry.Version}) " +
                      $"vs run(LastRun={regEntry.LastRunUtc} v{regEntry.Version}) -> " +
                      (cmp > 0 ? "当前更新，抢回自启" : "当前更旧，让位"));
            return cmp > 0 ? self : null;
        }
        catch (Exception ex)
        {
            OpLog.Log("[knowninstalls] PickPreferredExe 失败（已忽略）: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 排序比较：**最后运行时间为主、版本号为辅**（用户 2026-09-16 定）。
    /// 返回 &gt;0 表示 a 应排在 b 前面（a 赢）。
    /// </summary>
    private static int CompareRank(InstallEntry a, InstallEntry b)
    {
        int byTime = a.LastRunValue.CompareTo(b.LastRunValue);
        if (byTime != 0) return byTime;

        // 两份都没运行过（或时间完全相同）→ 比版本
        return a.VersionValue.CompareTo(b.VersionValue);
    }

    /// <summary>
    /// 候选集 = 登记表条目 ∪ { 卸载键指向的那一份（视作"从没运行过"，排最后） }。
    ///
    /// 为什么要并入卸载键那份：安装包装完就把位置写进卸载键，但**新版可能从未启动过**
    /// ⇒ 若只认登记表，Run 键会一直停在"安装版"而用户其实跑的是绿色版。
    /// 并入后：绿色版因"刚运行过"排序更高 → 按 D4 抢回自启，符合用户预期。
    /// （对比：Run 指向一个**完全不在候选集**里的 exe 时仍走 ④ 不动，保护手工配置。）
    /// </summary>
    private static List<InstallEntry> BuildCandidates(InstallTable table)
    {
        var list = new List<InstallEntry>(table.Entries);

        string? installedPath = ReadInstalledExePath();
        if (!string.IsNullOrEmpty(installedPath) && FindIn(list, installedPath) == null)
        {
            list.Add(new InstallEntry
            {
                Path = installedPath,
                Version = ReadVersion(installedPath),
                LastRunUtc = string.Empty,        // 未知 ⇒ 排最后
                Kind = KindInstalled,
            });
        }
        return list;
    }

    // ==================================================================
    // 表操作
    // ==================================================================

    private static InstallEntry? Find(InstallTable t, string path) => FindIn(t.Entries, path);

    private static InstallEntry? FindIn(List<InstallEntry> list, string path)
    {
        foreach (var e in list)
        {
            if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    private static void UpsertSelf(InstallTable table, string self)
    {
        var hit = FindIn(table.Entries, self);
        if (hit == null)
        {
            table.Entries.Add(new InstallEntry
            {
                Path = self,
                Version = ReadVersion(self),
                LastRunUtc = DateTime.UtcNow.ToString("o"),
                Kind = DetectKind(self),
            });
        }
        else
        {
            hit.Version = ReadVersion(self);
            hit.LastRunUtc = DateTime.UtcNow.ToString("o");
            hit.Kind = DetectKind(self);
        }
    }

    /// <summary>
    /// GC：清掉"已消失"的记录。
    /// ⚠️ **必须**经 <see cref="StartupManager.IsSafeForExistenceCheck"/> 再判存在 ——
    /// 直接 `File.Exists` 对未就绪的映射盘 / U 盘恒返回 false，
    /// 用户把绿色版放移动盘、没插时启动就会被误清。
    /// </summary>
    private static void PruneMissing(InstallTable table)
    {
        int removed = 0;
        for (int i = table.Entries.Count - 1; i >= 0; i--)
        {
            string p = table.Entries[i].Path;
            if (string.IsNullOrWhiteSpace(p)) { table.Entries.RemoveAt(i); removed++; continue; }
            if (!StartupManager.IsSafeForExistenceCheck(p)) continue;   // 不可探测 → 保留
            if (File.Exists(p)) continue;
            OpLog.Log($"[knowninstalls] GC：移除已不存在的记录 [{p}]");
            table.Entries.RemoveAt(i);
            removed++;
        }
        if (removed > 0)
        {
            OpLog.Log($"[knowninstalls] GC 共移除 {removed} 条");
        }
    }

    private static void TrimToLimit(InstallTable table)
    {
        if (table.Entries.Count <= MaxEntries) return;
        table.Entries.Sort((a, b) => CompareRank(b, a));      // 时间倒序
        table.Entries.RemoveRange(MaxEntries, table.Entries.Count - MaxEntries);
    }

    // ==================================================================
    // 环境探测
    // ==================================================================

    /// <summary>读 exe 的 ProductVersion；失败返回空串。</summary>
    private static string ReadVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return string.Empty;
            string? v = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
            return string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 卸载键里"上次安装目录"指向的 exe；读不到返回 null。
    /// 依次查 HKCU（`PrivilegesRequired=lowest` 时的正常位置）与 HKLM（提权安装）。
    /// </summary>
    private static string? ReadInstalledExePath()
    {
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? k = root.OpenSubKey(UninstallSubKey);
                if (k?.GetValue(InnoAppPathValue) is string dir && !string.IsNullOrWhiteSpace(dir))
                {
                    string exe = Path.Combine(dir.TrimEnd('\\', '/'), "GammaBrightnessTool.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch
            {
                // 无权限 / 视图差异：忽略，继续下一个 hive
            }
        }
        return null;
    }

    /// <summary>判断这一份是安装版还是绿色版（仅用于诊断，不影响仲裁）。</summary>
    private static string DetectKind(string exePath)
    {
        try
        {
            string? installedExe = ReadInstalledExePath();
            if (!string.IsNullOrEmpty(installedExe) &&
                string.Equals(installedExe, exePath, StringComparison.OrdinalIgnoreCase))
            {
                return KindInstalled;
            }
        }
        catch
        {
            // 忽略
        }
        return KindPortable;
    }

    // ==================================================================
    // 诊断（P5 自动化接口用）
    // ==================================================================

    /// <summary>
    /// 登记表摘要，供 `--auto ui.get:Biz_KnownInstalls` 读取（测试断言用）。
    /// 逐行格式：`Path|Version|LastRun|Kind`，按「最后运行时间倒序 → 版本倒序」排列；
    /// 空表返回 `(empty)`。
    /// </summary>
    public static string DescribeForDiagnostics()
    {
        try
        {
            var t = LoadTable();
            if (t.Entries.Count == 0) return "(empty)";
            var ordered = t.Entries
                .OrderByDescending(e => e.LastRunValue)
                .ThenByDescending(e => e.VersionValue)
                .ToList();
            var sb = new System.Text.StringBuilder();
            foreach (var e in ordered)
            {
                sb.AppendLine($"{e.Path}|{e.Version}|{e.LastRunUtc}|{e.Kind}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return "ERR:" + ex.Message;
        }
    }

    /// <summary>
    /// 只读探针：报告"若此刻做仲裁会怎么判"，不产生任何写入。
    /// 供 `--auto ui.get:Biz_StartupTarget` 读取。
    /// </summary>
    public static string DescribeArbitration()
    {
        try
        {
            string self = Application.ExecutablePath;
            if (!StartupManager.TryGetStartupCommandLine(out string raw)) return "run=(none)";
            if (!StartupManager.TryExtractExePath(raw, out string exe))
            {
                return $"run={raw}|parse=fail";
            }
            bool safe = StartupManager.IsSafeForExistenceCheck(exe);
            bool exists = safe && File.Exists(exe);
            string verdict;
            if (!exists) verdict = "switch(dangling)";                 // 走 P4-a 悬空修复
            else if (string.Equals(exe, self, StringComparison.OrdinalIgnoreCase)) verdict = "keep(self)";
            else verdict = PickPreferredExe(exe) != null ? "switch(arbitration)" : "keep";
            return $"run={exe}|safe={safe}|exists={exists}|self={self}|verdict={verdict}";
        }
        catch (Exception ex)
        {
            return "ERR:" + ex.Message;
        }
    }

    // ==================================================================
    // 持久化（原子写，照抄 SettingsManager 的做法）
    // ==================================================================

    private static InstallTable LoadTable()
    {
        try
        {
            if (!File.Exists(FilePath)) return new InstallTable();
            string json = File.ReadAllText(FilePath);
            var t = JsonSerializer.Deserialize<InstallTable>(json);
            if (t == null) return new InstallTable();
            t.Entries ??= new List<InstallEntry>();
            return t;
        }
        catch (Exception ex)
        {
            // 文件损坏 → 视作空表重建（fail-safe），绝不影响启动
            OpLog.Log($"[knowninstalls] 登记表损坏，按空表重建: {ex.Message}");
            return new InstallTable();
        }
    }

    private static void SaveTable(InstallTable table)
    {
        try
        {
            Directory.CreateDirectory(SettingsManager.AppDataDirectory);
            table.SchemaVersion = SchemaVersion;
            string json = JsonSerializer.Serialize(table, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            // 原子写：先写临时文件再整体 Move 覆盖，避免写一半被中断留下非法 JSON
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            OpLog.Log("[knowninstalls] 保存登记表失败（已忽略）: " + ex.Message);
        }
    }
}
