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

    /// <summary>
    /// 被逐屏白名单暂停：该屏回原生 ramp 且冻结写值，**优先级高于 Enabled**（用户 R2 决策）。
    /// ⚠ 纯运行时状态，**绝不写入 settings.json** —— 写进去会导致重启后该屏永久停用。
    /// </summary>
    public bool Paused { get; set; }
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
    /// F1（2026-09-17）「目标 ramp 是否已在屏上」的等价判定容差。
    /// 来源：`Monitor.SetGamma` 会给**非峰值** entry 加 ±1 抗缓存噪声（峰值 index 255 不动），
    /// 故"读回的屏幕 ramp"与"重建的目标 ramp"天然差 ≤1；取 2 覆盖取值/量化误差。
    /// 该容差下：876 的伪影跳、任何真实亮度/色温变化都会被正确判为"不等价"。
    /// </summary>
    private const int RampEqTolerance = 2;

    // ------------------------------------------------------------------
    // F2（2026-09-17）：ramp 空间过渡。
    // 起点不再是"反算参数→正向重建"，而是**屏幕真实 ramp 的 256 项读数**；
    // 逐帧在 ramp 空间线性插值 ⇒ 首帧(f=0)零跳变、末帧(f=1)精确命中目标。
    // ------------------------------------------------------------------
    /// <summary>逐屏过渡起点 ramp（键 = EDID）。</summary>
    private readonly Dictionary<string, NativeMethods.GammaRamp> _rampAnimStart = new();

    /// <summary>
    /// 过渡期间的"独占写屏"窗口。⚠️ 故意用**时间窗口**而不是纯布尔标志：
    /// 即使过渡因异常中断（窗体关闭/定时器被 Dispose 而未走收尾），窗口过期后
    /// 自动失效 ⇒ **绝不会**永久卡死常规写屏。
    /// </summary>
    private DateTime _rampTransitionUntil = DateTime.MinValue;

    /// <summary>独占窗口在过渡时长之外的余量（ms）。</summary>
    private const int RampTransitionGuardMs = 250;

    /// <summary>是否正在进行 ramp 空间过渡（供上层收尾判断）。</summary>
    public bool IsRampTransitionActive
    {
        get { lock (_lock) { return _rampAnimStart.Count > 0; } }
    }

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

    /// <summary>
    /// 该机驱动**是否需要**「每通道 ramp 峰值 ≥ 32768」的下限保护。
    /// ⚠ 这是**运行时探测结果**，不是编译期常量 —— 不同机器的 NVIDIA 驱动行为不同：
    /// 实测 RTX 4070 的 596.36 **强制**该限制（低于即整份拒收），
    /// GTX 1650 Ti 的 591.86 **无此限制**。若无条件套用保护，会在"不需要它"的机器上
    /// 把暗端的通道乘子抬到底线，**吃掉本来能生效的色温**（3.7.0 相对 3.6.0 的回归）。
    /// 默认 true = 保守（等价旧行为），启动时由探测结果覆盖。
    /// </summary>
    public static bool RampFloorNeeded { get; set; }

    /// <summary>
    /// 是否**由被动学习**刚刚翻转了 <see cref="RampFloorNeeded"/>（供上层落盘）。
    /// 上层读走并持久化后应置回 false。
    /// </summary>
    public static bool RampFloorLearned { get; set; }

    private readonly List<DeviceContext> _displays = new();

    /// <summary>
    /// F22（2026-09-22）：拓扑变化后「**立即**补救写屏」期间置 true ⇒
    /// <see cref="LearnRampFloor"/> 直接放弃学习。
    ///
    /// 原因：补救写屏发生在 `RefreshDisplays()` **之前**，此时 `_displays` 里可能仍含
    /// 「刚被拔掉、DC 已失效」的屏 ⇒ `SetDeviceGammaRamp` 必失败。若照常学习，这一次
    /// **假失败**会被当作「本机驱动有下限」⇒ 永久启用 `RampFloorNeeded`、把暗端色温
    /// 钳掉（用户可见的色彩损失），且已落盘、重启也不恢复。
    /// 真正的驱动下限仍由随后的常规写屏（`_displays` 已刷新为真实存在的屏）学习。
    /// </summary>
    private bool _suppressLearnRampFloor;
    private readonly object _lock = new();
    private bool _initialized;

    // ---------- Unified-mode state (PerMonitorEnabled=false) ----------
    private float _currentBrightness = 1.0f;   // UI brightness (0..1)
    private float _currentTemperature = DEFAULT_TEMPERATURE; // 色温 (K)

    // ---------- Per-monitor-mode state (PerMonitorEnabled=true) ----------
    // key = Monitor.EdidId; value = per-display (brightness, temperature)
    private readonly Dictionary<string, DisplayState> _displayStates = new();

    /// <summary>
    /// 正在被**过渡动画独占写屏**的屏。任何"全量写屏"路径都必须跳过它们 ——
    /// 否则周期性的 out-of-band 写屏（实测存在一个 ~2s 周期的全量写屏）会把动画
    /// 一帧抹平，用户看到的就是"跳变"（B2a 实测 Δ=0.2070/0.2207，一帧走完 94%）。
    /// </summary>
    private readonly HashSet<string> _animatingDisplays = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 当前持有的显示器（DeviceContext）数量。**只读诊断用**：
    /// F25-B 的后台重申若静默无效果，需要区分"没有屏可写"与"被暂停/过渡窗口吞掉"。
    /// </summary>
    public int DisplayCount
    {
        get { lock (_lock) return _displays.Count; }
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
            if (!IsWritable(s)) continue;   // 停用屏冻结不参与平均
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
            if (!IsWritable(s)) continue;
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
                return SnapshotOf(state);   // P0-3：返回快照，不外泄内部可变引用
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
            // P0-3（2026-09-23 代码审查）：此前直接包一层 ReadOnlyDictionary，
            // 但值仍是内部可变 DisplayState 引用 —— 调用方可绕过 _lock 改
            // Brightness/Paused/Enabled（并发下破坏状态一致性）。改为逐条快照。
            // 已逐一核对全部调用方（MainController / SettingsForm）均为只读消费，
            // 改快照不改变任何现有行为。
            var snap = new Dictionary<string, DisplayState>(_displayStates.Count);
            foreach (var kvp in _displayStates)
            {
                snap[kvp.Key] = SnapshotOf(kvp.Value);
            }
            return new ReadOnlyDictionary<string, DisplayState>(snap);
        }
    }

    /// <summary>
    /// P0-3：<see cref="DisplayState"/> 的**只读快照**（四个字段全拷贝，
    /// 含 Enabled / Paused —— GetMonitorDiagnostics 与逐屏过渡都读这两个）。
    /// </summary>
    private static DisplayState SnapshotOf(DisplayState s) => new()
    {
        Brightness = s.Brightness,
        Temperature = s.Temperature,
        Enabled = s.Enabled,
        Paused = s.Paused
    };

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
    	// 诊断：state 被整体重置时留痕（头号嫌疑）
    	OpLog.LogThrottled("resetstates",
    	    $"[gamma/reset] ResetDisplayStates(b={brightness * 100f:0}%, t={temperature:0}K) " +
    	        $"displays={_displays.Count} perMon={PerMonitorEnabled}", 400);
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
            // F9（2026-09-18）：**手动输入必须能打断在飞的平滑动画**。
            // 否则 `ApplyGamma*` 会被 F2 的独占写屏窗口静默吞掉（`DateTime.Now < _rampTransitionUntil`）⇒
            // "过渡进行的 1.45s 内滚轮/热键/拖动不生效，且调的那一档会丢"。
            // 只取消**采样与窗口**、不写屏 ⇒ 随后立即写入手动值，起点即屏幕当前真实值（不会跳）。
            CancelRampTransition(commitCurrentAsState: true);
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
                    if (!IsWritable(kvp.Value) || IsAnimating(kvp.Key)) continue;
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
            bool curChanged = SetBrightnessInternal(ref _currentBrightness, brightness);
            if (!PerMonitorEnabled)
            {
                // B1（2026-09-17）：值没变就别写屏 —— 与 SetTemperature 对称。
                // 否则 `SolarScheduler` 每 2 秒一次的 Tick（无条件调 SetBrightness）会
                // 触发一次全量写屏（实测日志每分钟恰好 30 条），既浪费又会打断过渡动画。
                if (curChanged) ApplyGamma();
            }
            else
            {
                // Set all ENABLED displays to the same absolute value
                bool anyChanged = curChanged;
                foreach (var kvp in _displayStates)
                    {
                        // 2026-09-20：判据由 IsWritable 改为 IsRecordable —— 绝对设定值
                        // **必须**写进被暂停屏的 state（仅记录意图、不写屏），否则该屏
                        // 离开暂停时只能恢复旧值，滑轨设定同步不过来。
                        if (!IsRecordable(kvp.Value) || IsAnimating(kvp.Key)) continue; // 停用屏冻结
                    var state = kvp.Value;
                    int percent = (int)Math.Round(brightness * 100);
                    percent = Math.Clamp(percent, 0, 100);
                    float snapped = percent / 100f;
                    if (snapped == state.Brightness) continue;
                    state.Brightness = snapped;
                    _displayStates[kvp.Key] = state;
                    anyChanged = true;
                }
                if (anyChanged) ApplyGammaAllDisplays();
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
            // F9（2026-09-18）：**手动输入必须能打断在飞的平滑动画**。
            // 否则 `ApplyGamma*` 会被 F2 的独占写屏窗口静默吞掉（`DateTime.Now < _rampTransitionUntil`）⇒
            // "过渡进行的 1.45s 内滚轮/热键/拖动不生效，且调的那一档会丢"。
            // 只取消**采样与窗口**、不写屏 ⇒ 随后立即写入手动值，起点即屏幕当前真实值（不会跳）。
            CancelRampTransition(commitCurrentAsState: true);

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
                        if (!IsWritable(kvp.Value) || IsAnimating(kvp.Key)) continue; // 停用屏冻结
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
                    // 2026-09-20：同 SetBrightness —— 暂停屏也要记录最新设定值（不写屏）。
                    if (!IsRecordable(kvp.Value) || IsAnimating(kvp.Key)) continue;   // 停用屏冻结
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

    /// <returns>是否**真的改变了**（同值返回 false）。</returns>
    private bool SetBrightnessInternal(ref float field, float brightness)
    {
        if (brightness == field) return false;
        int percent = (int)Math.Round(brightness * 100);
        percent = Math.Clamp(percent, 0, 100);
        field = percent / 100f;
        return true;
    }

    /// <summary>
    /// Applies current gamma to ALL displays.
    /// Unified mode: uses _currentBrightness/_currentTemperature.
    /// Per-monitor mode: uses each display's own state.
    /// </summary>
    private void ApplyGammaAllDisplays()
    {
        if (_displays.Count == 0) return;
        // F2（2026-09-17）：ramp 空间过渡期间**独占写屏** —— 期间一切"按 state 写常规值"的
        // 全量写屏一律跳过，否则会把过渡中的屏瞬间拉回 state 值（观感 = 闪一下）。
        // 触发源实例：`SolarScheduler` 每 2 秒 Tick 一次且**无条件**调 SetBrightness。
        // ⚠️ 用时间窗口（`_rampTransitionUntil`），异常中断时会自动过期 ⇒ 不会永久卡死写屏。
        if (DateTime.Now < _rampTransitionUntil) return;

        // ------------------------------------------------------------------
        // F14（2026-09-19）：**全局暂停期间，任何"按 state 全量重写"都必须写原生。**
        //
        // 此前本方法只看逐屏 `state.Paused` / `state.Enabled`，**不看全局 `_paused`** ——
        // 于是「功能停用 / 全屏暂停 / 白名单」生效时，只要有人调 `ReapplyAllDisplays()`
        // （逐屏过渡收尾 / 白名单过渡收尾 / 拔插屏）就会把屏幕按 `state` 覆盖成设定值。
        //
        // 实测（09-19 22:11:5x 日志，用户报「永久停用却是暖色调」）：
        //   `[22:11:53.994] push B=100% T=6600K paused=True (disable=True)`  ← 永久停用生效
        //   `[22:11:53.996] [disableAnim] exit=False start=85%/3900K -> 100%/6600K` ← 平滑回原生 ✓
        //   `[22:11:57.251] [fs/permon] exit left=[UGRFFFF]`                 ← 全屏退出
        //   `[22:11:58.465] [gamma/apply] per SAC2466: stateB=85 stateT=3900` ← **写回 Solar 暖色** ✗
        //   （时间戳精确吻合：全屏退出 + 1.2s 逐屏过渡 = 收尾时的这一次全量写屏）
        //
        // ⇒ 与 `ApplyPausedState()`（暂停时对所有屏 ResetGamma）语义对齐。
        // ⚠️ 只在**全局** `_paused` 时短路；"部分屏逐屏暂停"（`_paused == false`）
        //    仍走下面的逐屏分支（那里已有 `state.Paused → ResetGamma`）。
        // ------------------------------------------------------------------
        if (_paused)
        {
            foreach (var display in _displays)
            {
                if (IsAnimating(display.MonitorEdidId)) continue;   // 动画独占写屏（过渡收尾由它自己落定）
                display.ResetGamma();
            }
            return;
        }

        if (!PerMonitorEnabled)
        {
            var ramp = BuildGammaRamp(_currentBrightness, _currentTemperature);
            foreach (var display in _displays)
            {
                if (IsAnimating(display.MonitorEdidId)) continue;   // 动画独占写屏
                // 2026-09-16：统一模式同样要尊重"停用"标记。此前无条件对所有屏写 ramp，
                // 于是设置页里停用的输出（例如拔掉显卡欺骗器后 Windows 仍报 Active=True 的
                // 幻影输出 UGRFFFF）每次调节都被写一遍。独立模式原本就有这条 continue，这里对齐。
                if (_displayStates.TryGetValue(display.MonitorEdidId, out var uniState))
                {
                    if (uniState.Paused) { display.ResetGamma(); continue; }  // 逐屏暂停：强制回原生（优先于 Enabled）
                    if (!uniState.Enabled) continue;
                }
                // 诊断（2026-09-17）：统一模式分支此前**无日志** ⇒「日志没有」≠「没写屏」。
                // 不节流：实测存在 10ms 间隔的"下探+跳回"，节流会丢掉关键帧。
                bool uniOk = display.SetGamma(ramp);
                OpLog.Log($"[gamma/apply] uni {display.MonitorEdidId}: curB={_currentBrightness * 100f:0} " +
                          $"curT={_currentTemperature:0} R255={ramp.Red[255]} B255={ramp.Blue[255]} ok={uniOk}");
                if (!uniOk) LearnRampFloor(display, _currentBrightness, _currentTemperature);
            }
        }
        else
        {
            foreach (var display in _displays)
            {
                var edidId = display.MonitorEdidId;
                if (IsAnimating(edidId)) continue;   // 动画独占写屏
                if (string.IsNullOrEmpty(edidId) || !_displayStates.TryGetValue(edidId, out var state))
                    state = new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
                // 诊断：定位"屏幕被写回原生"的来源（正式版 OpLog 空实现，零开销）
                OpLog.LogThrottled("apply.per",
                    $"[gamma/apply] per {edidId}: Paused={state.Paused} Enabled={state.Enabled} " +
                    $"stateB={state.Brightness * 100f:0} stateT={state.Temperature:0} " +
                    $"anim={IsAnimating(edidId)} curB={_currentBrightness * 100f:0} curT={_currentTemperature:0}", 400);
                if (state.Paused) { display.ResetGamma(); continue; }  // 逐屏暂停：强制回原生（优先于 Enabled）
                if (!state.Enabled) continue;                          // 停用屏冻结，不写 gamma
                var ramp = BuildGammaRamp(state.Brightness, state.Temperature);
                if (!display.SetGamma(ramp)) LearnRampFloor(display, state.Brightness, state.Temperature);
            }
        }
    }

    /// <summary>
    /// Applies gamma to ONE display identified by EdidId.
    /// </summary>
    private void ApplyGamma(string edidId)
    {
        if (_displays.Count == 0) return;
        if (DateTime.Now < _rampTransitionUntil) return;   // F2：过渡期间独占写屏（同上）
        var display = _displays.FirstOrDefault(d =>
            string.Equals(d.MonitorEdidId, edidId, StringComparison.OrdinalIgnoreCase));
        if (display == null) return;
        if (!_displayStates.TryGetValue(edidId, out var state))
            state = new DisplayState { Brightness = _currentBrightness, Temperature = _currentTemperature };
        // 2026-09-20：单屏路径补齐与 ApplyGammaAllDisplays 逐屏分支**相同**的判据。
        // 此前只检查全局 `_paused`（在调用方 SetBrightness/SetTemperature(edid,…) 里），
        // 漏了**逐屏**暂停 —— 部分屏被白名单/全屏暂停时 `_paused == false`，
        // 这条路径会把暂停屏直接写成 state 值（＝破坏暂停）。
        // 与全量路径对齐后：暂停屏强制回原生、停用屏不写，两条路径语义一致。
        if (state.Paused) { display.ResetGamma(); return; }
        if (!state.Enabled) return;
        var ramp = BuildGammaRamp(state.Brightness, state.Temperature);
        bool oneOk = display.SetGamma(ramp);
        OpLog.Log($"[gamma/apply] one {edidId}: stateB={state.Brightness * 100f:0} stateT={state.Temperature:0} " +
                  $"R255={ramp.Red[255]} B255={ramp.Blue[255]} ok={oneOk}");
        if (!oneOk) LearnRampFloor(display, state.Brightness, state.Temperature);
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

        // ------------------------------------------------------------------
        // 驱动下限保护（2026-09-16）。
        // 实测：本机驱动要求**每个通道**的 ramp 峰值 >= 32767（≈65535×0.5）——
        // 逐档扫描证据（3900K）：60% 时 B[255]=33179 通过；56% 时 B[255]=32351 被拒。
        // 一旦任一通道越界，SetDeviceGammaRamp 返回 FALSE 且 GetLastError()=0，
        // 整条 ramp 不生效 → 屏幕卡在最后一个成功的值上，只能升不能降。
        //
        // 成因：亮度系数 physical∈[0.5,1] 与色温通道乘子**相乘**（同一张 3×256 查色表），
        // 暖色的蓝乘子(3900K≈0.633、3300K≈0.507)、冷色的红乘子(10000K≈0.202) 再乘
        // 一次亮度就跌破下限。关掉色温（三通道等比）时不发生，与实测吻合。
        //
        // 处置（用户 2026-09-16 定方向：**优先保亮度**，色温下限可往上移）：
        // 把低于下限的乘子**逐个抬到刚好达标**，不动已达标的通道。
        // 亮度由最大通道（red）决定 ⇒ 亮度保持准确；色温在暗端被削弱，
        // 等价于"低亮度下调不到那么暖/那么冷"。
        // ------------------------------------------------------------------
        // 仅当**该机驱动确实需要**时才钳制（RampFloorNeeded 由启动探测决定）。
        if (RampFloorNeeded)
        {
        	double required = PHYSICAL_MIN / physical;
        	bool lowBoundClamped = false;
        	if (redMul < required) { redMul = required; lowBoundClamped = true; }
        	if (greenMul < required) { greenMul = required; lowBoundClamped = true; }
        	if (blueMul < required) { blueMul = required; lowBoundClamped = true; }
        	if (lowBoundClamped)
        	{
        		OpLog.LogThrottled("ramp.lowbound",
        		$"[gamma] 驱动下限保护：亮度 {brightness * 100f:0}% 需通道乘子 >= {required:0.###}；" +
        		$"实发 R={redMul:0.###} G={greenMul:0.###} B={blueMul:0.###}（色温 {temperature:0}K 在暗端被弱化）",
        		2000);
        	}
        }

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
    return RampToBrightness(ReadRamp(), _currentBrightness);
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
    return RampToTemperature(ReadRamp(), _currentTemperature);
    }
    }

    /// <summary>
    /// 按 **EDID 实例 ID** 读取该屏当前**实际生效**的亮度/色温（UI 0..1 / K）。
    /// 与 ReadCurrent* 的区别：后者固定读第一台显示器（ReadRamp 取 _displays[0]），
    /// 无法检测"看不见的第二屏"（显卡欺骗器 / 接了但不亮的副屏）。
    /// 多屏排查的唯一客观口径 —— gamma 内部 state 只是"想要的值"，
    /// ramp 才是"屏幕真的在显示的值"。找不到该屏时返回 NaN。
    /// </summary>
    public (float Brightness, float Temperature) ReadActualFor(string edidId)
    {
        lock (_lock)
        {
            foreach (var display in _displays)
            {
                if (!string.Equals(display.MonitorEdidId, edidId, StringComparison.OrdinalIgnoreCase)) continue;
                var r = display.GetCurrentRamp();
                return (RampToBrightness(r, 1f), RampToTemperature(r, _currentTemperature));
            }
            return (float.NaN, float.NaN);
        }
    }

    /// <summary>
    /// F2（2026-09-17）：采样各屏**当前真实 ramp** 作为过渡起点，并开启独占写屏窗口。
    /// 返回参与过渡的屏数（0 ⇒ 无可用起点，调用方应回退到参数插值）。
    ///
    /// ⚠️ 调用时机必须在"未启用平滑的轴已立即写到位"**之后** —— 这样起点 ramp 已包含
    /// 该轴的最终值，保持原有的"该轴立即变化"语义。
    /// ⚠️ 与 `IsTargetRampOnScreen` 同一套过滤：动画中的屏、Paused / 停用屏、读不到 ramp 的屏
    /// 一律不参与（它们保持不动）。
    /// </summary>
    public int BeginRampTransition(float targetBright, float targetTemp, int durationMs, bool allowWhilePaused = false)
    {
        lock (_lock)
        {
            _rampAnimStart.Clear();
            _rampTransitionUntil = DateTime.MinValue;
            if ((_paused && !allowWhilePaused) || _displays.Count == 0) return 0;

            foreach (var display in _displays)
            {
                string edidId = display.MonitorEdidId;
                if (IsAnimating(edidId)) continue;
                if (!string.IsNullOrEmpty(edidId) && _displayStates.TryGetValue(edidId, out var st))
                {
                    if (st.Paused || !st.Enabled) continue;
                }
                if (!display.TryGetCurrentRamp(out var cur)) continue;
                _rampAnimStart[edidId] = cur;
            }

            if (_rampAnimStart.Count == 0)
            {
                OpLog.Log("[ramp] BeginRampTransition: 无可用起点（屏不可写或读不到 ramp）⇒ 回退参数插值");
                return 0;
            }

            _rampTransitionUntil = DateTime.Now.AddMilliseconds(Math.Max(0, durationMs) + RampTransitionGuardMs);
            OpLog.Log($"[ramp] 过渡起点已采样：{_rampAnimStart.Count} 屏，逐屏 ramp 空间插值" +
                      $"（独占窗口 {Math.Max(0, durationMs) + RampTransitionGuardMs}ms）");
            return _rampAnimStart.Count;
        }
    }

    /// <summary>
    /// F2：按 f∈[0,1] 逐屏写"起点 ramp → 目标 ramp"的线性插值。
    /// f=0 ⇒ 屏幕当前值（零跳变）；f=1 ⇒ 目标 ramp（精确命中，无末帧跳）。
    /// </summary>
    public void StepRampTransition(float targetBright, float targetTemp, double f, bool allowWhilePaused = false,
                                bool ignoreAnimating = false)
    {
        lock (_lock)
        {
            if (_rampAnimStart.Count == 0) return;
            if (DateTime.Now >= _rampTransitionUntil)      // 兜底：窗口过期 ⇒ 放弃本帧
            {
                _rampAnimStart.Clear();
                return;
            }
            if (_paused && !allowWhilePaused) return;                            // 与 SetBrightness/SetTemperature 的暂停语义一致

            if (f < 0.0) f = 0.0; else if (f > 1.0) f = 1.0;
            var target = BuildGammaRamp(targetBright, targetTemp);

            foreach (var display in _displays)
            {
                string edidId = display.MonitorEdidId;
                if (!_rampAnimStart.TryGetValue(edidId, out var start)) continue;
                // F7：`ignoreAnimating` 供"本动画自己登记了独占写屏"的调用方使用
                //（`StartFullscreenTransition` 会把全部屏登记为 animation-exclusive）。
                // 若无条件跳过，那些屏一帧都不会被写 ⇒ 屏幕卡在起点。
                if (!ignoreAnimating && IsAnimating(edidId)) continue;
                if (!string.IsNullOrEmpty(edidId) && _displayStates.TryGetValue(edidId, out var st))
                {
                    if (st.Paused || !st.Enabled) continue;   // 中途变 Paused/停用 ⇒ 立即不再写它
                }
                if (!display.SetGamma(LerpRamp(start, target, f)))
                {
                    LearnRampFloor(display, targetBright, targetTemp);
                }
            }
        }
    }

    /// <summary>
    /// F2/F3：过渡收尾。末帧(f=1)已精确等于目标 ramp ⇒ 只同步内部 state，**不再额外写屏**
    /// （这就是原末帧 Δ640 硬跳的来源）。同时关闭独占窗口。
    /// </summary>
    public void EndRampTransition(float targetBright, float targetTemp, bool commitState = true)
    {
        lock (_lock)
        {
            bool wasActive = _rampAnimStart.Count > 0;
            _rampAnimStart.Clear();
            _rampTransitionUntil = DateTime.MinValue;      // 立即释放独占，供自检兜底写屏
            if (!wasActive) return;
            if (commitState) CommitTargetStateWithoutWrite(targetBright, targetTemp);
        }
    }

    /// <summary>
    /// Bug A（2026-09-17）：放弃在途的 ramp 过渡 —— 清掉起点采样、关闭独占写屏窗口。
    /// **不提交 state、不写屏**，屏幕停在当前那一帧；之后 `BeginRampTransition` 会重新
    /// 采样该帧作为新起点，所以调用方可以放心地「取消后立刻重开一段过渡」。
    /// 用于必须以「屏幕当前真实值」为基准重新决策的路径（重开色温总开关）。
    /// </summary>
    public void CancelRampTransition(bool commitCurrentAsState = false)
    {
        lock (_lock)
        {
            _rampAnimStart.Clear();
            _rampTransitionUntil = DateTime.MinValue;
            if (commitCurrentAsState)
            {
                // F10（2026-09-18）：把**屏幕当前实际值**提交为 `_current*` 基准（不写屏）。
                // 这样手动步进是"从你看到的位置继续"：动画走到 70% 时按 +5% ⇒ 75%，再按 ⇒ 80%，
                // 而不是用"动画开始前提交的陈旧值"（会凭空掉一个档位跨度），也不是用"动画目标"（会跳）。
                //
                // F10-fix（2026-09-18 上午）：色温**先 snap 到 100K 网格**再提交。
                //   `ReadCurrentTemperature()` 给出的是**连续**反算值（动画中间帧尤其如此）
                //   ⇒ 直接当基准会脱离产品约定（色温步进 100K），并被 `MainController.SaveSettings()`
                //   落盘成 6558 / 6658 / 6331 … 这类非法值。
                //   实测轨迹（连按 8 次「+色温」）：6600 → 6658 → 6331 → 6431 → 6531 → 6631 → 6381
                //   → 6481 → 6581（越走越低，且把配置写脏）。
                //   亮度侧无需处理：`CommitTargetStateWithoutWrite` 内部已 snap 到 1%。
                float readT = ReadCurrentTemperature();
                float snapT = (float)(Math.Round(readT / 100.0, MidpointRounding.AwayFromZero) * 100.0);
                snapT = Math.Clamp(snapT, MinTemperature, MaxTemperature);
                CommitTargetStateWithoutWrite(ReadCurrentBrightness(), snapT);
            }
        }
    }

    /// <summary>
    /// F4c（2026-09-17）：把统一目标 (亮度, 色温) **一次**写到屏幕。
    ///
    /// 为什么需要它：模式切换收尾时 `_currentBrightness` / `_currentTemperature` 里
    /// 可能有一个是**陈旧值**（独立模式期间被 `SolarScheduler` 写过）。原来的
    /// `SetBrightness()` + `SetTemperature()` 两次调用会先写出一张
    /// 「新亮度 + 陈旧色温」的中间 ramp（实测 `curB=100 curT=3900 R255=65535 B255=41476`），
    /// 2ms 后才被第二句纠正 —— 一帧错色温的瞬写。
    ///
    /// 本方法先把两个分量对齐（`CommitTargetStateWithoutWrite`），再只写屏一次，
    /// 从根上消掉这张中间 ramp。
    /// </summary>
    public void ApplyUnifiedTarget(float brightness, float temperature)
    {
        lock (_lock)
        {
            if (_paused) return;
            CommitTargetStateWithoutWrite(brightness, temperature);
            ApplyGammaAllDisplays();
        }
    }

    /// <summary>逐项在 ramp 空间线性插值。f=1 时**精确**等于 b（double 运算下无累积误差）。</summary>
    private static NativeMethods.GammaRamp LerpRamp(NativeMethods.GammaRamp a, NativeMethods.GammaRamp b, double f)
    {
        var outRamp = new NativeMethods.GammaRamp
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        for (int i = 0; i < 256; i++)
        {
            outRamp.Red[i] = (ushort)Math.Clamp(a.Red[i] + (b.Red[i] - a.Red[i]) * f, 0.0, 65535.0);
            outRamp.Green[i] = (ushort)Math.Clamp(a.Green[i] + (b.Green[i] - a.Green[i]) * f, 0.0, 65535.0);
            outRamp.Blue[i] = (ushort)Math.Clamp(a.Blue[i] + (b.Blue[i] - a.Blue[i]) * f, 0.0, 65535.0);
        }
        return outRamp;
    }

    /// <summary>
    /// F1（2026-09-17）：判断「目标 (亮度, 色温) 对应的 ramp」是否与**屏幕上实际生效的 ramp**
    /// 实质相同。用于在过渡入口短路「净变化为 0 的过渡伪影」。
    ///
    /// 为什么必须比 ramp，而不是比参数：
    ///   平滑过渡的起点是 `ReadCurrentBrightness()/ReadCurrentTemperature()` 从屏幕 ramp
    ///   **反算**出来的参数，再经 `BuildGammaRamp()` **正向重建**回 ramp —— 这一对映射
    ///   **不可往返**。实测：identity ramp 被反算成 6558K，而 `BuildGammaRamp(6558K)` 的
    ///   蓝乘子 ≈0.9866 ⇒ 首帧把屏幕硬推"偏暖 1.34%"(Δ876)、中间 20 帧慢慢回来、
    ///   末帧再由落定写入弹回(Δ640) ⇒ **净变化为 0** 却产生「眨眼偏暖 → 慢回 → 眨眼弹回」
    ///   的可见闪变（用户 2026-09-17 报的"开关闪 / 启动闪"）。
    ///   ⇒ 参数相同 **≠** ramp 相同；**ramp 相同才是真的没有变化**。
    ///
    /// 保守返回 false 的情形（一律走原路径，行为完全不变）：
    ///   · 暂停中（`_paused`）—— 写屏本就被吞，不做新判断
    ///   · 没有任何显示器
    ///   · 任一屏读不到 ramp（读不到就不下结论）
    ///   · 任一屏处于 `Paused` 或 `!Enabled`（这两态的目标语义特殊：Paused 写原生 / 停用不写）
    ///   · 任一屏正在做动画过渡（动画独占写屏）
    /// </summary>
    public bool IsTargetRampOnScreen(float brightness, float temperature, bool allowWhilePaused = false)
    {
        lock (_lock)
        {
            if (_paused && !allowWhilePaused) return false;
            if (_displays.Count == 0) return false;
            var target = BuildGammaRamp(brightness, temperature);
            foreach (var display in _displays)
            {
                string edidId = display.MonitorEdidId;
                if (IsAnimating(edidId)) return false;
                if (!string.IsNullOrEmpty(edidId) && _displayStates.TryGetValue(edidId, out var st))
                {
                    if (st.Paused || !st.Enabled) return false;
                }
                if (!display.TryGetCurrentRamp(out var current)) return false;
                if (!RampEquals(current, target, RampEqTolerance)) return false;
            }
            return true;
        }
    }

    /// <summary>逐项比较两张 ramp 是否在容差内一致（含峰值索引 —— 峰值也参与判定）。</summary>
    private static bool RampEquals(NativeMethods.GammaRamp a, NativeMethods.GammaRamp b, int tolerance)
    {
        if (a.Red == null || a.Green == null || a.Blue == null) return false;
        if (b.Red == null || b.Green == null || b.Blue == null) return false;
        for (int i = 0; i < 256; i++)
        {
            if (Math.Abs(a.Red[i] - b.Red[i]) > tolerance) return false;
            if (Math.Abs(a.Green[i] - b.Green[i]) > tolerance) return false;
            if (Math.Abs(a.Blue[i] - b.Blue[i]) > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// F1 配套：把内部目标状态对齐到 (b, t)，但**不写屏**。
    /// 只允许在 `IsTargetRampOnScreen` 返回 true（屏幕 ramp 已等于该目标）之后调用
    /// ⇒ 不写屏不会造成任何画面偏差。
    /// 统一模式改 `_current*`；逐屏模式同步各可控屏的 state（与 SetBrightness/SetTemperature
    /// 的"整批设同值"语义一致），保证后续 ReapplyAllDisplays 写出的仍是同一张 ramp。
    /// ⚠️ 不动 `!Enabled` / 动画中的屏 —— 与 IsRecordable + IsAnimating 同一判据
    /// （2026-09-20：**暂停屏也要记**，理由见 <see cref="IsRecordable"/>）。
    /// </summary>
    public void CommitTargetStateWithoutWrite(float brightness, float temperature)
    {
        lock (_lock)
        {
            int percent = (int)Math.Round(brightness * 100);
            percent = Math.Clamp(percent, 0, 100);
            _currentBrightness = percent / 100f;
            _currentTemperature = (float)Math.Round(Math.Clamp(temperature, MinTemperature, MaxTemperature));
            if (!PerMonitorEnabled) return;
            foreach (var kvp in _displayStates)
            {
                if (!IsRecordable(kvp.Value) || IsAnimating(kvp.Key)) continue;
                kvp.Value.Brightness = _currentBrightness;
                kvp.Value.Temperature = _currentTemperature;
            }
        }
    }

    /// <summary>从 ramp 峰值反推 UI 亮度（0..1）。调用方须已持 _lock。</summary>
    private static float RampToBrightness(NativeMethods.GammaRamp ramp, float fallback)
    {
        double redPeak = ramp.Red[255];
        double bluePeak = ramp.Blue[255];
        double peak = Math.Max(redPeak, bluePeak);
        if (peak <= 0) return fallback;
        double physical = peak / 65535.0;
        double ui = (physical - PHYSICAL_MIN) / (1.0 - PHYSICAL_MIN);
        return (float)Math.Clamp(ui, 0.0, 1.0);
    }

    /// <summary>从 ramp 三通道比例反推色温（K）。调用方须已持 _lock。
    /// 2026-09-18 修正：分支判据改为比较 **greenMul 与 blueMul**，并给中性点特判
    /// ⇒ 3300–10000K 全程往返误差 0.0K（见 `_devtools/verify_roundtrip_6600.py`）。
    /// ⚠️ 仍有两处会失真，调用方须知：①驱动下限保护改写过 ramp（暗端色温被削弱）；
    /// ②过渡动画的中间帧（ramp 空间插值，不是任何"设定值"）。</summary>
    private static float RampToTemperature(NativeMethods.GammaRamp ramp, float fallback)
    {
        double r = ramp.Red[255], g = ramp.Green[255], b = ramp.Blue[255];
        double peak = Math.Max(r, Math.Max(g, b));
        if (peak <= 0) return fallback;
        double redMul = r / peak;
        double greenMul = g / peak;
        double blueMul = b / peak;

        // ------------------------------------------------------------------
        // 分支修正（2026-09-18）—— 原判据 `redMul >= blueMul` 在 **(6600, 6688)K** 走错侧：
        //   `GetRedMultiplier` 在该区间公式值 >1，被 `Math.Clamp(...,0,1)` 压成 1.0
        //   ⇒ redMul == blueMul == 1.0 ⇒ 判据成立 ⇒ 用**暖侧**绿反函数去解一个**冷侧**的 ramp。
        //   后果：identity 反算成 6558K（应为 6600）；6658K 反算成 6231K（方向相反）；
        //         该带 13 个 10K 采样点不可往返，最大偏差 487K。
        //
        // 改用 **绿 vs 蓝** 判侧（与 Tanner Helland 公式的分界一致）：
        //   · greenMul > blueMul ⇒ 暖侧 ⇒ 原暖侧绿反函数
        //   · greenMul < blueMul ⇒ 冷侧 ⇒ **冷侧**绿反函数（原代码此处分错侧）
        //   · 三通道相等（identity）⇒ 中性点 ⇒ 直接返回 6600（产品定义的白色点）
        // ------------------------------------------------------------------
        const double Eps = 1e-9;
        if (greenMul > blueMul + Eps)
        {
            double x = (greenMul * 255.0 + 161.1195681661) / 99.4708025861;
            return (float)Math.Clamp(100.0 * Math.Exp(x), MIN_TEMPERATURE, MAX_TEMPERATURE);
        }
        if (greenMul < blueMul - Eps)
        {
            double z = greenMul * 255.0 / 288.1221695283;
            double cold = 60.0 + Math.Pow(z, 1.0 / -0.0755148492);
            return (float)Math.Clamp(100.0 * cold, MIN_TEMPERATURE, MAX_TEMPERATURE);
        }
        return (float)Math.Clamp(DEFAULT_TEMPERATURE, MIN_TEMPERATURE, MAX_TEMPERATURE);
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
    /// <summary>
    /// 逐屏**写屏**统一判据（2026-09-16 逐屏白名单）：停用=冻结、暂停=回原生，两者都不写屏。
    /// ⚠ 新增任何写值路径都必须复用本判据 —— 本项目已两次因"新增路径漏判"踩坑。
    /// </summary>
    private static bool IsWritable(DisplayState s) => s.Enabled && !s.Paused;

    /// <summary>
    /// 逐屏**记录意图**统一判据（2026-09-20 用户需求）：值该不该写进 `state`（＝用户想要的值）。
    ///
    /// 与 <see cref="IsWritable"/>（值该不该写进**屏幕**）刻意分开，两者语义不同：
    /// - **手动停用**的屏：用户明确不想让它受控 ⇒ 连 state 都不记（保持既有行为）。
    /// - **白名单 / 全屏暂停**的屏：它**仍然是受控屏**，只是此刻被强制回原生
    ///   ⇒ **必须记录用户的最新设定**。否则离开暂停时只能恢复到"进入暂停前的旧值"，
    ///   而暂停期间用户拖过的滑轨设定**永远同步不到这块屏上**
    ///   （用户 2026-09-20 实测反馈："当有屏幕的白名单停止生效时，那么滑轨的设定
    ///   也应该同步到该显示器上，其他全屏暂停也是一样的道理"）。
    ///
    /// ⚠️ 写屏侧**无需**额外保护：`ApplyGammaAllDisplays()` 的逐屏分支里
    ///   `if (state.Paused) { display.ResetGamma(); continue; }` 排在
    ///   `if (!state.Enabled) continue;` **之前** ⇒ 暂停屏照样回原生，不会被写成设定值。
    /// </summary>
    private static bool IsRecordable(DisplayState s) => s.Enabled;

    /// <summary>
    /// 设置逐屏暂停标志（纯运行时）。Paused 优先于 Enabled：即使该屏被手动停用，
    /// 白名单命中它时照样暂停（回原生 + 不参与平均）—— 用户 R2 决策。
    /// </summary>
    /// <summary>
    /// 设置"动画独占写屏"的屏集合（过渡动画期间调用）。这些屏会被所有全量写屏路径跳过，
    /// 由动画自己逐帧写 —— 这是"模式切换瞬间不被周期性写屏抹平"的关键。
    /// 传 null/空集合表示过渡结束，恢复常规写屏。
    /// </summary>
    public void SetAnimatingDisplays(IEnumerable<string>? ids)
    {
        lock (_lock)
        {
            _animatingDisplays.Clear();
            if (ids == null) return;
            foreach (string id in ids)
            {
                if (!string.IsNullOrEmpty(id)) _animatingDisplays.Add(id);
            }
        }
    }

    /// <summary>该屏是否正被过渡动画独占（调用方须已持有 _lock）。</summary>
    private bool IsAnimating(string? edidId)
        => !string.IsNullOrEmpty(edidId) && _animatingDisplays.Contains(edidId!);

    /// <returns>是否**真的改变了**（已同值返回 false）。调用方据此检测"失同步"。</returns>
    public bool SetDisplayPaused(string edidId, bool paused)
    {
        lock (_lock)
        {
            if (!_displayStates.TryGetValue(edidId, out var state)) return false;
            if (state.Paused == paused) return false;
            state.Paused = paused;
            _displayStates[edidId] = state;
            return true;
        }
    }

    /// <summary>
    /// 逐屏帧写：把 (brightness, temperature) 直接写到指定屏，**不改 state、不受 Paused 阻挡**。
    /// 供逐屏暂停的进入/离开过渡动画使用（思路同 <see cref="ApplyPausedFrame"/>）。
    /// </summary>
    public void ApplyFrameToDisplay(string edidId, float brightness, float temperature)
    {
        lock (_lock)
        {
            foreach (var display in _displays)
            {
                if (!string.Equals(display.MonitorEdidId, edidId, StringComparison.OrdinalIgnoreCase)) continue;
                display.SetGamma(BuildGammaRamp(brightness, temperature));
                return;
            }
        }
    }

    /// <summary>把指定屏写回原生 ramp（逐屏版；暂停过渡收尾用）。</summary>
    public void ResetDisplayNative(string edidId)
    {
        lock (_lock)
        {
            foreach (var display in _displays)
            {
                if (!string.Equals(display.MonitorEdidId, edidId, StringComparison.OrdinalIgnoreCase)) continue;
                display.ResetGamma();
                return;
            }
        }
    }

    /// <summary>按当前 state 重写全部屏（Paused→原生 / 停用→冻结 / 其余→state）。逐屏过渡收尾调用。</summary>
    public void ReapplyAllDisplays()
    {
        lock (_lock)
        {
            ApplyGammaAllDisplays();
        }
    }

    /// <summary>
    /// F22（2026-09-22）：**拓扑变化后的"立即"补救写屏**。语义与
    /// <see cref="ReapplyAllDisplays"/> 完全一致（同样尊重全局暂停 → 原生、逐屏暂停 →
    /// 原生、停用屏 → 不写），唯一差别是**期间不学习驱动下限**
    /// （见 <see cref="_suppressLearnRampFloor"/>）。
    ///
    /// 用途：`DisplaySettingsChanged`（拔插屏 / 改分辨率）时 Windows/驱动会把各屏 ramp
    /// **复位为原生（≈100%）**。而 `RefreshDisplays()`（重新枚举 + 新建 DC，实测 ~0.9 s、
    /// 极端 9.7 s）远慢于写屏 ⇒ 若沿用老顺序（先刷新、后写屏），低亮度下会出现
    /// **0.9~1.5 s** 的"屏幕挂在原生亮度"空窗 —— 就是用户看到的「拔插屏闪一下」。
    /// 本方法让调用方**先**把已知可写的屏拉回设定值（几十毫秒），再去做昂贵的枚举。
    /// </summary>
    public void ReapplyAllDisplaysForTopologyChange()
    {
        lock (_lock)
        {
            bool prev = _suppressLearnRampFloor;
            _suppressLearnRampFloor = true;
            try
            {
                ApplyGammaAllDisplays();
            }
            finally
            {
                _suppressLearnRampFloor = prev;
            }
        }
    }

    /// <summary>
    /// 写屏失败后的**被动学习**：说明本机驱动拒绝这份 ramp ⇒ 启用「驱动下限保护」并用受保护 ramp 重写一次。
    ///
    /// ⚠ 为什么不主动探测：主动探测必须写一份"低峰值测试 LUT"，屏幕会**闪一下**。
    /// 而 `Display.SetGamma` 本来就返回成功/失败 ⇒ **真实写入被拒的那一刻即可判定**，
    /// 零额外写屏。大多数机器在**启动第一次写屏**（用户设定值）时就能判出来。
    /// </summary>
    private void LearnRampFloor(DeviceContext display, float brightness, float temperature)
    {
        if (RampFloorNeeded) return;      // 已启用保护仍失败 ⇒ 是别的原因，不反复重试
        // F22（2026-09-22）：补救写屏期间禁学 —— 那时 `_displays` 可能含已失效的 DC，
        // 写入必失败，学习会把「假失败」当成本机驱动有下限（见 _suppressLearnRampFloor）。
        if (_suppressLearnRampFloor) return;
        RampFloorNeeded = true;
        RampFloorLearned = true;
        OpLog.Log($"[gamma] 写屏被驱动拒绝（亮度 {brightness * 100f:0}% 色温 {temperature:0}K）" +
                  $" ⇒ 自动启用『驱动下限保护』并用受保护 ramp 重写");
        display.SetGamma(BuildGammaRamp(brightness, temperature));
    }

    /// <summary>
    /// 探测该机驱动是否存在「每通道 ramp 峰值 ≥ 32768」限制。
    /// 判别器 = 线性 LUT (32768, 32768, **32000**)：
    /// 限制型驱动（本机 RTX 4070 596.36）会**整份拒收** → 返回 false；
    /// 宽松驱动（笔记本 GTX 1650 Ti 591.86）接受 → 返回 true。
    /// 这与 `_devtools\probe_crossmachine.py` 的 6 组判别完全一致，可回归验证。
    /// </summary>
    /// <param name="restoreAfter">true=探测后立刻按当前状态恢复真实 ramp（会多写一次屏）。</param>
    /// <returns>true=驱动接受；false=驱动拒绝；null=无法探测（无可用屏）。</returns>
    public bool? ProbeRampFloorLimit(bool restoreAfter = true)
    {
        lock (_lock)
        {
            if (_displays.Count == 0) return null;

            // 优先挑一块"启用中"的屏；都停用就取第一块。
            DeviceContext? target = null;
            foreach (var d in _displays)
            {
                if (_displayStates.TryGetValue(d.MonitorEdidId, out var st) && st.Enabled) { target = d; break; }
            }
            target ??= _displays[0];

            var probe = new NativeMethods.GammaRamp
            {
                Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256]
            };
            for (int i = 0; i < 256; i++)
            {
                probe.Red[i] = (ushort)(32768.0 * i / 255.0);
                probe.Green[i] = (ushort)(32768.0 * i / 255.0);
                probe.Blue[i] = (ushort)(32000.0 * i / 255.0);
            }

            bool accepted = target.SetGamma(probe);
            OpLog.Log($"[gamma] 驱动下限探测：探针 LUT (32768/32768/32000) → " +
                      $"{(accepted ? "被接受" : "被拒绝")}（屏 {target.MonitorEdidId}）");

            if (restoreAfter) ApplyGammaAllDisplays();
            return accepted;
        }
    }

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
        // ------------------------------------------------------------------
        // F25-A（2026-09-23）：**把慢枚举移出临界区**。
        //
        // 实测（用户物理拔欺骗器，`00:30:03`）：`Monitor.GetAll()` 在拓扑 churn 期要
        //   **4.33 s**（另一轮 5.0 s / 更早一轮 9.7 s），而原实现把它整个罩在
        //   `lock (_lock)` 里 ⇒ 这 4.3 s 内**任何其它写屏路径都拿不到锁**：
        //   F22 的"立即写"已在 churn 期被驱动拒收，而后续重试全被这把锁挡在门外
        //   ⇒ 屏幕以原生亮度停留 4.3 s（用户观感 = 拔欺骗器时"数次闪屏"）。
        //
        // 修法：锁**只护状态替换**，不护枚举本身。`Monitor.GetAll()` 与
        //   `TryCreateDeviceContext()` 都在锁外完成，拿到结果后再进锁一次性替换
        //   ⇒ 临界区从 ~4.3 s 缩到 ~0 ms，写屏路径随时能进来。
        //
        // ⛔ 替换仍在锁内完成，且顺序固定「**先 Dispose 旧的 → 再装新的**」，
        //    保证任何 reader 看到的 `_displays` 永远是完整可用的列表。
        // ⛔ `PerMonitorSeed()` 要求"调用方已持有 _lock" ⇒ 播种必须留在锁内。
        // ⛔ 若在锁外期间别人已经刷新过，这里会以"后到的枚举"覆盖 —— 结果仍然自洽
        //    （本方法仅由 UI 线程的 `OnDisplayChanged` / `OnSystemResumed` 调用，实际不会并发）。
        // ------------------------------------------------------------------
        var monitors = Monitor.GetAll();
        var fresh = new List<DeviceContext>();
        foreach (var monitor in monitors)
        {
            var dc = monitor.TryCreateDeviceContext();
            if (dc != null)
            {
                fresh.Add(dc);
            }
        }

        lock (_lock)
        {
            // ⛔ F26（2026-09-23）：这里**必须**用 `DisposeKeepRamp()`，不能用 `Dispose()`！
            //    `DisplayContext.Dispose()` 会先把 ramp 写成**原生 100%**（那是为"退出时回正常"
            //    设计的），而本方法只是**内部换一批 DC 句柄**、紧接着就要把屏幕重写为设定值
            //    ⇒ 用 `Dispose()` 会让**每次拓扑事件都闪一次全亮**。
            //    这正是「拔插显示器会闪几次」的根因（软件模拟 WM_DISPLAYCHANGE 已复现）。
            foreach (var display in _displays)
            {
                display.DisposeKeepRamp();
            }
            _displays.Clear();

            foreach (var dc in fresh)
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
                if (!IsWritable(s)) continue;   // 停用屏冻结不参与基准
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
