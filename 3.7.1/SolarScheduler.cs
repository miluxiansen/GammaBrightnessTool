namespace GammaBrightnessTool;

/// <summary>方案 A（2026-10-03）：手动接管的**轴**（亮度 / 色温各自独立）。</summary>
public enum SolarAxis
{
    Brightness,
    Temperature,
}

/// <summary>
/// "时间调整"调度器：按日出/日落自动调节色温与亮度。
///
/// 行为规则（与用户确认一致）：
/// - 正常运行时，按日出/日落平滑过渡色温+亮度（过渡时长 TransitionMinutes）。
/// - 用户手动调亮度/色温（滚轮/弹窗/热键/预设/挡位）→ 立即停止调度并持久化
///   手动接管标志（重启软件也保持），直到"关闭再开启总开关"才恢复。
/// - 调整时间调整页的目标值滑块不打断调度，且当前立即跟随新目标值。
/// - 色温总开关关闭时，只调亮度不调色温。
///
/// 平滑机制：每个 tick 计算目标值（Interpolate），当前值以固定速率向目标
/// 移动。速率 = 白天↔夜晚差值 / TransitionMinutes（见 <see cref="MaxStep"/>），因此：
///  - 过渡期内目标值本身缓慢变化，当前值直接跟随（精确曲线）；
///  - 刚启动 / 重开总开关等"追赶"场景，**仅当 TransitionMinutes &gt; 0 时**
///    才按过渡时长平滑追赶。
///
/// ⚠️ 两处必须知道的事实（2026-09-16 实测修正，此前注释与实现不符）：
///
/// 1. **`TransitionMinutes == 0`（也是默认值）时，tick 是"瞬时到位"而不是平滑。**
///    <see cref="MaxStep"/> 在 `transition &lt;= TimeSpan.Zero` 时返回 0，
///    而 <see cref="MoveToward"/> 把 `maxStep &lt;= 0` 解释为"直接写到目标值"。
///    所以"当前值以过渡时长平滑追赶目标"这句承诺**只在 TransitionMinutes &gt; 0 时成立**。
///
/// 2. **<see cref="Start"/> 会立即执行一次 <see cref="Tick"/>。**
///    因此调用方**绝不能**在刚启动一段 1200ms 平滑动画之后紧接着调用
///    `Tick()` / `MainController.ApplySolarScheduler()` —— 那次 Tick 会把动画的
///    第一帧直接覆盖成终点（真机上表现为色温"跳一下"）。
///    实测：色温比单步 0.033（正常）→ 0.349（被覆盖）。
///    正确做法：把 Solar 接手推迟到过渡结束的回调里
///    （见 `MainController.SetColorTemperatureEnabled` 的 `StartSmoothTransition(... , done)`）。
/// </summary>
public sealed class SolarScheduler : IDisposable
{
    private readonly GammaController _gamma;
    private readonly AppSettings _settings;
    private System.Windows.Forms.Timer? _timer;
    private bool _running;

    /// <summary>调度 tick 间隔（秒）。</summary>
    private const int TickSeconds = 2;

    public SolarScheduler(GammaController gamma, AppSettings settings)
    {
        _gamma = gamma;
        _settings = settings;
    }

    public bool IsRunning => _running;

    /// <summary>
    /// 写屏抑制谓词：返回 true 时 <see cref="Tick"/> 只计算目标、**不写屏**。
    /// 由 `MainController` 注入（当前用于「统一化动画在飞」）——
    /// 动画期间任何 out-of-band 全量写屏都会把动画的一帧抹平：
    /// 实测关闭独立控制的 1.2s 动画被 2s 周期的 tick 打断，
    /// 屏幕出现 **1 帧下探 Δ26530**（10ms 后又被动画抹回）。
    /// ⚠️ 只抑制写屏，不影响目标计算；下一次 tick 会照常把值对齐。
    /// </summary>
    public Func<bool>? SuppressWrites { get; set; }

