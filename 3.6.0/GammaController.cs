using System.Collections.ObjectModel;

namespace GammaBrightnessTool;

/// <summary>
/// Per-display state for a single monitor.
/// </summary>
public sealed class DisplayState
{
    /// <summary>UI brightness 0..1.</summary>
    public float Brightness { get; set; } = 1.0f;
    /// <summary>Color temperature in K.</summary>
    public float Temperature { get; set; } = GammaController.DEFAULT_TEMPERATURE;
    /// <summary>Whether this display is controlled. Disabled displays freeze at their current values.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Controls display brightness via gamma ramp adjustment.
///
/// 3.6.0 dual-mode architecture:
/// - Unified mode (PerMonitorEnabled=false, default): all displays share
///   _currentBrightness/_currentTemperature. GammaController behaves exactly
///   as before. No changes to existing MainController callers.
/// - Per-monitor mode (PerMonitorEnabled=true): each display has its own
///   (Brightness, Temperature) in _displayStates[EdidId]. All existing
///   SetBrightness/SetTemperature overloads continue to apply to ALL enabled
///   displays (hotkey/wheel/preset always affect all monitors). New single-
///   monitor overloads are added for the popup's per-row controls.
///
/// Verified 2026-08-25: on the current clone-mode machine, GDI enumeration
/// already merges the two EDID entries into one DC per physical panel, so
/// each Monitor maps 1:1 to one gamma DC without extra merge logic.
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

    // Physical gamma scale floor: below this the driver rejects the ramp.
    // (Measured on the user's dual-DP clone-mode machine: 0.49 fails,
    // 0.51 succeeds.)
    private const float PHYSICAL_MIN = 0.50f;

    private readonly List<DeviceContext> _displays = new();
    private readonly object _lock = new();
    private bool _initialized;

    // ---------- Unified-mode state (PerMonitorEnabled=false) ----------
    private float _currentBrightness = 1.0f;   // UI brightness (0..1)
    private float _currentTemperature = DEFAULT_TEMPERATURE; // 色温 (K)

    // ---------- Per-monitor-mode state (PerMonitorEnabled=true) ----------
    // key = Monitor.EdidId; value = per-display (brightness, temperature)
    private readonly Dictionary<string, DisplayState> _displayStates = new();

    /// <summary>
    /// Whether per-monitor independent control is enabled.
    /// </summary>
    public bool PerMonitorEnabled { get; set; } = false;

    // 全屏暂停：true 时所有调节入口 no-op（保留内部状态），
    // 屏幕显示原生色彩（默认 ramp）。退出全屏后 SetPaused(false)
    // 按保留的当前亮度/色温重放。
    private bool _paused;

    /// <summary>是否处于全屏暂停（调节被忽略，屏幕为原生色彩）。</summary>
    public bool IsPaused
    {
        get { lock (_lock) return _paused; }
    }

    // ---------- Unified-mode properties (read current primary display) ----------
    public float CurrentBrightness
    {
        get
        {
            lock (_lock)
            {
                // PerMonitor 模式返回所有启用屏的平均亮度（供托盘 tooltip / 状态显示）；
                // 统一模式返回全局值。注意 _currentBrightness 字段仍作为
                // 新屏种子与统一重置基准，不被此读取改变。
                if (PerMonitorEnabled) return AverageBrightnessInternal();
                return _currentBrightness;
            }
        }
    }

    /// <summary>
    /// 当前色温（K）。线程安全读取。统一模式返回全局值，
    /// PerMonitor 模式返回"所有启用屏的平均色温"（供托盘 tooltip / OSD 浮窗）。
    /// </summary>
    public float CurrentTemperature
    {
        get
        {
            lock (_lock)
            {
                if (PerMonitorEnabled) return AverageTemperatureInternal();
                return _currentTemperature;
            }
        }
    }

    /// <summary>
    /// Per-monitor enabled: average brightness across all displays with a
    /// known state. Returns 1.0f when no display states exist.
    /// </summary>
    public float AverageBrightness
    {
        get { lock (_lock) return AverageBrightnessInternal(); }
    }

    /// <summary>
    /// Per-monitor enabled: average temperature (simple average, K).
    /// </summary>
    public float AverageTemperature
    {
        get { lock (_lock) return AverageTemperatureInternal(); }
    }

