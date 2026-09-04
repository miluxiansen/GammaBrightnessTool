using System.Runtime.InteropServices;
using System.Text;

namespace GammaBrightnessTool;

/// <summary>
/// Listens for system events that can invalidate the applied gamma ramp:
///   - WM_POWERBROADCAST / PBT_APMRESUMEAUTOMATIC (system resume from sleep)
///   - WM_DISPLAYCHANGE (monitor hot-plug, resolution/refresh change)
/// and for fullscreen transitions of the foreground window (games/video),
/// so the gamma can be paused while a fullscreen app owns the screen and
/// restored when it exits.
///
/// Fullscreen detection follows LightBulb's approach: the foreground
/// window's CLIENT rect (borderless) must fully cover the monitor's
/// ENTIRE bounds (rcMonitor, not the working area), and system windows
/// (desktop, taskbar, shell overlays) are excluded by class name.
///
/// Detection triggers are twofold:
///   1. EVENT_SYSTEM_FOREGROUND hook — instant response when the user
///      switches to/from a fullscreen window via taskbar/Alt-Tab/click.
///   2. A 1-second polling timer — catches in-place fullscreen toggles
///      (F11 in browsers, a player's fullscreen button) where the
///      foreground window does NOT change, so the WinEvent hook never
///      fires. (LightBulb also uses 1-second polling for its gamma
///      freshness check.)
///
/// The monitor is a hidden message-only window (NativeWindow) created on the
/// UI thread, so callbacks arrive on the UI thread and can touch WinForms
/// controls / gamma state directly.
/// </summary>
public sealed class SystemEventMonitor : IDisposable
{
    // WM_POWERBROADCAST sub-events
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007; // unused, documented

    // Display change
    private const int WM_DISPLAYCHANGE = 0x007E;

    // WinEvent hook for foreground-window changes
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Fullscreen polling interval: catches F11-style in-place toggles that
    // never change the foreground window (same cadence as LightBulb's
    // gamma freshness poll).
    private static readonly TimeSpan FullscreenPollInterval = TimeSpan.FromSeconds(1);

