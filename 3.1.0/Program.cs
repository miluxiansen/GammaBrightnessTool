namespace GammaBrightnessTool;

/// <summary>
/// Application entry point.
/// </summary>
internal static class Program
{
    private static Mutex? _mutex;
    private static MainController? _controller;

    /// <summary>
    /// Exposes the running controller to windows like SettingsForm so they
    /// can trigger controller-level actions (e.g. language switch that keeps
    /// the in-memory settings in sync and refreshes the tray tooltip).
    /// </summary>
    public static MainController? Instance => _controller;

    [STAThread]
    static void Main(string[] args)
    {
        // Handle icon generation command
        if (args.Contains("--generate-icon"))
        {
            GenerateIcon.Run();
            return;
        }

        // Single instance check
        _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running (e.g. an older green build
            // still sitting in the tray). Silently returning would make the
            // user think the new version started while they are still
            // looking at the old instance's windows, so tell them instead.
            MessageBox.Show(
                "检测到 GammaBrightnessTool 已在运行。\n\n" +
                "请先退出旧版本（右键托盘图标 -> 退出），再启动新版本，" +
                "否则看到的仍是旧版本界面。",
                "Gamma Brightness",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // NOTE: No SetProcessDPIAware() call here. That legacy API switches
        // the process to SYSTEM DPI awareness, which OVERRIDES the manifest's
        // PerMonitorV2 declaration and pins the process to the DPI of the
        // primary monitor at startup. Under System awareness GetDpiForMonitor
        // and GetMonitorInfo return virtualized coordinates, so after the
        // user changes the display scaling (e.g. 175% -> 150%) the popup
        // sizing/positioning reads stale values and the popup lands in the
        // wrong place. The manifest (dpiAwareness=PerMonitorV2) already sets
        // the correct mode; calling the legacy API here only downgrades it.

        // Parse arguments
        bool silent = args.Contains("--silent") || args.Contains("-s");

        try
        {
            _controller = new MainController();
            _controller.Initialize(silent);

            // Handle application exit
            Application.ApplicationExit += OnApplicationExit;

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

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        _controller?.Dispose();
        ReleaseMutex();
    }
}
