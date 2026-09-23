using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// 环境快照（2026-09-19 新增）：启动时把「系统构建 / 各 GPU 驱动 / 逐屏 HDR·自动管理颜色」
/// 打进 ops.log 若干行（前缀 <c>[env]</c>）。
///
/// <para>
/// 为什么需要：排查跨机 ramp 差异时，日志里过去**只有应用自己的行为、没有环境上下文**
/// ⇒ 拿到一份用户的日志也无法判断成因。本项目已实测：同一支判别器 LUT
/// （32768/32768/32000）在 RTX 4070 被整份拒收、在笔记本 GTX 1650 Ti 被接受；
/// 而 **HDR / 自动管理颜色会改变 gamma 管线**，是"同机同驱动却表现不同"的头号嫌疑。
/// </para>
///
/// <para>
/// 零成本：正式版 <see cref="OpLog"/> 是空实现 ⇒ 本类只在带日志的构建里产生输出；
/// 且只在启动时调用一次。
/// </para>
/// </summary>
internal static class DisplayEnv
{
    // ⚠️ QDC_ONLY_ACTIVE_PATHS 的真实值是 2（1 = QDC_ALL_PATHS）。
    //    本文件**故意不复用** NativeMethods.QDC_ONLY_ACTIVE_PATHS（其值为 1，语义是"全部路径"）；
    //    既有代码两处调用都用同一个常量 ⇒ 自洽可用，故不去改动它，新代码用正确值。
    private const int QDC_ONLY_ACTIVE = 2;

