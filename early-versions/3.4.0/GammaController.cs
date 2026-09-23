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
    /// 色温范围与默认值。6600K 为中性白（R=G=B=1），在此值下 ramp 退化为
    /// 纯亮度缩放，完全不影响现有亮度行为。
    /// </summary>
    public const float MIN_TEMPERATURE = 3300f;
    public const float MAX_TEMPERATURE = 10000f;
    public const float DEFAULT_TEMPERATURE = 6600f;

    /// <summary>
    /// 色温步进（K）。默认 100K，可通过设置调整为 50~3000K。
    /// </summary>
    public const float TEMPERATURE_STEP = 100f;
    public const float DEFAULT_TEMPERATURE_STEP = 100f;
    public const float MIN_TEMPERATURE_STEP = 50f;
    public const float MAX_TEMPERATURE_STEP = 3000f;

    /// <summary>
    /// Per-notch brightness step (0..1), defaults to 5%. The wheel handler
    /// reads this instead of the constant so the setting UI can change it.
    /// </summary>
    public float StepSize { get; set; } = DEFAULT_STEP;

    /// <summary>
    /// Per-notch color-temperature step (K). The temperature wheel/hotkey
    /// handlers read this instead of the constant so the setting UI can
    /// change it (50~3000K, default 100K).
    /// </summary>
    public float TemperatureStepSize { get; set; } = DEFAULT_TEMPERATURE_STEP;

    /// <summary>
    /// Configurable color-temperature clamp range (K). Defaults to the
    /// full hardware range [MinTemperature, MaxTemperature]; the user
    /// can narrow it (e.g. 4000~8000K) from the settings page. The slider,
    /// wheel and hotkeys all clamp to this range.
    /// </summary>
    public float MinTemperature { get; set; } = MIN_TEMPERATURE;
    public float MaxTemperature { get; set; } = MAX_TEMPERATURE;

    // Physical gamma scale floor: below this the driver rejects the ramp.
    // (Measured on the user's dual-DP clone-mode machine: 0.49 fails,
    // 0.51 succeeds.)
    private const float PHYSICAL_MIN = 0.50f;

    private readonly List<DeviceContext> _displays = new();
    private readonly object _lock = new();
    private float _currentBrightness = 1.0f;   // UI brightness (0..1)
    private float _currentTemperature = DEFAULT_TEMPERATURE; // 色温 (K)
    private bool _initialized;

    public float CurrentBrightness
    {
        get
        {
            lock (_lock) return _currentBrightness;
        }
    }

    /// <summary>
    /// 当前色温（K）。线程安全读取。
    /// </summary>
    public float CurrentTemperature
    {
        get
        {
            lock (_lock) return _currentTemperature;
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

    /// <summary>
    /// Sets color temperature to an absolute value (K). 6600K is the
    /// neutral white point; values below are warmer (reddish), above are
    /// cooler (bluish). The value is snapped to the nearest 100K so the
    /// display layer never sees a float like 6599.9995.
    /// </summary>
    public void SetTemperature(float kelvin)
    {
        lock (_lock)
        {
            // Keep a fine 100K grid for the display layer (the wheel
            // already steps by TemperatureStepSize; the slider snaps to
            // the 100K grid). No extra grid snapping here so wheel steps
            // like 500K stay exact (6600 -> 7100, not 7000).
            float snapped = Math.Clamp(kelvin, MinTemperature, MaxTemperature);
            if (Math.Abs(snapped - _currentTemperature) < 0.5f) return;
            _currentTemperature = snapped;
            ApplyGamma();
        }
    }

    /// <summary>
    /// Adjusts color temperature by a relative delta (K). The delta is
    /// applied directly on top of the current value and clamped to
    /// [MinTemperature, MaxTemperature]. The wheel/hotkey callers pass
    /// their configured TemperatureStepSize as the delta.
    /// </summary>
    public void AdjustTemperature(float deltaK)
    {
        lock (_lock)
        {
            float next = _currentTemperature + deltaK;
            next = Math.Clamp(next, MinTemperature, MaxTemperature);
            if (Math.Abs(next - _currentTemperature) < 0.5f) return;
            _currentTemperature = next;
            ApplyGamma();
        }
    }

    private void SetBrightnessInternal(float brightness)
    {
        if (brightness == _currentBrightness) return;

        // Snap to integer percent: kills accumulated float drift and keeps
        // the value clean for display (CurrentBrightness*100 is always an
        // exact integer like 85, never 84.999996).
        int percent = (int)Math.Round(brightness * 100);
        percent = Math.Clamp(percent, (int)(MIN_BRIGHTNESS * 100), (int)(MAX_BRIGHTNESS * 100));
        _currentBrightness = percent / 100f;
        ApplyGamma();
    }

    /// <summary>
    /// Builds and applies gamma ramp for current brightness and temperature.
    /// </summary>
    private void ApplyGamma()
    {
        if (_displays.Count == 0) return;

        var ramp = BuildGammaRamp(_currentBrightness, _currentTemperature);

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
    ///
    /// Color temperature (K) is applied as per-channel multipliers using
    /// the Tanner Helland algorithm (the same one LightBulb uses):
    /// warmer = more red / less blue, cooler = more blue / less red.
    /// At 6600K all three multipliers equal 1.0, so the ramp is exactly
    /// the old brightness-only ramp (backward compatible).
    /// </summary>
    private static NativeMethods.GammaRamp BuildGammaRamp(float brightness, float temperature)
    {
        var ramp = new NativeMethods.GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };

        // UI 0..1 -> physical 0.5..1.0
        float physical = PHYSICAL_MIN + (1.0f - PHYSICAL_MIN) * brightness;

        // Tanner Helland temperature -> per-channel multipliers.
        double redMul = GetRedMultiplier(temperature);
        double greenMul = GetGreenMultiplier(temperature);
        double blueMul = GetBlueMultiplier(temperature);

        // Compute in double to avoid float rounding pushing the 0% peak
        // (32767.5) up to 32768, which this machine's driver rejects.
        for (int i = 0; i < 256; i++)
        {
            // Linear brightness scaling: output = input * physical
            // Map 0-255 to 0-65535, then apply brightness and temperature.
            double input = i * 65535.0 / 255.0 * physical;
            ushort r = input * redMul >= 65535.0 ? ushort.MaxValue : (ushort)(input * redMul);
            ushort g = input * greenMul >= 65535.0 ? ushort.MaxValue : (ushort)(input * greenMul);
            ushort b = input * blueMul >= 65535.0 ? ushort.MaxValue : (ushort)(input * blueMul);
            ramp.Red[i] = r;
            ramp.Green[i] = g;
            ramp.Blue[i] = b;
        }

        return ramp;
    }

    // --- Tanner Helland temperature -> RGB multiplier (LightBulb algorithm) ---
    // http://tannerhelland.com/4435/convert-temperature-rgb-algorithm-code
    // All multipliers are 1.0 at 6600K (neutral white).
    // Public: reused by ColorTemperature to render the kelvin as a color
    // for the popup slider fill / mode icon.

    public static double GetRedMultiplier(float temperature)
    {
        if (temperature > 6600f)
        {
            return Math.Clamp(
                Math.Pow(temperature / 100.0 - 60.0, -0.1332047592) * 329.698727446 / 255.0,
                0.0, 1.0);
        }
        return 1.0;
    }

    public static double GetGreenMultiplier(float temperature)
    {
        if (temperature > 6600f)
        {
            return Math.Clamp(
                Math.Pow(temperature / 100.0 - 60.0, -0.0755148492) * 288.1221695283 / 255.0,
                0.0, 1.0);
        }
        return Math.Clamp(
            (Math.Log(temperature / 100.0) * 99.4708025861 - 161.1195681661) / 255.0,
            0.0, 1.0);
    }

    public static double GetBlueMultiplier(float temperature)
    {
        if (temperature >= 6600f) return 1.0;
        if (temperature <= 1900f) return 0.0;
        return Math.Clamp(
            (Math.Log(temperature / 100.0 - 10.0) * 138.5177312231 - 305.0447927307) / 255.0,
            0.0, 1.0);
    }

    /// <summary>
    /// 读取屏幕当前实际亮度（UI 0..1）。取红蓝两通道中乘子为 1 的通道峰值：
    /// peak = 65535 * physical，physical = 0.5 + 0.5*ui，反推 ui。
    /// </summary>
    public float ReadCurrentBrightness()
    {
        lock (_lock)
        {
            var ramp = ReadRamp();
            double redPeak = ramp.Red[255];
            double bluePeak = ramp.Blue[255];
            double peak = Math.Max(redPeak, bluePeak);
            if (peak <= 0) return _currentBrightness;
            double physical = peak / 65535.0;
            double ui = (physical - PHYSICAL_MIN) / (1.0 - PHYSICAL_MIN);
            return (float)Math.Clamp(ui, 0.0, 1.0);
        }
    }

    /// <summary>
    /// 读取屏幕当前实际色温（K）。从 ramp 通道比例反推 Tanner Helland
    /// 乘子：暖侧红=1 用绿反推，冷侧蓝=1 用红反推。用于启动平滑起点。
    /// </summary>
    public float ReadCurrentTemperature()
    {
        lock (_lock)
        {
            var ramp = ReadRamp();
            double r = ramp.Red[255], g = ramp.Green[255], b = ramp.Blue[255];
            double peak = Math.Max(r, Math.Max(g, b));
            if (peak <= 0) return _currentTemperature;
            double redMul = r / peak;
            double greenMul = g / peak;
            double blueMul = b / peak;
            if (redMul >= blueMul)
            {
                // 暖侧：greenMul = (ln(t/100)*99.4708025861 - 161.1195681661)/255
                double x = (greenMul * 255.0 + 161.1195681661) / 99.4708025861;
                return (float)Math.Clamp(100.0 * Math.Exp(x), MIN_TEMPERATURE, MAX_TEMPERATURE);
            }
            else
            {
                // 冷侧：redMul = pow(t/100-60, -0.1332047592) * 329.698727446/255
                double y = redMul * 255.0 / 329.698727446;
                double t = 60.0 + Math.Pow(y, 1.0 / -0.1332047592);
                return (float)Math.Clamp(100.0 * t, MIN_TEMPERATURE, MAX_TEMPERATURE);
            }
        }
    }

    /// <summary>读取第一台显示器的当前 ramp（克隆模式下各显示器输出相同）；无显示器时返回默认线性 ramp。</summary>
    private NativeMethods.GammaRamp ReadRamp()
    {
        foreach (var display in _displays)
        {
            return display.GetCurrentRamp();
        }
        return NativeMethods.GammaRamp.CreateDefault();
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
