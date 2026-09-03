using System.Runtime.InteropServices;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Represents a display monitor and provides gamma control capabilities.
/// Extracted and simplified from LightBulb's implementation.
///
/// 3.6.0: each Monitor now carries a stable EDID-based identity (EdidId)
/// used as the key for per-monitor state (AppSettings.MonitorStates /
/// MonitorNames). The GDI DeviceName (\\.\DISPLAYn) is only used to open
/// the gamma DC — it must NOT be persisted (changes on hotplug/reboot).
/// Verified 2026-08-25: on this machine (dual-DP clone) GDI enumeration
/// already merges the two EDID entries into a single szDevice, so each
/// Monitor maps 1:1 to one physical panel and one gamma DC.
/// </summary>
public sealed class Monitor : IDisposable
{
    private string _deviceName;
    private bool _isPrimary;
    private DeviceContext? _deviceContext;

    /// <summary>
    /// GDI device name (\\.\DISPLAYn) used to open the gamma DC. Not stable
    /// across reboots/hotplug — do not use as a persisted key.
    /// </summary>
    public string DeviceName => _deviceName;

    /// <summary>
    /// Stable EDID instance ID (base form, no trailing \Instance index):
    /// MONITOR\SAC2466\{4d36e96e-...}. Identifies the physical panel.
    /// Empty when EDID enumeration fails (fallback: use DeviceName).
    /// </summary>
    public string EdidId { get; private set; } = "";

    public bool IsPrimary => _isPrimary;

    /// <summary>
    /// Physical (native) pixel width of the current display mode, read via
    /// GetDeviceCaps on a per-device DC. 0 when the DC could not be created.
    /// </summary>
    public int PhysicalWidthPx { get; private set; }

    /// <summary>Physical (native) pixel height of the current display mode.</summary>
    public int PhysicalHeightPx { get; private set; }

    /// <summary>Effective DPI of this monitor (96 at 100% scaling).</summary>
    public int DpiX { get; private set; } = 96;

    /// <summary>Windows-style scale percentage, e.g. 175 at DPI 168.</summary>
    public int ScalePercent => DpiX > 0 ? (int)Math.Round(DpiX * 100.0 / 96.0) : 0;

    private Monitor(string deviceName, bool isPrimary, string edidId, int physW, int physH, int dpiX)
    {
        _deviceName = deviceName;
        _isPrimary = isPrimary;
        EdidId = edidId;
        PhysicalWidthPx = physW;
        PhysicalHeightPx = physH;
        if (dpiX > 0) DpiX = dpiX;
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
            _deviceContext = new DeviceContext(dc, EdidId);
            return _deviceContext;
        }

