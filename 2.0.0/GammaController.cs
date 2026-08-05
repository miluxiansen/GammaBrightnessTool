namespace GammaBrightnessTool;

/// <summary>
/// Controls display brightness via gamma ramp adjustment.
/// Thread-safe. Brightness range: 0% ~ 100% (UI).
///
/// The UI brightness (0..1) is REMAPPED to the physical gamma scale
/// (0.5..1.0) because this machine's GPU/driver REJECTS SetDeviceGammaRamp
/// calls whose ramp peak is below ~32768 (i.e. physical brightness < 50%).
/// With the remap, UI 0% = physical 50% (peak 32767, still accepted) and
/// UI 100% = physical 100%. Every UI level therefore produces a real,
/// monotonic brightness change and is accepted by the driver.
/// </summary>
public sealed class GammaController : IDisposable
{
    public const float MIN_BRIGHTNESS = 0.00f;
    public const float MAX_BRIGHTNESS = 1.00f;
    public const float DEFAULT_STEP = 0.05f; // 5% per wheel notch

    // Physical gamma scale floor: below this the driver rejects the ramp.
    // (Measured on the user's dual-DP clone-mode machine: 0.49 fails,
    // 0.51 succeeds.)
    private const float PHYSICAL_MIN = 0.50f;

    private readonly List<DeviceContext> _displays = new();
    private readonly object _lock = new();
    private float _currentBrightness = 1.0f;   // UI brightness (0..1)
    private bool _initialized;

    public float CurrentBrightness
    {
        get
        {
            lock (_lock) return _currentBrightness;
        }
    }

    /// <summary>
    /// Initializes the controller and enumerates displays.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        var monitors = Monitor.GetAll();
        foreach (var monitor in monitors)
        {
            var dc = monitor.TryCreateDeviceContext();
            if (dc != null)
            {
                _displays.Add(dc);
            }
        }

        _initialized = true;
    }

    /// <summary>
    /// Adjusts brightness by a relative delta.
    /// </summary>
    public void AdjustBrightness(float delta)
    {
        lock (_lock)
        {
            SetBrightnessInternal(_currentBrightness + delta);
        }
    }

    /// <summary>
    /// Sets brightness to an absolute value.
    /// </summary>
    public void SetBrightness(float brightness)
    {
        lock (_lock)
        {
            SetBrightnessInternal(brightness);
        }
    }

    private void SetBrightnessInternal(float brightness)
    {
        _currentBrightness = Math.Clamp(brightness, MIN_BRIGHTNESS, MAX_BRIGHTNESS);
        ApplyGamma();
    }

    /// <summary>
    /// Builds and applies gamma ramp for current brightness.
    /// </summary>
    private void ApplyGamma()
    {
        if (_displays.Count == 0) return;

        var ramp = BuildGammaRamp(_currentBrightness);

        foreach (var display in _displays)
        {
            display.SetGamma(ramp);
        }
    }

    /// <summary>
    /// Builds a gamma ramp with uniform brightness scaling (original 1.0.0
    /// behavior): output = input * physicalBrightness. The whole ramp
    /// scales linearly, so the peak (i=255) also drops, making the screen
    /// appear genuinely darker (not just a contrast/color-depth change).
    ///
    /// The incoming value is the UI brightness (0..1); it is remapped to
    /// the physical gamma scale (0.5..1.0) so the ramp peak never falls
    /// below ~32768, which this machine's driver requires (otherwise it
    /// rejects the ramp and the brightness stops changing below 50%).
    /// </summary>
    private static NativeMethods.GammaRamp BuildGammaRamp(float brightness)
    {
        var ramp = new NativeMethods.GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        // UI 0..1 -> physical 0.5..1.0
        float physical = PHYSICAL_MIN + (1.0f - PHYSICAL_MIN) * brightness;

        for (int i = 0; i < 256; i++)
        {
            // Linear brightness scaling: output = input * physical
            // Map 0-255 to 0-65535, then apply brightness
            ushort value = (ushort)(i * 65535 / 255 * physical);
            ramp.Red[i] = value;
            ramp.Green[i] = value;
            ramp.Blue[i] = value;
        }

        return ramp;
    }

    /// <summary>
    /// Resets all displays to 100% brightness.
    /// </summary>
    public void ResetGamma()
    {
        lock (_lock)
        {
            _currentBrightness = 1.0f;
            foreach (var display in _displays)
            {
                display.ResetGamma();
            }
        }
    }

    public void Dispose()
    {
        ResetGamma();
        foreach (var display in _displays)
        {
            display.Dispose();
        }
        _displays.Clear();
    }
}
