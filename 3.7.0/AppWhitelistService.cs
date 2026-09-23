using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// 候选应用一行（白名单列表用）。<c>Source</c> 标注来源：
/// <see cref="AppWhitelistService.SourceVisible"/> = 当前有窗口 /
/// <see cref="AppWhitelistService.SourceTray"/> = 停在任务栏托盘（含隐藏/溢出）。
/// </summary>
public sealed record WhitelistCandidate(string Path, string Name, string Source);

/// <summary>
/// 应用白名单的候选集服务（实现依据：设计稿 §16）。
///
/// 职责边界：**只做"候选集收敛"，不做价值判断** —— 判断"该不该暂停"交给用户勾选。
///
/// **识别判据（2026-09-14 定稿，用户口径）**：
///   候选 = **正在运行的进程** 且（ **当前有可见应用窗口**  ∪  **停在任务栏托盘** ）
///   * 有窗口 → `EnumWindows` 实枚举：可见、未最小化、非系统窗类、非辅助窗类、尺寸过闸门；
///   * 有托盘 → `NotifyIconSettings\*\ExecutablePath`，但**必须能在正在运行的进程表里找到**
///     （这一步是关键：注册表里还留着已卸载/未运行程序的陈旧条目，只按"在运行"过滤才干净）。
///
/// 为什么用这个判据（两条教训）：
///   ① 早先"只看窗口"漏了 `wallpaper engine` / 英伟达控制面板 / 网易UU远程 这类
///      **只在托盘常驻、没有稳定顶层窗口**的程序；
///   ② 早先"只看托盘注册表"又把 60 条陈旧条目全搬了进来（acrotray / 各类更新器…），
///      因为注册表记录的是"**曾经**注册过托盘图标的一切"。
///   **黑名单不作为主要手段**（用户 2026-09-14 指正）：加不完，而且会误伤
///   —— 曾把 `ArmouryCrate.*` 拉黑，结果它窗口打开后反而识别不到。
///   故黑名单只保留"永远不可能是用户想暂停的程序"（桌面/任务栏/shell 宿主）。
///
/// 运行时判据：<see cref="AnyVisibleWindowFor"/>（见 §16.5）。全程只读。
/// </summary>
public static class AppWhitelistService
{
    public const string SourceVisible = "visible";
    public const string SourceTray = "tray";

    /// <summary>有窗口但**已最小化**（2026-09-15 新增）：与"真的未运行"区分开。
    /// 之前把最小化直接排除，导致界面上被误标成"未运行"（用户反馈）。</summary>
    public const string SourceMinimized = "minimized";

