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

                        // If the left-click popup is open, wheel over the tray
                        // icon adjusts the popup's slider/value directly (popup
                        // stays open, no OSD). Otherwise use the wheel OSD flow.
                        if (_popup != null && _popup.IsShown)
                        {
                            _popup.AdjustByWheel(delta);
                        }
                        else
                        {
                            _gamma.AdjustBrightness(step);
                            // OSD can be disabled via the setting (ShowOverlay).
                            if (IsOverlayEnabled?.Invoke() != false)
                            {
                                _overlay.Show(_gamma.CurrentBrightness);
                            }
                            _trayIcon.UpdateTooltip(_gamma.CurrentBrightness);
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

    public void Uninstall()
    {
        _mouseLeaveTimer?.Stop();
        if (_hookHandle != IntPtr.Zero)
        {
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