    /// <summary>
    /// 【3.7.1】启动首次 tick 的「平滑写」委托：由 `MainController` 注入。
    /// 参数 (targetBright, targetTemp)，返回 true 表示已按平滑方式接管（本次不再直接写屏）。
    ///
    /// 背景：`TransitionMinutes == 0`（默认）时首次 tick 是**瞬时到位**，而启动时屏幕
    /// 是退出 `ResetGamma` 写的原生（100%/6600K）⇒ 硬切到夜间目标（85%/3900K）表现为
    /// 「闪烁 B（一次跳变）」；若叠加 Windows 显示重配（开机首次/切刷新率会把 ramp 反复
    /// 复位为原生），因缺少 40 帧「重试覆盖」而无人纠正 ⇒ 「闪烁 A（反复横跳）」。
    /// 改为平滑后：既消除硬切，又恢复 40 帧持续重写（= 自愈重试）。
    ///
    /// ⛔ 只对**首次** tick 生效（见 <see cref="_firstTickPending"/>）：周期 tick 仍是
    /// 原语义，否则会与进行中的过渡动画互相覆盖（本文件头部注释另有一处同类教训）。
    /// </summary>
    public Func<float, float, bool>? SmoothWrite { get; set; }

    /// <summary>
    /// 【3.7.1】首个 tick 是否尚未按「平滑写」处理过。<see cref="Start"/> 时重置为 true。
    /// 仅用于把「进程启动/重开总开关后的首次对齐」与「每 2 秒的周期对齐」区分开。
    /// </summary>
    private bool _firstTickPending = true;

    /// <summary>亮度变化通知（每次实际写入 gamma 后触发），供设置窗下拉同步。</summary>
    public event EventHandler<float>? BrightnessChanged;
    /// <summary>色温变化通知（每次实际写入 gamma 后触发），供设置窗下拉同步。</summary>
    public event EventHandler<float>? TemperatureChanged;

    /// <summary>
    /// 启动调度（开始按日出日落自动调节）。启动时立即 tick 一次，从当前
    /// 值平滑过渡到目标值（而非瞬间跳变）。
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        // 【3.7.1】每次 Start 都把「首次 tick」重新置位：进程启动、以及用户
        // 关闭再开启总开关之后的首次对齐，都应有机会走平滑（消除硬切）。
        _firstTickPending = true;
        Tick(); // 立即对齐一次，之后按定时器平滑追赶
        _timer ??= new System.Windows.Forms.Timer { Interval = TickSeconds * 1000 };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>停止调度（保持当前 gamma 值不动）。</summary>
    public void Stop()
    {
        if (_timer != null) _timer.Tick -= OnTimerTick;
        _timer?.Stop();
        _running = false;
    }

    private void OnTimerTick(object? sender, EventArgs e) => Tick();

