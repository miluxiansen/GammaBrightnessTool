namespace GammaBrightnessTool;

/// <summary>
/// Controls display brightness via gamma ramp adjustment.
/// Thread-safe. Brightness range: 10% ~ 100%.
/// </summary>
public sealed class GammaController : IDisposable
{
    public const float MIN_BRIGHTNESS = 0.10f;
    public const float MAX_BRIGHTNESS = 1.00f;
    public const float DEFAULT_STEP = 0.05f; // 5% per wheel notch

    private readonly List<DeviceContext> _displays = new();
    private readonly object _lock = new();
    private float _currentBrightness = 1.0f;
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
    /// Builds a gamma ramp with uniform brightness scaling.
    /// </summary>
    private static NativeMethods.GammaRamp BuildGammaRamp(float brightness)
    {
        var ramp = new NativeMethods.GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        for (int i = 0; i < 256; i++)
        {
            // Linear brightness scaling: output = input * brightness
            // Map 0-255 to 0-65535, then apply brightness
            ushort value = (ushort)(i * 65535 / 255 * brightness);
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
