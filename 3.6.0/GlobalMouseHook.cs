using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Global low-level mouse hook that ONLY intercepts wheel events
/// when the cursor is confirmed to be over our tray icon.
/// Uses TrayIconManager's mouse tracking state.
/// </summary>
public sealed class GlobalMouseHook : IDisposable
{
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookHandle;
    private readonly TrayIconManager _trayIcon;
    private readonly GammaController _gamma;
    private readonly BrightnessOverlay _overlay;
    private BrightnessPopup? _popup;

    /// <summary>
    /// Resolves the effective wheel direction (true = inverted, up-wheel
    /// dims). Injected by the controller so the setting UI can flip it at
    /// runtime without rebuilding the hook.
    /// </summary>
    public Func<bool>? IsInvertedScroll { get; set; }

    /// <summary>
    /// Resolves whether the wheel OSD overlay should be shown (false = only
    /// adjust brightness, no OSD popup). Injected by the controller.
    /// </summary>
    public Func<bool>? IsOverlayEnabled { get; set; }

    /// <summary>
    /// Resolves whether wheel brightness adjustment is enabled at all
    /// (false = the wheel over the tray icon does nothing, hotkeys still
    /// work). Injected by the controller.
    /// </summary>
    public Func<bool>? IsWheelEnabled { get; set; }

    /// <summary>
    /// Resolves whether color temperature adjustment is enabled, so the
    /// wheel path's tooltip hides the temperature value when it is off
    /// (matching every other UpdateTooltip call site). Injected by the
    /// controller.
    /// </summary>
    public Func<bool>? IsColorTemperatureEnabled { get; set; }

    /// <summary>
    /// 全屏暂停中：true 时滚轮亮度调节被完全忽略（不显示 OSD 浮窗，
    /// 事件仍被吞掉防止传给其他应用）。Injected by the controller.
    /// </summary>
    public Func<bool>? IsPaused { get; set; }

    /// <summary>
    /// 用户通过滚轮手动调节亮度时触发（用于暂停时间调整调度）。
    /// Injected by the controller.
    /// </summary>
    public Action? OnUserAdjustment { get; set; }

    /// <summary>
    /// 3.6.0: 滚轮 OSD 显示入口（独立模式多屏分发由 MainController 注入）。
    /// </summary>
    public Action? ShowOverlay { get; set; }

    // Brightness throttling
    private long _lastBrightnessUpdate;   // Environment.TickCount64 (monotonic)
    private readonly TimeSpan _brightnessThrottle = TimeSpan.FromMilliseconds(50);

    // Mouse leave detection timer
    private readonly System.Windows.Forms.Timer _mouseLeaveTimer;

    public GlobalMouseHook(TrayIconManager trayIcon, GammaController gamma, BrightnessOverlay overlay)
    {
        _trayIcon = trayIcon;
        _gamma = gamma;
        _overlay = overlay;

        _mouseProc = MouseHookCallback;

        // Timer to detect when mouse leaves the icon area
        _mouseLeaveTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _mouseLeaveTimer.Tick += (s, e) => _trayIcon.CheckMouseLeave();
    }

    /// <summary>
    /// Sets the persistent brightness popup that must be dismissed before
    /// showing the wheel OSD (mutual exclusion between popup and OSD).
    /// </summary>
    public void SetPopup(BrightnessPopup popup)
    {
        _popup = popup;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        IntPtr moduleHandle = GetModuleHandle(null!);
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to install mouse hook. Error: {Marshal.GetLastWin32Error()}");
        }

        _mouseLeaveTimer.Start();
        OpLog.Log("[hook] global mouse hook installed (WH_MOUSE_LL)");
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Left/right click anywhere: if the popup is visible and the
            // click lands OUTSIDE the popup, dismiss it (same behavior as
            // the right-click context menu: click-away closes).
            if (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN)
            {
                if (_popup != null && _popup.IsShown)
                {
                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var hwndUnder = WindowFromPoint(hookStruct.pt);
                    var root = GetAncestor(hwndUnder, GA_ROOT);
                    if (root != _popup.Handle)
                    {
                        _popup.Dismiss();
                    }
                }
            }
            // Wheel: only respond when the cursor is geometrically over our
            // tray icon. Uses IsMouseOverIconNow (real-time hit-test on the
            // current icon rect with a short cache) instead of the
            // IsMouseOverIcon state machine: the state machine is reset on
            // DPI change and only restored by a WM_MOUSEMOVE, so wheel
            // handling would stay dead until the user moves the mouse.
            else if (wParam == (IntPtr)WM_MOUSEWHEEL)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var cursorPos = new Point(hookStruct.pt.x, hookStruct.pt.y);