    /// <summary>
    /// 立即执行一次调度对齐（对当前时刻计算目标并平滑移动）。
    /// 供目标值滑块拖动时实时跟随新目标值调用。
    /// </summary>
    public void Tick()
    {
        // ===== [W11] Solar 定时写入通道 · **2 s 周期** =====
        //   写屏: _gamma.SetTemperature/SetBrightness（同值早退不写屏；
        //         ⭐ 实测：Solar 常开 73 s 窗口内 [gamma/apply] 27 条而 [gamma/write] **0 条**）
        //   落盘: 无（由 MainController 侧）
        //      ① 过渡/动画在飞 ⇒ `SuppressWrites` 为真 ⇒ **只算不写**（见下）；
        //      ② `gamma/apply`（提交）≠ `gamma/write`（真写屏），排查时**必须分开看**。
        //   ⛔ 禁止在本通道内读 `_gamma.Current*` 后又立刻写——会被 24ms 合帧通道覆盖。
        // ==============================================
        // F4a（2026-09-17）：有过渡/动画在飞 ⇒ 本次只算不写。
        // 与 `MainController.OnManualAdjustment` 的"取消在途动画"不同：
        // 这里**不打断动画**，只是让周期性的 out-of-band 写屏让路（下次 tick 照常对齐）。
        if (SuppressWrites?.Invoke() == true) return;
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSunriseSunset(now);
        var transition = TimeSpan.FromMinutes(Math.Max(0, _settings.TransitionMinutes));

        double targetTemp = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayTemperature, _settings.NightTemperature, transition);
        double targetBright = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayBrightness, _settings.NightBrightness, transition);

        // 目标值 clamp 到硬范围（色温受色温页 Min~Max 限制，亮度 0~1）。
        targetTemp = Math.Clamp(targetTemp, _gamma.MinTemperature, _gamma.MaxTemperature);
        // 2026-10-01：亮度范围（参考色温范围的上行）—— 时间调整目标值同样受限制。
        targetBright = Math.Clamp(targetBright, _gamma.MinBrightness, _gamma.MaxBrightness);

        // ------------------------------------------------------------------
        // 【3.7.1】首次 tick 优先交给「平滑写」委托（见 SmoothWrite 文档注释）。
        // · 位置在 SuppressWrites 之后 —— 暂停/停用时 Tick 已提前 return，不受影响；
        // · 只消费一次 —— 周期 tick 保持原语义（否则会与进行中的过渡动画互相覆盖，
        //   本文件头部注释 2 记录了同类事故）；
        // · 委托返回 false（无差值/不可平滑）⇒ 调用方回落到原有的瞬时写路径。
        // 目的：消除启动时的「原生 → 夜间目标」硬切（闪烁 B ），并恢复 40 帧
        //       持续重写这层自愈重试（压制闪烁 A）。
        // ------------------------------------------------------------------
        if (_firstTickPending)
        {
            _firstTickPending = false;
            if (SmoothWrite != null && SmoothWrite((float)targetBright, (float)targetTemp))
            {
                return; // 已由平滑过渡接管，本次不直接写屏
            }
            // ------------------------------------------------------------------
            // ⛔ 修复（2026-09-29）：**启动闪** —— 消除「两次单轴写」夹出的中间帧。
            //
            // 实测（3.7.1，自造残留场景复现，非推理）：
            //   前提 = Solar 活跃 且 屏幕已被写成目标值（例如强杀 GBT 后残留 85%/3900K）。
            //   此时 SmoothWrite 因「无差值」不接管（needB/needT 全 false，见
            //   MainController 注入的委托），控制权落到下面的**瞬时写**路径；
            //   而那条路径是**两次单轴写**（SetTemperature + SetBrightness），
            //   各自 ApplyGamma() 写**整份** ramp 并带出**另一轴**的 _current*。
            //   新进程 _currentBrightness 的初值是 1.0f（GammaController.cs:134）
            //   ⇒ 第①次 SetTemperature(3900) 写出 **100%/3900K**（日志里就是 curB=100）
            //   ⇒ 第②次 SetBrightness(0.85) 才写出 85%/3900K
            //   ⇒ 中间那一帧亮度 100% ⇒ 屏幕亮度瞬间 +15% = 用户看到的启动闪。
            //   （3.7.0/3.7.1 两份日志同形态：相隔 2~3ms 的两次 [gamma/apply] uni。）
            //
            // 修法：写屏之前先把内部状态对齐到**屏幕实际值**（纯内存、不写屏）——
            //   与 `StartSmoothTransition` 里解决同类问题的做法一致。对齐后两轴都与屏幕
            //   相同 ⇒ SetTemperature / SetBrightness 均命中**同值早退**（B1 已加的优化，
            //   GammaController.cs:711）⇒ **一次写屏都不发生** ⇒ 零闪。
            // ⚠️ 仅对**首次** tick 生效：周期 tick 时 _current* 已被之前的写屏更新。
            // ⚠️ 此处屏幕值必然等于目标值（否则 SmoothWrite 会接管），故最终态不变。
            // ------------------------------------------------------------------
            _gamma.CommitTargetStateWithoutWrite(
                _gamma.ReadCurrentBrightness(), _gamma.ReadCurrentTemperature());
        }

        double curTemp = _gamma.CurrentTemperature;
        double curBright = _gamma.CurrentBrightness;

        // 平滑步长：白天↔夜晚全程在 TransitionMinutes 内走完。
        double tempMaxStep = MaxStep(_settings.DayTemperature, _settings.NightTemperature, transition);
        double brightMaxStep = MaxStep(_settings.DayBrightness, _settings.NightBrightness, transition);

        // 平滑开关关闭时该通道瞬时到位（不按过渡时长平滑）。
        double newTemp = _settings.TemperatureSmooth ? MoveToward(curTemp, targetTemp, tempMaxStep) : targetTemp;
        double newBright = _settings.BrightnessSmooth ? MoveToward(curBright, targetBright, brightMaxStep) : targetBright;

        // 方案 A（2026-10-03）：**按轴**尊重"手动接管"——被接管的轴 Solar 一律不写。
        //   缺陷背景：旧实现只有单一全局标记，打开色温总开关会把标记清掉 ⇒ 亮度轴也被交回
        // 色温总开关关闭时只调亮度（保持 gamma 当前色温 = 中性 6600K）。
        if (_settings.ColorTemperatureEnabled && !_settings.SolarTemperatureOverridden)
            _gamma.SetTemperature((float)newTemp);
        if (!_settings.SolarBrightnessOverridden)
            _gamma.SetBrightness((float)newBright);
        TemperatureChanged?.Invoke(this, _gamma.CurrentTemperature);
        BrightnessChanged?.Invoke(this, _gamma.CurrentBrightness);
    }

    /// <summary>
    /// 立即按当前时刻目标值瞬时应用（不经过平滑追赶），供时间调整页
    /// 目标值滑块拖动时实时预览。不改变调度器运行状态（不 Start/Stop）。
    /// </summary>
    public void ApplyNowInstant()
    {
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSunriseSunset(now);
        var transition = TimeSpan.FromMinutes(Math.Max(0, _settings.TransitionMinutes));

        double targetTemp = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayTemperature, _settings.NightTemperature, transition);
        double targetBright = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayBrightness, _settings.NightBrightness, transition);

        targetTemp = Math.Clamp(targetTemp, _gamma.MinTemperature, _gamma.MaxTemperature);
        // 2026-10-01：亮度范围（参考色温范围的上行）—— 时间调整目标值同样受限制。
        targetBright = Math.Clamp(targetBright, _gamma.MinBrightness, _gamma.MaxBrightness);

        // 方案 A：与 Tick 同口径 —— 被手动接管的轴不写（预览也不该动它，否则误导用户）。
        if (_settings.ColorTemperatureEnabled && !_settings.SolarTemperatureOverridden)
            _gamma.SetTemperature((float)targetTemp);
        if (!_settings.SolarBrightnessOverridden)
            _gamma.SetBrightness((float)targetBright);
        TemperatureChanged?.Invoke(this, _gamma.CurrentTemperature);
        BrightnessChanged?.Invoke(this, _gamma.CurrentBrightness);
    }

    /// <summary>每 tick 允许的最大变化量；过渡时长为 0 时返回 0（表示瞬时到位）。</summary>
    private static double MaxStep(double day, double night, TimeSpan transition)
    {
        if (transition <= TimeSpan.Zero) return 0;
        double span = Math.Abs(day - night);
        double tickCount = transition.TotalSeconds / TickSeconds;
        if (tickCount <= 0) return 0;
        return span / tickCount;
    }

    /// <summary>向目标移动一步；maxStep<=0 表示瞬时到位。</summary>
    private static double MoveToward(double current, double target, double maxStep)
    {
        if (maxStep <= 0) return target;
        double diff = target - current;
        if (Math.Abs(diff) <= maxStep) return target;
        return current + Math.Sign(diff) * maxStep;
    }

    /// <summary>
    /// 根据模式返回当天的日出/日落时刻：手动模式用设置的时间，物理位置
    /// 模式用坐标 + 太阳时算法计算。
    /// </summary>
    /// <summary>Returns the current target values (brightness + temperature),
    /// same math as Tick(). Used as the smooth-transition target when the
    /// solar master switch is toggled on.</summary>
    public (float Bright, float Temp) GetCurrentTargets()
    {
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSunriseSunset(now);
        var transition = TimeSpan.FromMinutes(Math.Max(0, _settings.TransitionMinutes));
        double targetTemp = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayTemperature, _settings.NightTemperature, transition);
        double targetBright = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayBrightness, _settings.NightBrightness, transition);
        targetTemp = Math.Clamp(targetTemp, _gamma.MinTemperature, _gamma.MaxTemperature);
        // 2026-10-01：亮度范围（参考色温范围的上行）—— 时间调整目标值同样受限制。
        targetBright = Math.Clamp(targetBright, _gamma.MinBrightness, _gamma.MaxBrightness);
        return ((float)targetBright, (float)targetTemp);
    }

    private (TimeOnly Sunrise, TimeOnly Sunset) GetSunriseSunset(DateTime now)
    {
        if (_settings.SolarManualMode)
        {
            return (
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunriseMinutes, 0, 1439))),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunsetMinutes, 0, 1439)))
            );
        }
        return SolarTimes.Calculate(_settings.SolarLatitude, _settings.SolarLongitude, now);
    }

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
