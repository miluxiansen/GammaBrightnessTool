using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Represents a display monitor and provides gamma control capabilities.
/// Extracted and simplified from LightBulb's implementation.
///
/// 3.6.0: each Monitor now carries a stable EDID-based identity (EdidId)
/// used as the key for per-monitor state (AppSettings.MonitorStates /
/// MonitorNames). The GDI DeviceName (\\.\DISPLAYn) is only used to open
/// the gamma DC — it must NOT be persisted (changes on hotplug/reboot).
/// Verified 2026-08-25: on this machine (dual-DP clone) GDI enumeration
/// already merges the two EDID entries into a single szDevice, so each
/// Monitor maps 1:1 to one physical panel and one gamma DC.
/// </summary>
public sealed class Monitor : IDisposable
{
    private string _deviceName;
    private bool _isPrimary;
    private DeviceContext? _deviceContext;

    /// <summary>
    /// GDI device name (\\.\DISPLAYn) used to open the gamma DC. Not stable
    /// across reboots/hotplug — do not use as a persisted key.
    /// </summary>
    public string DeviceName => _deviceName;

    /// <summary>
    /// Stable EDID instance ID (base form, no trailing \Instance index):
    /// MONITOR\SAC2466\{4d36e96e-...}. Identifies the physical panel.
    /// Empty when EDID enumeration fails (fallback: use DeviceName).
    /// </summary>
    public string EdidId { get; private set; } = "";

    public bool IsPrimary => _isPrimary;

    /// <summary>
    /// Physical (native) pixel width of the current display mode, read via
    /// GetDeviceCaps on a per-device DC. 0 when the DC could not be created.
    /// </summary>
    public int PhysicalWidthPx { get; private set; }

    /// <summary>Physical (native) pixel height of the current display mode.</summary>
    public int PhysicalHeightPx { get; private set; }

    /// <summary>Effective DPI of this monitor (96 at 100% scaling).</summary>
    public int DpiX { get; private set; } = 96;

    /// <summary>Windows-style scale percentage, e.g. 175 at DPI 168.</summary>
    public int ScalePercent => DpiX > 0 ? (int)Math.Round(DpiX * 100.0 / 96.0) : 0;

    /// <summary>
    /// 显示器在**虚拟屏幕坐标系**中的矩形（物理像素）。PerMonitorV2 进程下与
    /// GetWindowRect / DWMWA_EXTENDED_FRAME_BOUNDS 同口径，可直接做"窗口是否完全落入"判定。
    /// 2026-09-16 逐屏白名单用。
    /// </summary>
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Right { get; private set; }
    public int Bottom { get; private set; }

    /// <summary>
    /// Win32 <b>HMONITOR</b>（2026-09-19 全屏逐屏用）。`MonitorFromWindow(hwnd,
    /// MONITOR_DEFAULTTONEAREST)` 返回的句柄可直接与它比对，从而把"全屏窗口"
    /// 映射到本显示器的 EDID 键 —— 零新增 Win32 API。
    /// ⚠ 不要与 <see cref="DeviceContext.Handle"/> 混淆：那个是 GDI 的 <b>HDC</b>
    ///   （`CreateDC` 得到），两者类型相同但语义完全不同。
    /// </summary>
    public IntPtr HMonitor { get; private set; }

    private Monitor(string deviceName, bool isPrimary, string edidId, int physW, int physH, int dpiX,
                    int left = 0, int top = 0, int right = 0, int bottom = 0, IntPtr hMonitor = default)
    {
        _deviceName = deviceName;
        _isPrimary = isPrimary;
        EdidId = edidId;
        PhysicalWidthPx = physW;
        PhysicalHeightPx = physH;
        Left = left; Top = top; Right = right; Bottom = bottom;
        HMonitor = hMonitor;
        if (dpiX > 0) DpiX = dpiX;
    }

