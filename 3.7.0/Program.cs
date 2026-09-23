using System.Diagnostics;

namespace GammaBrightnessTool;

/// <summary>
/// Application entry point.
/// </summary>
internal static class Program
{
    private static Mutex? _mutex;
    private static MainController? _controller;
    private static System.Windows.Forms.Timer? _selfTestTimer;

    /// <summary>
    /// Exposes the running controller to windows like SettingsForm so they
    /// can trigger controller-level actions (e.g. language switch that keeps
    /// the in-memory settings in sync and refreshes the tray tooltip).
    /// </summary>
    public static MainController? Instance => _controller;

    /// <summary>
    /// 全局崩溃兜底（只记录、不改行为）：任何 UI 线程/未处理异常都会先写
    /// %TEMP%\GammaBrightnessTool_crash.log（时间+进程+完整异常栈），然后再走
    /// 原有退出路径——不吞异常、不改变程序生命周期，只为崩溃留下可定位证据。
    /// </summary>
    private static void InstallGlobalCrashLogging()
    {
        // UI 线程未处理异常：WinForms 路由到 ThreadException。记录后立即退出
        // （Application.Exit），保持"事件内未处理异常=程序退出"的既有语义；
        // 不调用则消息循环可能继续在脆弱状态下运行。
        Application.ThreadException += (_, e) =>
        {
            LogCrash(e.Exception);
            Application.Exit();
        };
        // 非 UI 线程 / 致命未处理异常：进程即将终止，仅记录。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown"));
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "GammaBrightnessTool_crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] PID={Environment.ProcessId}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}---{Environment.NewLine}");
        }
        catch
        {
            // 记录失败（磁盘满/权限等）不影响程序本身
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Handle icon generation command
        if (args.Contains("--generate-icon"))
        {
            GenerateIcon.Run();
            return;
        }

        // Export the original tray sun glyph as PNG for reuse in the popup
        if (args.Contains("--export-tray-icon"))
        {
            GenerateIcon.ExportTrayIconPng();
            return;
        }

        // Diagnostic: verify embedded resources load from the single-file
        // build, then exit (read-only, no side effects).
        if (args.Contains("--check-resources"))
        {
            int ok = 0;
            foreach (var suffix in new[] { "tray-sun-black-16.png", "tray-sun-white-16.png", "colortemp-ring-color-24.png", "colortemp-ring-color-256.png", "colortemp-ring-black-16.png", "colortemp-ring-white-16.png", "gear-black-16.png", "gear-white-24.png", "黑色未置顶.png", "黑色已置顶.png", "白色未置顶.png", "白色已置顶.png" })
            {
                var n = typeof(IconGenerator).Assembly.GetManifestResourceNames()
                    .FirstOrDefault(x => x.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
                if (n != null) { ok++; Console.WriteLine("OK   " + suffix); }
                else { Console.WriteLine("MISS " + suffix); }
            }
            try
            {
                using var icon = IconGenerator.CreateMultiSizeTrayIcon();
                Console.WriteLine("OK   tray icon created (" + icon.Width + "x" + icon.Height + ")");
                ok++;
            }
            catch (Exception ex) { Console.WriteLine("FAIL tray icon: " + ex.Message); }
            Console.WriteLine("check-resources done, " + ok + " checks");
            return;
        }

        // ---- `--reset-gamma`：把各屏 gamma ramp 复位为原生 100% 线性 -------------
        //
        // 为什么需要它（2026-09-16 用户实测报告「亮度与色温不随软件卸载而停止生效」）：
        //   gamma ramp 是**系统级持久状态**，写入后即使进程结束也依然生效。
        //   程序**正常退出**会走 ApplicationExit → Dispose → ResetGamma（屏幕回原生）；
        //   但安装包的 `PrepareToInstall` / `InitializeUninstall` 用的是
        //   `taskkill /f`（**强杀**）→ 不触发 ApplicationExit → ResetGamma 从不执行，
        //   于是**卸载后屏幕仍停在最后一次写入的亮度/色温上**。
        //   （同理：任务管理器结束进程、崩溃退出，都会留下残留。）
        //
        // 本入口**独立于运行中的实例**：不需要互斥、不碰配置、不建 UI，
        // 只打开各屏 DC 写一次原生 ramp 就退出。安装包在 taskkill **之后**调用它，
        // 此时已无实例会再改 ramp（否则自愈定时器 2s 内会把它改回去）。
        //
        // 手动手工复位（用户侧应急）：
        //   "GammaBrightnessTool.exe" --reset-gamma
        if (args.Contains("--reset-gamma"))
        {
            int n = 0, total = 0;
            foreach (var m in Monitor.GetAll())
            {
                total++;
                using var dc = m.TryCreateDeviceContext();
                if (dc != null && dc.ResetGamma()) n++;
            }
            OpLog.Log($"[cli] --reset-gamma: {n}/{total} display(s) reset to native ramp");
            Console.WriteLine($"reset-gamma: {n}/{total}");
            return;
        }

        // ---- P5 / L2：`--auto <command>` --------------------------------------
        // 必须在单实例检查（下面的 HasRunningInstances/KillExistingInstances 与
        // Mutex）**之前**处理。若走正常启动路径，第二个实例会杀掉正在运行的实例并
        // 自己接管 → "驱动已打开的设置窗"完全做不到，测出的状态也全错。
        // 这里只连接既有实例的命名管道转发命令，连不上就明确报错：不杀、不接管。
        int autoIdx = Array.IndexOf(args, "--auto");
        if (autoIdx >= 0)
        {
            if (autoIdx + 1 >= args.Length)
            {
                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
                Console.Error.WriteLine(
                    "usage: GammaBrightnessTool.exe --auto <command> [--allow-destructive] [--out <file>]");
                Environment.Exit(2);
            }
            Environment.Exit(AutomationCli.Run(
                args[autoIdx + 1],
                args.Contains("--allow-destructive"),
                GetArgValue(args, "--out")));
        }

        // Show-settings flag is handled after the single-instance check below.
        bool showSettingsArg = args.Contains("--show-settings");

        // 自动化自测模式：只读/可逆检查（见 SelfTest.cs）。
        // 此模式下绝不 Kill 任何进程（含同族实例）；互斥被占则放弃并提示。
        bool selfTest = args.Contains("--selftest");

        // 加速冷启动：完整杀旧 + 互斥获取，仅在确有同族进程运行时执行。
        // 多数冷启动（开机自启/手动双击）此刻并无其他实例，跳过进程枚举直接拿互斥。
        if (!selfTest && HasRunningInstances())
        {
            KillExistingInstances();
            Thread.Sleep(300); // 给 WaitForExit 后的句柄/互斥彻底释放一点时间
        }

        // Single instance check: green builds are timestamp-named, so the
        // process name varies between versions; the mutex is the reliable
        // "is another instance running?" signal. If one is running, kill it
        // (same tool family, safe to replace) and start fresh.
        _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out bool createdNew);
        if (createdNew)
        {
            // 快路径：成功创建互斥 = 无其他实例在跑，直接继续初始化（冷启动主路径）。
        }
        else
        {
            // 互斥被另一实例持有（便携场景竞态窗口：旧版进程已退出但互斥句柄尚未
            // 释放；或极少数僵尸进程）。杀旧后重试获取，仍失败则短暂重试后放弃。
            if (selfTest)
            {
                // 自测模式红线：不触碰任何进程、不 Kill。等待占用方（用户主动
                // 退出）释放互斥，最长 30s；超时则放弃并提示。
                OpLog.Log("[selftest] waiting for mutex release (existing instance must be closed by user) ...");
                _mutex?.Dispose();
                _mutex = null;
                for (int i = 0; i < 60; i++)
                {
                    _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out createdNew);
                    if (createdNew) break;
                    _mutex.Dispose();
                    _mutex = null;
                    Thread.Sleep(500);
                }
                if (!createdNew)
                {
                    Console.WriteLine(
                        "Selftest: another GammaBrightnessTool instance is running.\n" +
                        "Close it first, then run again. No processes were touched.");
                    OpLog.Log("[selftest] abort: mutex not released within 30s (no kill performed)");
                    return;
                }
            }
            KillExistingInstances();
            Thread.Sleep(300);
            _mutex?.Dispose();
            _mutex = null;
            // ------------------------------------------------------------------
            // F15（2026-09-19）：用户报「启动/重启软件时，任务栏出现一个往上升的黑色图标
            // 又立刻被打断消失」。实测定位 = **下面那个 `MessageBox`**（标题 "Gamma Brightness"
            // 在项目里唯一），它在任务栏占一个按钮、深色主题下是黑的，且**只存活 62ms**：
            //   ① 新实例 `KillExistingInstances()` 杀掉旧实例后，旧实例的退出清理
            //      （gamma 复位 / 写盘 / Dispose）尚未完成 ⇒ Mutex 未释放；
            //   ② 原重试预算 10×100ms=1s 不够 ⇒ `createdNew=false` ⇒ 弹**模态框**并阻塞；
            //   ③ 用户若再点一次（或紧随的另一个实例启动）⇒ 它的 `KillExistingInstances()`
            //      把**正卡在这个框上的进程**也杀掉 ⇒ 框"一闪即灭"。
            // 三重修法：
            //   a) 重试预算 1s → **5s**（正常时序根本走不到失败分支）；
            //   b) **不再弹模态框** —— 真"有实例在跑"时，用户本就不该看到第二个实例的提示，
            //      而且模态框会阻塞 + 被下一个实例杀掉，观感最差；
            //   c) 保留诊断日志（正式版 OpLog 空实现，零开销）。
            // ------------------------------------------------------------------
            for (int i = 0; i < 50; i++)
            {
                _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out createdNew);
                if (createdNew)
                {
                    if (i > 0) OpLog.Log($"[singleton] mutex 获取成功（重试 {i} 次后）");
                    break;
                }
                _mutex.Dispose();
                _mutex = null;
                Thread.Sleep(100);
            }
            if (!createdNew)
            {
                OpLog.Log("[singleton] CONFLICT：KillExistingInstances 已等待退出 + 5s 重试仍 createdNew=false " +
                          "⇒ 静默退出（不再弹模态提示框，见 F15）");
                return;
            }
        }

        // DPI 模式：PerMonitorV2。本应用是手工 DPI 布局（AutoScaleMode.None +
        // 冻结 _dpiScale + pt 字体），PMv2 会把已创建控件渲染上下文切到所在屏物理
        // DPI 造成错乱——因此 DPI/缩放变更【不】做原地重建，而是重启整个进程
        // （RequestAutoRestart）：新进程按当前系统 DPI 创建全部 UI（设置窗/托盘
        // 菜单/弹窗），与"重启软件后正常"一致并自动化。PMv2 的价值 = 每个窗口都会
        // 收到可靠的 WM_DPICHANGED，作为"缩放变了"的触发信号。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 全局崩溃兜底：任何未处理异常先落盘 %TEMP%\GammaBrightnessTool_crash.log
        // 再走原退出路径（不吞异常、不改变生命周期，仅留崩溃证据）。
        InstallGlobalCrashLogging();

        // Parse arguments
        bool silent = args.Contains("--silent") || args.Contains("-s");
        bool showSettings = showSettingsArg;
        OpLog.Log($"[start] pid={Environment.ProcessId} ver={Application.ProductVersion} " +
                  $"silent={silent} showSettings={showSettings} args=[{string.Join(",", args)}]");

        // 环境快照（2026-09-19）：系统构建 / 各 GPU 驱动 / 逐屏 HDR·自动管理颜色。
        // 为什么开机就打：跨机 ramp 差异的排查过去**缺环境上下文** —— 拿到一份日志也无法判断成因
        // （实测：同一判别器 LUT 在 RTX 4070 被拒、GTX 1650 Ti 被接受；HDR/自动管理颜色会改变
        //   gamma 管线，是头号嫌疑）。正式版 OpLog 为空实现 ⇒ 零开销。
        DisplayEnv.Log();

        try
        {
            _controller = new MainController();
            _controller.Initialize(silent, showSettings);
            // F20：记下**启动时各显示器**的有效 DPI 作为基准（之后用它判断"是否真的改了缩放"）。
            // ⛔ 2026-09-22 回归修复：原判据 `Graphics.FromHwnd(IntPtr.Zero).DpiX` 在进程内
            //   恒定不变（屏幕 DC 被 GDI 按进程缓存）⇒ 改缩放永不重启。详见 GetMonitorDpiSnapshot 注释。
            _startupMonitorDpi = GetMonitorDpiSnapshot();

            // Handle application exit
            Application.ApplicationExit += OnApplicationExit;

            // 全局显示变更兜底（托盘菜单/任何顶层窗口 DpiChanged 之外的补充信号）：
            // 改缩放、分辨率变更、显示器热插拔都会触发 WM_DISPLAYCHANGE。归口
            // RequestAutoRestart（内部 3s 冷却防连环重启）。PMv2 下 SettingsForm
            // 开着由 DpiChanged 先触发；此处保证设置窗关着（只开托盘）时也能跟随。
            // 全局显示变更（改缩放、分辨率、显示器热插拔）：置系统级变更标记并请求重启。
            // 设置窗收到 DpiChanged 后会查此标记：若为 true（重启将至）则跳过窗口内重建，
            // 避免"先原地重建一帧、紧接着进程重启"的双重跳动。
            // ------------------------------------------------------------------
            // F20（2026-09-20）：**区分「改缩放」与「拔插屏」**。
            //
            // 用户实测（01:24）：「关掉显示器瞬间闪一下，恢复正常色温后又迅速变回暖色」。
            // 日志铁证（01:22:16~26）：
            //   [display] 拓扑：1 个输出            ← 拔屏
            //   [restart] auto restart triggered    ← 原实现一律走这里（整进程自杀重启）
            //   [gamma/apply] uni … curB=100 curT=6600   ← 新进程先写原生
            //   … Solar 直到 **9 秒后** 才写回 85%/3900K  ⇒ 就是那次"闪"
            //
            // 拔插屏根本不需要重启进程（`RefreshDisplays()` 足够），**只有 DPI/缩放变化**
            // 才必须重启（手工 DPI 布局无法原地切换）。⇒ 用"系统 DPI 是否改变"来分流。
            // ⚠️ 2026-09-22 回归修复：判据必须**真的能随缩放变化** —— 原用屏幕 DC 的
            //    `Graphics.FromHwnd(IntPtr.Zero).DpiX`（进程内冻结在启动值）⇒ 判据恒 false。
            //    现改为「逐屏实时快照（GetDpiForMonitor）vs 启动基准」。
            // ------------------------------------------------------------------
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            {
                // 【A/B 自证探针】同一时刻打印两种读数：若"旧(屏幕DC)"仍等于启动值、
                // 而"新(逐屏 GetDpiForMonitor)"已变 ⇒ 直接证实回归成因与修复有效性。
                // 只写日志、不参与判断。
                OpLog.Log($"[dpi-probe] 旧(屏幕DC)={GetLegacyScreenDcDpi()} " +
                          $"新(逐屏)={DescribeDpi(GetMonitorDpiSnapshot())} " +
                          $"基准={DescribeDpi(_startupMonitorDpi)}");

                // 「改缩放」与「插拔屏」**分开判断**（两类需要的动作正好相反）。
                var kind = ClassifyDisplayChange(out string detail);
                if (kind == DisplayChangeKind.ScaleChanged)
                {
                    SystemScaleChangePending = true;
                    OpLog.Log($"[event] DisplaySettingsChanged {detail} **DPI 变化** -> request restart");
                    RequestAutoRestart();
                    return;
                }

                // 插拔屏 / 改分辨率 / 无实质变化 ⇒ 只刷新，不重启（F20 初衷）
                OpLog.Log($"[event] DisplaySettingsChanged {detail} -> 仅刷新显示器列表（不重启）");
                _controller?.HandleTopologyChangedWithoutRestart();
            };

            // 自动化自测：等主控制器就绪后延迟执行，完成即退出（exit code=失败数>0）
            if (selfTest)
            {
                _selfTestTimer = new System.Windows.Forms.Timer { Interval = 1200 };
                _selfTestTimer.Tick += (_, _) =>
                {
                    _selfTestTimer.Stop();
                    _selfTestTimer.Dispose();
                    _selfTestTimer = null;
                    int failures = SelfTest.RunAll();
                    OpLog.Log($"[selftest] exit code = {(failures > 0 ? 1 : 0)}");
                    Environment.ExitCode = failures > 0 ? 1 : 0;
                    Application.Exit();
                };
                _selfTestTimer.Start();
            }

            // Run message loop
            Application.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"程序启动失败: {ex.Message}",
                "Gamma Brightness - 错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Kill other GammaBrightnessTool processes. Green builds are
    /// timestamp-named (GammaBrightnessTool_3.2.0_20260810_1826.exe), so we
    /// match by process-name prefix rather than the exact name. Only
    /// processes whose name starts with "GammaBrightnessTool" and is NOT an
    /// installer/uninstaller are touched（判定统一走 IsSiblingInstance）;
    /// anything else (including unrelated apps) is left alone.
    /// </summary>
    /// 快速探测是否存在同族进程（进程名以 GammaBrightnessTool 开头，不含自身）。
    /// 冷启动加速：仅在确有旧实例时执行完整的"杀旧 + 等待"流程，避免每次启动
    /// 都枚举全部进程。安装版/自启固定名与旧版并存的场景由本方法覆盖。
    /// </summary>
    private static bool HasRunningInstances()
    {
        var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self.Id) continue;
            string name;
            try { name = p.ProcessName; }
            catch { continue; }
            if (IsSiblingInstance(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取 "--flag value" 形式的参数值；flag 缺失或后面没有值时返回 null。
    /// </summary>
    private static string? GetArgValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// 判断某进程名是否属于「本工具的另一个实例」（可被安全地替换 / 杀掉）。
    ///
    /// ⛔⛔ 为什么不能只按前缀匹配（2026-09-22 实测事故）：
    ///   安装包的进程名**也以产品名开头** —— Inno Setup 会把 setup 自解压成
    ///   `GammaBrightnessTool_Setup_<版本>_<时间戳>.tmp` 并在**该子进程**里跑向导，
    ///   引导进程则是 `GammaBrightnessTool_Setup_<版本>_<时间戳>.exe`。
    ///   裸前缀匹配会把它们当成「旧实例」杀掉 ⇒ **正在运行的安装包被程序反杀**
    ///   （用户看到「点 Install 后安装包立马闪退」）。
    ///   ⇒ 名字里含 Setup / unins / Installer 的一律**不是**同族实例，绝不触碰。
    ///
    /// 反过来：绿色版文件名形如 `GammaBrightnessTool_<版本>_<时间戳>[_log].exe`
    /// （前缀 + 下划线，不含上述关键字）⇒ 仍然会被正常识别为同族并替换。
    /// </summary>
    private static bool IsSiblingInstance(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!name.StartsWith("GammaBrightnessTool", StringComparison.OrdinalIgnoreCase))
            return false;

        // 安装器 / 卸载器：不是「另一个实例」，杀掉 = 打断安装 —— 一律不动。
        string[] notSiblings = { "Setup", "unins", "Installer" };
        foreach (var s in notSiblings)
        {
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        return true;
    }

    private static void KillExistingInstances()
    {
        var self = Process.GetCurrentProcess();
        int killed = 0;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self.Id) continue;
            string name;
            try { name = p.ProcessName; }
            catch { continue; }
            if (!IsSiblingInstance(name)) continue;
            try
            {
                // 诊断（2026-09-19）：把"杀了谁、有没有等到它真的退出"打全 ——
                // 静态字段 `_lastTopoDesc` 那种"只记结果不记过程"的日志没法定位时序竞争。
                OpLog.Log($"[singleton] kill pid={p.Id} name={name}");
                p.Kill();
                bool exited = p.WaitForExit(3000);
                OpLog.Log($"[singleton] pid={p.Id} WaitForExit(3000)={exited}");
                if (exited) killed++;
            }
            catch (Exception ex) { OpLog.Log($"[singleton] kill pid={p.Id} 失败（忽略）: {ex.Message}"); }
        }
        OpLog.Log($"[singleton] KillExistingInstances 完成：成功等待退出 {killed} 个");
    }

    /// <summary>
    /// Release the single-instance mutex before restarting,
    /// so the new process can acquire it immediately.
    /// </summary>
    public static void ReleaseMutex()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch { /* Mutex may already be released */ }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static bool _autoRestarting;
    private static DateTime _lastAutoRestartUtc = DateTime.MinValue;

    /// <summary>F20（2026-09-20；2026-09-22 回归修复）：**启动时**各显示器的有效 DPI 快照
    /// （设备名 → DPI）。用于区分"改缩放"（必须重启）与"拔插屏/改分辨率"（只需刷新显示器列表）。
    ///
    /// ⛔ 为什么不能用 `Graphics.FromHwnd(IntPtr.Zero).DpiX`（原实现，实测否决）：
    ///   屏幕 DC 由 GDI **按进程缓存**，其 LOGPIXELSX 在进程生命周期内**恒定不变** —— 冻结在
    ///   进程启动时的系统 DPI。实测（2026-09-22 GBT_dpi.log 与 ops 日志时序交织，13 组数据全吻合）：
    ///     pid=14616 启动于 125% ⇒ 此后 3 次事件全读 120（同期窗口 DpiChanged 已到 168）；
    ///     pid=4256  启动于 175% ⇒ 9 分钟内 4 次事件全读 168（同期窗口已到 216 / 144）。
    ///   ⇒ 判据恒为 false ⇒ `RequestAutoRestart()` 被永久抑制 ⇒ **改缩放不再重启进程**
    ///     （3.6.0 无此判据一律重启，故无此问题；这是 3.7.0 独有回归）。
    ///   现改用 `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`（shcore，实时；与 `Monitor.GetAll()` /
    ///   设置页"显示器信息"的缩放比**同源**，故该页能实时跟随时这里也能）。
    /// </summary>
    private static Dictionary<string, int> _startupMonitorDpi =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>F20：当前各显示器的有效 DPI 快照。**实时**查询，失败返回已收集到的部分。
    /// 键优先用 **EDID** —— `\\.\DISPLAYn` 编号会随拔插变化（实测 2 → 52 → 1）；
    /// EDID 缺失、或与已有键碰撞（同 EDID 多路：一条直连 + 一条经 KVM/欺骗器）时回退用设备名。</summary>
    private static Dictionary<string, int> GetMonitorDpiSnapshot()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var m in Monitor.GetAll())
            {
                if (m.DpiX <= 0) continue;
                // 键选择：EDID 优先（型号级，跨拔插/编号变化稳定）；读不到或与已有键碰撞时回退设备名。
                // ⚠️ 回退会让"同一物理状态因枚举顺序不同映射成不同键集"（已知限制 D1/D2），
                //    且 EDID 瞬时空读会导致键形态在 EdidId↔DeviceName 间翻转（T2）——
                //    两者都**就地打显式告警**，便于立刻发现该场景已出现
                //    （现有 `[display] ⚠ 同 EDID 多路` 带去重逻辑，此处更易漏）。
                string key = m.EdidId;
                if (key.Length == 0)
                {
                    OpLog.Log($"[dpi-key] ⚠ EDID 读为空 ⇒ 回退设备名 {m.DeviceName}" +
                              "（本帧键形态可能与基准不一致；见 F20影响评估_20260922.md 已知限制 T2）");
                    key = m.DeviceName;
                }
                else if (map.ContainsKey(key))
                {
                    OpLog.Log($"[dpi-key] ⚠ EDID 键碰撞（{ShortKeyForLog(key)} 已存在，另一路 {m.DeviceName}）" +
                              " ⇒ 回退设备名（已知限制 D1/D2；见 F20影响评估_20260922.md）");
                    key = m.DeviceName;
                }
                map[key] = m.DpiX;
            }
        }
        catch { /* 枚举失败：用已收集到的部分（判据对空表返回 None，即宁可不重启） */ }
        return map;
    }

    /// <summary>F20（2026-09-22 二改）：显示变更的**分类结果** —— 「插拔屏」与「改缩放」分开判断。</summary>
    internal enum DisplayChangeKind
    {
        /// <summary>无实质变化（或快照不可用）⇒ 不重启。</summary>
        None,

        /// <summary>只有拓扑变化（拔插屏 / 改分辨率）⇒ 只刷新，**不重启**（F20 初衷）。</summary>
        TopologyOnly,

        /// <summary>有屏幕的 DPI/缩放真的变了 ⇒ **必须重启**（手工 DPI 布局无法原地切换）。</summary>
        ScaleChanged,
    }

    /// <summary>F20（2026-09-22 二改）：把「显示变更」分成两类**分开判断**。
    ///
    /// 为什么必须分开：这两件事需要**完全相反**的动作 ——
    ///   * 拔插屏：**绝不能重启**。重启会让新进程先写原生 ramp，Solar 最多要 9 秒才写回，
    ///     用户看到的就是「关掉显示器瞬间闪一下」（2026-09-20 01:24 实测）。
    ///   * 改缩放：**必须重启**。本应用手工 DPI 布局，运行中无法让已创建控件切到新 DPI。
    /// 原实现用一个混合判据（且会在事件路径改写基准）⇒ 两者互相污染 ⇒ 改缩放被永久抑制。
    ///
    /// 结果与动作：
    ///   ScaleChanged  ⇒ 重启进程（**基准不改写**：事后再查必须仍判 ScaleChanged）
    ///   TopologyOnly  ⇒ 只刷新显示器列表 + **把新快照收编为新基准**
    ///   None          ⇒ 只刷新（窗口跨屏等无实质变化的广播）
    ///
    /// 「收编为新基准」解决两个盲区：
    ///   ① 拔插屏后 `\\.\DISPLAYn` 编号会变（实测 2 → 52 → 1）⇒ 新编号不在基准里，
    ///      紧接着在其上改缩放会**漏判**；收编后即可正常检出。
    ///   ② 启动时枚举失败（基准为空）⇒ 原实现会让判据**永久失效**；现在首帧自动补建。
    /// </summary>
    private static DisplayChangeKind ClassifyDisplayChange(out string detail)
    {
        var now = GetMonitorDpiSnapshot();
        if (now.Count == 0)
        {
            detail = "快照为空（枚举失败）";
            return DisplayChangeKind.None;   // 查不到 ⇒ 宁可不重启（避免打断 Solar 恢复流程）
        }

        if (_startupMonitorDpi.Count == 0)
        {
            _startupMonitorDpi = now;        // 惰性补建：否则判据永久失效
            detail = "基准为空 ⇒ 已用当前快照补建 " + DescribeDpi(now);
            return DisplayChangeKind.None;
        }

        // ① 「改缩放」：同一块屏（键相同）在两份快照里都存在、且 DPI 值变了。
        var scaled = new List<string>();
        foreach (var kv in now)
        {
            if (_startupMonitorDpi.TryGetValue(kv.Key, out int old) && old != kv.Value)
                scaled.Add($"{ShortKeyForLog(kv.Key)}:{old}->{kv.Value}");
        }
        if (scaled.Count > 0)
        {
            // ⛔ 此处**绝不**改写基准 —— 否则紧随的 RequestAutoRestart 再查会得到 None ⇒ 重启被吃掉。
            detail = "dpi " + string.Join(" , ", scaled);
            return DisplayChangeKind.ScaleChanged;
        }

        // ② 「插拔屏 / 改分辨率」：键集合变了（某块屏消失或新增）。
        if (!SameKeySet(now, _startupMonitorDpi))
        {
            string was = DescribeDpi(_startupMonitorDpi);
            _startupMonitorDpi = now;        // 收编：编号变化后仍能检出后续改缩放
            detail = $"拓扑变化 {was} -> {DescribeDpi(now)}（已收编为新基准）";
            return DisplayChangeKind.TopologyOnly;
        }

        detail = "无实质变化 " + DescribeDpi(now);
        return DisplayChangeKind.None;
    }

    /// <summary>F20：两份逐屏 DPI 快照的键集合是否相同（即拓扑是否一致）。</summary>
    private static bool SameKeySet(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var k in a.Keys)
        {
            if (!b.ContainsKey(k)) return false;
        }
        return true;
    }

    /// <summary>F20：把快照键压成**日志用短标识**，避免 EDID 实例路径（约 54 字符）把日志行撑爆。
    /// `MONITOR\SAC2466\{…}` ⇒ 取型号段 `SAC2466`；`\\.\DISPLAY1` 原样返回（本就短）；其他取末 8 字符。</summary>
    private static string ShortKeyForLog(string key)
    {
        if (key.StartsWith("\\\\.\\DISPLAY", StringComparison.OrdinalIgnoreCase)) return key;
        int a = key.IndexOf('\\');
        if (a >= 0)
        {
            int b = key.IndexOf('\\', a + 1);
            if (b > a + 1) return key.Substring(a + 1, b - a - 1);   // 型号段，如 SAC2466
        }
        return key.Length <= 12 ? key : key.Substring(key.Length - 8);
    }

    /// <summary>F20：把逐屏 DPI 快照压成一行日志（设备名按序，只为稳定可比）。</summary>
    private static string DescribeDpi(Dictionary<string, int> map)
    {
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder("dpi=");
        if (keys.Count == 0) return sb.Append("(未知)").ToString();
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append('+');
            sb.Append(map[keys[i]]);
        }
        return sb.ToString();
    }

    /// <summary>【仅诊断，非判据】旧判据的读数：**屏幕 DC** 的 DPI。
    /// ⛔ 已知缺陷（2026-09-22 实测否决）：GDI 按**进程**缓存屏幕 DC，其 LOGPIXELSX 在
    ///   进程生命周期内恒定不变（冻结在启动值）⇒ 曾导致改缩放永不重启。
    ///   现只用于 `[dpi-probe]` 日志对照，**不得**再参与任何逻辑判断。
    ///   待用户下一轮测试确认后即可删除本方法。</summary>
    private static int GetLegacyScreenDcDpi()
    {
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(g.DpiX);
        }
        catch { return -1; }
    }

    /// <summary>系统级显示变更已发生（改缩放等，进程重启将至）。窗口据此跳过无谓的原地重建。</summary>
    public static bool SystemScaleChangePending { get; private set; }

    /// <summary>
    /// 系统缩放/DPI 变更后重启整个进程。本应用手工 DPI 布局，运行中无法让已创建
    /// 控件切换到新 DPI（SystemAware 冻结在启动值、PMv2 原地重建错乱）——唯一可靠
    /// 方案是"重启"（用户验证：重启后一切正常）：新进程按当前系统 DPI 重新创建
    /// 设置窗、托盘菜单、弹窗、OSD，全部自动跟随。PMv2 下各窗口 DpiChanged 触发
    /// 此方法；DisplaySettingsChanged 作全局兜底（托盘菜单等无窗体场景）。
    /// </summary>
    public static void RequestAutoRestart()
    {
        // ------------------------------------------------------------------
        // F20b（2026-09-20）：**统一把关 —— 只有「系统 DPI 真的变了」才重启。**
        //
        // 用户实测（01:36/01:37 日志）：修了 `DisplaySettingsChanged` 的判据后，
        //   `[event] … 拓扑变化 -> 仅刷新（不重启）` 已生效，**但紧接着仍有**
        //   `[restart] auto restart triggered` ⇒ 说明还有**第二条**调用路径。
        //   定位到候选：`SettingsForm._dpiDebounce`（窗口 `DpiChanged` 延迟触发）等
        //   —— 拔插屏时窗口会被系统移到另一块屏，**窗口 DPI 变了但系统 DPI 没变**，
        //   照样调到这里 ⇒ 重启 ⇒ 新进程写原生 ⇒ Solar 9 秒后才写回暖色 ⇒ 闪。
        //
        // 把判据收到本方法内：所有调用点自动获得保护，不必逐个改。
        // ⚠️ 真正的"改缩放"仍会重启 —— 判据与基准必须在事件路径上保持**只读**
        //    （2026-09-22 回归修复：原实现在此既改写基准，又用了一个进程内恒定的读数 ⇒ 恒 false）。
        // ------------------------------------------------------------------
        var kind = ClassifyDisplayChange(out string detail);
        if (kind != DisplayChangeKind.ScaleChanged)
        {
            // 抑制重启 ⇒ 撤销"重启将至"标记：否则 SettingsForm 会一直跳过原地重建，
            // 而进程又没重启 ⇒ 设置窗卡在旧布局既不重建也不重启（QA 发现的 TOCTOU）。
            // ⛔ 只在**确定不重启**的分支复位；真正要重启时（ScaleChanged）绝不触碰。
            SystemScaleChangePending = false;
            OpLog.Log($"[restart] **抑制重启**：非改缩放（{detail}）" +
                      " ⇒ 只刷新显示器列表（拔插屏 / 窗口跨屏 / 无实质变化）");
            _controller?.HandleTopologyChangedWithoutRestart();
            return;
        }

        // 防连环重启：系统在改缩放后会广播多轮事件；每轮首事件即重启并冷却 3s。
        if (_autoRestarting) return;
        if ((DateTime.UtcNow - _lastAutoRestartUtc).TotalMilliseconds < 3000) return;
        _autoRestarting = true;
        _lastAutoRestartUtc = DateTime.UtcNow;
        OpLog.Log("[restart] auto restart triggered (dpi/display change)");
        try
        {
            // 设置窗打开时：先把"当前页 + 滚动位置"落盘，重启后 SettingsForm 自动恢复
            // （见 SettingsForm.SaveStateForRestart / TryRestoreRestartState）。
            bool openSettings = SettingsForm.IsOpen;
            if (openSettings) SettingsForm.SaveStateForRestart();
            string args = openSettings ? "--show-settings" : "";
            ReleaseMutex();
            var psi = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = args,
                UseShellExecute = true
            };
            Process.Start(psi);

            // ------------------------------------------------------------------
            // F24-a（2026-09-22 23:55）：**交接期持续重申 ramp —— 不再立刻退出。**
            //
            // 依据（用户毫秒级 ramp 监控 × 应用日志交叉验证，`_devtools/B_prime实施报告_20260922.md` §12）：
            //   · OS 改缩放后把 ramp **复位为原生**（监控 `41.784` 那一下**日志无任何写屏** ⇒ 外部来源），
            //     且会**反复**复位；
            //   · 新进程要到 `42.330` 才首次写屏 ⇒ 中间 **581 ms 硬停在全亮**。
            //   · 那 581 ms 里旧进程还活着（原本 82 ms 后才 Exit）⇒ 完全可以每 100 ms 重申一次设定值。
            //   ⛔ 实测"退出前只写一次"（原 F23②）**无效**：写入落在 OS 重配进行中被覆盖。
            //      必须**周期性**重申 —— 任意一次落在重配之后即修正。
            //
            // ⛔ 有界：`HandoverExitMs` 后无条件自退，避免新进程启动失败时留下幽灵进程。
            // ⛔ 重申走 `ReapplyAllDisplaysForTopologyChange()`（尊重暂停/停用 + 禁用被动学习）。
            // 正常情况下新进程 ~0.45 s 就用 singleton 逻辑 kill 掉本进程，3 s 上限基本走不到。
            // ------------------------------------------------------------------
            if (openSettings) SettingsForm.HideForRestart();
            StartHandoverKeepAlive();
        }
        catch
        {
            // 重启失败则保持现状运行（下次触发再试）。
            _autoRestarting = false;
        }
    }

    /// <summary>F24-a：交接期重申间隔。</summary>
    private const int HandoverReassertMs = 100;

    /// <summary>F24-a：交接期上限 —— 到点无条件自退（防幽灵进程）。</summary>
    private const int HandoverExitMs = 3000;

    private static System.Windows.Forms.Timer? _handoverTimer;
    private static System.Windows.Forms.Timer? _handoverExitTimer;

    /// <summary>
    /// F24-a（2026-09-22 23:55）：自动重启的**交接期保活**。
    ///
    /// 本进程已拉起新进程，但**暂不退出**：每 <see cref="HandoverReassertMs"/> ms 重申一次目标 ramp，
    /// 覆盖"OS 复位 ramp → 新进程首次写屏"之间的空窗（实测 581 ms）。
    /// 新进程启动后会用自己的 singleton 逻辑 kill 本进程（实测 ~0.45 s），届时交接自然结束；
    /// 若新进程没能起来，<see cref="HandoverExitMs"/> 到点也会自退，不留幽灵。
    /// ⛔ 两个定时器都是 UI 线程定时器，必须在有消息循环的情况下工作（本方法在重启流程内、循环仍在）。
    /// </summary>
    private static void StartHandoverKeepAlive()
    {
        _handoverTimer = new System.Windows.Forms.Timer { Interval = HandoverReassertMs };
        _handoverTimer.Tick += (_, _) => _controller?.ReapplyGammaBeforeRestart();
        _handoverTimer.Start();
        // 立刻先写一次，不用等第一个 tick
        _controller?.ReapplyGammaBeforeRestart();

        _handoverExitTimer = new System.Windows.Forms.Timer { Interval = HandoverExitMs };
        _handoverExitTimer.Tick += (_, _) =>
        {
            OpLog.Log($"[restart] 交接期达上限 {HandoverExitMs} ms（新进程未接管）⇒ 旧进程自退");
            _handoverExitTimer?.Stop();
            _handoverTimer?.Stop();
            Application.Exit();
        };
        _handoverExitTimer.Start();
    }

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        OpLog.Log("[exit] application exit");
        _controller?.Dispose();
        ReleaseMutex();
    }
}
