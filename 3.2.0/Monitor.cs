using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Represents a display monitor and provides gamma control capabilities.
/// Extracted and simplified from LightBulb's implementation.
/// </summary>
public sealed class Monitor : IDisposable
{
    private string _deviceName;
    private bool _isPrimary;
    private DeviceContext? _deviceContext;

    public string DeviceName => _deviceName;
    public bool IsPrimary => _isPrimary;

    private Monitor(string deviceName, bool isPrimary)
    {
        _deviceName = deviceName;
        _isPrimary = isPrimary;
    }

    /// <summary>
    /// Creates a device context for gamma operations.
    /// </summary>
    public DeviceContext? TryCreateDeviceContext()
    {
        if (_deviceContext != null)
            return _deviceContext;

        var dc = CreateDC("DISPLAY", _deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            // Fallback to primary display DC
            dc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        }

        if (dc != IntPtr.Zero)
        {
            _deviceContext = new DeviceContext(dc);
            return _deviceContext;
        }

        return null;
    }

    /// <summary>
    /// Enumerates all available monitors.
    /// </summary>
    public static IReadOnlyList<Monitor> GetAll()
    {
        var monitors = new List<Monitor>();
        var seenDevices = new HashSet<string>();

        var callback = new MonitorEnumProc((IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>()
            };

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                // Skip duplicates (some drivers report same monitor multiple times)
                if (!seenDevices.Contains(mi.szDevice))
                {
                    seenDevices.Add(mi.szDevice);
                    monitors.Add(new Monitor(mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
            }

            return true;
        });

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return monitors;
    }

    public void Dispose()
    {
        _deviceContext?.Dispose();
        _deviceContext = null;
    }
}

/// <summary>
/// Wrapper for a GDI device context with gamma control.
/// </summary>
public sealed class DeviceContext : IDisposable
{
    private IntPtr _handle;
    private GammaRamp _originalRamp;
    private bool _disposed;

    // Monotonic counter driving the cache-breaking noise. A counter (not a
    // Random) guarantees every consecutive call produces a DIFFERENT ramp:
    // a random +0/+1 has a 50% chance of repeating, which would let the
    // driver cache the identical second call (and, in clone mode, reset the
    // first DC's ramp back to default).
    private static int _noiseCounter;

    public IntPtr Handle => _handle;

    public DeviceContext(IntPtr handle)
    {
        _handle = handle;
        _originalRamp = GammaRamp.CreateDefault();
        GetDeviceGammaRamp(_handle, ref _originalRamp);
    }

    /// <summary>
    /// Applies a gamma ramp to this display.
    ///
    /// WORKAROUND (restored from 3.0.0): some GPU drivers (this machine's
    /// dual-DP clone setup included) cache gamma ramps and treat two
    /// IDENTICAL consecutive SetDeviceGammaRamp calls as a no-op or even
    /// re-sync the output back to the default 100% ramp. In clone mode
    /// ApplyGamma issues one call per enumerated DC for the SAME physical
    /// output; without variation the second call resets the first, which
    /// the user sees as "dims for a moment, then immediately brightens".
    /// A tiny random offset (+0/+1 per channel) makes every ramp unique so
    /// the driver applies each one normally.
    ///
    /// PEAK PROTECTION (new in 3.1.0): the noise is applied ONLY to
    /// non-peak values (index &lt; 255). At UI 0% the ramp peak is exactly
    /// 32767 (physical 50%), and a +1 noise on the peak would push it to
    /// 32768, which this machine's driver REJECTS, making it fall back to
    /// the default 100% ramp (screen flashes bright while the UI still
    /// shows 0%). Keeping the peak untouched preserves the accepted value
    /// while still breaking the driver cache.
    /// </summary>
    public bool SetGamma(GammaRamp ramp)
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        // Break the driver cache: vary +0/+1 on non-peak entries only. The
        // counter alternates 0,1,0,1,... so two consecutive calls ALWAYS
        // produce different ramps (random noise would repeat ~50% of the
        // time and let the cache hit).
        int noise = Interlocked.Increment(ref _noiseCounter) % 2;
        var modifiedRamp = new GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        for (int i = 0; i < 256; i++)
        {
            // Never touch the peak (index 255): at UI 0% it is exactly
            // 32767 and must not be pushed to 32768 (driver rejects).
            if (i < 255)
            {
                modifiedRamp.Red[i] = (ushort)Math.Min(65535, ramp.Red[i] + noise);
                modifiedRamp.Green[i] = (ushort)Math.Min(65535, ramp.Green[i] + noise);
                modifiedRamp.Blue[i] = (ushort)Math.Min(65535, ramp.Blue[i] + noise);
            }
            else
            {
                modifiedRamp.Red[i] = ramp.Red[i];
                modifiedRamp.Green[i] = ramp.Green[i];
                modifiedRamp.Blue[i] = ramp.Blue[i];
            }
        }

        return SetDeviceGammaRamp(_handle, ref modifiedRamp);
    }

    /// <summary>
    /// Resets gamma to a standard 100% linear ramp.
    ///
    /// NOTE: we deliberately do NOT restore the ramp captured at startup
    /// (_originalRamp). If a previous run exited abnormally (crash, forced
    /// kill), the system gamma may be stuck at a leftover value; restoring
    /// that leftover would make the screen look dimmer after every exit.
    /// A fresh linear 100% ramp is the correct "normal" state.
    /// </summary>
    public bool ResetGamma()
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        var defaultRamp = GammaRamp.CreateDefault();
        return SetDeviceGammaRamp(_handle, ref defaultRamp);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ResetGamma();
            DeleteDC(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }
}