    /// <summary>
    /// Creates a device context for gamma operations.
    /// </summary>
    public DeviceContext? TryCreateDeviceContext()
    {
        if (_deviceContext != null)
            return _deviceContext;

        var dc = CreateDC("DISPLAY", _deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            // ------------------------------------------------------------------
            // P0-1 修复（2026-09-23 代码审查）：**删除"回退主屏 DC"分支**。
            //
            // 原实现按名打开 DC 失败时回退 CreateDC("DISPLAY", null, …)（= 主屏），
            // 而 DeviceContext.MonitorEdidId 仍标着**原屏** EDID —— 独立控制模式下
            // 某块屏 DC 打开失败时，gamma 会写到**主屏**且无任何日志（"写错屏且自以为没写错"）。
            // 拓扑 churn 期（拔插屏瞬间）按名 CreateDC 失败属正常现象，
            // RefreshDisplays() 稍后会重新枚举补齐 ⇒ 改为返回 null：该屏本次不接管（宁缺勿错）。
            // 三处调用方（GammaController.Initialize / RefreshDisplays / Program --reset-gamma）
            // 均已判空，行为 = 该屏缺席，等下一轮重试。
            // ------------------------------------------------------------------
            OpLog.Log($"[display] CreateDC 失败（{DeviceName}）⇒ 本屏暂不接管（等待下次 RefreshDisplays 重试；不再回退主屏 DC）");
            return null;
        }

        _deviceContext = new DeviceContext(dc, EdidId);
        return _deviceContext;
    }

