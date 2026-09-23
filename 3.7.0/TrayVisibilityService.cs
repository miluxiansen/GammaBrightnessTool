using System.Diagnostics;
using Microsoft.Win32;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// 托盘图标「常驻显示」（<c>NotifyIconSettings\...\IsPromoted</c>）的<b>唯一</b>读写入口（P1）。
///
/// 本类被授权触碰的系统区域<b>只有</b>两处：
///   * <c>HKCU\Control Panel\NotifyIconSettings\&lt;本软件条目&gt;\IsPromoted</c>（读写）；
///   * <c>HKCU\Software\Classes\...\TrayNotify\SystemTrayChevronVisibility</c>（<b>只读</b>，P2″ 判定用）。
///
/// 三条红线（设计稿 §6.2 / §3.5，与卸载器、自检的策略一致）：
///   * <b>绝不 CreateSubKey</b> —— 托盘条目由 explorer 在本程序以 NIF_GUID 注册时生成
///     （「缺失即生成、免重启 explorer」的已验证机制），程序自建会产生孤儿条目；
///   * <b>绝不删除任何键/值</b>；
///   * <b>绝不触碰 TrayNotify\IconStreams / PastIconsStream</b>（全系统共享的旧式托盘历史，
///     删除会重置所有软件的托盘偏好）。
///
/// 匹配以 <b>IconGuid</b> 为主（本程序固定 GUID，绿色版 / 安装版换路径不受影响），
/// 路径比对仅作兜底（§6.2）。
/// </summary>
internal static class TrayVisibilityService
{
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
    private const string ChevronKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify";
    private const string ChevronValue = "SystemTrayChevronVisibility";

