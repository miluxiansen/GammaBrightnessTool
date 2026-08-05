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

    // Brightness throttling
    private DateTime _lastBrightnessUpdate = DateTime.MinValue;
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
        // Only process mouse wheel events
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
        {
            // CRITICAL: Only respond if mouse is confirmed over our icon
            if (_trayIcon.IsMouseOverIcon)
            {
                // Throttle brightness updates
                if (DateTime.Now - _lastBrightnessUpdate > _brightnessThrottle)
                {
                    _lastBrightnessUpdate = DateTime.Now;

                    var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                    float step = delta > 0 ? GammaController.DEFAULT_STEP : -GammaController.DEFAULT_STEP;

                    _gamma.AdjustBrightness(step);
                    _overlay.Show(_gamma.CurrentBrightness);
                    _trayIcon.UpdateTooltip(_gamma.CurrentBrightness);
                }

                // Block the event from propagating to other apps
                return (IntPtr)1;
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