    /// <summary>
    /// Returns the EDID instance ID (base form) for a GDI device name,
    /// or "" if the monitor is not found / not active.
    /// Uses EnumDisplayDevices level 1 (adapter) + level 2 (monitor).
    /// </summary>
    internal static string GetEdidId(string gdiDeviceName)
    {
        try
        {
            int ai = 0;
            while (true)
            {
                var adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, (uint)ai, ref adapter, 0))
                    break;

                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 ||
                    !string.Equals(adapter.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    ai++;
                    continue;
                }

                // Found the adapter; get its first ACTIVE monitor
                int mi = 0;
                while (true)
                {
                    var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(adapter.DeviceName, (uint)mi, ref monitor, 0))
                        break;

                    if ((monitor.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                    {
                        // Base form: MONITOR\VendorModel\{GUID} (strip \Instance)
                        var id = monitor.DeviceID ?? "";
                        int lastSlash = id.LastIndexOf('\\');
                        if (lastSlash > 0)
                            id = id.Substring(0, lastSlash);
                        return id;
                    }
                    mi++;
                }
                return ""; // adapter has no active monitor
            }
        }
        catch
        {
            return "";
        }
        return "";
    }

    /// <summary>上次记录的拓扑描述（用于**去重**：`GetAll()` 被每秒调用，内容不变就不重复打）。</summary>
    private static string? _lastTopoDesc;
    private static readonly object _topoLock = new();

    /// <summary>
    /// Enumerates all available monitors (GDI), each with its EDID-based
    /// stable identity. Duplicate GDI entries are skipped (some drivers
    /// report the same monitor twice); EDID duplicates within one GDI
    /// entry are naturally merged by GDI (verified on dual-DP clone).
    /// </summary>
    public static IReadOnlyList<Monitor> GetAll()
    {
        var monitors = new List<Monitor>();
        var seenDevices = new HashSet<string>();

        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>()
            };

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                // Skip duplicates (some drivers report same monitor multiple times)
                if (!seenDevices.Contains(mi.szDevice))
                {
                    seenDevices.Add(mi.szDevice);
                    string edidId = GetEdidId(mi.szDevice);

                    // Physical resolution of the current mode (native pixels).
                    int physW = 0, physH = 0;
                    IntPtr dc = NativeMethods.CreateDC("DISPLAY", mi.szDevice, null, IntPtr.Zero);
                    if (dc != IntPtr.Zero)
                    {
                        physW = NativeMethods.GetDeviceCaps(dc, NativeMethods.HORZRES);
                        physH = NativeMethods.GetDeviceCaps(dc, NativeMethods.VERTRES);
                        NativeMethods.DeleteDC(dc);
                    }

                    // Effective per-monitor DPI.
                    int dpiX = 96;
                    if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiXv, out _) == 0 && dpiXv > 0)
                        dpiX = (int)dpiXv;

                    monitors.Add(new Monitor(mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, edidId, physW, physH, dpiX,
                        mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom, hMonitor));
                }
            }

            return true;
        });

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        // ------------------------------------------------------------------
        // 诊断（2026-09-19，用户要求）：记录**显示器拓扑**。
        // 现场形态：一块物理屏 + 两条 DP（一条直连、一条经 KVM）、显卡欺骗器改插主板 HDMI
        //   ⇒ 会同时出现「同一适配器多路」与「跨 GPU（核显/独显各一路）」两种形态，
        //     而这两类只在现场才看得见，日志无痕迹则事后无法判断。
        // 只记日志、**不改任何行为**。同 EDID 多路时额外告警（逐屏设置会共用同一份 ——
        //   若两路是**同一物理屏**则这正是期望行为；若是两块同型号屏则设置会互相覆盖）。
        // ------------------------------------------------------------------
        try
        {
            string desc = string.Join(" | ", monitors.Select(m =>
                $"{m.DeviceName}={(m.EdidId.Length > 0 ? m.EdidId : "(无EDID)")}"));
            bool changed;
            lock (_topoLock)
            {
                changed = desc != _lastTopoDesc;
                _lastTopoDesc = desc;
            }

            if (!changed) return monitors;   // 拓扑没变 ⇒ 不重复打日志（GetAll 每秒被调用）

            OpLog.Log($"[display] 拓扑：{monitors.Count} 个输出 ⇒ {desc}");

            var dups = monitors
                .Where(m => m.EdidId.Length > 0)
                .GroupBy(m => m.EdidId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} ×{g.Count()}（{string.Join(",", g.Select(m => m.DeviceName))}）")
                .ToList();
            if (dups.Count > 0)
            {
                OpLog.Log($"[display] ⚠ 同 EDID 多路 ⇒ 逐屏设置将共用同一份：{string.Join("；", dups)}");
            }
        }
        catch (Exception ex)
        {
            // 诊断日志失败不得影响主流程，但**必须留下痕迹** ——
            // 否则"日志没打出来"这件事本身无法诊断（2026-09-19 踩过）。
            OpLog.Log($"[display] 拓扑日志失败（不影响运行）：{ex.GetType().Name}: {ex.Message}");
        }

        return monitors;
    }

    // ------------------------------------------------------------------
    // Friendly display names (EDID Monitor-Name descriptor via DisplayConfig)
    // ------------------------------------------------------------------
    // GetDisplaySystemName falls back to the internal EDID model segment
    // ("SAC2466"), which users do not recognize. DisplayConfigGetDeviceInfo
    // (GET_TARGET_NAME) exposes the EDID 0xFC descriptor the way Windows
    // Settings does ("G5c II"), without admin rights. Build once per process.

    private static Dictionary<string, string>? _friendlyByModel;
    private static readonly object _friendlyLock = new();

    /// <summary>
    /// Returns the friendly (EDID Monitor-Name) display name for an EDID
    /// instance-id base ("MONITOR\SAC2466\{GUID}"), or null when the panel
    /// has no name descriptor (e.g. many laptop panels).
    /// </summary>
    public static string? GetEdidFriendlyName(string edidId)
    {
        string model = ExtractEdidModel(edidId);
        if (model.Length == 0) return null;
        EnsureFriendlyNames();
        return _friendlyByModel!.TryGetValue(model, out string? name) ? name : null;
    }

    private static string ExtractEdidModel(string edidId)
    {
        if (string.IsNullOrEmpty(edidId)) return "";
        // "MONITOR\SAC2466\{GUID}" -> "SAC2466"
        int slash = edidId.IndexOf('\\');
        if (slash > 0 && slash + 1 < edidId.Length)
        {
            int slash2 = edidId.IndexOf('\\', slash + 1);
            return slash2 > slash + 1 ? edidId.Substring(slash + 1, slash2 - slash - 1) : edidId.Substring(slash + 1);
        }
        return "";
    }

    private static void EnsureFriendlyNames()
    {
        if (_friendlyByModel != null) return;
        lock (_friendlyLock)
        {
            if (_friendlyByModel != null) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) == 0 && numPaths > 0)
                {
                    var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[numPaths];
                    IntPtr modes = Marshal.AllocHGlobal((int)numModes * 96); // mode info entries (generous)
                    try
                    {
                        if (NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) == 0)
                        {
                            for (int i = 0; i < numPaths; i++)
                            {
                                var name = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
                                {
                                    header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                                    {
                                        type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                                        size = Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                                        adapterId = paths[i].targetInfo.adapterId,
                                        id = paths[i].targetInfo.id
                                    }
                                };
                                if (NativeMethods.DisplayConfigGetDeviceInfo(ref name) == 0 &&
                                    !string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName) &&
                                    !string.IsNullOrEmpty(name.monitorDevicePath))
                                {
                                    // monitorDevicePath: \\?\DISPLAY#<Model>#<Instance>#{guid}
                                    string model = ExtractPathModel(name.monitorDevicePath);
                                    if (model.Length > 0 && !map.ContainsKey(model))
                                        map[model] = name.monitorFriendlyDeviceName.Trim();
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(modes);
                    }
                }
            }
            catch
            {
                // DisplayConfig unavailable — leave map empty, callers fall back.
            }
            _friendlyByModel = map;
        }
    }

    private static string ExtractPathModel(string devicePath)
    {
        // "\\?\DISPLAY#SAC2466#5&3954046&1&UID4352#{guid}" -> "SAC2466"
        try
        {
            string[] seg = devicePath.Split('#');
            if (seg.Length >= 2 && seg[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
                return seg[1];
        }
        catch
        {
        }
        return "";
    }

    public void Dispose()
    {
        _deviceContext?.Dispose();
        _deviceContext = null;
    }
}


/// <summary>
/// Wrapper for a GDI device context with gamma control.
/// </summary>
public sealed class DeviceContext : IDisposable
{
    /// <summary>The Monitor EdidId this DC belongs to ('' if unknown).</summary>
    public string MonitorEdidId { get; } = "";

    private IntPtr _handle;
    private bool _disposed;

    // Monotonic counter driving the cache-breaking noise. A counter (not a
    // Random) guarantees every consecutive call produces a DIFFERENT ramp:
    // a random +0/+1 has a 50% chance of repeating, which would let the
    // driver cache the identical second call (and, in clone mode, reset the
    // first DC's ramp back to default).
    private static int _noiseCounter;

    public IntPtr Handle => _handle;

    public DeviceContext(IntPtr handle, string monitorEdidId = "")
    {
        _handle = handle;
        MonitorEdidId = monitorEdidId;
    }

    /// <summary>
    /// Reads the currently active gamma ramp (used to probe the actual
    /// screen brightness/temperature for smooth-start animation).
    /// </summary>
    public GammaRamp GetCurrentRamp()
    {
        TryGetCurrentRamp(out var ramp);
        return ramp;
    }

    /// <summary>
    /// F17（2026-09-19）：判断"屏幕当前 ramp"是否已经等于"要写进去的目标 ramp"，
    /// **容忍 ±1 的破缓存抖动**（见 <see cref="SetGamma"/> 的噪声说明）。
    /// index 255 是峰值、从不加噪声 ⇒ 必须严格相等；其余 0..254 差 ≤1 即视为相同。
    /// </summary>
    private static bool RampEqualsWithinNoise(GammaRamp a, GammaRamp b, int tol = 1)
    {
        if (a.Red == null || b.Red == null || a.Green == null || b.Green == null
            || a.Blue == null || b.Blue == null) return false;
        // 峰值严格比较（它决定亮度/色温的观感，且本类从不对它加噪声）
        if (a.Red[255] != b.Red[255] || a.Green[255] != b.Green[255] || a.Blue[255] != b.Blue[255])
            return false;
        for (int i = 0; i < 255; i++)
        {
            if (Math.Abs((int)a.Red[i] - (int)b.Red[i]) > tol) return false;
            if (Math.Abs((int)a.Green[i] - (int)b.Green[i]) > tol) return false;
            if (Math.Abs((int)a.Blue[i] - (int)b.Blue[i]) > tol) return false;
        }
        return true;
    }

    /// <summary>
    /// Reads the currently active gamma ramp and reports whether the read
    /// actually succeeded.
    ///
    /// ⚠️ 与 GetCurrentRamp() 的区别（2026-09-17 F1）：后者读取失败时**静默返回默认
    /// identity ramp**，所以**不能**用于"屏幕当前值 == 目标值"这类等价判定 ——
    /// 会把"读不到"误判成"屏幕已经是 identity"。等价判定必须用本方法。
    /// </summary>
    public bool TryGetCurrentRamp(out GammaRamp ramp)
    {
        ramp = GammaRamp.CreateDefault();
        if (_disposed || _handle == IntPtr.Zero) return false;
        bool ok = GetDeviceGammaRamp(_handle, ref ramp);
        if (!ok) ramp = GammaRamp.CreateDefault();
        return ok;
    }

    /// <summary>
    /// Applies a gamma ramp to this display.
    ///
    /// WORKAROUND (restored from 3.0.0): some GPU drivers (this machine's
    /// dual-DP clone setup included) cache gamma ramps and treat two
    /// IDENTICAL consecutive SetDeviceGammaRamp calls as a no-op or even
    /// re-sync the output back to the default 100% ramp. In clone mode
    /// ApplyGamma issues one call per enumerated DC for the SAME physical
    /// output; without variation the second call resets the first, which
    /// the user sees as "dims for a moment, then immediately brightens".
    /// A tiny random offset (+0/+1 per channel) makes every ramp unique so
    /// the driver applies each one normally.
    ///
    /// PEAK PROTECTION (new in 3.1.0): the noise is applied ONLY to
    /// non-peak values (index &lt; 255). At UI 0% the ramp peak is exactly
    /// 32767 (physical 50%), and a +1 noise on the peak would push it to
    /// 32768, which this machine's driver REJECTS, making it fall back to
    /// the default 100% ramp (screen flashes bright while the UI still
    /// shows 0%). Keeping the peak untouched preserves the accepted value
    /// while still breaking the driver cache.
    /// </summary>
    public bool SetGamma(GammaRamp ramp)
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        // ------------------------------------------------------------------
        // F17（2026-09-19）：**先读回屏幕当前 ramp —— 若已经等于目标，就完全不写。**
        //
        // 背景（用户报「正常运行中偶发、无规律闪屏，且监控脚本看不到任何 gamma 变动」）：
        //   下面为了破驱动缓存，每次都会给 index 0..254 各加 0/1 抖动 ⇒
        //   **哪怕调用方要写的值一模一样**，实际写下去的 ramp 也**永远不同**
        //   ⇒ 驱动每次都要重新挂载 gamma 表 ⇒ **屏幕闪一下**（观感＝整屏重渲染）。
        //   而本函数**刻意不动 index 255（峰值）** ⇒ 外部采样若只读峰值，
        //   就**完全看不出变化** —— 与用户的观察逐条吻合。
        //
        // 为什么用"读回比较"而不是"缓存上次写入值"：
        //   为了**保住自愈能力** —— 驱动若被重置（切全屏 / 睡眠恢复 / 驱动刷新），
        //   屏幕 ramp ≠ 目标 ⇒ 照常写入恢复；只有屏幕本来就已经正确时才跳过。
        //   ⇒ 被消掉的只是"白写一次 = 白闪一次"那部分开销。
        // ------------------------------------------------------------------
        // 判据：**只看屏幕当前 ramp 是否已经等于目标**（tol=1，容忍破缓存的 ±1 抖动）。
        //
        // ⚠️ 2026-09-19 二改：初版我还要求"目标与上次写入的目标**完全相同**（tol=0）"，
        //    结果**挡不住用户报的白名单开关闪屏**。原因：白名单总开关切换会启动
        //    1200ms 过渡动画，动画**每一帧的值都在动**（哪怕起点≈终点、净变化为 0）
        //    ⇒ 条件①永远不成立 ⇒ 38 帧每帧都真的写一张新表 ⇒ 驱动重挂载 38 次。
        //    这正是规则里记的「**净变化 0 的过渡伪影**＝闪的头号真凶」。
        // 去掉条件①后：动画帧若与屏幕现状无实质差异 ⇒ 直接跳过 ⇒ 不再闪；
        //   真调节不会被误吞（1% 亮度对应 R255 差约 328，远大于容差 1，见实测）。
        // 用"读回比较"而非"缓存上次写入值"是为了**保住自愈**：驱动被重置时屏幕
        //   ramp ≠ 目标 ⇒ 照常写入恢复。
        if (TryGetCurrentRamp(out GammaRamp current) && RampEqualsWithinNoise(current, ramp, 1))
        {
            return true;    // 跳过写屏（避免一次无谓的全屏重挂载/闪动）
        }

        // Break the driver cache: vary +0/+1 on non-peak entries only. The
        // counter alternates 0,1,0,1,... so two consecutive calls ALWAYS
        // produce different ramps (random noise would repeat ~50% of the
        // time and let the cache hit).
        int noise = Interlocked.Increment(ref _noiseCounter) % 2;
        var modifiedRamp = new GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        for (int i = 0; i < 256; i++)
        {
            // Never touch the peak (index 255): at UI 0% it is exactly
            // 32767 and must not be pushed to 32768 (driver rejects).
            if (i < 255)
            {
                modifiedRamp.Red[i] = (ushort)Math.Min(65535, ramp.Red[i] + noise);
                modifiedRamp.Green[i] = (ushort)Math.Min(65535, ramp.Green[i] + noise);
                modifiedRamp.Blue[i] = (ushort)Math.Min(65535, ramp.Blue[i] + noise);
            }
            else
            {
                modifiedRamp.Red[i] = ramp.Red[i];
                modifiedRamp.Green[i] = ramp.Green[i];
                modifiedRamp.Blue[i] = ramp.Blue[i];
            }
        }

        bool ok = SetDeviceGammaRamp(_handle, ref modifiedRamp);
        if (ok)
        {
            // ------------------------------------------------------------------
            // F24 诊断（2026-09-20）：用户报「偶发、无规律闪屏，但外部监控读不到 gamma 变化」。
            // F17 已挡住"写相同值也写一张新表"，所以还闪就只有两种可能：
            //   ① 仍有别的路径绕过 F17 在写屏；② **闪根本不是 gamma 写入造成的**（DWM/驱动层）。
            // 记录**每一次实际写屏**（每屏节流 500ms）⇒ 判定方法：
            //   闪的那一刻日志里**有**对应记录 ⇒ 是 gamma 写的（继续查那条路径）；
            //   闪的那一刻**没有**任何记录      ⇒ 确认与 gamma 无关，转查 DWM/显示驱动。
            // ------------------------------------------------------------------
            OpLog.LogThrottled("gamma.write." + MonitorEdidId,
                $"[gamma/write] {MonitorEdidId} R255={ramp.Red[255]} G255={ramp.Green[255]} " +
                $"B255={ramp.Blue[255]} R128={ramp.Red[128]} B128={ramp.Blue[128]} noise={noise}",
                50);
            // ⚠️ 节流窗口 2026-09-20 由 500ms 收紧到 **50ms**：原值会把"闪屏时几毫秒内的连续写"
            //    整段吞掉，导致 `02:37 写入 0 条` 这种**假阴性**（据此差点判定"与程序无关"）。
            //    ⚠️ 40fps 的过渡帧间隔约 30ms ⇒ 50ms 仍能完整保留帧序列，且不会把日志刷爆。
        }
        if (!ok)
        {
            // 2026-09-16 诊断：把被拒的 ramp 关键值打出来，定位驱动的拒绝阈值。
            OpLog.LogThrottled("gamma.writefail",
                $"[gamma] FAILED err={Marshal.GetLastWin32Error()} edid='{MonitorEdidId}' " +
                $"R[0]={modifiedRamp.Red[0]} R[255]={modifiedRamp.Red[255]} " +
                $"G[0]={modifiedRamp.Green[0]} G[255]={modifiedRamp.Green[255]} " +
                $"B[0]={modifiedRamp.Blue[0]} B[255]={modifiedRamp.Blue[255]} noise={noise}",
                500);
        }
        return ok;
    }

    /// <summary>
    /// Resets gamma to a standard 100% linear ramp.
    ///
    /// NOTE: we deliberately do NOT try to restore any ramp captured at
    /// startup. If a previous run exited abnormally (crash, forced kill),
    /// the system gamma may be stuck at a leftover value; restoring that
    /// leftover would make the screen look dimmer after every exit.
    /// A fresh linear 100% ramp is the correct "normal" state.
    /// </summary>
    public bool ResetGamma()
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        var defaultRamp = GammaRamp.CreateDefault();
        return SetDeviceGammaRamp(_handle, ref defaultRamp);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ResetGamma();
            DeleteDC(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }

    /// <summary>
    /// F26（2026-09-23）：释放 DC，但**不把 ramp 复位为原生**。
    ///
    /// ⛔ 专供「内部替换 DC 句柄」用（`GammaController.RefreshDisplays()`）：
    ///    那里只是把旧句柄换成新的，屏幕**紧接着就会被重写为用户的设定值**；
    ///    若在此期间调 <see cref="Dispose"/>，它会先把 ramp 写成**原生 100%**
    ///    ⇒ 每次拓扑事件（拔插屏 / 分辨率变化 / 系统唤醒）都会产生一次**全亮闪**。
    ///
    /// ⭐ 这就是「拔插显示器会闪几次」的**根因**（2026-09-23 01:15 用软件模拟
    ///    `WM_DISPLAYCHANGE` **不碰硬件**复现：每次事件后 ramp 都被写成
    ///    `R255=65535` 再被写回 `32767`，即"闪"）。
    ///    也解释了为什么应用日志里 `R255=65535` 出现 **0 次** —— 这条写原生路径
    ///    **完全不打日志**（`DisplayContext.ResetGamma()` 只调 `SetDeviceGammaRamp`）。
    ///
    /// ⛔ 退出路径**仍然必须**用 <see cref="Dispose"/>（带复位）—— 那是它的设计本意
    ///    （见 <see cref="ResetGamma"/> 注释：上次异常退出可能留下残值，退出时回原生才对）。
    /// </summary>
    public void DisposeKeepRamp()
    {
        if (_disposed)
            return;
        DeleteDC(_handle);
        _handle = IntPtr.Zero;
        _disposed = true;
    }
}
