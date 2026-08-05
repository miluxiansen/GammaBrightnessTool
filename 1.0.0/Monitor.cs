using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Represents a display monitor and provides gamma control capabilities.
/// Extracted and simplified from LightBulb's implementation.
/// </summary>
public sealed class Monitor : IDisposable
{
    private IntPtr _hMonitor;
    private string _deviceName;
    private bool _isPrimary;
    private DeviceContext? _deviceContext;

    public string DeviceName => _deviceName;
    public bool IsPrimary => _isPrimary;

    private Monitor(IntPtr hMonitor, string deviceName, bool isPrimary)
    {
        _hMonitor = hMonitor;
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
                    monitors.Add(new Monitor(hMonitor, mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
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

    public IntPtr Handle => _handle;

    public DeviceContext(IntPtr handle)
    {
        _handle = handle;
        _originalRamp = GammaRamp.CreateDefault();
        GetDeviceGammaRamp(_handle, ref _originalRamp);
    }

    /// <summary>
    /// Applies gamma ramp to this display.
    /// Includes workaround for driver cache issues.
    /// </summary>
    public bool SetGamma(GammaRamp ramp)
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        // Workaround: Some GPU drivers cache gamma ramps and ignore identical consecutive calls.
        // We add a tiny random offset to force the driver to apply the change.
        var modifiedRamp = new GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        var random = new Random();
        for (int i = 0; i < 256; i++)
        {
            // Add imperceptible noise (max 1/65535) to break driver cache
            short noise = (short)(random.Next(2) == 0 ? 0 : 1);
            modifiedRamp.Red[i] = (ushort)Math.Min(65535, ramp.Red[i] + noise);
            modifiedRamp.Green[i] = (ushort)Math.Min(65535, ramp.Green[i] + noise);
            modifiedRamp.Blue[i] = (ushort)Math.Min(65535, ramp.Blue[i] + noise);
        }

        return SetDeviceGammaRamp(_handle, ref modifiedRamp);
    }

    /// <summary>
    /// Resets gamma to the original values captured at creation.
    /// </summary>
    public bool ResetGamma()
    {
        if (_disposed || _handle == IntPtr.Zero)
            return false;

        return SetDeviceGammaRamp(_handle, ref _originalRamp);
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
