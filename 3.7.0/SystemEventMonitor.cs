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

    /// <summary>
    /// 2026-09-19（全屏逐屏）：上一次判定为「全屏」的窗口所在显示器的 HMONITOR。
    /// 用于区分「A 屏全屏 → 前台直接切到 B 屏全屏」—— 此时 <see cref="_fullscreenState"/>
    /// 恒为 true，只比 bool 的旧判据**一个事件都不会发** ⇒ 逐屏暂停集合会永远卡在 A 屏。
    /// </summary>
    private IntPtr _fullscreenMonitor;

    /// <summary>
    /// 当前判定为「全屏」的那个**窗口句柄**（2026-09-20）。
    ///
    /// 用途：让全屏暂停**不再依赖"该窗口是否仍在前台"**。
    ///
    /// 实测问题（用户 2026-09-20）：
    ///   关闭「多屏独立控制」+ 开启「全屏暂停」，让副屏某个窗口进全屏 ⇒ 全部屏暂停 ✓；
    ///   随后鼠标移到主屏操作 ⇒ `GetForegroundWindow()` 变成主屏窗口
    ///   ⇒ `CheckFullscreenState(前台)` 判定"不是全屏" ⇒ **误判为退出全屏、解除暂停**
    ///   ⇒ 用户看到「另一块屏还可控，且拉着已全屏的那块屏一起变色温」；
    ///   再点一下全屏屏 ⇒ 又变前台 ⇒ 暂停"又生效了"。
    ///   ⇒ 三个现象同一个根因：**判据被绑在"前台"上**。
    ///
    /// 修法：只有当**该窗口真的不再全屏**（退出全屏 / 关闭 / 最小化）时才退出暂停；
    ///   前台切走不影响判定（轮询仍会检查它）。
    /// </summary>
    private IntPtr _fullscreenHwnd;

    /// <summary>
    /// "保持全屏暂停"的诊断日志**只打第一条**（2026-09-20）。
    ///
    /// 本方法由 1s 轮询驱动；按 §Z23 的教训**不能用时间节流**（每秒调用 ⇒ `3600/间隔秒`
    /// 条/小时），而"保持"是**稳态**（会一直命中）⇒ 用一次性标志最干净：
    /// 进入保持时打一条、离开时复位，稳态零输出。
    /// </summary>
    private bool _fsHeldLogged;

    /// <summary>Raised on the UI thread when the system resumes from sleep.</summary>
    public event Action? Resumed;

    /// <summary>Raised on the UI thread when the display configuration changed.</summary>
    public event Action? DisplayChanged;

    /// <summary>
    /// Raised on the UI thread when the foreground window enters fullscreen.
    /// 参数 = 该窗口所在显示器的 HMONITOR（`IntPtr.Zero` = 未能取得 ⇒ 调用方**退化为全局暂停**）。
    /// 2026-09-19 全屏逐屏：由 `Action` 改为 `Action&lt;IntPtr&gt;`，把原本被丢弃的句柄带出来
    /// （零新增 Win32 API —— `MonitorFromWindow` 本来就在 `IsWindowFullscreen` 里调过）。
    /// </summary>
    public event Action<IntPtr>? FullscreenEntered;

    /// <summary>
    /// Raised on the UI thread when the foreground window exits fullscreen.
    /// 参数恒为 `IntPtr.Zero`（退出时已无「哪块屏」可言）。
    /// </summary>
    public event Action<IntPtr>? FullscreenExited;

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
        // 2026-09-16：**本进程自己的窗口取前台时，不改变全屏状态**。
        // OSD 浮窗 / 左键弹窗 / 设置窗都是覆盖层，不是"用户的应用窗口"，让它们参与
        // 全屏判定只会造成"暂停被自己人解除"的假翻转 —— 用户实测：全屏播放时首次唤
        // OSD，屏幕短暂恢复设定值又立刻回到暂停（[fs] state=False 的 fg 正是本进程的
        // WindowsForms 窗口，且同一进程第 2 次唤 OSD 就正常）。
        // 只忽略本进程：切到别的程序（不同 pid）仍按原逻辑立即退出全屏；
        // 也没有上一版被否决的"扫描所有窗口"那种"最大化窗口误判成全屏"的风险。
        if (PidOf(hwnd) == (uint)Environment.ProcessId)
        {
            OpLog.Log($"[fs] ignore own window fg=0x{hwnd.ToInt64():X} class='{ClassNameOf(hwnd)}'");
            return;
        }

        bool fullscreen = IsWindowFullscreen(hwnd, out IntPtr monitor);

        // ------------------------------------------------------------------
        // 2026-09-20（用户实测）：**前台切走不等于退出全屏**。
        //
        // 现象：副屏某窗口全屏 ⇒ 全部屏暂停；鼠标移到主屏操作（前台变成主屏窗口）
        //   ⇒ 下一 tick 拿"前台窗口"判定得到 fullscreen=false ⇒ 误发 FullscreenExited
        //   ⇒ 解除暂停（另一块屏恢复可控、且改色温会把全屏那块屏一起写）
        //   ⇒ 用户再点一下全屏屏，前台变回去 ⇒ 暂停"又生效了"。
        //
        // ⇒ 判据里补一条：**已记录的全屏窗口只要"仍然是全屏"，就维持现状**，
        //   不论当前前台是谁。真正退出暂停的条件收紧为
        //   「该窗口退出全屏 / 被关闭 / 被最小化」。
        //
        // ⚠️ 必须放在 `IsWindowFullscreen(hwnd)` 之后：若**前台本身也是全屏窗口**
        //   （例如 A 屏全屏 → 前台直接切到 B 屏全屏），应走下面的正常流程，
        //   以便 `_fullscreenMonitor` 跟着换屏（逐屏模式依赖它）。
        // ------------------------------------------------------------------
        if (!fullscreen && _fullscreenState && _fullscreenHwnd != IntPtr.Zero)
        {
            if (IsWindowFullscreen(_fullscreenHwnd, out IntPtr keepMon))
            {
                // 仍然全屏 ⇒ 维持状态；顺便保证 monitor 与之一致（理论上不会变）
                _fullscreenMonitor = keepMon;
                // 诊断只打**第一条**（本方法每秒轮询一次；按 §Z23 的教训不能用时间节流，
                // 而"保持"是稳态 ⇒ 用一次性标志，既留证据又不刷屏）。
                if (!_fsHeldLogged)
                {
                    _fsHeldLogged = true;
                    OpLog.Log($"[fs] 前台已离开全屏窗口 0x{_fullscreenHwnd.ToInt64():X}，" +
                              $"但它**仍然全屏** ⇒ 保持全屏暂停（前台=0x{hwnd.ToInt64():X}）");
                }
                return;
            }
            // 已不再全屏（退出/关闭/最小化）⇒ 落到下面走正常的"退出"流程
        }

        // ⚠ 2026-09-19（全屏逐屏）：判据必须**带上显示器**。
        //   A 屏全屏 → 用户把前台切到 B 屏的全屏窗口时 `fullscreen` 恒为 true，
        //   只比 bool 的旧判据 ⇒ **一个事件都不发** ⇒ 逐屏暂停集合永远卡在 A 屏。
        if (fullscreen == _fullscreenState && (!fullscreen || monitor == _fullscreenMonitor)) return;

        _fullscreenState = fullscreen;
        _fullscreenMonitor = fullscreen ? monitor : IntPtr.Zero;
        _fullscreenHwnd = fullscreen ? hwnd : IntPtr.Zero;   // 记录/清除"那个全屏窗口"
        _fsHeldLogged = false;                               // 状态翻转 ⇒ 复位保持日志标志
        // 2026-09-16 取证：记录翻转瞬间的"前台"是谁 —— 用于定位"全屏暂停时按热键偶发恢复"
        // 是否由前台窗口变化（Alt 组合键激活菜单 / 全屏应用自身换窗）误触发。只记录，不改判定。
        OpLog.Log($"[fs] state={fullscreen} hmon=0x{monitor.ToInt64():X} fg=0x{hwnd.ToInt64():X} " +
                  $"class='{ClassNameOf(hwnd)}' pid={PidOf(hwnd)}");
        if (fullscreen)
            FullscreenEntered?.Invoke(monitor);
        else
            FullscreenExited?.Invoke(IntPtr.Zero);
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
    /// <param name="monitor">
    /// 2026-09-19：输出该窗口所在显示器的 HMONITOR（**零新增 API** —— 这句
    /// `MonitorFromWindow` 本来就已在此方法里调用，只是以前把结果丢弃了）。
    /// 判定为「非全屏」时可能仍是非零值，调用方只能在本方法返回 true 时使用它。
    /// </param>
    private static bool IsWindowFullscreen(IntPtr hwnd, out IntPtr monitor)
    {
        monitor = IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindowVisible(hwnd)) return false;
        // 2026-09-20：最小化窗口 `IsWindowVisible` **仍返回 true**，且其 GetWindowRect
        // 给的是"恢复后的位置"（可能仍覆盖整屏）⇒ 会被误判成"仍然全屏"。
        // 保持全屏暂停的新判据会反复检查已记录的那个窗口 ⇒ 必须排除最小化。
        if (IsIconic(hwnd)) return false;
        if (IsSystemWindow(hwnd)) return false;

        // Get the monitor the window is (mostly) on.
        monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
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
/// <summary>取证用：窗口类名（失败返回空串）。</summary>
private static string ClassNameOf(IntPtr hwnd)
{
    if (hwnd == IntPtr.Zero) return "";
    var sb = new StringBuilder(256);
    return GetClassName(hwnd, sb, sb.Capacity) == 0 ? "" : sb.ToString();
}