    private float AverageBrightnessInternal()
    {
        if (_displayStates.Count == 0) return _currentBrightness;
        float sum = 0f;
        int enabledCount = 0;
        foreach (var s in _displayStates.Values)
        {
            if (!s.Enabled) continue;   // 停用屏冻结不参与平均
            sum += s.Brightness;
            enabledCount++;
        }
        if (enabledCount == 0) return _currentBrightness;
        return sum / enabledCount;
    }

    private float AverageTemperatureInternal()
    {
        if (_displayStates.Count == 0) return _currentTemperature;
        float sum = 0f;
        int enabledCount = 0;
        foreach (var s in _displayStates.Values)
        {
            if (!s.Enabled) continue;
            sum += s.Temperature;
            enabledCount++;
        }
        if (enabledCount == 0) return _currentTemperature;
        return sum / enabledCount;
    }

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
            MessageBox.Show(
                "未能获取任何显示器的 Gamma 控制权，亮度调节将不会生效。\n\n" +
                "请尝试：重启软件，或检查显卡驱动是否被禁用/异常。",
                "Gamma Brightness - 警告",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Returns the list of known display EdidIds. Call after Initialize().
    /// </summary>
    public IReadOnlyList<string> GetDisplayIds()
    {
        lock (_lock)
        {
            return _displays
                .Select(d => d.MonitorEdidId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();
        }
    }

    /// <summary>
    /// Gets the per-monitor state for a display. If PerMonitorEnabled is
    /// false or the display has no state, returns the unified state.
    /// </summary>
    public DisplayState GetDisplayState(string edidId)
    {
        lock (_lock)
        {
            if (!PerMonitorEnabled)
                return new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
            if (_displayStates.TryGetValue(edidId, out var state))
                return state;
            return new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
        }
    }

    /// <summary>
    /// Gets all per-monitor states. Only meaningful when PerMonitorEnabled=true.
    /// </summary>
    public IReadOnlyDictionary<string, DisplayState> GetAllDisplayStates()
    {
        lock (_lock)
        {
            return new ReadOnlyDictionary<string, DisplayState>(_displayStates);
        }
    }

    /// <summary>
    /// Sets whether a display is controlled (Enabled). Disabled displays
    /// freeze at their current values: they are skipped by all unified
    /// adjustments and their gamma is never rewritten.
    /// </summary>
    public void SetDisplayEnabled(string edidId, bool enabled)
    {
        lock (_lock)
        {
            if (!PerMonitorEnabled || !_displayStates.TryGetValue(edidId, out var state)) return;
            if (state.Enabled == enabled) return;
            state.Enabled = enabled;
            _displayStates[edidId] = state;
        }
    }

    /// <summary>
    /// Initializes per-monitor state for a display from its current ramp.
    /// Called when PerMonitorEnabled first becomes true, or when a new
    /// display is detected after hotplug. Uses the actual screen ramp as
    /// the baseline so the monitor starts at its current value.
    /// </summary>
    public void InitializeDisplayState(string edidId, float brightness, float temperature)
    {
        lock (_lock)
        {
            _displayStates[edidId] = new DisplayState { Brightness = brightness, Temperature = temperature };
        }
    }

    /// <summary>
    /// Resets every known display's per-monitor state to the same seed value
    /// (used when entering per-monitor mode). This guarantees the popup rows
    /// start from the displays' ACTUAL current picture (the unified value that
    /// was just on screen) instead of whatever stale per-display state a
    /// previous session/toggle left in the dictionary — otherwise the slider
    /// can show e.g. 0% while the screen is really at another level.
    /// </summary>
    public void ResetDisplayStates(float brightness, float temperature)
    {
        lock (_lock)
        {
            int bp = (int)Math.Round(Math.Clamp(brightness, 0f, 1f) * 100);
            float b = bp / 100f;
            float t = Math.Clamp(temperature, MinTemperature, MaxTemperature);
            var ids = _displays
                .Select(d => d.MonitorEdidId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();
            _displayStates.Clear();
            foreach (string id in ids)
            {
                _displayStates[id] = new DisplayState { Brightness = b, Temperature = t };
            }
        }
    }

    /// <summary>
    /// Pauses or resumes gamma application (fullscreen auto-pause).
    /// While paused every adjustment entry point is a no-op and the
    /// internal brightness/temperature state is preserved; the screen
    /// shows the native (default) ramp. On resume the preserved values
    /// are replayed. During a pause the internal state intentionally
    /// stays unchanged, so user adjustments while fullscreen are ignored
    /// (they take effect again after exiting fullscreen).
    ///
    /// NOTE: this method only flips the pause flag — it does NOT change
    /// the picture immediately. The visual transition is driven by the
    /// controller via ApplyPausedFrame() (smooth animation frames) and
    /// finalized by ApplyPausedState() (native ramp on pause, replay on
    /// resume).
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
        }
    }

    /// <summary>
    /// Applies a raw picture frame while paused (used by the fullscreen
    /// enter/exit smooth transition animation). Only meaningful while
    /// paused: writes the ramp directly to every display WITHOUT touching
    /// the internal brightness/temperature state, so the preserved values
    /// survive the animation and can be replayed on resume.
    /// </summary>
    public void ApplyPausedFrame(float brightness, float temperature)
    {
        lock (_lock)
        {
            if (!_paused) return;
            if (_displays.Count == 0) return;
            var ramp = BuildGammaRamp(brightness, temperature);
            foreach (var display in _displays)
            {
                display.SetGamma(ramp);
            }
        }
    }

    /// <summary>
    /// Finalizes the paused state on the screen: while paused, resets
    /// every display to the native (default) ramp; on resume, replays
    /// the preserved brightness/temperature. Called by the controller
    /// after the fullscreen transition animation ends (or instantly when
    /// the smooth option is off).
    /// </summary>
    public void ApplyPausedState()
    {
        lock (_lock)
        {
            if (_paused)
            {
                foreach (var display in _displays)
                {
                    display.ResetGamma();
                }
            }
            else
            {
                ApplyGamma();
            }
        }
    }

    /// <summary>
    /// Adjusts brightness by a relative delta. The delta is applied to the
    /// INTEGER percentage state (rounded), so repeated small deltas never
    /// accumulate float error (e.g. 0.05f steps drift to 0.79999995, which
    /// would display as 79% instead of 80%).
    /// Unified mode: adjusts all displays. Per-monitor mode: adjusts all
    /// ENABLED displays (each from their own current value).
    /// </summary>
    public void AdjustBrightness(float delta)
    {
        lock (_lock)
        {
            if (_paused) return;
            int step = Math.Max(1, (int)Math.Round(Math.Abs(delta * 100))) * Math.Sign(delta);

            if (!PerMonitorEnabled)
            {
                int percent = (int)Math.Round(_currentBrightness * 100) + step;
                SetBrightnessInternal(ref _currentBrightness, percent / 100f);
                ApplyGamma();
            }
            else
            {
                // Each enabled display shifts by the same absolute step
                // 只调节启用屏；停用屏冻结（不改变其值）
                foreach (var kvp in _displayStates)
                {
                    if (!kvp.Value.Enabled) continue;
                    var state = kvp.Value;
                    int newPercent = (int)Math.Round(state.Brightness * 100) + step;
                    float snapped = Math.Clamp(newPercent / 100f, 0f, 1f);
                    if (snapped == state.Brightness) continue;
                    state.Brightness = snapped;
                    _displayStates[kvp.Key] = state;
                }
                ApplyGammaAllDisplays();
            }
        }
    }

    /// <summary>
    /// Sets brightness to an absolute value. The value is snapped to the
    /// nearest integer percent so the display layer never sees a float like
    /// 0.84999996.
    /// Unified mode: sets all displays. Per-monitor mode: sets all ENABLED
    /// displays to the same value.
    /// </summary>
    public void SetBrightness(float brightness)
    {
        lock (_lock)
        {
            if (_paused) return;
            SetBrightnessInternal(ref _currentBrightness, brightness);
            if (!PerMonitorEnabled)
            {
                ApplyGamma();
            }
            else
            {
                // Set all ENABLED displays to the same absolute value
                foreach (var kvp in _displayStates)
                    {
                        if (!kvp.Value.Enabled) continue; // 停用屏冻结
                    var state = kvp.Value;
                    int percent = (int)Math.Round(brightness * 100);
                    percent = Math.Clamp(percent, 0, 100);
                    float snapped = percent / 100f;
                    if (snapped == state.Brightness) continue;
                    state.Brightness = snapped;
                    _displayStates[kvp.Key] = state;
                }
                ApplyGammaAllDisplays();
            }
        }
    }

    /// <summary>
    /// Sets brightness for a SINGLE display (used by per-monitor popup rows).
    /// Only meaningful when PerMonitorEnabled=true. No-op if edidId not known.
    /// </summary>
    public void SetBrightness(string edidId, float brightness)
    {
        lock (_lock)
        {
            if (_paused) return;
            if (!PerMonitorEnabled || !_displayStates.ContainsKey(edidId)) return;
            var state = _displayStates[edidId];
            int percent = (int)Math.Round(brightness * 100);
            percent = Math.Clamp(percent, 0, 100);
            float snapped = percent / 100f;
            if (snapped == state.Brightness) return;
            state.Brightness = snapped;
            _displayStates[edidId] = state;
            ApplyGamma(edidId);
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
            if (_paused) return;

            if (!PerMonitorEnabled)
            {
                float next = _currentTemperature + deltaK;
                next = Math.Clamp(next, MinTemperature, MaxTemperature);
                next = (float)Math.Round(next);
                if (next == _currentTemperature) return;
                _currentTemperature = next;
                ApplyGamma();
            }
            else
            {
                foreach (var kvp in _displayStates)
                    {
                        if (!kvp.Value.Enabled) continue; // 停用屏冻结
                    float next = kvp.Value.Temperature + deltaK;
                    next = Math.Clamp(next, MinTemperature, MaxTemperature);
                    next = (float)Math.Round(next);
                    if (next == kvp.Value.Temperature) continue;
                    _displayStates[kvp.Key].Temperature = next;
                }
                ApplyGammaAllDisplays();
            }
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
            if (_paused) return;
            float snapped = Math.Clamp(kelvin, MinTemperature, MaxTemperature);
            snapped = (float)Math.Round(snapped);
            if (!PerMonitorEnabled)
            {
                if (snapped == _currentTemperature) return;
                _currentTemperature = snapped;
                ApplyGamma();
            }
            else
            {
                // per-monitor：与 SetBrightness(float) 对称——把目标写进每台启用屏
                // 的状态再整批写屏。此前只更新 _currentTemperature 就调
                // ApplyGammaAllDisplays（它逐屏读各自 state），导致"全局设色温"
                // 在独立控制下对屏幕完全无效果（2026-09-03 排查 Bug1）。
                _currentTemperature = snapped;   // 仍作新屏种子/无状态回退基准
                bool anyChanged = false;
                foreach (var kvp in _displayStates)
                {
                    if (!kvp.Value.Enabled) continue;   // 停用屏冻结
                    if (Math.Abs(snapped - kvp.Value.Temperature) < 0.5f) continue;
                    kvp.Value.Temperature = snapped;
                    anyChanged = true;
                }
                if (anyChanged) ApplyGammaAllDisplays();
            }
        }
    }

    /// <summary>
    /// Sets color temperature for a SINGLE display (used by per-monitor
    /// popup rows). Only meaningful when PerMonitorEnabled=true.
    /// </summary>
    public void SetTemperature(string edidId, float kelvin)
    {
        lock (_lock)
        {
            if (_paused) return;
            if (!PerMonitorEnabled || !_displayStates.ContainsKey(edidId)) return;
            float snapped = Math.Clamp(kelvin, MinTemperature, MaxTemperature);
            snapped = (float)Math.Round(snapped);
            if (snapped == _displayStates[edidId].Temperature) return;
            _displayStates[edidId].Temperature = snapped;
            ApplyGamma(edidId);
        }
    }

    private void SetBrightnessInternal(ref float field, float brightness)
    {
        if (brightness == field) return;
        int percent = (int)Math.Round(brightness * 100);
        percent = Math.Clamp(percent, 0, 100);
        field = percent / 100f;
    }

    /// <summary>
    /// Applies current gamma to ALL displays.
    /// Unified mode: uses _currentBrightness/_currentTemperature.
    /// Per-monitor mode: uses each display's own state.
    /// </summary>
    private void ApplyGammaAllDisplays()
    {
        if (_displays.Count == 0) return;

        if (!PerMonitorEnabled)
        {
            var ramp = BuildGammaRamp(_currentBrightness, _currentTemperature);
            foreach (var display in _displays)
                display.SetGamma(ramp);
        }
        else
        {
            foreach (var display in _displays)
            {
                var edidId = display.MonitorEdidId;
                if (string.IsNullOrEmpty(edidId) || !_displayStates.TryGetValue(edidId, out var state))
                    state = new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
                if (!state.Enabled) continue; // 停用屏冻结，不写 gamma
                var ramp = BuildGammaRamp(state.Brightness, state.Temperature);
                display.SetGamma(ramp);
            }
        }
    }

    /// <summary>
    /// Applies gamma to ONE display identified by EdidId.
    /// </summary>
    private void ApplyGamma(string edidId)
    {
        if (_displays.Count == 0) return;
        var display = _displays.FirstOrDefault(d =>
            string.Equals(d.MonitorEdidId, edidId, StringComparison.OrdinalIgnoreCase));
        if (display == null) return;
        if (!_displayStates.TryGetValue(edidId, out var state))
            state = new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
        var ramp = BuildGammaRamp(state.Brightness, state.Temperature);
        display.SetGamma(ramp);
    }

    /// <summary>
    /// Builds and applies gamma ramp for unified-mode current brightness
    /// and temperature. Called by legacy ApplyGamma() path.
    /// </summary>
    private void ApplyGamma()
    {
        ApplyGammaAllDisplays();
    }

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
                double x = (greenMul * 255.0 + 161.1195681661) / 99.4708025861;
                return (float)Math.Clamp(100.0 * Math.Exp(x), MIN_TEMPERATURE, MAX_TEMPERATURE);
            }
            else
            {
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

    /// <summary>
    /// Rebuilds the display device-context list and reapplies the current
    /// brightness/temperature. Used for self-healing after monitor
    /// hot-plug / resolution change / system resume.
    /// </summary>
    public void RefreshDisplays()
    {
        lock (_lock)
        {
            foreach (var display in _displays)
            {
                display.Dispose();
            }
            _displays.Clear();

            var monitors = Monitor.GetAll();
            foreach (var monitor in monitors)
            {
                var dc = monitor.TryCreateDeviceContext();
                if (dc != null)
                {
                    _displays.Add(dc);

                    // 3.6.0: 为新显示器初始化 per-monitor 状态（保留已存在的）。
                    // 新显示器播种：per-monitor 下取"现有启用屏的平均值"作为起点
                    // （而不是陈旧的 _currentBrightness/_currentTemperature 统一种子——
                    // 逐屏拖动只更新 state，统一种子不会随之变化，会导致热插拔的新屏
                    // 起点与当前画面不一致，2026-09-03 巡检 Bug B）。
                    if (!string.IsNullOrEmpty(dc.MonitorEdidId) && !_displayStates.ContainsKey(dc.MonitorEdidId))
                    {
                        var (seedB, seedT) = PerMonitorSeed();
                        _displayStates[dc.MonitorEdidId] = new DisplayState
                        {
                            Brightness = seedB,
                            Temperature = seedT
                        };
                    }
                }
            }

            // 3.6.0: 清理已不存在显示器的状态（热插拔拔出后）。
            var liveIds = _displays
                .Select(d => d.MonitorEdidId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _displayStates.Keys.Where(k => !liveIds.Contains(k)).ToList())
            {
                _displayStates.Remove(stale);
            }

            // 暂停中只重建列表，不重放（保持原生色彩）。
            if (_paused) return;

            ApplyGammaAllDisplays();
        }
    }

    /// <summary>
    /// 新屏播种基准：per-monitor 模式下取"现有启用屏的平均值"（与 tooltip/
    /// OSD 的 Current* 语义一致），避免使用从不随逐屏拖动更新的陈旧统一种子
    /// 字段。无启用屏/非 per-monitor 时回退统一种子。调用方必须已持有 _lock。
    /// </summary>
    private (float Brightness, float Temperature) PerMonitorSeed()
    {
        if (PerMonitorEnabled && _displayStates.Count > 0)
        {
            float sumB = 0f, sumT = 0f;
            int count = 0;
            foreach (var s in _displayStates.Values)
            {
                if (!s.Enabled) continue;   // 停用屏冻结不参与基准
                sumB += s.Brightness;
                sumT += s.Temperature;
                count++;
            }
            if (count > 0) return (sumB / count, sumT / count);
        }
        return (_currentBrightness, _currentTemperature);
    }

    /// <summary>
    /// 3.6.0: 初始化/修复 per-monitor 状态字典。独立模式开启或显示器
    /// 热插拔后调用：保留已有 EDID 的状态，新 EDID 用启动时的统一值。
    /// </summary>
    public void ReconcileDisplayStates()
    {
        lock (_lock)
        {
            if (!PerMonitorEnabled) return;

            foreach (var display in _displays)
            {
                var edidId = display.MonitorEdidId;
                if (string.IsNullOrEmpty(edidId)) continue;
                if (!_displayStates.ContainsKey(edidId))
                {
                    var (seedB, seedT) = PerMonitorSeed();
                    _displayStates[edidId] = new DisplayState
                    {
                        Brightness = seedB,
                        Temperature = seedT
                    };
                }
            }

            var liveIds = _displays
                .Select(d => d.MonitorEdidId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _displayStates.Keys.Where(k => !liveIds.Contains(k)).ToList())
            {
                _displayStates.Remove(stale);
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