    private const int GET_TARGET_NAME = 2;
    private const int GET_ADAPTER_NAME = 4;
    private const int GET_ADVANCED_COLOR_INFO = 9;
    // ⚠️ 是 **11**，不是 12（12 是 GET_MONITOR_SPECIALIZATION）。
    //    实测：type 11 → SDRWhiteLevel=1000（= 80 nits，标准 SDR 参考白）；
    //    type 12 → 返回的是另一结构体，值无意义（曾误读为 542/6）。
    private const int GET_SDR_WHITE_LEVEL = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct ADVANCED_COLOR_INFO
    {
        public NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;                 // bit0 支持 / bit1 启用 / bit2 wideColorEnforced / bit3 forceDisabled
        // ⛔ 误删恢复（2026-09-23 15:59 死代码清理审计）：colorEncoding 是**互操作布局字段**
        //    （原生 DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO 的第 4 个成员，UINT32）。
        //    死代码批把它当"无引用字段"删了 ⇒ Marshal.SizeOf 从 24 变 20 ⇒
        //    Query<ADVANCED_COLOR_INFO> 的 header.size 与原生期望不符，
        //    DisplayConfigGetDeviceInfo 按精确 size 校验会整表拒绝
        //    ⇒ [env] 环境快照的 advancedColor / SDRWhiteLevel 采集失效；
        //    即使个别系统接受小 size，也会把 colorEncoding 槽位误读成 bitsPerColorChannel。
        //    本字段虽在托管代码中"无读取点"，但它占据的是**原生内存布局槽位** ——
        //    与清理批次自定的 B 类原则（互操作结构体字段=布局契约，不可删）一致，必须保留。
        public int colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDR_WHITE_LEVEL
    {
        public NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ADAPTER_NAME
    {
        public NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string adapterDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr info);

    /// <summary>采集并写日志。任何一段失败都只留一条痕迹，绝不影响启动。</summary>
    public static void Log()
    {
        try { LogOs(); }
        catch (Exception ex) { OpLog.Log($"[env] OS 采集失败（不影响运行）：{ex.GetType().Name}: {ex.Message}"); }

        try { LogGpus(); }
        catch (Exception ex) { OpLog.Log($"[env] GPU 采集失败（不影响运行）：{ex.GetType().Name}: {ex.Message}"); }

        try { LogDisplays(); }
        catch (Exception ex) { OpLog.Log($"[env] 显示/HDR 采集失败（不影响运行）：{ex.GetType().Name}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ OS
    private static void LogOs()
    {
        string product = "?", display = "?", build = "?", ubr = "?", edition = "?";
        using (RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            if (k != null)
            {
                product = k.GetValue("ProductName") as string ?? "?";
                display = k.GetValue("DisplayVersion") as string ?? "?";
                build = k.GetValue("CurrentBuild") as string ?? "?";
                edition = k.GetValue("EditionID") as string ?? "?";
                object? u = k.GetValue("UBR");
                ubr = u == null ? "?" : Convert.ToString(u, System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            }
        }
        // ⚠️ Win11 上 `ProductName` **仍写着 "Windows 10 ..."**（MS 从未更新该值）
        //    ⇒ 以**构建号**为准纠正：>= 22000 = Windows 11，否则 Windows 10。
        //    本机实证：ProductName="Windows 10 Pro for Workstations"，而 build=26200 实为 Win11 25H2。
        string osName = product;
        if (int.TryParse(build, out int b))
            osName = (b >= 22000 ? "Windows 11" : "Windows 10") + " (注册表 ProductName=\"" + product + "\")";
        OpLog.Log($"[env] OS={osName} ver={display} build={build}.{ubr} edition={edition} " +
                  $"{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
    }

    // ----------------------------------------------------------------- GPU
    private const string DisplayClassGuid = @"{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static void LogGpus()
    {
        int n = 0;
        using (RegistryKey? cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + DisplayClassGuid))
        {
            if (cls == null) return;
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;   // 只取 0000/0001/...
                using RegistryKey? g = cls.OpenSubKey(sub);
                if (g == null) continue;
                string desc = g.GetValue("DriverDesc") as string ?? "";
                if (desc.Length == 0) continue;                                // 空槽位跳过
                string ver = g.GetValue("DriverVersion") as string ?? "?";
                string date = g.GetValue("DriverDate") as string ?? "?";
                OpLog.Log($"[env] GPU[{n}] {desc}  driver={ver}{ExternalNvidiaVersion(ver)} date={date}");
                n++;
            }
        }
        if (n == 0) OpLog.Log("[env] GPU：注册表未取到（非致命）");
    }

    /// <summary>
    /// NVIDIA 的 DriverVersion 是**内部号**（形如 <c>32.0.15.9636</c>），对外版本取末 5 位
    /// ⇒ <c>596.36</c>。非 NVIDIA/取不出时返回空串。
    /// </summary>
    private static string ExternalNvidiaVersion(string ver)
    {
        var digits = new StringBuilder();
        foreach (char c in ver)
            if (char.IsDigit(c)) digits.Append(c);
        if (digits.Length < 5) return "";
        string tail = digits.ToString();
        tail = tail.Substring(tail.Length - 5);
        return $"（对外 {tail.Substring(0, 3)}.{tail.Substring(3)}）";
    }

    // ------------------------------------------------------------- 显示/HDR
    private static void LogDisplays()
    {
        if (NativeMethods.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE, out uint numPaths, out uint numModes) != 0)
        {
            OpLog.Log("[env] DisplayConfig：GetDisplayConfigBufferSizes 失败");
            return;
        }
        if (numPaths == 0) return;

        var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[numPaths];
        // ⚠️ modeInfoArray **不能传 NULL**（numModes>0 时会返回 122 且**不填充** pathInfoArray）——
        //    踩过：表现为"所有名字都取不到"，极易误判成 API 不可用。
        IntPtr modes = Marshal.AllocHGlobal((int)numModes * 96);   // DISPLAYCONFIG_MODE_INFO 实际 64B，给富余
        try
        {
            uint n = numPaths, m = numModes;
            if (NativeMethods.QueryDisplayConfig(QDC_ONLY_ACTIVE, ref n, paths, ref m, modes, IntPtr.Zero) != 0)
            {
                OpLog.Log("[env] DisplayConfig：QueryDisplayConfig 失败");
                return;
            }

            for (int i = 0; i < n; i++)
            {
                var tgt = paths[i].targetInfo;
                string name = FriendlyName(tgt) ?? "(取不到)";
                var ac = Query<ADVANCED_COLOR_INFO>(GET_ADVANCED_COLOR_INFO, tgt);
                var sw = Query<SDR_WHITE_LEVEL>(GET_SDR_WHITE_LEVEL, tgt);
                string adapter = AdapterVendor(tgt);

                string acTxt = ac == null
                    ? "advancedColor=查询失败"
                    : $"advancedColor 支持={Flag(ac.Value.value, 0)} 启用={Flag(ac.Value.value, 1)} " +
                      $"wideEnforced={Flag(ac.Value.value, 2)} bpc={ac.Value.bitsPerColorChannel}";

                string swTxt = sw == null ? "" : $" SDRWhiteLevel={sw.Value.SDRWhiteLevel}";
                string pathTxt = paths[i].sourceInfo.id == 0 ? "" : $" srcId={paths[i].sourceInfo.id}";
                OpLog.Log($"[env] DISPLAY[{i}] {name}  tgtId={tgt.id}{pathTxt} dev={adapter} {acTxt}{swTxt}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(modes);
        }
    }

    private static string Flag(uint value, int bit) => ((value >> bit) & 1) != 0 ? "True" : "False";

    /// <summary>取显示器友好名（复用既有 GET_TARGET_NAME 声明）。失败返回 null。</summary>
    private static string? FriendlyName(NativeMethods.DISPLAYCONFIG_PATH_TARGET_INFO tgt)
    {
        var name = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = GET_TARGET_NAME,
                size = Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = tgt.adapterId,
                id = tgt.id
            }
        };
        if (NativeMethods.DisplayConfigGetDeviceInfo(ref name) != 0) return null;
        string s = name.monitorFriendlyDeviceName;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// 从适配器设备路径里取 PCI 厂商，判定该屏**挂在哪个 GPU**（这是跨 GPU 场景的关键信息，
    /// 例如 <c>VEN_10DE</c>=NVIDIA、<c>VEN_8086</c>=Intel、<c>VEN_1002</c>=AMD）。
    /// </summary>
    private static string AdapterVendor(NativeMethods.DISPLAYCONFIG_PATH_TARGET_INFO tgt)
    {
        var an = Query<ADAPTER_NAME>(GET_ADAPTER_NAME, tgt);
        if (an == null) return "(未知)";
        string p = an.Value.adapterDevicePath ?? "";
        int i = p.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > p.Length) return "(非 PCI)";
        string ven = p.Substring(i, 8).ToUpperInvariant();
        return ven switch
        {
            "VEN_10DE" => ven + "=NVIDIA",
            "VEN_8086" => ven + "=Intel",
            "VEN_1002" => ven + "=AMD",
            "VEN_1414" => ven + "=Microsoft",
            _ => ven
        };
    }

    /// <summary>
    /// 按 <paramref name="type"/> 查询设备信息。用 IntPtr 版调用以便复用于多种结构体。
    /// 失败返回 null（**不抛**，环境采集失败绝不影响启动）。
    /// </summary>
    private static T? Query<T>(int type, NativeMethods.DISPLAYCONFIG_PATH_TARGET_INFO tgt) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            // ⚠️ 必须**先清零**：驱动对不支持的 type 可能**返回 0 却不写入**，
            //    AllocHGlobal 出来的是**未初始化内存** ⇒ 会把垃圾当成结果读到
            //    （实测：未清零时把 SDR 白点读成 542，清零并改用正确 type 后为 1000）。
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            var hdr = default(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER);
            hdr.type = type;
            hdr.size = size;
            hdr.adapterId = tgt.adapterId;
            hdr.id = tgt.id;
            Marshal.StructureToPtr(hdr, p, false);
            if (DisplayConfigGetDeviceInfo(p) != 0) return null;
            return Marshal.PtrToStructure<T>(p);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}