    private sealed class MessageWindow : NativeWindow
    {
        private readonly SystemEventMonitor _owner;
        public MessageWindow(SystemEventMonitor owner) { _owner = owner; }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_POWERBROADCAST:
                    if ((int)m.WParam == PBT_APMRESUMEAUTOMATIC)
                    {
                        _owner.OnResume();
                    }
                    break;
                case WM_DISPLAYCHANGE:
                    _owner.OnDisplayChange();
                    break;
            }
            base.WndProc(ref m);
        }
    }

    private MessageWindow? _window;
    private IntPtr _foregroundHook = IntPtr.Zero;
    // CRITICAL: the WinEvent callback delegate MUST be kept alive in a field
    // for the lifetime of the hook. SetWinEventHook P/Invokes marshal the
    // method group into a delegate, but nothing else references it; once the
    // GC collects it, any foreground-window event invokes a dead delegate and
    // the runtime calls Environment.FailFast ("callback was made on a garbage
    // collected delegate"). This field pins the delegate for the hook's life.
    private WinEventDelegate? _foregroundHookDelegate;
    private System.Windows.Forms.Timer? _fullscreenTimer;
    private System.Windows.Forms.Timer? _resumeDelayTimer;
    private bool _fullscreenState;

    /// <summary>Raised on the UI thread when the system resumes from sleep.</summary>
    public event Action? Resumed;

    /// <summary>Raised on the UI thread when the display configuration changed.</summary>
    public event Action? DisplayChanged;

    /// <summary>Raised on the UI thread when the foreground window enters fullscreen.</summary>
    public event Action? FullscreenEntered;

    /// <summary>Raised on the UI thread when the foreground window exits fullscreen.</summary>
    public event Action? FullscreenExited;

    public void Initialize()
    {
        if (_window != null) return;

        _window = new MessageWindow(this);
        _window.CreateHandle(new CreateParams
        {
            Caption = "GammaBrightnessTool.SystemEventMonitor",
            Style = unchecked((int)0x80000000) // WS_POPUP
        });

        // Foreground-window hook: fires on UI thread (OutOfContext uses the
        // calling thread's message pump, which is the main UI thread).
        // The delegate must be stored in a field (_foregroundHookDelegate) to
        // keep it alive; otherwise the GC collects it and any event crashes
        // the process with FailFast.
        _foregroundHookDelegate = ForegroundChangedCallback;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Polling fallback: catches F11-style fullscreen toggles where the
        // foreground window does not change (WinEvent hook would never fire).
        _fullscreenTimer = new System.Windows.Forms.Timer { Interval = (int)FullscreenPollInterval.TotalMilliseconds };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreenState(GetForegroundWindow());
        _fullscreenTimer.Start();
    }

    private void OnResume()
    {
        // Defer slightly: right after resume the display stack may not be
        // ready yet. A short delay lets the driver settle before we replay.
        // （旧实现注释声称延迟但立即 Invoke；这里用一次性 Timer 落 400ms。）
        _resumeDelayTimer?.Stop();
        _resumeDelayTimer?.Dispose();
        _resumeDelayTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _resumeDelayTimer.Tick += (_, _) =>
        {
            _resumeDelayTimer?.Stop();
            _resumeDelayTimer?.Dispose();
            _resumeDelayTimer = null;
            Resumed?.Invoke();
        };
        _resumeDelayTimer.Start();
        OpLog.Log("[sys] resume received, re-raise delayed 400ms");
    }

    private void OnDisplayChange()
    {
        OpLog.Log("[sys] display change (WM_DISPLAYCHANGE)");
        DisplayChanged?.Invoke();
    }

    private void ForegroundChangedCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only react to foreground changes for top-level windows.
        if (idObject != 0 /* OBJID_WINDOW */) return;

        CheckFullscreenState(hwnd);
    }

    /// <summary>
    /// Evaluates the given window's fullscreen state and raises
    /// FullscreenEntered / FullscreenExited on transitions.
    /// </summary>
    private void CheckFullscreenState(IntPtr hwnd)
    {
        bool fullscreen = IsWindowFullscreen(hwnd);
        if (fullscreen == _fullscreenState) return;

        _fullscreenState = fullscreen;
        if (fullscreen)
            FullscreenEntered?.Invoke();
        else
            FullscreenExited?.Invoke();
    }

    /// <summary>
    /// Detects whether the given top-level window is fullscreen, using the
    /// same rules as LightBulb:
    ///   - window must be visible and NOT a system window (desktop, taskbar,
    ///     shell overlays — checked by class name);
    ///   - the window's absolute CLIENT rect (borders excluded) must fully
    ///     cover the monitor's ENTIRE bounds (rcMonitor).
    /// The taskbar is intentionally NOT excluded (rcMonitor, not rcWork), so
    /// a true fullscreen window that extends under the taskbar is detected,
    /// while a normal maximized window (whose client area stops at the
    /// taskbar) is not.
    /// </summary>
    private static bool IsWindowFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindowVisible(hwnd)) return false;
        if (IsSystemWindow(hwnd)) return false;

        // Get the monitor the window is (mostly) on.
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        if (!GetWindowRect(hwnd, out RECT windowRect)) return false;
        if (!GetClientRect(hwnd, out RECT clientRect)) return false;

        // Absolute client rect (client coords are relative to the window).
        var absClient = new RECT
        {
            Left = windowRect.Left + clientRect.Left,
            Top = windowRect.Top + clientRect.Top,
            Right = windowRect.Left + clientRect.Right,
            Bottom = windowRect.Top + clientRect.Bottom
        };

        // Full coverage of the entire monitor bounds (rcMonitor).
        RECT mon = mi.rcMonitor;
        return absClient.Left <= mon.Left
            && absClient.Top <= mon.Top
            && absClient.Right >= mon.Right
            && absClient.Bottom >= mon.Bottom;
    }

    /// <summary>
    /// System windows that must never be treated as fullscreen: the desktop
    /// (Progman/WorkerW), the taskbar, Start menu hosts and shell overlays.
    /// Same list as LightBulb's Window.IsSystemWindow().
    /// </summary>
    private static bool IsSystemWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0) return false;

        switch (sb.ToString())
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "ImmersiveLauncher":
            case "ImmersiveSwitchList":
            case "MultitaskingViewFrame":
            case "ForegroundStaging":
            case "ApplicationManager_DesktopShellWindow":
            case "XamlExplorerHostIslandWindow":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Re-checks the current foreground window immediately (used at startup
    /// and when the pause setting is toggled on, so the state matches
    /// reality without waiting for the next foreground change or poll tick).
    /// </summary>
    public void RefreshFullscreenState()
    {
        CheckFullscreenState(GetForegroundWindow());
    }

    public void Dispose()
    {
        _fullscreenTimer?.Stop();
        _fullscreenTimer?.Dispose();
        _fullscreenTimer = null;
        _resumeDelayTimer?.Stop();
        _resumeDelayTimer?.Dispose();
        _resumeDelayTimer = null;

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _foregroundHookDelegate = null;
        _window?.DestroyHandle();
        _window = null;
    }

    // ---- P/Invoke ----

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
