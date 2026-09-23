namespace GammaBrightnessTool;

/// <summary>
/// Application entry point.
/// </summary>
internal static class Program
{
    private static Mutex? _mutex;
    private static MainController? _controller;

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
            // Another instance is running - exit silently
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