                // CRITICAL: Only respond if the cursor is over our icon
                if (_trayIcon.IsMouseOverIconNow(cursorPos))
                {
                    // 全屏暂停中：完全吞掉滚轮事件（不调节、不显示 OSD）。
                    if (IsPaused?.Invoke() == true)
                    {
                        return (IntPtr)1;
                    }

                    // If the wheel brightness switch is off, swallow the
                    // event (keep it from other apps) but do nothing.
                    if (IsWheelEnabled?.Invoke() != false)
                    {
                        // Throttle brightness updates (TickCount64 is monotonic
                        // and immune to system clock changes, unlike DateTime.Now).
                        if (Environment.TickCount64 - _lastBrightnessUpdate > _brightnessThrottle.TotalMilliseconds)
                        {
                            _lastBrightnessUpdate = Environment.TickCount64;

                            int delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);

                            // Wheel direction: invert when the user asked for it
                            // (up-wheel dims instead of brightens).
                            if (IsInvertedScroll?.Invoke() == true)
                            {
                                delta = -delta;
                            }

                            float step = delta > 0 ? _gamma.StepSize : -_gamma.StepSize;

                            bool popupShown = _popup != null && _popup.IsShown;
                            OpLog.LogThrottled("wheel",
                                $"[wheel] delta={delta} step={step:0.00} popupShown={popupShown} " +
                                $"brightness={_gamma.CurrentBrightness * 100:0.#}% temp={_gamma.CurrentTemperature:0}K",
                                200);

                            // If the left-click popup is open, wheel over the tray
                            // icon adjusts the popup's slider/value directly (popup
                            // stays open, no OSD). Otherwise use the wheel OSD flow.
                            Action work;
                            if (popupShown)
                            {
                                int wheelDelta = delta;
                                work = () => _popup.AdjustByWheel(wheelDelta);
                            }
                            else
                            {
                                float adjStep = step;
                                work = () =>
                                {
                                    _gamma.AdjustBrightness(adjStep);
                                    OnUserAdjustment?.Invoke();
                                    // OSD can be disabled via the setting (ShowOverlay).
                                    if (IsOverlayEnabled?.Invoke() != false)
                                    {
                                        ShowOverlay?.Invoke();
                                    }
                                    _trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature,
                                        IsColorTemperatureEnabled?.Invoke() ?? false);
                                };
                            }
                            RunOnUiThread(work);
                        }
                    }

                    // Block the event from propagating to other apps
                    return (IntPtr)1;
                }
            }
        }

        // Always pass through to next hook for non-icon areas
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// 把重活（gamma 写屏、OSD 显示、tooltip 刷新）排到 UI 线程消息队列尾部。
    /// 低层钩子回调虽运行在安装线程的消息泵上，但若在回调内同步执行这些操作，
    /// 会长时间占住钩子调用（Windows 对低层钩子有超时，超时会被静默卸载——
    /// 表现为托盘滚轮突然失灵）。消息泵可用时用 BeginInvoke 异步执行，
    /// 不可用（OSD 窗体尚未建句柄/退出竞态）则退化为同步执行。
    /// </summary>
    private void RunOnUiThread(Action work)
    {
        bool queued = false;
        try
        {
            if (_overlay.IsHandleCreated)
            {
                _overlay.BeginInvoke(work);
                queued = true;
            }
        }
        catch
        {
            // 退出竞态：句柄查询/投递失败时退化到同步执行（等价于旧行为）。
        }
        if (!queued) work();
    }

    public void Uninstall()
    {
        _mouseLeaveTimer?.Stop();
        if (_hookHandle != IntPtr.Zero)
        {
            OpLog.Log("[hook] global mouse hook uninstalled");
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Uninstall();
        _mouseLeaveTimer?.Dispose();
    }
}