        return null;
    }

    /// <summary>
    /// Returns the EDID instance ID (base form) for a GDI device name,
    /// or "" if the monitor is not found / not active.
    /// Uses EnumDisplayDevices level 1 (adapter) + level 2 (monitor).
    /// </summary>
    private static string GetEdidId(string gdiDeviceName)
    {
        try
        {
            int ai = 0;
            while (true)
            {
                var adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, (uint)ai, ref adapter, 0))
                    break;

                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 ||
                    !string.Equals(adapter.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    ai++;
                    continue;
                }

                // Found the adapter; get its first ACTIVE monitor
                int mi = 0;
                while (true)
                {
                    var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(adapter.DeviceName, (uint)mi, ref monitor, 0))
                        break;

                    if ((monitor.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                    {
                        // Base form: MONITOR\VendorModel\{GUID} (strip \Instance)
                        var id = monitor.DeviceID ?? "";
                        int lastSlash = id.LastIndexOf('\\');
                        if (lastSlash > 0)
                            id = id.Substring(0, lastSlash);
                        return id;
                    }
                    mi++;
                }
                return ""; // adapter has no active monitor
            }
        }
        catch
        {
            return "";
        }
        return "";
    }

    /// <summary>
    /// Enumerates all available monitors (GDI), each with its EDID-based
    /// stable identity. Duplicate GDI entries are skipped (some drivers
    /// report the same monitor twice); EDID duplicates within one GDI
    /// entry are naturally merged by GDI (verified on dual-DP clone).
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
                    string edidId = GetEdidId(mi.szDevice);

                    // Physical resolution of the current mode (native pixels).
                    int physW = 0, physH = 0;
                    IntPtr dc = NativeMethods.CreateDC("DISPLAY", mi.szDevice, null, IntPtr.Zero);
                    if (dc != IntPtr.Zero)
                    {
                        physW = NativeMethods.GetDeviceCaps(dc, NativeMethods.HORZRES);
                        physH = NativeMethods.GetDeviceCaps(dc, NativeMethods.VERTRES);
                        NativeMethods.DeleteDC(dc);
                    }

                    // Effective per-monitor DPI.
                    int dpiX = 96;
                    if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiXv, out _) == 0 && dpiXv > 0)
                        dpiX = (int)dpiXv;

                    monitors.Add(new Monitor(mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, edidId, physW, physH, dpiX));
                }
            }

            return true;
        });

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return monitors;
    }

    // ------------------------------------------------------------------
    // Friendly display names (EDID Monitor-Name descriptor via DisplayConfig)
    // ------------------------------------------------------------------
    // GetDisplaySystemName falls back to the internal EDID model segment
    // ("SAC2466"), which users do not recognize. DisplayConfigGetDeviceInfo
    // (GET_TARGET_NAME) exposes the EDID 0xFC descriptor the way Windows
    // Settings does ("G5c II"), without admin rights. Build once per process.

    private static Dictionary<string, string>? _friendlyByModel;
    private static readonly object _friendlyLock = new();

    /// <summary>
    /// Returns the friendly (EDID Monitor-Name) display name for an EDID
    /// instance-id base ("MONITOR\SAC2466\{GUID}"), or null when the panel
    /// has no name descriptor (e.g. many laptop panels).
    /// </summary>
    public static string? GetEdidFriendlyName(string edidId)
    {
        string model = ExtractEdidModel(edidId);
        if (model.Length == 0) return null;
        EnsureFriendlyNames();
        return _friendlyByModel!.TryGetValue(model, out string? name) ? name : null;
    }

    private static string ExtractEdidModel(string edidId)
    {
        if (string.IsNullOrEmpty(edidId)) return "";
        // "MONITOR\SAC2466\{GUID}" -> "SAC2466"
        int slash = edidId.IndexOf('\\');
        if (slash > 0 && slash + 1 < edidId.Length)
        {
            int slash2 = edidId.IndexOf('\\', slash + 1);
            return slash2 > slash + 1 ? edidId.Substring(slash + 1, slash2 - slash - 1) : edidId.Substring(slash + 1);
        }
        return "";
    }

    private static void EnsureFriendlyNames()
    {
        if (_friendlyByModel != null) return;
        lock (_friendlyLock)
        {
            if (_friendlyByModel != null) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) == 0 && numPaths > 0)
                {
                    var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[numPaths];
                    IntPtr modes = Marshal.AllocHGlobal((int)numModes * 96); // mode info entries (generous)
                    try
                    {
                        if (NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) == 0)
                        {
                            for (int i = 0; i < numPaths; i++)
                            {
                                var name = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
                                {
                                    header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                                    {
                                        type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                                        size = Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                                        adapterId = paths[i].targetInfo.adapterId,
                                        id = paths[i].targetInfo.id
                                    }
                                };
                                if (NativeMethods.DisplayConfigGetDeviceInfo(ref name) == 0 &&
                                    !string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName) &&
                                    !string.IsNullOrEmpty(name.monitorDevicePath))
                                {
                                    // monitorDevicePath: \\?\DISPLAY#<Model>#<Instance>#{guid}
                                    string model = ExtractPathModel(name.monitorDevicePath);
                                    if (model.Length > 0 && !map.ContainsKey(model))
                                        map[model] = name.monitorFriendlyDeviceName.Trim();
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(modes);
                    }
                }
            }
            catch
            {
                // DisplayConfig unavailable — leave map empty, callers fall back.
            }
            _friendlyByModel = map;
        }
    }

    private static string ExtractPathModel(string devicePath)
    {
        // "\\?\DISPLAY#SAC2466#5&3954046&1&UID4352#{guid}" -> "SAC2466"
        try
        {
            string[] seg = devicePath.Split('#');
            if (seg.Length >= 2 && seg[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
                return seg[1];
        }
        catch
        {
        }
        return "";
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
    /// <summary>The Monitor EdidId this DC belongs to ('' if unknown).</summary>
    public string MonitorEdidId { get; } = "";

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

    public DeviceContext(IntPtr handle, string monitorEdidId = "")
    {
        _handle = handle;
        MonitorEdidId = monitorEdidId;
        GetDeviceGammaRamp(_handle, ref _originalRamp);
    }

    /// <summary>
    /// Reads the currently active gamma ramp (used to probe the actual
    /// screen brightness/temperature for smooth-start animation).
    /// </summary>
    public GammaRamp GetCurrentRamp()
    {
        var ramp = GammaRamp.CreateDefault();
        if (_disposed || _handle == IntPtr.Zero) return ramp;
        GetDeviceGammaRamp(_handle, ref ramp);
        return ramp;
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
