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

    /// <summary>
    /// Per-notch brightness step (0..1), defaults to 5%. The wheel handler
    /// reads this instead of the constant so the setting UI can change it.
    /// </summary>
    public float StepSize { get; set; } = DEFAULT_STEP;

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

        if (_displays.Count == 0)
        {
            // No usable display DC: brightness changes would silently do
            // nothing (the UI would still count up/down). Surface it once
            // instead of pretending everything works.
            MessageBox.Show(
                "未能获取任何显示器的 Gamma 控制权，亮度调节将不会生效。\n\n" +
                "请尝试：重启软件，或检查显卡驱动是否被禁用/异常。",
                "Gamma Brightness - 警告",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Adjusts brightness by a relative delta. The delta is applied to the
    /// INTEGER percentage state (rounded), so repeated small deltas never
    /// accumulate float error (e.g. 0.05f steps drift to 0.79999995, which
    /// would display as 79% instead of 80%).
    /// </summary>
    public void AdjustBrightness(float delta)
    {
        lock (_lock)
        {
            // Math.Max(1, ...) must apply to the ABSOLUTE value; clamping a
            // negative delta to 1 would flip the direction (decrease would
            // become increase).
            int step = Math.Max(1, (int)Math.Round(Math.Abs(delta * 100))) * Math.Sign(delta);
            int percent = (int)Math.Round(_currentBrightness * 100) + step;
            SetBrightnessInternal(percent / 100f);
        }
    }

    /// <summary>
    /// Sets brightness to an absolute value. The value is snapped to the
    /// nearest integer percent so the display layer never sees a float like
    /// 0.84999996.
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
        // Snap to integer percent: kills accumulated float drift and keeps
        // the value clean for display (CurrentBrightness*100 is always an
        // exact integer like 85, never 84.999996).
        int percent = (int)Math.Round(brightness * 100);
        percent = Math.Clamp(percent, (int)(MIN_BRIGHTNESS * 100), (int)(MAX_BRIGHTNESS * 100));
        _currentBrightness = percent / 100f;
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
    /// Builds a gamma ramp with uniform brightness scaling: output =
    /// input * physicalBrightness. The whole ramp scales linearly, so the
    /// peak (i=255) also drops, making the screen appear genuinely darker
    /// (not just a contrast/color-depth change).
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

        // Compute in double to avoid float rounding pushing the 0% peak
        // (32767.5) up to 32768, which this machine's driver rejects.
        for (int i = 0; i < 256; i++)
        {
            // Linear brightness scaling: output = input * physical
            // Map 0-255 to 0-65535, then apply brightness
            double value = i * 65535.0 / 255.0 * physical;
            ushort v = value >= 65535.0 ? ushort.MaxValue : (ushort)value;
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
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