    // ---- 黑名单：只保留"永远不是用户想暂停的程序"（不做厂商级围堵，避免误伤）----
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",                  // 桌面/文件管理器常驻；白名单化约等于关闭调节
        "ShellExperienceHost",       // 任务切换器/通知中心宿主
        "StartMenuExperienceHost",   // 开始菜单宿主
        "TextInputHost",             // 输入体验宿主
        // 注意：**不能拉黑 ApplicationFrameHost** —— 它是 UWP 的帧宿主，奥创等 UWP 应用
        // 的可见窗口正是它拥有的（实测 title='Armoury Crate'）。拉黑它 => UWP 应用全部识别不到。
        // 正确做法：遇到 ApplicationFrameWindow 时下钻子窗口取真实应用进程（见 ResolveWindowPid）。
    };

    // ---- 辅助窗口类名（用于"有窗口"这一路；0×0/无标题的窗口另由尺寸闸门挡掉）----
    // 注意：**不列 `Chrome_WidgetWin_0`** —— 实测迅雷主窗口就是该类（603×473，标题 index.html），
    // 按类名排除会误杀真应用；Chromium 的"消息窗"是 0×0，已被尺寸闸门挡掉。
    private static readonly HashSet<string> AuxClassExact = new(StringComparer.Ordinal)
    {
        "IME",
        "MSCTFIME UI",
        "GDI+ Hook Window Class",
        "Tao Thread Event Target",
        "Electron_NotifyIconHostWindow",
        "Electron_SystemPreferencesHostWindow",
        "Chrome_SystemMessageWindow",
        "NVOpenGLPbuffer",
    };

    private static readonly string[] AuxClassPrefixes = { "Electron_" };
    // 包含匹配（Thunder 系背景窗的拼写就是 Backgroud；Qt 系消息窗含 MessageWindow）
    private static readonly string[] AuxClassContains = { "MessageWindow", "_Backgroud_Window" };

    /// <summary>本程序自身的 exe 名（候选里不该出现自己）。</summary>
    private static readonly string SelfExeName = ResolveSelfExeName();

    private static string ResolveSelfExeName()
    {
        try
        {
            string? p = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(p)
                ? "GammaBrightnessTool"
                : Path.GetFileNameWithoutExtension(p);
        }
        catch
        {
            return "GammaBrightnessTool";
        }
    }

    /// <summary>尺寸阈值（§16.4 规则 4，实测标定）：
    /// `min(宽,高) ≥ 16` 且 `宽×高 ≥ 1600`（滤掉 14×14 / 1×1 / 0×0 / 细长条）。</summary>
    private static bool PassesSizeGate(int w, int h)
        => w >= 16 && h >= 16 && (long)w * h >= 1600;

    /// <summary>
    /// 「实质相交」门槛（2026-09-20，见 <see cref="GetHitScreens"/> 的 ③ 分支）：
    /// 窗口有这么多比例的面积落在某屏上，才算"窗口在这块屏上"。
    ///
    /// 用途：区分**跨屏**与**溢出**。
    ///   · 窗口 97% 在主屏、3% 溢出到副屏 ⇒ 只算"在主屏上"（未跨屏）⇒ 直接归属主屏
    ///   · 窗口横跨两屏各 ~50% ⇒ 两块都算 ⇒ **跨屏** ⇒ 不归属（保持不动）
    ///
    /// 取值依据（对用户实测几何的离线验算，脚本 `_devtools/_verify_dominant.py`）：
    ///   3% 这类"窄条溢出"必须**不算**跨屏（用户明确要求这种窗口生效），
    ///   而"跨屏拖动"至少是一屏过半（≥50%）⇒ 5% 有足够区分度。
    /// </summary>
    private const double CrossScreenFloor = 0.05;

    private static bool IsAuxiliaryClass(string cls)
    {
        if (AuxClassExact.Contains(cls)) return true;
        foreach (string p in AuxClassPrefixes)
        {
            if (cls.StartsWith(p, StringComparison.Ordinal)) return true;
        }
        foreach (string s in AuxClassContains)
        {
            if (cls.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    private static bool IsAcceptedName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && !IgnoredFileNames.Contains(name)
           && !string.Equals(name, SelfExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描候选：**正在运行的进程** 且（当前有可见应用窗口 ∪ 停在任务栏托盘）。
    /// 按 exe 全路径去重；两路都命中时标注"当前有窗口"。
    /// </summary>
    public static List<WhitelistCandidate> CollectCandidates()
    {
        var byPath = new Dictionary<string, WhitelistCandidate>(StringComparer.OrdinalIgnoreCase);
        var pidCache = new Dictionary<uint, string>();

        // ---- A. 当前有可见应用窗口 ----
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;   // 真隐藏（SW_HIDE）不算候选项
            if (IsCloaked(hwnd)) return true;          // 挂起/预热/其他虚拟桌面的 UWP 窗口不算
            if (!GetWindowRect(hwnd, out RECT r)) return true;
            if (!PassesSizeGate(r.Right - r.Left, r.Bottom - r.Top)) return true;

            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0) return true;
            string cls = sb.ToString();
            if (SystemEventMonitor.IsSystemWindowClass(cls)) return true;
            if (IsAuxiliaryClass(cls)) return true;

            // 最小化不算"显示中"，但**仍是候选**（标注为"已最小化"），
            // 以便与"真的未运行"区分（IsWindowVisible 对最小化窗口仍返回 true）。
            bool minimized = IsIconic(hwnd);

            GetWindowThreadProcessId(hwnd, out uint pid);
            pid = ResolveWindowPid(hwnd, pid);   // UWP：下钻到真实应用进程
            string? path = ResolveExePath(pid, pidCache);
            if (path == null) return true;

            string name = Path.GetFileNameWithoutExtension(path);
            if (!IsAcceptedName(name)) return true;

            // 可见优先：已在列表里的条目，只有"更可见"的状态才能覆盖它。
            if (byPath.TryGetValue(path, out WhitelistCandidate? exist))
            {
                if (exist.Source == SourceVisible) return true;   // 已是最强状态
                if (exist.Source == SourceMinimized && minimized) return true;
            }
            byPath[path] = new WhitelistCandidate(path, name,
                minimized ? SourceMinimized : SourceVisible);
            return true;
        }, IntPtr.Zero);

        // ---- B. 停在任务栏托盘（**只取正在运行的**，滤掉注册表里的陈旧条目）----
        // "是否在运行"用**进程名**判定而不是 FullPath：托盘条目常指向 SYSTEM / 提权进程
        // （如英伟达 NVDisplay.Container、UU 加速器），非提权调用读不到它们的 exe 路径，
        // 按路径判会误判为"没在运行"而漏掉。进程名来自快照，无需任何权限。
        HashSet<string> runningNames = CollectRunningProcessNames();
        foreach (string path in ReadTrayAppPaths(runningNames))
        {
            if (byPath.ContainsKey(path)) continue;   // 已有"当前有窗口"的标注，保留它
            string name = Path.GetFileNameWithoutExtension(path);
            if (!IsAcceptedName(name)) continue;
            byPath[path] = new WhitelistCandidate(path, name, SourceTray);
        }

        return byPath.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 正在运行的所有进程的**进程名**（无需权限：来自系统快照，不是 OpenProcess）。
    /// **排除 session 0（服务会话）** —— 服务永远没有用户界面，不可能是"停在托盘里的软件"；
    /// 这一条能干净地挡掉 Office 点击运行、各类更新器服务，而不必维护厂商黑名单。
    /// </summary>
    private static HashSet<string> CollectRunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId == 0) continue;   // 服务会话
                    set.Add(p.ProcessName);
                }
                catch
                {
                    // 个别进程取不到信息，跳过
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[whitelist] CollectRunningProcessNames failed", ex);
        }
        return set;
    }

    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";

    /// <summary>
    /// 读取"有托盘图标"的 exe 全路径，**并且该进程必须正在运行**
    /// （按 <paramref name="runningNames"/> 的进程名判定，见 <see cref="CollectRunningProcessNames"/>）。
    /// 含已知文件夹 GUID 前缀还原（`{7C5A40EF-…}\Intel\…` 这类）与存在性校验。
    /// </summary>
    private static IEnumerable<string> ReadTrayAppPaths(HashSet<string> runningNames)
    {
        var result = new List<string>();
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
            if (root == null) return result;

            foreach (string sub in root.GetSubKeyNames())
            {
                using RegistryKey? k = root.OpenSubKey(sub);
                if (k?.GetValue("ExecutablePath") is not string raw || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string? path = ResolveKnownFolderTokens(raw.Trim());
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { path = Path.GetFullPath(path); } catch { continue; }

                string name = Path.GetFileNameWithoutExtension(path);
                if (!runningNames.Contains(name)) continue;   // 关键：只保留"确实在运行"的
                if (!File.Exists(path)) continue;
                result.Add(path);
            }
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[whitelist] ReadTrayAppPaths failed", ex);
        }
        return result;
    }

    /// <summary>
    /// 路径形如 `{F38BF404-...}\explorer.exe` 时，用 `SHGetKnownFolderPath` 还原成绝对路径
    /// （§16.3 坑 1；本机实测 Windows / ProgramFilesX86 / ProgramFilesCommonX64 都出现过）。
    /// 不含 GUID 前缀时原样返回。
    /// </summary>
    private static string ResolveKnownFolderTokens(string raw)
    {
        if (!raw.StartsWith("{", StringComparison.Ordinal)) return raw;

        int end = raw.IndexOf('}');
        if (end <= 0) return raw;

        string guidText = raw.Substring(1, end - 1);
        if (!Guid.TryParse(guidText, out Guid knownFolder)) return raw;

        string? basePath = TryGetKnownFolderPath(knownFolder);
        if (string.IsNullOrWhiteSpace(basePath)) return raw;

        string rest = raw.Substring(end + 1).TrimStart('\\');
        return Path.Combine(basePath, rest);
    }

    private static string? TryGetKnownFolderPath(Guid knownFolder)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = SHGetKnownFolderPath(ref knownFolder, 0, IntPtr.Zero);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ==================================================================
    //  运行时判据（§16.5）
    // ==================================================================

    /// <summary>
    /// 白名单集合中是否存在"可见、未最小化、且过过滤"的顶层窗口。
    /// 用它而不是"前台窗口"，是因为停靠/边缘唤出的窗口**不一定夺取前台**（QQ 场景）。
    /// 每 1 s 调用一次；同样是"先过滤后解析路径"。
    /// </summary>
    public static bool AnyVisibleWindowFor(HashSet<string> whitelistPaths)
    {
        if (whitelistPaths == null || whitelistPaths.Count == 0) return false;

        var pidCache = new Dictionary<uint, string>();
        bool hit = false;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsIconic(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;   // 同上：cloaked 不算"显示中"
            if (!GetWindowRect(hwnd, out RECT r)) return true;
            if (!PassesSizeGate(r.Right - r.Left, r.Bottom - r.Top)) return true;

            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0) return true;
            string cls = sb.ToString();
            if (SystemEventMonitor.IsSystemWindowClass(cls)) return true;
            if (IsAuxiliaryClass(cls)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            pid = ResolveWindowPid(hwnd, pid);   // UWP：下钻到真实应用进程
            string? path = ResolveExePath(pid, pidCache);
            if (path == null) return true;

            // 与候选扫描用**同一套黑名单**：资源管理器 / 任务栏壳 / UWP 宿主等永不触发暂停。
            // 它们是常驻前台窗口，一旦命中就会把暂停变成常开 —— 这是第二道防线
            // （第一道见 ComputeGroupKey 里"系统目录 exe 不做产品名归并"）。
            if (!IsAcceptedName(Path.GetFileNameWithoutExtension(path))) return true;

            // 按**归并键**匹配（不是按 exe 路径）：窗口进程与托盘进程属于同一软件时也能命中，
        // 见 GroupKeyFor 的说明（抖音：douyin.exe 的窗口 + douyin_tray.exe 的托盘）。
        if (whitelistPaths.Contains(GroupKeyFor(path)))
        {
            hit = true;
            return false;   // 提前结束枚举
        }
        return true;
        }, IntPtr.Zero);

        return hit;
    }

    /// <summary>
    /// 逐屏命中判定（2026-09-16 逐屏白名单）：返回「是否存在合格的白名单窗口」与
    /// 「被窗口**完全落入**的屏幕键集合」。
    ///
    /// 与 <see cref="AnyVisibleWindowFor"/> 用**同一套过滤链**（可见 / 未最小化 / 未 cloaked /
    /// 过尺寸闸门 / 非系统窗类 / 非辅助窗类 / 过黑名单 / 命中的是归并键）。
    ///
    /// 三条关键差异：
    /// 1. 用 **DWMWA 视觉边界**而非 GetWindowRect（见 DWMWA_EXTENDED_FRAME_BOUNDS 的说明）；
    /// 2. **不提前结束枚举** —— 要收集全部命中屏；
    /// 3. 返回 `AnyWindow` 与 `HitEdids` 两个值，调用方据此区分
    ///    「无窗口（应清空暂停集）」与「窗口跨屏中（应保持不动）」—— 二者语义不同，不能混。
    ///
    /// ⚠️ 2026-09-20 新增「主导屏」兜底（B11）：窗口**装不进**任何一块屏
    /// （典型：窗口比屏还宽 2px，见 <see cref="DominantScreenRatio"/>）时，
    /// "完全落入"永不成立 ⇒ 该屏永不进入 S。此时若窗口有 ≥95% 面积落在某屏内，
    /// 也判为归属该屏。仅在没有屏满足"完全落入"时启用，常规路径零回归。
    ///
    /// 屏键优先取 <see cref="Monitor.EdidId"/>（回落 DeviceName），与 gamma 的 _displayStates 键一致。
    /// </summary>
    public static (bool AnyWindow, HashSet<string> HitEdids, HashSet<string> OverlapEdids) GetHitScreens(
        HashSet<string> whitelistGroupKeys, IReadOnlyList<Monitor> monitors, int tolerance = 4)
    {
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overlap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (whitelistGroupKeys == null || whitelistGroupKeys.Count == 0 || monitors == null || monitors.Count == 0)
            return (false, hit, overlap);

        var pidCache = new Dictionary<uint, string>();
        bool any = false;
        // 「跨屏判定」的归属签名（循环内只收集、循环后统一去重输出，见 LogOwnershipOnce）
        var ownershipSigs = new List<string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsIconic(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;

            RECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
            {
                if (!GetWindowRect(hwnd, out r)) return true;   // DWM 不可用时回退
            }
            if (!PassesSizeGate(r.Right - r.Left, r.Bottom - r.Top)) return true;

            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0) return true;
            string cls = sb.ToString();
            if (SystemEventMonitor.IsSystemWindowClass(cls)) return true;
            if (IsAuxiliaryClass(cls)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            pid = ResolveWindowPid(hwnd, pid);
            string? path = ResolveExePath(pid, pidCache);
            if (path == null) return true;
            if (!IsAcceptedName(Path.GetFileNameWithoutExtension(path))) return true;
            if (!whitelistGroupKeys.Contains(GroupKeyFor(path))) return true;

            any = true;

            // ① 完全落入判定：视觉边界 ⊆ 屏矩形（带容差）
            // ② 同时算"相交面积" —— 供 ③ 的兜底与调用方判断"窗口是否还碰着旧屏"
            long winW = r.Right - r.Left;
            long winH = r.Bottom - r.Top;
            long winArea = winW > 0 && winH > 0 ? winW * winH : 0;

            bool anyFit = false;
            var interArea = new List<(string Key, long Area)>(monitors.Count);

            foreach (var m in monitors)
            {
                if (m.Right - m.Left <= 0 || m.Bottom - m.Top <= 0) continue;
                string key = string.IsNullOrEmpty(m.EdidId) ? m.DeviceName : m.EdidId;
                if (string.IsNullOrEmpty(key)) continue;

                if (r.Left >= m.Left - tolerance && r.Top >= m.Top - tolerance &&
                    r.Right <= m.Right + tolerance && r.Bottom <= m.Bottom + tolerance)
                {
                    hit.Add(key);
                    anyFit = true;
                }

                // 相交（面积 > 0）的屏全部收集：调用方用它判断"窗口是否还碰着旧屏"
                long ow = Math.Min(r.Right, m.Right) - Math.Max(r.Left, m.Left);
                long oh = Math.Min(r.Bottom, m.Bottom) - Math.Max(r.Top, m.Top);
                if (ow > 0 && oh > 0)
                {
                    overlap.Add(key);
                    interArea.Add((key, ow * oh));
                }
            }

            // ------------------------------------------------------------------
            // ③ 「跨屏判定」（2026-09-20，用户澄清规则）
            //
            // 用户原话：「把跨屏生效时候的要**完全拖入到新屏幕才生效**的这条规定给误会了，
            //   这个规则是要在**跨屏时**才生效，**没有跨屏则不生效**」。
            //
            // ⇒ 「完全落入」只是**跨屏时**的判据（用户把窗口从 A 屏拖到 B 屏，必须完全
            //   到达 B 才切换 —— Q1/Q6）。**没有跨屏时不该要求它**：否则"比屏还宽的窗口"
            //   （Win11 DPI 取整：逻辑 1464 × 1.75 = 2562 > 屏宽 2560）**永远无法完全落入**
            //   ⇒ 白名单对该应用长期不生效。用户实测原话：「只有 workbuddy 的窗口
            //   **四条边都露出屏幕**时，白名单才生效」——正是这条误用造成的。
            //
            // 判据（只统计**实质相交**的屏，见 <see cref="CrossScreenFloor"/>）：
            //   · 1 块 ⇒ **没有跨屏** ⇒ 归属该屏（**不要求**完全落入）✓
            //   · ≥2 块 ⇒ **跨屏**（正在拖动 / 真跨屏）⇒ 不归属
            //     ⇒ 由调用方 `EvaluateWhitelistPerMonitor` 的 `overlap` 粘滞逻辑
            //        保持 S 不变 ⇒ Q1/Q6 语义**完全不变** ✓
            //
            // 实测校验（窗口 2562×1358 @(75,0)、两屏各 2560×1440）：
            //   主屏 97.0% / 副屏 3.0% ⇒ 只有主屏过 5% 门槛 ⇒ 归属主屏 ✓（用户场景）
            //   跨屏各约 50%          ⇒ 两块都过门槛 ⇒ 不归属 ⇒ 保持不动 ✓
            // ------------------------------------------------------------------
            if (!anyFit && winArea > 0)
            {
                var substantial = new List<(string Key, double Ratio)>(interArea.Count);
                foreach ((string key, long area) in interArea)
                {
                    double ratio = (double)area / winArea;
                    if (ratio >= CrossScreenFloor) substantial.Add((key, ratio));
                }

                if (substantial.Count == 1)
                {
                    hit.Add(substantial[0].Key);
                    // 只**收集**，不在循环内输出 —— 本方法每秒被调用一次，
                    // 直接输出会把 ops 日志刷爆（实测 89% 的行都是它，并把日志
                    // 轮转窗口从 9.5h 压到 1h，冲掉真正的证据）。
                    // 循环外由 LogOwnershipOnce 做**内容快照比对**后输出。
                    ownershipSigs.Add(
                        $"{substantial[0].Key} 窗口 {winW}x{winH} @({r.Left},{r.Top}) " +
                        $"该屏占 {substantial[0].Ratio * 100.0:0.0}%（未跨屏 ⇒ 直接归属）");
                }
            }

            return true;
        }, IntPtr.Zero);

        LogOwnershipOnce(ownershipSigs);

        return (any, hit, overlap);
    }

    /// <summary>
    /// 「跨屏判定」归属的**去重**日志（2026-09-20）。
    ///
    /// ⛔ 为什么必须去重：`GetHitScreens` 由白名单定时器**每秒调用一次**。
    ///   第一版在这里直接输出日志，实测 1 小时产出 1350 条、占 ops 日志 **89%**，
    ///   把 `[gamma/write]`、`[whitelist/permon]` 等真正的证据全部淹没，
    ///   还把日志轮转窗口从 9.5 小时压缩到 1 小时（旧记录被冲掉）。
    ///
    /// ✅ 用**内容快照比对**（不是时间节流 —— 时间节流在每秒调用的路径上等于没节流）：
    ///   · 窗口不动 ⇒ 签名不变 ⇒ **0 条**（稳态零开销）
    ///   · 窗口移动/尺寸变化一次 ⇒ **恰好 1 条**
    ///   · 脱离该判定（恢复"完全落入"或变成跨屏）⇒ 复位，下次进入时能重新打一条
    /// </summary>
    private static readonly object OwnershipLogLock = new();
    private static string _lastOwnershipSig = "";

    private static void LogOwnershipOnce(List<string> sigs)
    {
        string sig = string.Join(" ; ", sigs);
        if (sig.Length == 0)
        {
            // 已脱离该判定 ⇒ 复位，保证下次进入时能再打一条（否则同样的几何不会再报）
            lock (OwnershipLogLock) _lastOwnershipSig = "";
            return;
        }
        lock (OwnershipLogLock)
        {
            if (sig == _lastOwnershipSig) return;   // 内容没变 ⇒ 不重复输出
            _lastOwnershipSig = sig;
        }
        OpLog.Log("[whitelist/hit] 跨屏判定：" + sig);
    }

    /// <summary>规范化判等用集合（OrdinalIgnoreCase 全路径）。</summary>
    public static HashSet<string> MakePathSet(IEnumerable<string>? paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths == null) return set;
        foreach (string p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { set.Add(Path.GetFullPath(p)); } catch { /* 忽略非法路径 */ }
        }
        return set;
    }

    // ==================================================================
    //  "同一软件"归并键（2026-09-15）
    // ==================================================================

    private static readonly Dictionary<string, string> GroupKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 归并键 —— 把"同一个软件"的多个进程（**窗口进程 / 托盘进程 / 子进程**）归成一组，
    /// 用户只需勾一次即可覆盖全部。
    ///
    /// 主键取**版本资源的 `ProductName`**：本机实测抖音的 `douyin.exe` / `douyin_guard.exe` /
    /// `douyin_tray.exe` 三者 `ProductName` 都是 `douyin`（窗口属前者、托盘属后者、子目录也不同）
    /// → 按它归并干净利落。
    ///
    /// ⚠️ **不能只用数字签名者**：实测英伟达的 `NVDisplay.Container.exe` 是
    /// "Microsoft Windows Hardware Compatibility Publisher" 签的，而控制面板 `nvcplui.exe` 是
    /// NVIDIA 签的 —— 按签名者归并反而把它们分开（此前把该方案说成"三例全覆盖"是误判）。
    ///
    /// 退化顺序：`ProductName` → `CompanyName` → 所在目录。
    /// </summary>
    public static string GroupKeyFor(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "";
        lock (GroupKeyCache)
        {
            if (GroupKeyCache.TryGetValue(exePath, out string? cached)) return cached;
        }
        string key = ComputeGroupKey(exePath);
        lock (GroupKeyCache) { GroupKeyCache[exePath] = key; }
        return key;
    }

    private static string ComputeGroupKey(string exePath)
    {
        // ⚠️ 系统目录（%SystemRoot% 下）的 exe **不参与产品名归并**：
        // 它们的 ProductName 一律是 "Microsoft® Windows® Operating System"（实测 任务管理器/设置/
        // 资源管理器/任务栏壳 都相同），按它归并会把它们全并成一组 —— 于是"勾了任务管理器，
        // 打开资源管理器或展开任务栏托盘就会命中同组而误触发暂停"（用户实测的真实事故）。
        // 这类 exe 只用文件名做键，即**不做跨进程归并**。
        try
        {
            string? win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(win)
                && exePath.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return "f:" + Path.GetFileName(exePath);
            }
        }
        catch
        {
            // 取不到系统目录就照常走下面的常规归并
        }

        try
        {
            System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return "p:" + vi.ProductName.Trim();
            if (!string.IsNullOrWhiteSpace(vi.CompanyName)) return "c:" + vi.CompanyName.Trim();
        }
        catch
        {
            // 读不到版本资源（受保护路径等）→ 退化到目录
        }
        try
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir)) return "d:" + dir;
        }
        catch
        {
            // 忽略
        }
        return "f:" + exePath;
    }

    /// <summary>把白名单路径集合换算成"归并键"集合（运行判据按组匹配）。</summary>
    public static HashSet<string> MakeGroupKeySet(IEnumerable<string>? paths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (paths == null) return set;
        foreach (string p in paths)
        {
            string k = GroupKeyFor(p);
            if (!string.IsNullOrEmpty(k)) set.Add(k);
        }
        return set;
    }

    // ==================================================================
    //  Win32
    // ==================================================================

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// UWP 关键处理（LightBulb 同款，§16.2）：UWP 应用的**可见顶层窗口由帧宿主
    /// `ApplicationFrameHost` 拥有**（实测奥创：`ApplicationFrameWindow` title='Armoury Crate'
    /// 归 ApplicationFrameHost 所有，而真正的 `ArmouryCrate.exe` 只持有隐藏的 IME 窗）。
    /// 因此必须**下钻它的子窗口**，找出属于真实应用进程的子窗口，用那个 pid 去解析 exe 路径；
    /// 否则白名单对 UWP 应用永远失效。
    /// 非 ApplicationFrameWindow 时原样返回窗口自身 pid。
    /// </summary>
    private static uint ResolveWindowPid(IntPtr hwnd, uint ownPid)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0) return ownPid;
        if (!string.Equals(sb.ToString(), "ApplicationFrameWindow", StringComparison.Ordinal))
        {
            return ownPid;
        }

        uint found = 0;
        EnumChildWindows(hwnd, (h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != 0 && pid != ownPid)
            {
                found = pid;
                return false;   // 找到即停
            }
            return true;
        }, IntPtr.Zero);

        // 找不到真实应用进程（UWP 已挂起 / 子窗口不可枚举）→ 返回 0，调用方跳过该窗口。
        // **绝不能退回宿主 pid**：否则 ApplicationFrameHost 这个"壳"会变成候选条目，
        // 用户勾上后会被永久判定为"显示中"（实测就是这个坑）。
        return found;
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// DWM 视觉边界（**不含**阴影/resize border）。
    /// ⚠ "窗口是否完全落入某屏"必须用它，不能用 GetWindowRect：Win10+ 的窗口有不可见
    /// resize border，GetWindowRect 会外扩 **7px × DPI缩放**（实测 175% 下 12px）⇒
    /// 最大化窗口被永久误判为"未完全落入"（实测 28 个合格窗口中 1 个）；
    /// 视觉边界下最大化窗口恰好等于屏幕矩形，天然判为完全落入（**无需 IsZoomed 分支**）。
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute,
        out int pvAttribute, int cbAttribute);

    private const uint DWMWA_CLOAKED = 14;

    /// <summary>
    /// 窗口是否被 DWM **遮蔽（cloaked）**：挂起的 UWP、不在当前虚拟桌面、后台预热的窗口
    /// 都会是 cloaked —— 它们 `IsWindowVisible` 仍是 true，但用户根本看不到。
    /// 实测"设置"(SystemSettings) 的 CoreWindow 即使没打开也是 `vis=True 799x590`，
    /// 只判 IsWindowVisible 会把它当成"显示中" → 白名单永久生效。故必须以 cloaked 为准。
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                   && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern IntPtr SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>取进程 exe 全路径（失败/null 表示受保护进程或无权限 —— 静默跳过，不报错）。
    /// <paramref name="cache"/> 为本次枚举内的 pid→路径缓存，避免重复 OpenProcess。</summary>
    private static string? ResolveExePath(uint pid, Dictionary<uint, string> cache)
    {
        if (pid == 0) return null;
        if (cache.TryGetValue(pid, out string? cached)) return cached;

        string? result = null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                uint size = 1024;
                var sb = new StringBuilder((int)size);
                if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                {
                    result = sb.ToString();
                }
            }
            catch
            {
                result = null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        cache[pid] = result!;   // 失败也缓存（null），避免同一 pid 反复试探
        return result;
    }
}
