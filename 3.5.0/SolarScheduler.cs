namespace GammaBrightnessTool;

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
/// 移动。速率 = 白天↔夜晚差值 / TransitionMinutes，因此：
///  - 过渡期内目标值本身缓慢变化，当前值直接跟随（精确曲线）；
///  - 刚启动 / 重开总开关等"跳变"场景，当前值以过渡时长平滑追赶目标。
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
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSunriseSunset(now);
        var transition = TimeSpan.FromMinutes(Math.Max(0, _settings.TransitionMinutes));

        double targetTemp = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayTemperature, _settings.NightTemperature, transition);
        double targetBright = SolarTimes.Interpolate(
            now, sunrise, sunset, _settings.DayBrightness, _settings.NightBrightness, transition);

        // 目标值 clamp 到硬范围（色温受色温页 Min~Max 限制，亮度 0~1）。
        targetTemp = Math.Clamp(targetTemp, _gamma.MinTemperature, _gamma.MaxTemperature);
        targetBright = Math.Clamp(targetBright, 0.0, 1.0);

        double curTemp = _gamma.CurrentTemperature;
        double curBright = _gamma.CurrentBrightness;

        // 平滑步长：白天↔夜晚全程在 TransitionMinutes 内走完。
        double tempMaxStep = MaxStep(_settings.DayTemperature, _settings.NightTemperature, transition);
        double brightMaxStep = MaxStep(_settings.DayBrightness, _settings.NightBrightness, transition);

        // 平滑开关关闭时该通道瞬时到位（不按过渡时长平滑）。
        double newTemp = _settings.TemperatureSmooth ? MoveToward(curTemp, targetTemp, tempMaxStep) : targetTemp;
        double newBright = _settings.BrightnessSmooth ? MoveToward(curBright, targetBright, brightMaxStep) : targetBright;

        // 色温总开关关闭时只调亮度（保持 gamma 当前色温 = 中性 6600K）。
        if (_settings.ColorTemperatureEnabled)
            _gamma.SetTemperature((float)newTemp);
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
        targetBright = Math.Clamp(targetBright, 0.0, 1.0);

        if (_settings.ColorTemperatureEnabled)
            _gamma.SetTemperature((float)targetTemp);
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
        targetBright = Math.Clamp(targetBright, 0.0, 1.0);
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
