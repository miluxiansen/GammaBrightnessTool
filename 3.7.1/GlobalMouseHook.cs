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
    ///
    /// 参数 = **要显示的亮度（0..1）**；传 <c>null</c> = "沿用界面显示值
    /// <c>UiBrightness</c>"（含暂停期显示屏幕实际生效值的语义）。
    ///
    /// ⛔ 2026-10-01（契约 IV-1）：本路径的写入是**延迟 24ms 合帧**的
    ///    （`popup.AdjustByWheel` ⇒ `QueueAdjust`），所以**必须**传"刚算出的值"
    ///    （`popup.TrackedBrightness`）。**传 null 会让 OSD 回读 `_gamma` 而滞后一格**
    ///    —— 这正是"下滚却变亮"的成因。只有"暂停期只读展示"的分支才允许传 null。
    /// </summary>
    public Action<float?>? ShowOverlay { get; set; }

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
                    // 2026-09-16 修正：暂停期间**不再整个吞掉**滚轮 —— OSD 是查看入口，通用设置
                    // 里开了就该能唤出来看"屏幕现在到底是什么值"。改值仍然冻结（见下方 work 分支）。

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
                            // ⛔ 不节流（intervalMs=0）：2026-10-01 更正一个错误诊断 ——
                            //   本行原为 200ms 节流，而调节节流只有 50ms（下方 _brightnessThrottle），
                            //   导致 200ms 内最多 4 次真实滚轮事件只落 1 行日志 ⇒ 曾据此误判出
                            //   "一格却写两次 ⇒ 回环"（**不存在**）。判据所依赖的日志被节流 ⇒ 判据无效。
                            //   现改为 1 格 = 1 行，使"滚轮格数 ↔ 写屏次数"可对账。
                            //   滚轮是人类步速事件，无刷屏风险。
                            OpLog.LogThrottled("wheel",
                                $"[wheel] delta={delta} step={step:0.00} popupShown={popupShown} " +
                                $"brightness={_gamma.CurrentBrightness * 100:0.#}% temp={_gamma.CurrentTemperature:0}K",
                                0);

                            // If the left-click popup is open, wheel over the tray
                            // icon adjusts the popup's slider/value directly (popup
                            // stays open, no OSD). Otherwise use the wheel OSD flow.
                            // ⛔ 三条分支 = 三条**时序不同**的写入通道，见
                            Action work;
                            if (popupShown)
                            {
                            // ===== [W2] 托盘滚轮·弹窗已开 → 转 W5 · **延迟 24ms** 写入通道 =====
                            //   写屏: popup.AdjustByWheel(delta) ⇒ OnBrightnessChanged
                            //         ⇒ MainController.OnPopupBrightnessChanged ⇒ QueueAdjust("popupB")
                            //         ⇒ FlushAdjusts（AdjustFlushMs=24ms）后才 SetBrightness
                            //   落盘: QueueAdjust 的 apply 里 SaveSettings()
                            //   UI  : 弹窗自绘滑轨 + 广播 BrightnessChanged
                            //   ⛔ 本通道**不可**在调用后立刻回读 _gamma.CurrentBrightness（滞后一格）；
                            //      需显示值请读弹窗自身追踪值。
                            // =====================================================================
                            // popupShown 已保证非空；先捕获到局部变量，避免 lambda 内
                            // 引用可空字段导致 CS8602（不改变运行时行为）。
                            var popup = _popup!;
                            int wheelDelta = delta;
                            work = () => popup.AdjustByWheel(wheelDelta);
                            }
                            else if (IsPaused?.Invoke() == true)
                            {
                            // ===== [W0] 托盘滚轮·暂停中 · **只读**通道（不写屏）=====
                            //   写屏: 无（有意不调 OnUserAdjustment，避免误触发 Solar 手动接管）
                            //   UI  : 仅 ShowOverlay 展示"屏幕实际生效值"
                            // =========================================================
                            // 2026-09-16：暂停期间滚轮**只唤出 OSD**，不调节、不触发 OnUserAdjustment
                            // （后者会把 Solar 停掉并标记手动接管，暂停期间属于误触发）。
                            work = () =>
                            {
                            // 暂停期：只读展示"屏幕实际生效值" ⇒ 传 null（维持原语义）。
                            if (IsOverlayEnabled?.Invoke() != false)
                            {
                            ShowOverlay?.Invoke(null);
                            }
                            };
                            }
                            else
                            {
                            // ===== [W1] 托盘滚轮·弹窗未开 · **复用左键弹窗机制** =====
                            //      而是弹窗的三条原则：
                            //        P1 显示源 = 自跟踪值（**绝不回读 `_gamma`**）
                            //        P2 一格只发一次（`UpdateBrightness` 同值早退）
                            //        P3 入站只有静默同步一条路（`PrepareTrayWheelBrightness`
                            //           → `SyncFromGamma`，不发事件 ⇒ 无回环）
                            //   ⛔ 上一轮失败的教训：只把写入换成 `popup.AdjustByWheel`（延迟 24ms），
                            //      却让 OSD 仍旧读 `UiBrightness`（回读 `_gamma`）⇒ 写出上一格旧值
                            //      ⇒ 用户见"下滚却变亮"。**搬调用 ≠ 搬机制。**
                            //   ⛔ 若 `_popup` 尚未就绪（Install 与 SetPopup 之间的窗口），
                            //      退化为旧的同步直写路径，避免 NRE。
                            // =========================================================
                            var popup = _popup;
                            int wheelDelta = delta;
                            if (popup != null)
                            {
                            work = () =>
                            {
                            // ① 静默对齐自跟踪值（纯内存，不写屏不发事件；`_mode` 不动）
                            popup.PrepareTrayWheel(
                            _gamma.CurrentBrightness, _gamma.CurrentTemperature);
                            // ② 用弹窗的同一套算术算出新值 —— **亮度轴**，且不改 `_mode`
                            //    （避免污染弹窗下次打开时的模式记忆；见该方法注释）
                            popup.AdjustBrightnessByWheel(wheelDelta);
                            OnUserAdjustment?.Invoke();
                            // ③ ⭐ OSD 显示"刚算出的值" —— **不回读 _gamma**（P1）
                            if (IsOverlayEnabled?.Invoke() != false)
                            {
                            ShowOverlay?.Invoke(popup.TrackedBrightness);
                            }
                            // ④ tooltip 同样用自跟踪值，避免它比 OSD 慢半拍（同源同值）
                            _trayIcon.UpdateTooltip(popup.TrackedBrightness, popup.TrackedTemperatureK,
                            IsColorTemperatureEnabled?.Invoke() ?? false);
                            };
                            }
                            else
                            {
                            float adjStep = step;
                            work = () =>
                            {
                            _gamma.AdjustBrightness(adjStep);
                            OnUserAdjustment?.Invoke();
                            if (IsOverlayEnabled?.Invoke() != false)
                            {
                            ShowOverlay?.Invoke(null);
                            }
                            _trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature,
                            IsColorTemperatureEnabled?.Invoke() ?? false);
                            };
                            }
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