/// <summary>取证用：窗口所属进程 id（失败返回 0）。</summary>
private static uint PidOf(IntPtr hwnd)
{
    if (hwnd == IntPtr.Zero) return 0;
    GetWindowThreadProcessId(hwnd, out uint pid);
    return pid;
}

    /// System windows that must never be treated as fullscreen: the desktop
    /// (Progman/WorkerW), the taskbar, Start menu hosts and shell overlays.
    /// Same list as LightBulb's Window.IsSystemWindow().
    /// </summary>
    private static bool IsSystemWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0) return false;
        return IsSystemWindowClass(sb.ToString());
    }

    /// <summary>
    /// 系统窗口类名判定（与 LightBulb 同表）。**internal** 供应用白名单候选集复用
    /// （见设计稿 §16.4 规则 3）—— 避免两份类名表漂移。
    /// </summary>
    internal static bool IsSystemWindowClass(string className)
    {
        switch (className)
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

    /// <summary>
    /// 当前是否处于全屏、以及它在哪块显示器上（`IntPtr.Zero` = 未知 / 当前非全屏）。
    /// 供 MainController 在「全屏进行中才打开『按显示器生效』」时就地把全局暂停收窄为逐屏。
    /// </summary>
    public IntPtr CurrentFullscreenMonitor => _fullscreenState ? _fullscreenMonitor : IntPtr.Zero;

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

    /// <summary>窗口是否**最小化**（2026-09-20 新增）：`IsWindowVisible` 对最小化窗口仍返回 true，
    /// 且其 `GetWindowRect` 是"恢复后的位置"，可能仍覆盖整屏 ⇒ 必须显式排除，
    /// 否则"保持全屏暂停"的新判据会把最小化的全屏窗口一直当成全屏。</summary>
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // 2026-09-16 取证用（仅供 [fs] 日志，不参与任何判定）
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
