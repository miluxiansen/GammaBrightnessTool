using System.Diagnostics;

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
            foreach (var suffix in new[] { "tray-sun-black-16.png", "tray-sun-white-16.png", "colortemp-ring-color-24.png", "colortemp-ring-color-256.png", "gear-black-16.png", "gear-white-24.png" })
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

        // Single instance check: green builds are timestamp-named, so the
        // process name varies between versions; the mutex is the reliable
        // "is another instance running?" signal. If one is running, kill it
        // (same tool family, safe to replace) and start fresh.
        _mutex = new Mutex(true, "GammaBrightnessTool_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            KillExistingInstances();

            // The old process's mutex handle dies with it; retry until we own it.
            _mutex?.Dispose();
            _mutex = null;
            for (int i = 0; i < 20; i++)
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
                    "无法启动：另一个 GammaBrightnessTool 实例仍在运行。",
                    "Gamma Brightness",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
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
        bool showSettings = showSettingsArg;

        try
        {
            _controller = new MainController();
            _controller.Initialize(silent, showSettings);

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
    /// Kill other GammaBrightnessTool processes. Green builds are
    /// timestamp-named (GammaBrightnessTool_3.2.0_20260810_1826.exe), so we
    /// match by process-name prefix rather than the exact name. Only
    /// processes whose name starts with "GammaBrightnessTool" are touched;
    /// anything else (including unrelated apps) is left alone.
    /// </summary>
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

    private static void OnApplicationExit(object? sender, EventArgs e)
    {
        _controller?.Dispose();
        ReleaseMutex();
    }
}