    /// <summary>
    /// 当前是否被设为常驻。true = 任一命中条目的 IsPromoted 为 1；false = 全部为 0；
    /// null = 条目尚未生成、或都没有该值（状态未知）。
    /// </summary>
    public static bool? ReadPromoted()
    {
        try
        {
            List<string> names = FindEntryNames();
            if (names.Count == 0)
            {
                // null（"未知"）与 false（"已生成但被隐藏"）性质完全不同：
                // null 多半意味着条目还没被 explorer 建出来，此时写入是无对象的 no-op。
                OpLog.Log("[TrayVisibility] ReadPromoted: 无匹配条目 -> null（未知；多半是条目尚未生成）");
                return null;
            }

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
            if (root == null)
            {
                OpLog.Log("[TrayVisibility] ReadPromoted: NotifyIconSettings 根键打不开 -> null");
                return null;
            }

            bool sawValue = false;
            bool anyTrue = false;
            foreach (string name in names)
            {
                using RegistryKey? k = root.OpenSubKey(name);
                object? raw = k?.GetValue("IsPromoted");
                if (raw is int v)
                {
                    sawValue = true;
                    if (v != 0) anyTrue = true;
                    OpLog.Log($"[TrayVisibility] ReadPromoted: entry={name} IsPromoted={v} (DWord)");
                }
                else
                {
                    // 真实故障模式：值存在但类型不是 DWord 时会被静默当成"无值"，
                    // 这里必须显式记出来，否则事后完全看不出为什么读到 null。
                    OpLog.Log($"[TrayVisibility] ReadPromoted: entry={name} IsPromoted 缺失或非 DWord" +
                              $"（实际={raw?.ToString() ?? "<null>"} 类型={raw?.GetType().Name ?? "-"}）");
                }
            }
            bool? result = sawValue ? anyTrue : (bool?)null;
            OpLog.Log($"[TrayVisibility] ReadPromoted -> {Describe(result)}");
            return result;
        }
        catch (Exception ex)
        {
            OpLog.Log("[TrayVisibility] ReadPromoted failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>把三态结果写成能一眼看懂的文字（null 与 false 必须区分开）。</summary>
    internal static string Describe(bool? v) =>
        v == null ? "null（未知/条目未生成）" : (v.Value ? "true（常驻）" : "false（溢出区）");

    /// <summary>
    /// 写入常驻 / 隐藏。返回是否成功；false = 未找到任何匹配条目（静默失败）——
    /// 此时<b>没有写入对象</b>，操作是安全的 no-op（§6.4），由写入阶梯 + P0 兜底。
    /// </summary>
    public static bool TrySetPromoted(bool promoted)
    {
        try
        {
            List<string> names = FindEntryNames();
            if (names.Count == 0)
            {
                // 条目是 **explorer** 建的，所以"explorer 在不在 / 托盘区有没有初始化"
                // 是首个要排除的因素 —— 这两个数字能立刻把问题分到"explorer 侧"还是"我们侧"。
                bool rootExists = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey) != null;
                OpLog.Log($"[TrayVisibility] 诊断: explorer 进程数={Process.GetProcessesByName("explorer").Length}；" +
                          $"NotifyIconSettings 根键={(rootExists ? "存在" : "不存在（explorer 尚未建立任何托盘条目）")}");

                if (!promoted)
                {
                    OpLog.Log("[TrayVisibility] TrySetPromoted(False): 无条目可写 -> no-op（隐藏态不自建条目）");
                    return false;
                }

                // ---- 方案 B：条目缺失时**自建**（用户 2026-09-14 明确授权）----
                if (!TryCreateOwnEntry()) return false;
                names = FindEntryNames();
                if (names.Count == 0)
                {
                    OpLog.Log("[TrayVisibility] 自建条目后仍匹配不到 -> 放弃");
                    return false;
                }
            }
            OpLog.Log($"[TrayVisibility] TrySetPromoted({promoted}): 匹配 {names.Count} 条 -> {string.Join(", ", names)}");
            foreach (string name in names)
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(
                    NotifyIconSettingsKey + @"\" + name, writable: true);
                if (k == null)
                {
                    OpLog.Log($"[TrayVisibility] TrySetPromoted: entry={name} 打不开（可写）-> 跳过");
                    continue;
                }
                k.SetValue("IsPromoted", promoted ? 1 : 0, RegistryValueKind.DWord);
                OpLog.Log($"[TrayVisibility] TrySetPromoted: entry={name} 已写入 IsPromoted={(promoted ? 1 : 0)}");
            }
            OpLog.Log($"[TrayVisibility] IsPromoted={(promoted ? 1 : 0)} on {names.Count} entry(ies)");
            return true;
        }
        catch (Exception ex)
        {
            OpLog.Log("[TrayVisibility] Set failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 【方案 B · 用户 2026-09-14 明确授权】条目缺失时<b>自建</b> NotifyIconSettings 条目。
    ///
    /// ⚠️ 这是对设计稿 §6.2「绝不 CreateSubKey」红线的**有意识偏离**。偏离依据是实测：
    /// explorer 只在"图标注册发生在任务栏初始化期间（TaskbarCreated）"时才建条目；程序在
    /// explorer 稳态运行时安装 / 首次启动，图标虽注册成功但<b>永远没有条目</b>，于是图标落在
    /// `^` 溢出区、且本开关没有写入对象 —— 真机全新安装已复现（2026-09-14）。
    ///
    /// 安全护栏（缺一不可）：
    ///   1. 子键名由 <b>IconGuid 确定性派生</b> → 幂等，重复运行永远落到同一个键，
    ///      不会自己制造重复条目；
    ///   2. 只写 <c>IconGuid</c> / <c>ExecutablePath</c> / <c>IsPromoted</c> 三个值，
    ///      <b>不写</b> IconSnapshot（仅为设置界面预览缓存）；
    ///   3. 目标子键若已存在且属于<b>别的程序</b>（IconGuid 对不上）→ 立即放弃，
    ///      绝不改写他人条目；
    ///   4. <b>绝不删除</b>任何键或值，<b>绝不触碰</b> TrayNotify\IconStreams。
    /// </summary>
    public static bool TryCreateOwnEntry()
    {
        try
        {
            string name = OwnEntryName();
            string path = NotifyIconSettingsKey + @"\" + name;

            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(path))
            {
                if (existing != null)
                {
                    // 已存在：确认是"我们自己"的才继续，否则绝不碰别人的条目
                    string? g = existing.GetValue("IconGuid") as string;
                    bool hasGuid = !string.IsNullOrEmpty(g) && Guid.TryParse(g, out _);
                    bool mine = hasGuid && Guid.TryParse(g!, out Guid gg) && gg == TrayIconManager.IconGuid;
                    if (mine)
                    {
                        // 已是完整条目：不重复自建（保持幂等）
                    }
                    else if (!hasGuid)
                    {
                        // 2026-09-16 修复：**残缺条目（快照壳）** —— 子键存在但没有 IconGuid。
                        //
                        // 实测成因（本轮托盘自愈测试复现）：条目被外部删除后，explorer 会为
                        // 仍在注册的托盘图标**重建一个只有 `IconSnapshot` 的壳**
                        // （IconSnapshot 用于"设置 → 个性化 → 任务栏 → 其他系统托盘图标"的
                        //  预览图），**不写** IconGuid / ExecutablePath。
                        // 于是形成死锁：
                        //   FindEntryNames() 按 IconGuid 匹配 → 0 条（读不到值）
                        //   TryCreateOwnEntry()   看到同名键已存在 → 判"不属于本软件"→ 放弃自建
                        //   ⇒ 既匹配不到、又不能自建，**本开关永久失效**且图标无法回到常驻区。
                        //
                        // 判定放宽：无 IconGuid 的条目**归属未知**（不是"别人的"），而键名由
                        // 我们的 IconGuid 确定性派生 ⇒ 补齐我们自己的三个值。
                        // 保留已有值（IconSnapshot 等）—— 绝不删除任何键/值。
                        OpLog.Log($"[TrayVisibility] 自建：子键 {name} 存在但**无 IconGuid**" +
                                  $"（残缺/快照壳）-> 补齐本软件标识（保留已有值）");
                    }
                    else
                    {
                        OpLog.Log($"[TrayVisibility] 自建放弃：子键 {name} 已存在且不属于本软件" +
                                  $"（IconGuid={g}）-> 不改写他人条目");
                        return false;
                    }
                }
            }

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: true);
            if (root == null)
            {
                OpLog.Log("[TrayVisibility] 自建失败：NotifyIconSettings 根键无法以可写方式打开");
                return false;
            }
            using RegistryKey k = root.CreateSubKey(name);
            k.SetValue("IconGuid", TrayIconManager.IconGuid.ToString("B"), RegistryValueKind.String);
            k.SetValue("ExecutablePath", Application.ExecutablePath, RegistryValueKind.String);
            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            OpLog.Log($"[TrayVisibility] **自建条目** {name}（IconGuid + ExecutablePath + IsPromoted=1）；" +
                      "未写 IconSnapshot（仅设置界面预览缓存）。方案 B 试验：请观察 explorer 是否认账、" +
                      "以及后续是否出现重复项（scan 日志里 IconGuid 命中数会从 1 变 2）。");
            return true;
        }
        catch (Exception ex)
        {
            OpLog.Log("[TrayVisibility] 自建条目失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 由 IconGuid 确定性派生一个纯数字子键名（与 explorer 的命名风格一致）。
    /// 确定性是幂等的保证：重复运行永远得到同一个名字，不会自己制造第二个条目。
    /// </summary>
    private static string OwnEntryName()
    {
        byte[] b = TrayIconManager.IconGuid.ToByteArray();
        ulong hi = BitConverter.ToUInt64(b, 0);
        ulong lo = BitConverter.ToUInt64(b, 8);
        return (hi ^ lo).ToString();
    }

    /// <summary>
    /// P2″：系统「隐藏的图标菜单」总开关是否<b>被用户明确关闭</b>。
    /// fail-open：<b>只有明确读到 0</b> 才返回 true；缺失 / 异常 / 非 0 一律 false（= 不锁）。
    /// 权衡（§7.5）：误锁的代价只是「不能隐藏图标」，漏锁的代价是图标彻底消失且无法自救。
    /// </summary>
    public static bool IsOverflowMenuExplicitlyOff()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(ChevronKey);
            object? raw = k?.GetValue(ChevronValue);
            // 只有【明确读到 0】才返回 true；缺失 / 异常 / 非 0 一律 false
            bool off = raw is int v && v == 0;
            // 原始值必须记：否则事后分不清"读到 1 所以不锁"和"键缺失所以不锁"。
            OpLog.Log($"[TrayVisibility] 系统总开关 {ChevronValue} = {raw?.ToString() ?? "<缺失>"}" +
                      $"（类型={raw?.GetType().Name ?? "-"}）→ 明确为关={off}");
            return off;
        }
        catch (Exception ex)
        {
            OpLog.Log($"[TrayVisibility] 读取系统总开关异常 -> 不锁（fail-open）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 找出本软件在 NotifyIconSettings 下的条目子键名。
    /// 主匹配 = IconGuid（NIF_GUID 注册时由 explorer 落盘）；路径比对仅作兜底，
    /// 且「有 GUID 命中则忽略路径命中」（§6.2）。
    /// </summary>
    private static List<string> FindEntryNames()
    {
        List<string> found = new();
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
        if (root == null) return found;

        string myPath = Application.ExecutablePath;
        bool guidHit = false;
        List<string> pathHits = new();
        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? k = root.OpenSubKey(name);
            if (k == null) continue;

            if (k.GetValue("IconGuid") is string g &&
                Guid.TryParse(g, out Guid guid) &&
                guid == TrayIconManager.IconGuid)
            {
                found.Add(name);
                guidHit = true;
                continue;
            }

            if (k.GetValue("ExecutablePath") is string p &&
                string.Equals(p, myPath, StringComparison.OrdinalIgnoreCase))
            {
                pathHits.Add(name);
            }
        }
        if (!guidHit) found.AddRange(pathHits);

        // 匹配不到时，没有这几个数字就完全无法定位原因（是 GUID 没落盘？还是路径对不上？）
        string[] all = root.GetSubKeyNames();
        OpLog.Log($"[TrayVisibility] scan: 共 {all.Length} 条 NotifyIconSettings；" +
                  $"IconGuid 命中 {found.Count - (guidHit ? 0 : pathHits.Count)}（GUID={TrayIconManager.IconGuid}）；" +
                  $"ExecutablePath 命中 {pathHits.Count}（当前 exe={myPath}）" +
                  (guidHit ? "  → 采用 GUID 命中" : (pathHits.Count > 0 ? "  → 退化为路径命中" : "  → 无命中")));
        return found;
    }
}
