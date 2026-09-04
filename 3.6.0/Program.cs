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
            for (int i = 0; i < 10; i++)
            {
                _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out createdNew);
                if (createdNew) break;
                _mutex.Dispose();
                _mutex = null;
                Thread.Sleep(100);
            }
            if (!createdNew)
            {
                MessageBox.Show(
                    "无法启动：另一个 GammaBrightnessTool 实例仍在运行。\n请先在任务管理器结束所有 GammaBrightnessTool.exe 进程。",
                    "Gamma Brightness",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
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

        try
        {
            _controller = new MainController();
            _controller.Initialize(silent, showSettings);

            // Handle application exit
            Application.ApplicationExit += OnApplicationExit;

            // 全局显示变更兜底（托盘菜单/任何顶层窗口 DpiChanged 之外的补充信号）：
            // 改缩放、分辨率变更、显示器热插拔都会触发 WM_DISPLAYCHANGE。归口
            // RequestAutoRestart（内部 3s 冷却防连环重启）。PMv2 下 SettingsForm
            // 开着由 DpiChanged 先触发；此处保证设置窗关着（只开托盘）时也能跟随。
            // 全局显示变更（改缩放、分辨率、显示器热插拔）：置系统级变更标记并请求重启。
            // 设置窗收到 DpiChanged 后会查此标记：若为 true（重启将至）则跳过窗口内重建，
            // 避免"先原地重建一帧、紧接着进程重启"的双重跳动。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            {
                SystemScaleChangePending = true;
                OpLog.LogThrottled("sys.displaychange", "[event] DisplaySettingsChanged -> request restart");
                RequestAutoRestart();
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
    /// processes whose name starts with "GammaBrightnessTool" are touched;
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
            if (name.StartsWith("GammaBrightnessTool", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void KillExistingInstances()
    {
        var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self.Id) continue;
            string name;
            try { name = p.ProcessName; }
            catch { continue; }
            if (!name.StartsWith("GammaBrightnessTool", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { /* already exiting or access denied */ }
        }
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
            Application.Exit();
        }
        catch
        {
            // 重启失败则保持现状运行（下次触发再试）。
            _autoRestarting = false;
        }
    }

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        OpLog.Log("[exit] application exit");
        _controller?.Dispose();
        ReleaseMutex();
    }
}
