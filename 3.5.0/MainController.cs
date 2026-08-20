using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GammaBrightnessTool;

/// <summary>
/// Main application controller that coordinates all components.
/// </summary>
public sealed class MainController : IDisposable
{
    private TrayIconManager? _trayIcon;
    private GlobalMouseHook? _mouseHook;
    private GammaController? _gamma;
    private BrightnessOverlay? _overlay;
    private BrightnessPopup? _popup;
    private AppSettings? _settings;
    // Per-slot registration results from the last RegisterHotkeys() run,
    // keyed by slot name. Used to detect OS-level conflicts when a hotkey
    // binding changes. Unbound slots are not present here.
    private readonly Dictionary<string, bool> _hotKeyRegistration = new();
    // While the user is recording a hotkey in the settings window,
    // ALL hotkeys are temporarily unregistered so the new combo can
    // be typed without triggering any existing binding.
    private bool _hotKeysSuspended;
    // Polls the tray icon rect while the popup is open so the popup follows
    // the icon across DPI changes / taskbar moves without depending on
    // WM_DPICHANGED delivery to a hidden message window (which Windows does
    // not send to hidden windows, and CheckMouseLeave only runs while the
    // cursor is over the icon). This is the same "always use fresh
    // coordinates" philosophy as the wheel OSD path.
    private System.Windows.Forms.Timer? _popupAnchorTimer;
    private static readonly TimeSpan PopupAnchorInterval = TimeSpan.FromMilliseconds(200);
    // 时间调整调度器（按日出日落自动调节色温/亮度）。
    private SolarScheduler? _solarScheduler;
    // 系统事件监听（睡眠唤醒/显示器变化/全屏检测）。
    private SystemEventMonitor? _systemMonitor;
    // 全屏暂停中：true 时 gamma 已被暂停（原生色彩）。
    private bool _fullscreenPaused;
    // 进入全屏前的亮度/色温（退出全屏时平滑恢复的目标值）。
    private float _fullscreenBrightnessBefore = 1.0f;
    private float _fullscreenTemperatureBefore = GammaController.DEFAULT_TEMPERATURE;

    // 全屏进入/退出平滑过渡动画（独立 timer，不与挡位动画 _smoothTimer 冲突）。
    private System.Windows.Forms.Timer? _fullscreenAnimTimer;
    private DateTime _fullscreenAnimStartTime;
    private float _fullscreenAnimStartBright, _fullscreenAnimTargetBright;
    private float _fullscreenAnimStartTemp, _fullscreenAnimTargetTemp;
    // true = 退出全屏的恢复动画（结束时解除暂停并重放内部值）；
    // false = 进入全屏的暂停动画（结束时保持暂停、定格原生 ramp）。
    private bool _fullscreenAnimExit;
    // 全屏动画各通道是否平滑（由 BrightnessSmooth/TemperatureSmooth 决定，
    // 关闭的通道在动画中瞬时到位，避免"关平滑仍动画"）。
    private bool _fullscreenAnimSmoothB, _fullscreenAnimSmoothT;

    // 右键菜单"禁用"：true 时 gamma 暂停调节（原生色彩），与全屏暂停独立。
    // 禁用期间所有调节入口被拦（滚轮/热键/弹窗）；到期自动平滑恢复。
    private bool _disableActive;
    // 禁用前记录的值（到期恢复时平滑过渡回的目标值）。
    private float _disableBrightnessBefore = 1.0f;
    private float _disableTemperatureBefore = GammaController.DEFAULT_TEMPERATURE;
    // 禁用到期定时器（1 秒 tick，检查 DisableUntil 是否已到）。
    private System.Windows.Forms.Timer? _disableTimer;
    // 禁用进入/恢复的平滑过渡动画（独立 timer，不与全屏/挡位动画冲突）。
    private System.Windows.Forms.Timer? _disableAnimTimer;
    private DateTime _disableAnimStartTime;
    private float _disableAnimStartBright, _disableAnimTargetBright;
    private float _disableAnimStartTemp, _disableAnimTargetTemp;
    private bool _disableAnimExit;
    // 恢复动画结束时的回调（用于定时器到期后的收尾）。
    private Action? _disableAnimDone;
    // 禁用动画各通道是否平滑（同上，通道独立）。
    private bool _disableAnimSmoothB, _disableAnimSmoothT;

    public void Initialize(bool silent, bool showSettingsOnStart = false)
    {
        // 1. Run startup integrity check (registry, settings, tray icon visibility)
        IntegrityChecker.RunCheck();

        // 2. Load settings (fresh load after check, auto-creates default if not exists)
        _settings = SettingsManager.Load();
        // 过渡时长上限 60 分钟（旧版本设置可能残留 180）。
        _settings.TransitionMinutes = Math.Clamp(_settings.TransitionMinutes, 0, 60);
        // Apply saved language. Language.System (the default) follows the
        // system UI language, falling back to English when unsupported.
        Localization.Setting = _settings.Language;
        Localization.Current = Localization.Resolve(_settings.Language).Effective;
        // Apply the saved theme (System follows the Windows app theme).
        ThemeManager.Apply(_settings.Theme);
        // Popup theme is independent of the main UI theme.
        ThemeManager.ApplyPopupTheme(_settings.PopupTheme);

        // 2. Create tray icon first (visible immediately)
        _trayIcon = new TrayIconManager();
        _trayIcon.Initialize();
        _trayIcon.OnUninstallRequested += OnUninstallRequested;
        _trayIcon.OnSettingsRequested += OnSettingsRequested;
        _trayIcon.OnLeftClickRequested += OnLeftClickRequested;
        _trayIcon.OnContextMenuOpening += OnContextMenuOpening;
        _trayIcon.OnTrayDpiChanged += OnTrayDpiChanged;
        _trayIcon.OnIconRectChanged += OnIconRectChanged;
        _trayIcon.DisableSolarEnabled = () => _settings?.SolarAdjustEnabled == true;
        _trayIcon.DisableGetRemaining = () => GetDisableRemaining();
        _trayIcon.DisableGetUntil = () => GetDisableUntil();
        _trayIcon.DisableSolarActive = () => IsSolarDisableActive();
        _trayIcon.DisableIsDaytime = () => IsDaytimeNow();
        _trayIcon.OnDisableRequested += OnDisableRequested;

        // 3. Initialize gamma controller
        _gamma = new GammaController();
        _gamma.Initialize();
        _gamma.StepSize = _settings.StepSize; // apply saved step size
        _gamma.TemperatureStepSize = _settings.TemperatureStepSize; // apply saved temperature step
        _gamma.MinTemperature = _settings.MinTemperature; // apply saved temperature range
        _gamma.MaxTemperature = _settings.MaxTemperature;
        ApplyStartupGamma(); // 平滑/瞬时应用保存的亮度与色温

        // 4. Create brightness overlay
        _overlay = new BrightnessOverlay();
        _overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;

        // 5. Install mouse hook
        _mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
        {
            // Runtime-resolved so the settings UI changes apply immediately.
            IsInvertedScroll = () => _settings?.InvertScroll ?? false,
            IsOverlayEnabled = () => _settings?.ShowOverlay ?? true,
            IsWheelEnabled = () => _settings?.WheelEnabled ?? true,
            IsColorTemperatureEnabled = () => _settings?.ColorTemperatureEnabled ?? false,
            // 全屏暂停/禁用中：滚轮调节+OSD 完全忽略（GammaController._paused 已拦
            // 内部状态，这里拦 OSD 浮窗显示）。
            IsPaused = () => _fullscreenPaused || _disableActive,
            // 滚轮调节（弹窗未开时走 _gamma 直调路径）需要同时：
            // 1) 暂停时间调整调度（OnManualAdjustment）；
            // 2) 通知设置页刷新亮度/色温显示（BrightnessChanged / TemperatureChanged），
            //    否则亮度挡位下拉的实时值对滚轮无反应。
            OnUserAdjustment = () =>
            {
                OnManualAdjustment();
                BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1.0f);
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            }
        };
        _mouseHook.Install();

        // 5b. Create left-click brightness popup (docked above tray icon)
        _popup = new BrightnessPopup();
        // 禁用模式（右键菜单"禁用"）或全屏暂停期间弹窗滑块不可拖动。
        _popup.IsDisableActive = () => _disableActive || _fullscreenPaused;
        _popup.StepSize = _settings.StepSize; // popup wheel uses the same step as the OSD path
        _popup.TemperatureStepSize = _settings.TemperatureStepSize; // temperature wheel step (K)
        _popup.MinTemperature = _settings.MinTemperature; // temperature slider range
        _popup.MaxTemperature = _settings.MaxTemperature;
        _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled; // master switch drives popup layout
        _popup.OnBrightnessChanged += OnPopupBrightnessChanged;
        _popup.OnTemperatureChanged += OnPopupTemperatureChanged;
        _popup.OnShownChanged += OnPopupShownChanged;
        _mouseHook.SetPopup(_popup);

        // 6. Update tray tooltip with current brightness
        _trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);

        // 7. Register saved hotkeys (brightness up/down)
        RegisterHotkeys();

        // 7a. 系统事件监听：睡眠唤醒/显示器热插拔自愈 + 全屏自动暂停。
        //     开关默认开启；关闭时不挂监听（省资源）。
        if (_settings!.GammaSelfHealEnabled || _settings.PauseInFullscreenEnabled)
        {
            _systemMonitor = new SystemEventMonitor();
            _systemMonitor.Resumed += OnSystemResumed;
            _systemMonitor.DisplayChanged += OnDisplayChanged;
            _systemMonitor.FullscreenEntered += OnFullscreenEntered;
            _systemMonitor.FullscreenExited += OnFullscreenExited;
            _systemMonitor.Initialize();
            // 启动时同步一次全屏状态（比如软件开机自启时正处于全屏游戏）。
            if (_settings.PauseInFullscreenEnabled)
            {
                _systemMonitor.RefreshFullscreenState();
            }
        }

        // 7b. 启动时间调整调度器（若总开关开启且未被手动接管）。
        _solarScheduler = new SolarScheduler(_gamma, _settings!);
        // 时间调整调度器写入 gamma 后也通知设置窗，保持亮度/色温挡位下拉实时同步；
        // 同时同步弹窗滑块（若弹窗可见，其显示也跟随时间调整变化）。
        _solarScheduler.BrightnessChanged += (_, v) =>
        {
            _popup?.SyncFromGamma(v, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            BrightnessChanged?.Invoke(this, v);
        };
        _solarScheduler.TemperatureChanged += (_, v) =>
        {
            _popup?.SyncFromGamma(_gamma?.CurrentBrightness ?? 1f, v);
            TemperatureChanged?.Invoke(this, v);
        };
        if (_settings!.SolarAdjustEnabled && !_settings.SolarManuallyOverridden)
        {
            _solarScheduler.Start();
        }

        // 7c. 恢复"禁用"状态（设置持久化了 DisableUntil）。
        //     若已过期：清除并平滑恢复 gamma；未过期：立即进入禁用（保持原生色彩）。
        RestoreDisableState();

        // 8. Auto-show settings window for testing automation (--show-settings)
        if (showSettingsOnStart)
        {
            // Defer until the message loop is running so the window is
            // shown after the tray is fully initialized (calling Show()
            // synchronously before Application.Run() can leave the window
            // created-but-invisible).
            System.Windows.Forms.Application.Idle += OnIdleShowSettings;
        }
    }

    // ==================== 亮度/色温平滑 ====================

    /// <summary>启动渐变定时器与状态。</summary>
    private System.Windows.Forms.Timer? _smoothTimer;
    private DateTime _smoothStartTime;
    private float _smoothStartBright, _smoothTargetBright;
    private float _smoothStartTemp, _smoothTargetTemp;
    private bool _smoothBrightActive, _smoothTempActive;
    private const int SmoothDurationMs = 1200;
    private const int SmoothTickMs = 30;

    /// <summary>亮度平滑开关（默认开）。</summary>
    public bool GetBrightnessSmooth() => _settings?.BrightnessSmooth ?? true;

    /// <summary>色温平滑开关（默认开）。</summary>
    public bool GetTemperatureSmooth() => _settings?.TemperatureSmooth ?? true;

    /// <summary>设置亮度平滑开关。</summary>
    public void SetBrightnessSmooth(bool enabled)
    {
        if (_settings == null) return;
        _settings.BrightnessSmooth = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>设置色温平滑开关。</summary>
    public void SetTemperatureSmooth(bool enabled)
    {
        if (_settings == null) return;
        _settings.TemperatureSmooth = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>Gamma 自愈开关（默认开）。</summary>
    public bool GetGammaSelfHealEnabled() => _settings?.GammaSelfHealEnabled ?? true;

    /// <summary>全屏自动暂停开关（默认开）。</summary>
    public bool GetPauseInFullscreenEnabled() => _settings?.PauseInFullscreenEnabled ?? true;

    /// <summary>设置 Gamma 自愈开关。关闭时不再响应唤醒/显示器变化事件。</summary>
    public void SetGammaSelfHealEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.GammaSelfHealEnabled = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>设置全屏自动暂停开关。开启时立即同步一次全屏状态，
    /// 关闭时若正处于暂停则立即恢复 gamma。</summary>
    public void SetPauseInFullscreenEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.PauseInFullscreenEnabled = enabled;
        SettingsManager.Save(_settings);
        if (enabled)
        {
            _systemMonitor?.RefreshFullscreenState();
        }
        else if (_fullscreenPaused)
        {
            // 用户手动关闭暂停：平滑恢复 gamma（从原生回到暂停前值）。
            // 与退出全屏同理：恢复动画期间保持暂停标志，动画结束时解除。
            StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
        }
    }

    /// <summary>
    /// 启动时应用保存的亮度/色温。对应平滑开关开启且时间调整调度未运行
    /// 时，从屏幕当前实际值做约 1.2 秒的 ease-out 渐变；否则直接设置。
    /// 时间调整运行时由其自身的过渡时长规则平滑，这里直接设值。
    /// </summary>
    private void ApplyStartupGamma()
    {
        if (_settings == null || _gamma == null) return;
        float targetBright = _settings.LastBrightness;
        float targetTemp = _settings.ColorTemperatureEnabled
            ? _settings.LastTemperature
            : GammaController.DEFAULT_TEMPERATURE;

        // 时间调整调度运行时由其自身的过渡时长规则平滑，不启用本平滑。
        bool solarRuns = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
        StartSmoothTransition(targetBright, targetTemp,
            _settings.BrightnessSmooth && !solarRuns,
            _settings.TemperatureSmooth && !solarRuns);
    }

    /// <summary>
    /// 通用平滑过渡：把当前 gamma 值平滑过渡到目标值（ease-out cubic，
    /// 约 SmoothDurationMs）。smoothBright/smoothTemp 为 false 的通道瞬时
    /// 到位。时间调整调度运行中不应调用（调度由 SolarScheduler 自己平滑）。
    /// 供启动恢复、亮度挡位/色温挡位切换等大跨度跳变共用。
    /// </summary>
    private Action? _smoothDone;

    /// <summary>ease-out cubic 缓动（所有平滑动画共用）。</summary>
    private static double EaseOutCubic(double t)
    {
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }

    private void StartSmoothTransition(float targetBright, float targetTemp, bool smoothBright, bool smoothTemp, Action? done = null)
    {
        if (_gamma == null) return;
        if (!smoothBright && !smoothTemp)
        {
            _gamma.SetBrightness(targetBright);
            _gamma.SetTemperature(targetTemp);
            done?.Invoke();
            return;
        }
        // 非平滑通道瞬时到位，避免停留在旧值。
        if (!smoothBright) _gamma.SetBrightness(targetBright);
        if (!smoothTemp) _gamma.SetTemperature(targetTemp);

        _smoothStartBright = _gamma.ReadCurrentBrightness();
        _smoothStartTemp = _gamma.ReadCurrentTemperature();
        _smoothTargetBright = targetBright;
        _smoothTargetTemp = targetTemp;
        _smoothBrightActive = smoothBright;
        _smoothTempActive = smoothTemp;
        _smoothStartTime = DateTime.Now;
        _smoothDone = done;
        // 动画进行中再次调用（连续切挡位）时复用 timer，参数直接覆盖。
        if (_smoothTimer == null)
        {
            _smoothTimer = new System.Windows.Forms.Timer { Interval = SmoothTickMs };
            _smoothTimer.Tick += OnSmoothTick;
        }
        _smoothTimer.Start();
    }

    private void OnSmoothTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _smoothStartTime).TotalMilliseconds / SmoothDurationMs;
        if (t >= 1.0) t = 1.0;
        double ease = EaseOutCubic(t);
        if (_smoothBrightActive)
            _gamma?.SetBrightness((float)(_smoothStartBright + (_smoothTargetBright - _smoothStartBright) * ease));
        if (_smoothTempActive)
            _gamma?.SetTemperature((float)(_smoothStartTemp + (_smoothTargetTemp - _smoothStartTemp) * ease));
        if (t >= 1.0)
        {
            _smoothTimer?.Stop();
            _smoothTimer?.Dispose();
            _smoothTimer = null;
            // 动画结束：显式写入精确目标值，杜绝 ease 未到 1 的中间值
            // 残留（如 6599.9998 而非 6600）。
            if (_smoothBrightActive) _gamma?.SetBrightness(_smoothTargetBright);
            if (_smoothTempActive) _gamma?.SetTemperature(_smoothTargetTemp);
            // 动画结束：托盘提示显示最终值；动画结束后各通道通知一次，
            // 让设置页下拉同步到最终值（不在动画中间值上跳变）。
            if (_smoothBrightActive)
                BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
            if (_smoothTempActive)
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            // 动画完成、gamma 已写入精确目标值后才保存：平滑路径下
            // SetColorTemperature/SetBrightnessLevel 不再立即 SaveSettings
            // （否则会保存成动画开始前的旧值，污染 LastTemperature/
            // LastBrightness）。
            SaveSettings();
            _trayIcon?.UpdateTooltip(
                _gamma?.CurrentBrightness ?? 1f,
                _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE,
                _settings?.ColorTemperatureEnabled ?? false);
            var done = _smoothDone;
            _smoothDone = null;
            done?.Invoke();
        }
    }
    private void OnIdleShowSettings(object? sender, EventArgs e)
    {
        System.Windows.Forms.Application.Idle -= OnIdleShowSettings;
        OnSettingsRequested(this, EventArgs.Empty);
    }

    private void OnLeftClickRequested(object? sender, EventArgs e)
    {
        // Dismiss the wheel OSD first so popup and OSD never overlap
        _overlay?.Hide();

        #if DEBUG
        PopupDebug.Log("OnLeftClickRequested: BEGIN");
        #endif

        // Live icon rect: no cache, and self-heals (triggers icon recovery
        // when the shell temporarily loses the icon) — same robustness as
        // the wheel path's IsMouseOverIconNow.
        var iconRect = _trayIcon?.GetIconRectLive();
        if (iconRect.HasValue)
        {
            #if DEBUG
            PopupDebug.Log($"OnLeftClickRequested: iconRect={iconRect.Value}");
            #endif
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, iconRect.Value);
        }
        else
        {
            // Tray rect unavailable (shell hiccup, explorer restart, icon
            // temporarily lost). Fall back to a default position above the
            // taskbar of the screen under the cursor so the left-click is
            // never a silent no-op. The anchor timer will re-anchor the
            // popup to the real icon rect as soon as the icon recovers.
            var cursorPos = Cursor.Position;
            var screen = Screen.FromPoint(cursorPos);
            var wa = screen.WorkingArea;
            var fallbackRect = new Rectangle(
                wa.Left + (wa.Width - 120) / 2,
                wa.Bottom - 40,
                120,
                40);
            #if DEBUG
            PopupDebug.Log($"OnLeftClickRequested: FALLBACK cursor={cursorPos} screen={screen.Bounds} wa={wa} fallbackRect={fallbackRect}");
            #endif
            _popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, fallbackRect);
        }
    }

    private void OnContextMenuOpening(object? sender, EventArgs e)
    {
        // Dismiss popup + OSD so the context menu is the only visible surface
        _popup?.Dismiss();
        _overlay?.Hide();
    }

    private void OnTrayDpiChanged(object? sender, EventArgs e)
    {
        #if DEBUG
        PopupDebug.Log($"OnTrayDpiChanged: IsShown={_popup?.IsShown}");
        #endif

        // The tray icon moved to a new physical position after a DPI change.
        // If the left-click popup is open, re-anchor it above the icon's new
        // rect so it follows instead of staying at the stale position.
        if (_popup != null && _popup.IsShown)
        {
            var iconRect = _trayIcon?.GetIconRectLive();
            if (iconRect.HasValue)
            {
                _popup.ReanchorTo(iconRect.Value);
            }
        }
    }

    private void OnIconRectChanged(object? sender, EventArgs e)
    {
        // The icon moved (DPI change, taskbar relocation). Re-anchor the
        // popup above its new rect. Driven by the polling timer via
        // PollIconRect while the popup is open.
        if (_popup != null && _popup.IsShown)
        {
            var iconRect = _trayIcon?.GetIconRectLive();
            if (iconRect.HasValue)
            {
                _popup.ReanchorTo(iconRect.Value);
            }
        }
    }

    private void OnPopupShownChanged(object? sender, EventArgs e)
    {
        #if DEBUG
        PopupDebug.Log($"OnPopupShownChanged: IsShown={_popup?.IsShown}");
        #endif

        // Start polling the icon rect while the popup is visible, stop when
        // it closes. The timer drives PollIconRect, which raises
        // OnIconRectChanged only when the icon actually moved — so the
        // popup follows the icon across DPI changes without any window
        // churn when the icon is stationary.
        if (_popup != null && _popup.IsShown)
        {
            if (_popupAnchorTimer == null)
            {
                _popupAnchorTimer = new System.Windows.Forms.Timer
                {
                    Interval = (int)PopupAnchorInterval.TotalMilliseconds
                };
                _popupAnchorTimer.Tick += OnPopupAnchorTick;
            }
            _popupAnchorTimer.Start();
        }
        else
        {
            _popupAnchorTimer?.Stop();
        }
    }

    private void OnPopupAnchorTick(object? sender, EventArgs e)
    {
        if (_popup == null || !_popup.IsShown) return;

        // Re-anchor unconditionally on every tick: the popup must follow
        // the icon even when the icon's PHYSICAL rect is unchanged (DPI
        // change resizes the popup via WinForms auto-scaling, which can
        // misplace it; the icon itself stays put). ReanchorTo is idempotent
        // — with a stationary icon it re-applies the same size/position and
        // is cheap (SetWindowPos to the same location). PollIconRect still
        // drives OnIconRectChanged for icon-move cases, but the tick no
        // longer depends on it.
        var iconRect = _trayIcon?.GetIconRectLive();
        if (iconRect.HasValue)
        {
            _popup.ReanchorTo(iconRect.Value);
        }
    }

    /// <summary>
    /// 更新托盘提示。色温调节关闭时只显示亮度（不带色温值）。
    /// </summary>
    private void UpdateTrayTooltip(float? brightness = null, float? temperatureK = null)
    {
        float b = brightness ?? _gamma?.CurrentBrightness ?? 1.0f;
        float t = temperatureK ?? _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;
        bool showTemp = _settings?.ColorTemperatureEnabled ?? false;
        _trayIcon?.UpdateTooltip(b, t, showTemp);
    }

    private void OnPopupBrightnessChanged(object? sender, float brightness)
    {
        if (_disableActive) return; // disabled: ignore popup adjustment
        OnManualAdjustment();
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? brightness);
        SaveSettings();
    }

    /// <summary>
    /// 左键弹窗色温滑块回调：应用色温并保存。
    /// </summary>
    private void OnPopupTemperatureChanged(object? sender, float kelvin)
    {
        if (_disableActive) return; // disabled: ignore popup adjustment
        OnManualAdjustment();
        _gamma?.SetTemperature(kelvin);
        SaveSettings();
        TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
    }

    private void OnOverlayBrightnessChanged(object? sender, float brightness)
    {
        if (_disableActive) return; // disabled: ignore OSD adjustment
        // 与弹窗/热键路径一致：OSD 滑块拖拽也属手动调节，需暂停时间调整调度。
        OnManualAdjustment();
        _gamma?.SetBrightness(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
        BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? brightness);
        SaveSettings();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        // Non-modal settings window; the tray stays fully usable while it is open.
        SettingsForm.ShowOrActivate();
    }

    private void OnUninstallRequested(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            Localization.Get("UninstallPrompt"),
            Localization.Get("UninstallTitle"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            PerformUninstall();
        }
    }

    private void PerformUninstall()
    {
        // 1. Remove startup registry entry
        StartupManager.SetStartup(false);

        // 2. Release mutex so uninstaller can run
        Program.ReleaseMutex();

        var exePath = Application.ExecutablePath;
        var appName = Path.GetFileName(exePath);
        var appDir = Path.GetDirectoryName(exePath) ?? "";

        // 3a. Installed version: hand off to the Inno Setup uninstaller (unins000.exe).
        // It removes the app directory (including itself), desktop/start-menu shortcuts
        // and registry values recorded at install time — which a self-cleanup batch cannot do.
        var uninsExe = Path.Combine(appDir, "unins000.exe");
        if (File.Exists(uninsExe))
        {
            var uninsPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = uninsExe,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(uninsPsi);
            Application.Exit();
            return;
        }

        // 3b. Green version: self-cleanup via batch script
        var batchPath = Path.Combine(Path.GetTempPath(), $"uninstall_{appName}.bat");

        var batchContent = $@"
@echo off
chcp 65001 >nul
timeout /t 2 /nobreak >nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v IconStreams /f 2>nul
reg delete ""HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify"" /v PastIconsStream /f 2>nul
rmdir /s /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GammaBrightnessTool")}"" 2>nul
del /f /q ""{Path.Combine(appDir, "settings.json")}"" 2>nul
del /f /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Gamma Brightness Tool.lnk")}"" 2>nul
del /f /q ""{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GammaBrightnessTool.lnk")}"" 2>nul
del /f /q ""{exePath}"" 2>nul
del /f /q ""{batchPath}"" 2>nul
";

        File.WriteAllText(batchPath, batchContent);

        // 4. Launch uninstaller and exit
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batchPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi);

        Application.Exit();
    }
    private void OnLanguageChanged(object? sender, Language lang)
    {
        if (_settings != null)
        {
            // lang is the user's raw choice (may be Language.System). Resolve
            // it to a concrete effective language; when the system language
            // is not supported, fall back to English and tell the user.
            var (effective, supported) = Localization.Resolve(lang);

            Localization.Setting = lang;
            Localization.Current = effective;
            _settings.Language = lang;
            SettingsManager.Save(_settings);
            // Refresh the tooltip text with the new language (NIM_MODIFY only — no icon re-registration needed, the sun glyph does not depend on language).
            _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);

            if (lang == Language.System && !supported)
            {
                MessageBox.Show(
                    Localization.Get("SystemLanguageUnsupported"),
                    Localization.Get("Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
    }

    /// <summary>
    /// Switches the UI language from the settings window. Keeps the in-memory
    /// settings instance in sync (so later saves don't overwrite the choice)
    /// and refreshes the tray tooltip immediately.
    /// </summary>
    public void ChangeLanguage(Language lang)
    {
        OnLanguageChanged(null, lang);
    }
    /// <summary>
    /// Returns the theme mode chosen in settings (System/Dark/Light).
    /// </summary>
    public ThemeMode GetTheme()
    {
        return _settings?.Theme ?? ThemeMode.System;
    }

    /// <summary>
    /// Stores the theme choice. Keeps the in-memory settings in sync,
    /// persists it, and applies it immediately so open windows (settings
    /// form, tray menu) repaint in the new theme without a restart.
    /// </summary>
    public void SetTheme(ThemeMode theme)
    {
        if (_settings == null) return;
        _settings.Theme = theme;
        SettingsManager.Save(_settings);
        ThemeManager.Apply(theme);
    }

    /// <summary>
    /// Exports the current settings to the given file as pretty-printed
    /// JSON. Returns true on success.
    /// </summary>
    public bool ExportSettings(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to export settings: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Imports settings from a JSON file produced by ExportSettings,
    /// validates it, replaces the in-memory settings, persists it and
    /// applies every live side-effect (language/theme/hotkeys/gamma/...).
    /// Returns true on success.
    /// </summary>
    public bool ImportSettings(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var imported = JsonSerializer.Deserialize<AppSettings>(json);
            if (imported == null) return false;

            // Validation: values must stay within sane ranges so a hand-
            // edited or corrupted file cannot push the gamma driver out of
            // bounds or break the UI.
            imported.LastBrightness = Math.Clamp(imported.LastBrightness, 0.0f, 1.0f);
            imported.LastTemperature = Math.Clamp(imported.LastTemperature, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
            imported.StepSize = Math.Clamp(imported.StepSize, 0.01f, 0.5f);
            imported.TemperatureStepSize = Math.Clamp(imported.TemperatureStepSize, 50.0f, 3000.0f);
            imported.MinTemperature = Math.Clamp(imported.MinTemperature, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
            imported.MaxTemperature = Math.Clamp(imported.MaxTemperature, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
            if (imported.MinTemperature >= imported.MaxTemperature)
            {
                imported.MinTemperature = GammaController.MIN_TEMPERATURE;
                imported.MaxTemperature = GammaController.MAX_TEMPERATURE;
            }

            // 导入以文件为准：先解除当前禁用状态（否则 gamma 暂停会拦截
            // ApplyImportedSettings 的 SetBrightness/SetTemperature），
            // 应用后再按导入的 DisableUntil 重新评估（RestoreDisableState：
            // 未过期→重新进入禁用；过期/无→保持正常）。
            if (_disableActive)
            {
                _disableActive = false;
                _gamma?.SetPaused(false);
            }
            _settings = imported;
            SettingsManager.Save(_settings);
            ApplyImportedSettings();
            RestoreDisableState();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to import settings: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Applies all live side-effects of a settings change (shared by import
    /// and reset): language, theme, popup theme, gamma step/range, hotkeys,
    /// brightness/temperature values and the solar scheduler state.
    /// </summary>
    private void ApplyImportedSettings()
    {
        if (_settings == null) return;

        Localization.Setting = _settings.Language;
        Localization.Current = Localization.Resolve(_settings.Language).Effective;
        ThemeManager.Apply(_settings.Theme);
        ThemeManager.ApplyPopupTheme(_settings.PopupTheme);

        if (_gamma != null) _gamma.StepSize = _settings.StepSize;
        if (_popup != null) _popup.StepSize = _settings.StepSize;
        if (_gamma != null) _gamma.TemperatureStepSize = _settings.TemperatureStepSize;
        if (_popup != null) _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
        if (_gamma != null) { _gamma.MinTemperature = _settings.MinTemperature; _gamma.MaxTemperature = _settings.MaxTemperature; }
        if (_popup != null) { _popup.MinTemperature = _settings.MinTemperature; _popup.MaxTemperature = _settings.MaxTemperature; }

        // 全屏暂停状态与开关联动：开关关闭时若正在暂停则平滑恢复。
        // 禁用中不解除 gamma 暂停（禁用与全屏是独立的暂停源）。
        if (!_settings.PauseInFullscreenEnabled && _fullscreenPaused && !_disableActive)
        {
            _fullscreenPaused = false;
            if (!_disableActive) _gamma?.SetPaused(false);
            StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
        }

        RegisterHotkeys();

        _gamma?.SetBrightness(_settings.LastBrightness);
        if (_settings.ColorTemperatureEnabled)
            _gamma?.SetTemperature(_settings.LastTemperature);
        else
            _gamma?.SetTemperature(GammaController.DEFAULT_TEMPERATURE);
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);

        ApplySolarScheduler();
    }

    /// <summary>
    /// Returns the popup theme mode chosen in settings (System/Dark/Light),
    /// independent of the main UI theme.
    /// </summary>
    public ThemeMode GetPopupTheme()
    {
        return _settings?.PopupTheme ?? ThemeMode.System;
    }

    /// <summary>
    /// Stores the popup theme choice. Keeps the in-memory settings in sync,
    /// persists it, and applies it immediately so the floating popups
    /// repaint in the new theme without a restart.
    /// </summary>
    public void SetPopupTheme(ThemeMode theme)
    {
        if (_settings == null) return;
        _settings.PopupTheme = theme;
        SettingsManager.Save(_settings);
        ThemeManager.ApplyPopupTheme(theme);
    }

    /// <summary>Returns the per-wheel-notch brightness step (0..1).</summary>
    public float GetStepSize() => _settings?.StepSize ?? GammaController.DEFAULT_STEP;

    /// <summary>Stores the step size and applies it to the gamma controller.</summary>
    public void SetStepSize(float step)
    {
        if (_settings == null) return;
        _settings.StepSize = Math.Clamp(step, 0.01f, 1.0f);
        if (_gamma != null) _gamma.StepSize = _settings.StepSize;
        if (_popup != null) _popup.StepSize = _settings.StepSize;
        SettingsManager.Save(_settings);
    }


    /// <summary>Applies a fixed brightness level (0..1) with OSD feedback,
    /// matching the tray menu brightness submenu behavior.</summary>
    public void SetBrightnessLevel(float brightness)
    {
        if (_gamma == null) return;
        OnManualAdjustment();
        bool solarRuns = _settings != null && _settings.SolarAdjustEnabled && _settings.SolarManuallyOverridden != true;
        if (_settings?.BrightnessSmooth == true && !solarRuns)
            StartSmoothTransition(brightness, _gamma.CurrentTemperature, true, false);
        else
        {
            _gamma.SetBrightness(brightness);
            SaveSettings();
        }
        _overlay?.Show(brightness);
        _trayIcon?.UpdateTooltip(brightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
        BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? brightness);
        // 平滑路径：LastBrightness 保存移到 OnSmoothTick 动画完成分支
        // （显式写入精确目标值之后），否则会保存成动画开始前的旧值。
    }

    /// <summary>Returns whether the wheel direction is inverted.</summary>
    public bool GetInvertScroll() => _settings?.InvertScroll ?? false;
    /// <summary>Stores the inverted-wheel preference.</summary>
    public void SetInvertScroll(bool invert)
    {
        if (_settings == null) return;
        _settings.InvertScroll = invert;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns whether the wheel brightness adjustment is enabled.</summary>
    public bool GetWheelEnabled() => _settings?.WheelEnabled ?? true;

    /// <summary>Stores the wheel brightness master switch.</summary>
    public void SetWheelEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.WheelEnabled = enabled;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns the per-wheel-notch color temperature step (K, default 100).</summary>
    public float GetTemperatureStepSize() => _settings?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP;

    /// <summary>Returns whether color temperature adjustment is enabled.</summary>
    public bool GetColorTemperatureEnabled() => _settings?.ColorTemperatureEnabled ?? false;

    /// <summary>Stores the color temperature master switch and updates the popup layout.</summary>
    public void SetColorTemperatureEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.ColorTemperatureEnabled = enabled;
        // Switch transitions are covered by the temperature smooth option:
        // on -> glide to the saved temperature; off -> glide to neutral 6600K.
        // But when the current temperature is already within 50K of the target
        // (e.g. 6596K vs 6600K) a smooth animation is pointless: apply directly.
        float curT = _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;
        // 开关目标：开启 -> 恢复上次色温；关闭 -> 中性 6600K。
        float targetT = enabled ? _settings.LastTemperature : GammaController.DEFAULT_TEMPERATURE;
        // 与目标差值 <50K 时直接到位（避免 6596->6600 这类无意义动画）。
        bool needsSmooth = _settings.TemperatureSmooth && !_disableActive
            && Math.Abs(curT - targetT) >= 50f;
        if (needsSmooth)
            StartSmoothTransition(_gamma?.CurrentBrightness ?? 1f, targetT, false, true);
        else
            _gamma?.SetTemperature(targetT);
        // 色温总开关与色温快捷键子开关的关系（用户逻辑 2026-08-20 确认）：
        // 子开关生效 = 子开关持久值 && 主开关 && 色温总开关（三者"与"）。
        // 因此色温总开关切换【不修改】子开关持久值（关→开时才能恢复用户
        // 原来的开关状态）；实际生效由 RegisterHotkeys 注册条件中的
        // ColorTemperatureEnabled != false 控制：色温关 → 不注册；开 → 注册。
        // UI 锁定/解锁由 SettingsForm.SyncHotKeySubToggles 处理。
        SettingsManager.Save(_settings);
        // 色温总开关切换后立即重注册：关 → 色温热键实际注销；开 → 恢复注册。
        RegisterHotkeys();
        if (_popup != null) _popup.TemperatureEnabled = enabled; // update popup layout immediately
        // Update tray tooltip (hide temperature when disabled)
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, enabled);
        // 色温总开关影响时间调整的色温输出：重算一次让调度器对齐。
        ApplySolarScheduler();
    }

    /// <summary>Stores the per-wheel-notch color temperature step (K) and syncs it everywhere.</summary>
    public void SetTemperatureStepSize(float stepK)
    {
        if (_settings == null) return;
        _settings.TemperatureStepSize = Math.Clamp(stepK, GammaController.MIN_TEMPERATURE_STEP, GammaController.MAX_TEMPERATURE_STEP);
        SettingsManager.Save(_settings);
        if (_gamma != null) _gamma.TemperatureStepSize = _settings.TemperatureStepSize;
        if (_popup != null) _popup.TemperatureStepSize = _settings.TemperatureStepSize;
    }

    /// <summary>Returns the lower bound of the configurable color temperature range (K).</summary>
    public float GetMinTemperature() => _settings?.MinTemperature ?? GammaController.MIN_TEMPERATURE;

    /// <summary>Returns the upper bound of the configurable color temperature range (K).</summary>
    public float GetMaxTemperature() => _settings?.MaxTemperature ?? GammaController.MAX_TEMPERATURE;

    /// <summary>Stores the color temperature range [min, max] (K) and syncs it everywhere.</summary>
    public void SetTemperatureRange(float minK, float maxK)
    {
        if (_settings == null) return;
        minK = Math.Clamp(minK, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        maxK = Math.Clamp(maxK, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        if (minK >= maxK) return; // invalid range: ignore
        _settings.MinTemperature = minK;
        _settings.MaxTemperature = maxK;
        SettingsManager.Save(_settings);
        if (_gamma != null) { _gamma.MinTemperature = minK; _gamma.MaxTemperature = maxK; }
        if (_popup != null) { _popup.MinTemperature = minK; _popup.MaxTemperature = maxK; }
        // Clamp the current temperature into the new range so the display
        // never sits outside what the user configured. The clamp happens
        // inside SetTemperature; skip a pointless smooth animation when the
        // clamped delta is tiny (<50K, same rule as the color temp switch).
        if (_gamma != null && _settings.ColorTemperatureEnabled)
        {
            float cur = _gamma.CurrentTemperature;
            float clamped = Math.Clamp(cur, minK, maxK);
            bool needsSmooth = _settings.TemperatureSmooth && !_disableActive
                && Math.Abs(cur - clamped) >= 50f;
            if (needsSmooth)
                StartSmoothTransition(_gamma.CurrentBrightness, clamped, false, true);
            else
                _gamma.SetTemperature(clamped);
        }
    }

    /// <summary>
    /// Sets the color temperature directly (preset quick buttons).
    /// Ignored while color temperature is disabled; the preset buttons are
    /// disabled (grayed) by the settings page in that case anyway.
    /// </summary>
    public void SetColorTemperature(float kelvin)
    {
        if (_settings?.ColorTemperatureEnabled != true) return;
        OnManualAdjustment();
        bool solarRuns = _settings.SolarAdjustEnabled && _settings.SolarManuallyOverridden != true;
        bool smooth = _settings.TemperatureSmooth && !solarRuns;
        if (smooth)
        {
            // 平滑路径：TemperatureChanged 由动画结束后统一触发一次，
            // 避免下拉在动画中间值上高频跳变（预设显示错乱）。
            // SaveSettings 也移到动画完成后（OnSmoothTick 内），否则
            // 动画未开始时 gamma 还是旧值，会把 LastTemperature 保存成
            // 旧值（如 6000），下次开关色温恢复错误（实测 bug）。
            StartSmoothTransition(_gamma?.CurrentBrightness ?? 1f, kelvin, false, true);
        }
        else
        {
            _gamma?.SetTemperature(kelvin);
            TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            SaveSettings();
        }
        if (_gamma != null && _popup != null)
            _popup.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f,
            _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, true);
    }
    /// <summary>Returns the current color temperature in kelvin (read-only).</summary>
    public float GetCurrentTemperature() => _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;

    /// <summary>Returns the current brightness in 0..1 (read-only).</summary>
    public float GetCurrentBrightness() => _gamma?.CurrentBrightness ?? 1.0f;

    /// <summary>Raised whenever the color temperature changes (popup slider,
    /// preset buttons, wheel, hotkeys). Used by the settings page to keep
    /// the preset button highlight in sync.</summary>
    public event EventHandler<float>? TemperatureChanged;

    /// <summary>Raised whenever the brightness changes (popup slider, OSD,
    /// wheel, hotkeys, level presets). Used by the settings page to keep
    /// the brightness level dropdown in sync with the current value.</summary>
    public event EventHandler<float>? BrightnessChanged;


    // ==================== 时间调整（日出日落自动调节） ====================

    /// <summary>返回时间调整总开关。</summary>
    public bool GetSolarAdjustEnabled() => _settings?.SolarAdjustEnabled ?? false;

    /// <summary>返回模式（true = 手动，false = 物理位置）。</summary>
    public bool GetSolarManualMode() => _settings?.SolarManualMode ?? true;

    /// <summary>手动日出时刻（分钟）。</summary>
    public int GetManualSunriseMinutes() => _settings?.ManualSunriseMinutes ?? 440;

    /// <summary>手动日落时刻（分钟）。</summary>
    public int GetManualSunsetMinutes() => _settings?.ManualSunsetMinutes ?? 990;

    /// <summary>物理位置纬度。</summary>
    public double GetSolarLatitude() => _settings?.SolarLatitude ?? 39.9042;

    /// <summary>物理位置经度。</summary>
    public double GetSolarLongitude() => _settings?.SolarLongitude ?? 116.4074;

    /// <summary>是否已成功获取过物理位置。</summary>
    public bool GetSolarLocationSet() => _settings?.SolarLocationSet ?? false;

    /// <summary>白天目标色温（K）。</summary>
    public float GetDayTemperature() => _settings?.DayTemperature ?? 6600f;

    /// <summary>白天目标亮度（0~1）。</summary>
    public float GetDayBrightness() => _settings?.DayBrightness ?? 1.0f;

    /// <summary>夜晚目标色温（K）。</summary>
    public float GetNightTemperature() => _settings?.NightTemperature ?? 3900f;

    /// <summary>夜晚目标亮度（0~1）。</summary>
    public float GetNightBrightness() => _settings?.NightBrightness ?? 0.85f;

    /// <summary>过渡时长（分钟）。</summary>
    public int GetTransitionMinutes() => _settings?.TransitionMinutes ?? 0;

    /// <summary>手动接管标志。</summary>
    public bool GetSolarManuallyOverridden() => _settings?.SolarManuallyOverridden ?? false;

    /// <summary>
    /// 设置时间调整总开关。开启时清除手动接管标志并（重）启动调度；
    /// 关闭时停止调度。
    /// </summary>
    public void SetSolarAdjustEnabled(bool enabled)
    {
        if (_settings == null) return;
        _settings.SolarAdjustEnabled = enabled;
        if (enabled)
        {
            _settings.SolarManuallyOverridden = false;
        }
        SettingsManager.Save(_settings);
        if (enabled)
        {
            // Smooth to the current solar target, then start the scheduler.
            // (Starting immediately would make the first Tick jump straight
            // to the target value instead of transitioning smoothly.)
            bool smoothB = _settings.BrightnessSmooth;
            bool smoothT = _settings.TemperatureSmooth;
            if ((smoothB || smoothT) && !_disableActive)
            {
                var (tb, tt) = _solarScheduler?.GetCurrentTargets() ?? (1.0f, GammaController.DEFAULT_TEMPERATURE);
                StartSmoothTransition(tb, tt, smoothB, smoothT, () =>
                {
                    // 动画期间用户可能又把开关关掉（快速连按）：此时不得启动调度器，
                    // 否则"关闭"被旧动画的 done 回调覆盖，日落模式复活。
                    if (_settings?.SolarAdjustEnabled != true) return;
                    if (_settings.SolarManuallyOverridden) return;
                    _solarScheduler?.Start();
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? tb,
                        _gamma?.CurrentTemperature ?? tt, _settings?.ColorTemperatureEnabled ?? false);
                });
            }
            else
            {
                ApplySolarScheduler();
            }
        }
        else
        {
            // Disabling auto adjust: transition smoothly to the DAY target
            // (normal bright colors). Not LastBrightness/LastTemperature:
            // during solar running those may hold night manual values.
            // 必须先停调度器：否则它每 2 秒仍会把 gamma 拉回日落值，
            // 表现为"关闭后几秒又变回日落模式"。
            _solarScheduler?.Stop();
            float targetBright = _settings.DayBrightness;
            float targetTemp = _settings.ColorTemperatureEnabled
                ? _settings.DayTemperature
                : GammaController.DEFAULT_TEMPERATURE;
            bool smoothB = _settings.BrightnessSmooth;
            bool smoothT = _settings.TemperatureSmooth;
            if (smoothB || smoothT)
            {
                StartSmoothTransition(targetBright, targetTemp, smoothB, smoothT);
            }
            else
            {
                _gamma?.SetBrightness(targetBright);
                _gamma?.SetTemperature(targetTemp);
                SaveSettings();
                _popup?.SyncFromGamma(targetBright, targetTemp);
                BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? targetBright);
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? targetTemp);
                _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? targetBright,
                    _gamma?.CurrentTemperature ?? targetTemp, _settings?.ColorTemperatureEnabled ?? false);
            }
        }
    }

    /// <summary>设置模式（true = 手动，false = 物理位置），并立即重算。</summary>
    public void SetSolarManualMode(bool manual)
    {
        if (_settings == null) return;
        _settings.SolarManualMode = manual;
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>手动日出（分钟）。</summary>
    public void SetManualSunriseMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.ManualSunriseMinutes = Math.Clamp(minutes, 0, 1439);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>手动日落（分钟）。</summary>
    public void SetManualSunsetMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.ManualSunsetMinutes = Math.Clamp(minutes, 0, 1439);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>物理位置（纬度/经度）。</summary>
    public void SetSolarLocation(double latitude, double longitude)
    {
        if (_settings == null) return;
        _settings.SolarLatitude = latitude;
        _settings.SolarLongitude = longitude;
        _settings.SolarLocationSet = true;
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>白天目标色温（K）。</summary>
    public void SetDayTemperature(float kelvin)
    {
        if (_settings == null) return;
        _settings.DayTemperature = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>白天目标亮度（0~1）。</summary>
    public void SetDayBrightness(float brightness)
    {
        if (_settings == null) return;
        _settings.DayBrightness = Math.Clamp(brightness, 0f, 1f);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>夜晚目标色温（K）。</summary>
    public void SetNightTemperature(float kelvin)
    {
        if (_settings == null) return;
        _settings.NightTemperature = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>夜晚目标亮度（0~1）。</summary>
    public void SetNightBrightness(float brightness)
    {
        if (_settings == null) return;
        _settings.NightBrightness = Math.Clamp(brightness, 0f, 1f);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
        RefreshSolarNow();
    }

    /// <summary>过渡时长（分钟）。</summary>
    public void SetTransitionMinutes(int minutes)
    {
        if (_settings == null) return;
        _settings.TransitionMinutes = Math.Clamp(minutes, 0, 60);
        SettingsManager.Save(_settings);
        ApplySolarScheduler();
    }

    /// <summary>
    /// 目标值滑块拖动时调用：更新目标值但不打断调度，且当前立即跟随。
    /// 由设置页在滑块 ValueChanged/ValueCommitted 中调用 setter 后触发。
    /// </summary>
    public void RefreshSolarNow()
    {
        if (_settings?.SolarAdjustEnabled != true) return;
        _solarScheduler?.ApplyNowInstant();
    }

    /// <summary>
    /// 根据当前设置启停调度器（总开关 + 手动接管标志决定）。
    /// </summary>
    private void ApplySolarScheduler()
    {
        if (_solarScheduler == null || _gamma == null) return;
        if (_settings?.SolarAdjustEnabled == true && _settings.SolarManuallyOverridden != true)
        {
            if (!_solarScheduler.IsRunning) _solarScheduler.Start();
            else _solarScheduler.Tick();
        }
        else
        {
            _solarScheduler.Stop();
        }
    }

    // ==================== 系统事件（自愈 + 全屏暂停） ====================

    /// <summary>
    /// 睡眠唤醒后：部分驱动会重置 gamma ramp，重新应用当前亮度/色温。
    /// 若正处于全屏暂停则跳过（全屏期间本来就不应用 gamma）。
    /// </summary>
    private void OnSystemResumed()
    {
        if (_gamma == null) return;
        if (_settings?.GammaSelfHealEnabled != true) return;
        if (_fullscreenPaused) return;
        // 唤醒后驱动/显示栈可能尚未就绪，延迟一下再重放。
        var t = new System.Windows.Forms.Timer { Interval = 800 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (_gamma == null || _fullscreenPaused) return;
            _gamma.RefreshDisplays();
            _trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
        };
        t.Start();
    }

    /// <summary>
    /// 显示器热插拔/分辨率变化后：重建显示器列表并重放当前值。
    /// </summary>
    private void OnDisplayChanged()
    {
        if (_gamma == null) return;
        if (_settings?.GammaSelfHealEnabled != true) return;
        if (_fullscreenPaused) return;
        _gamma.RefreshDisplays();
        _trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
    }

    /// <summary>
    /// 进入全屏：暂停 gamma 调节，从当前画面平滑过渡到原生色彩
    /// （亮度 100%、色温 6600 中性白；1.2s ease-out，与挡位切换同节奏）。
    /// 内部亮度/色温状态保留，退出全屏时平滑恢复。
    /// </summary>
    private void OnFullscreenEntered()
    {
        if (_settings?.PauseInFullscreenEnabled != true) return;
        // 禁用中进入全屏：gamma 已暂停（原生色彩），无需重复暂停；
        // 仅同步全屏标志，退出全屏时不恢复（仍禁用）。
        if (_disableActive)
        {
            _fullscreenPaused = true;
            return;
        }
        // 退出动画进行中再次进入全屏：允许重入，翻转动画方向回原生。
        bool exitAnimRunning = _fullscreenAnimTimer != null && _fullscreenAnimExit;
        if (_fullscreenPaused && !exitAnimRunning) return;
        if (!_fullscreenPaused)
        {
            // 记住暂停前值，退出时平滑恢复。
            _fullscreenBrightnessBefore = _gamma?.CurrentBrightness ?? 1.0f;
            _fullscreenTemperatureBefore = _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE;
            _fullscreenPaused = true;
            // 翻转暂停标志（拦截调节入口），画面由动画驱动，不立即跳变。
            _gamma?.SetPaused(true);
        }
        // 从当前画面平滑过渡到原生（100% / 6600K）。
        StartFullscreenTransition(1.0f, GammaController.DEFAULT_TEMPERATURE, exit: false);
    }

    /// <summary>
    /// 退出全屏：从原生色彩平滑过渡回暂停前的亮度/色温，恢复调节。
    /// </summary>
    private void OnFullscreenExited()
    {
        if (!_fullscreenPaused) return;
        // 禁用中退出全屏：不恢复 gamma（仍禁用），仅清除全屏标志。
        if (_disableActive)
        {
            _fullscreenPaused = false;
            return;
        }
        // 注意：不在这里 SetPaused(false) —— 恢复动画期间必须保持
        // GammaController 暂停标志，否则 ApplyPausedFrame 的
        // if (!_paused) return; 守卫会让动画帧全部空转，画面 1.2s
        // 不动，动画结束才被 ApplyPausedState 一次性重放 → 瞬间跳变。
        // 暂停标志在动画结束（_fullscreenAnimExit=true）时解除。
        StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
    }

    /// <summary>
    /// 启动全屏暂停/恢复的平滑过渡动画（ease-out cubic，SmoothDurationMs）。
    /// 起点读屏幕当前实际值，终点为目标值；每帧用 ApplyPausedFrame 写画面
    /// （不动内部状态），动画结束后 ApplyPausedState 定稿（暂停→原生 ramp，
    /// 恢复→重放内部值）。用独立 timer，不与挡位平滑动画冲突。
    /// </summary>
    private void StartFullscreenTransition(float targetBright, float targetTemp, bool exit = false)
    {
        if (_gamma == null) return;
        // Fullscreen enter/exit is covered by the smooth switches per channel:
        // a channel with its smooth option off reaches its target instantly
        // (no pointless animation), the other channel still animates.
        bool fsSmoothB = _settings?.BrightnessSmooth == true;
        bool fsSmoothT = _settings?.TemperatureSmooth == true;
        if (!fsSmoothB && !fsSmoothT)
        {
            // 全屏进入：已 SetPaused(true)，定格原生 ramp；退出：解除暂停后重放内部值。
            if (exit)
            {
                _fullscreenPaused = false;
                if (!_disableActive) _gamma?.SetPaused(false);
            }
            _gamma?.ApplyPausedState();
            return;
        }
        _fullscreenAnimSmoothB = fsSmoothB;
        _fullscreenAnimSmoothT = fsSmoothT;
        // 不平滑的通道：动画开始时就把该通道直接写到目标值（画面立即到位），
        // 动画仅负责剩余通道。注意 ApplyPausedFrame 会在暂停态下写入，
        // 所以这里先置好帧数据再启动动画。
        _fullscreenAnimTargetBright = targetBright;
        _fullscreenAnimTargetTemp = targetTemp;
        _fullscreenAnimStartBright = _gamma.ReadCurrentBrightness();
        _fullscreenAnimStartTemp = _gamma.ReadCurrentTemperature();
        if (!fsSmoothB)
            _gamma?.ApplyPausedFrame(targetBright, _gamma.ReadCurrentTemperature());
        if (!fsSmoothT)
            _gamma?.ApplyPausedFrame(_gamma.ReadCurrentBrightness(), targetTemp);
        _fullscreenAnimExit = exit;
        _fullscreenAnimStartTime = DateTime.Now;
        if (_fullscreenAnimTimer == null)
        {
            _fullscreenAnimTimer = new System.Windows.Forms.Timer { Interval = SmoothTickMs };
            _fullscreenAnimTimer.Tick += OnFullscreenSmoothTick;
        }
        _fullscreenAnimTimer.Start();
    }

    private void OnFullscreenSmoothTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _fullscreenAnimStartTime).TotalMilliseconds / SmoothDurationMs;
        if (t >= 1.0) t = 1.0;
        double ease = EaseOutCubic(t);
        // 不平滑的通道直接使用目标值（动画开始时已瞬时写到位，这里保持一致）。
        float b = _fullscreenAnimSmoothB
            ? (float)(_fullscreenAnimStartBright + (_fullscreenAnimTargetBright - _fullscreenAnimStartBright) * ease)
            : _fullscreenAnimTargetBright;
        float k = _fullscreenAnimSmoothT
            ? (float)(_fullscreenAnimStartTemp + (_fullscreenAnimTargetTemp - _fullscreenAnimStartTemp) * ease)
            : _fullscreenAnimTargetTemp;
        _gamma?.ApplyPausedFrame(b, k);
        if (t >= 1.0)
        {
            _fullscreenAnimTimer?.Stop();
            _fullscreenAnimTimer?.Dispose();
            _fullscreenAnimTimer = null;
            if (_fullscreenAnimExit)
            {
                // 退出动画结束：先解除暂停，再重放内部值（= 动画终点，无跳变）。
                _fullscreenPaused = false;
                // 禁用激活中不解除 gamma 暂停（两个暂停源统一管理）。
                if (!_disableActive) _gamma?.SetPaused(false);
                _gamma?.ApplyPausedState();
            }
            else
            {
                // 进入动画结束：保持暂停，定格原生 ramp（= 动画终点，无跳变）。
                _gamma?.ApplyPausedState();
            }
        }
    }

    // ==================== 右键菜单"禁用" ====================

    /// <summary>禁用到期时间（null=未禁用）。</summary>
    public DateTime? GetDisableUntil() => _settings?.DisableUntil;

    /// <summary>是否处于禁用中（到期时间在未来且已激活）。</summary>
    public bool IsDisableActive() => _disableActive;

    /// <summary>禁用剩余时间（null=未禁用/已到期）。</summary>
    public TimeSpan? GetDisableRemaining()
    {
        var until = _settings?.DisableUntil;
        if (until == null) return null;
        if (until.Value == DateTime.MaxValue) return null; // 永久禁用
        var rem = until.Value - DateTime.Now;
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }

    /// <summary>
    /// 设置禁用：duration 为 null 表示永久禁用；TimeSpan.Zero 表示解除禁用。
    /// 禁用 = gamma 暂停调节（原生色彩），所有调节入口被拦；到期自动恢复。
    /// </summary>
    public void SetDisable(TimeSpan? duration)
    {
        if (_settings == null || _gamma == null) return;
        if (duration == null)
        {
            // 永久禁用：从现在起永不恢复。
            _settings.DisableUntil = DateTime.MaxValue;
            SettingsManager.Save(_settings);
            ApplyDisable(true);
        }
        else if (duration == TimeSpan.Zero)
        {
            // 解除禁用：清除到期时间并平滑恢复。
            _settings.DisableUntil = null;
            SettingsManager.Save(_settings);
            ApplyDisable(false);
        }
        else
        {
            _settings.DisableUntil = DateTime.Now + duration.Value;
            SettingsManager.Save(_settings);
            ApplyDisable(true);
        }
        // 启动/停止到期检查定时器。
        UpdateDisableTimer();
    }

    /// <summary>
    /// 激活或解除禁用。激活：记录当前值→暂停 gamma→平滑过渡到原生；
    /// 解除：平滑过渡回记录值→恢复 gamma 调节。复用暂停动画机制。
    /// </summary>
    private void ApplyDisable(bool disable)
    {
        if (_gamma == null) return;
        if (disable)
        {
            if (_disableActive) return;
            // 记录禁用前值（若全屏暂停中则用全屏记录值，保持一致性）。
            _disableBrightnessBefore = _gamma.CurrentBrightness;
            _disableTemperatureBefore = _gamma.CurrentTemperature;
            _disableActive = true;
            // 暂停标志：全屏与禁用任一激活都保持暂停。
            _gamma.SetPaused(true);
            // 停掉太阳能调度（禁用期间不自动调节）。
            _solarScheduler?.Stop();
            // 从当前画面平滑过渡到原生（100% / 6600K）。
            StartDisableTransition(1.0f, GammaController.DEFAULT_TEMPERATURE, exit: false, done: null);
        }
        else
        {
            if (!_disableActive) return;
            // 全屏仍暂停中：画面保持原生，直接解除禁用（不跑恢复动画，
            // 否则动画会先把画面拉到禁用前值、结束时又因全屏暂停被
            // ApplyPausedState 拉回原生，造成跳变）。
            if (_fullscreenPaused)
            {
                _disableActive = false;
                UpdateDisableTimer();
                return;
            }
            // 平滑恢复（动画期间保持暂停，结束才解除）。
            StartDisableTransition(_disableBrightnessBefore, _disableTemperatureBefore, exit: true, done: OnDisableResumed);
        }
    }

    /// <summary>禁用恢复动画结束后的收尾（解除暂停并重放内部值）。</summary>
    private void OnDisableResumed()
    {
        _disableActive = false;
        // 全屏仍暂停中则不解除 gamma 暂停（两个暂停源统一）。
        if (!_fullscreenPaused)
        {
            _gamma?.SetPaused(false);
        }
        _gamma?.ApplyPausedState();
        // 恢复太阳能调度（若开启且未被手动接管）。
        ApplySolarScheduler();
        UpdateDisableTimer();
    }

    /// <summary>启动禁用进入/恢复的平滑过渡动画（复用全屏动画节奏）。</summary>
    private void StartDisableTransition(float targetBright, float targetTemp, bool exit, Action? done)
    {
        if (_gamma == null) return;
        // 禁用进入/恢复由平滑开关按通道控制：没开启平滑的通道瞬时到位。
        bool disSmoothB = _settings?.BrightnessSmooth == true;
        bool disSmoothT = _settings?.TemperatureSmooth == true;
        if (!disSmoothB && !disSmoothT)
        {
            _gamma?.ApplyPausedState();
            done?.Invoke();
            return;
        }
        _disableAnimSmoothB = disSmoothB;
        _disableAnimSmoothT = disSmoothT;
        _disableAnimTargetBright = targetBright;
        _disableAnimTargetTemp = targetTemp;
        _disableAnimStartBright = _gamma.ReadCurrentBrightness();
        _disableAnimStartTemp = _gamma.ReadCurrentTemperature();
        // 不平滑的通道立即写到目标值（暂停态下 ApplyPausedFrame 直接写屏幕）。
        if (!disSmoothB)
            _gamma?.ApplyPausedFrame(targetBright, _gamma.ReadCurrentTemperature());
        if (!disSmoothT)
            _gamma?.ApplyPausedFrame(_gamma.ReadCurrentBrightness(), targetTemp);
        _disableAnimExit = exit;
        _disableAnimDone = done;
        _disableAnimStartTime = DateTime.Now;
        if (_disableAnimTimer == null)
        {
            _disableAnimTimer = new System.Windows.Forms.Timer { Interval = SmoothTickMs };
            _disableAnimTimer.Tick += OnDisableSmoothTick;
        }
        _disableAnimTimer.Start();
    }

    private void OnDisableSmoothTick(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _disableAnimStartTime).TotalMilliseconds / SmoothDurationMs;
        if (t >= 1.0) t = 1.0;
        double ease = EaseOutCubic(t);
        // 不平滑的通道直接使用目标值（动画开始时已瞬时写到位）。
        float b = _disableAnimSmoothB
            ? (float)(_disableAnimStartBright + (_disableAnimTargetBright - _disableAnimStartBright) * ease)
            : _disableAnimTargetBright;
        float k = _disableAnimSmoothT
            ? (float)(_disableAnimStartTemp + (_disableAnimTargetTemp - _disableAnimStartTemp) * ease)
            : _disableAnimTargetTemp;
        _gamma?.ApplyPausedFrame(b, k);
        if (t >= 1.0)
        {
            _disableAnimTimer?.Stop();
            _disableAnimTimer?.Dispose();
            _disableAnimTimer = null;
            var done = _disableAnimDone;
            _disableAnimDone = null;
            // 定格画面（暂停→原生 ramp；恢复→重放内部值）。
            _gamma?.ApplyPausedState();
            done?.Invoke();
        }
    }

    /// <summary>启动时恢复禁用状态（DisableUntil 持久化）。</summary>
    private void RestoreDisableState()
    {
        if (_settings == null || _gamma == null) return;
        var until = _settings.DisableUntil;
        if (until == null) return;
        if (until.Value <= DateTime.Now)
        {
            // 已过期：清除并恢复。
            _settings.DisableUntil = null;
            SettingsManager.Save(_settings);
        }
        else
        {
            // 未过期：立即进入禁用（启动时直接从原生色彩开始，无动画）。
            _disableBrightnessBefore = _gamma.CurrentBrightness;
            _disableTemperatureBefore = _gamma.CurrentTemperature;
            _disableActive = true;
            _gamma.SetPaused(true);
            _gamma.ApplyPausedState(); // 定格原生 ramp
            _solarScheduler?.Stop();
        }
        UpdateDisableTimer();
    }

    /// <summary>启动/停止禁用到期检查定时器（1 秒 tick）。</summary>
    private void UpdateDisableTimer()
    {
        var until = _settings?.DisableUntil;
        // DateTime.MaxValue 代表永久禁用，无需到期检查。
        bool needCheck = until != null && until.Value != DateTime.MaxValue && until.Value > DateTime.Now;
        if (needCheck)
        {
            if (_disableTimer == null)
            {
                _disableTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _disableTimer.Tick += OnDisableTimerTick;
            }
            _disableTimer.Start();
        }
        else
        {
            _disableTimer?.Stop();
        }
    }

    private void OnDisableTimerTick(object? sender, EventArgs e)
    {
        if (_settings == null) return;
        var until = _settings.DisableUntil;
        if (until == null) return;
        if (until.Value <= DateTime.Now)
        {
            // 到期：清除并平滑恢复。
            _settings.DisableUntil = null;
            SettingsManager.Save(_settings);
            _disableTimer?.Stop();
            ApplyDisable(false);
        }
    }

    /// <summary>
    /// 获取当前日出/日落时刻（供"禁用→日出/日落"选项计算到期时间）。
    /// 手动模式用设置的时间，物理位置模式用坐标计算。
    /// </summary>
    public (TimeOnly Sunrise, TimeOnly Sunset) GetSolarSunriseSunset()
    {
        if (_settings == null) return (new TimeOnly(6, 0), new TimeOnly(18, 0));
        if (_settings.SolarManualMode)
        {
            return (
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunriseMinutes, 0, 1439))),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunsetMinutes, 0, 1439)))
            );
        }
        return SolarTimes.Calculate(_settings.SolarLatitude, _settings.SolarLongitude, DateTime.Now);
    }

    /// <summary>
    /// 计算"日出/日落"禁用模式的到期时间：
    /// 白天（当前在日出~日落之间）→ 到日落时刻；
    /// 夜晚（当前在日落~日出之间）→ 到明天日出时刻。
    /// </summary>
    public DateTime GetSolarDisableUntil()
    {
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSolarSunriseSunset();
        bool isDaytime = now.TimeOfDay >= sunrise.ToTimeSpan() && now.TimeOfDay < sunset.ToTimeSpan();
        if (isDaytime)
        {
            // 白天：到今天的日落。
            var today = now.Date + sunset.ToTimeSpan();
            return today > now ? today : today.AddDays(1);
        }
        // 夜晚：到明天（或今天若尚未到日出）的日出。
        var next = now.Date + sunrise.ToTimeSpan();
        if (next <= now) next = next.AddDays(1);
        return next;
    }

    /// <summary>是否处于日出/日落禁用模式（到期时间为日出或日落时刻）。</summary>
    public bool IsSolarDisableActive()
    {
        var until = _settings?.DisableUntil;
        if (until == null) return false;
        var (sunrise, sunset) = GetSolarSunriseSunset();
        var now = DateTime.Now;
        // 到期时间匹配日出或日落时刻（±5 分钟容差）。
        bool matchesSunrise = Math.Abs((until.Value - now.Date - sunrise.ToTimeSpan()).TotalMinutes) < 5;
        bool matchesSunset = Math.Abs((until.Value - now.Date - sunset.ToTimeSpan()).TotalMinutes) < 5;
        return matchesSunrise || matchesSunset;
    }

    /// <summary>褰撳墠鏄惁涓虹櫧澶╋紙鏃ュ嚭~鏃ヨ惤涔嬮棿锛夈€?/summary>
    public bool IsDaytimeNow()
    {
        var now = DateTime.Now;
        var (sunrise, sunset) = GetSolarSunriseSunset();
        return now.TimeOfDay >= sunrise.ToTimeSpan() && now.TimeOfDay < sunset.ToTimeSpan();
    }

    /// <summary>
    /// 鍙抽敭鑿滃崟"绂佺敤"璇锋眰澶勭悊锛?    /// null=姘镐箙绂佺敤锛汿imeSpan.Zero=瑙ｉ櫎锛涙鏃堕暱=涓存椂绂佺敤锛?    /// -1 绉?鏃ュ嚭/鏃ヨ惤锛堟寜褰撳墠鏃跺埢璁＄畻鍒版湡鏃堕棿锛夈€?    /// </summary>
    private void OnDisableRequested(TimeSpan? duration)
    {
        if (duration == TimeSpan.FromSeconds(-1))
        {
            // 日出/日落模式：按当前白天/夜晚计算到期时刻。
            SetDisable(GetSolarDisableUntil() - DateTime.Now);
        }
        else
        {
            SetDisable(duration);
        }
    }

    /// <summary>Returns whether the wheel OSD overlay is shown.</summary>
    public bool GetShowOverlay() => _settings?.ShowOverlay ?? true;

    /// <summary>Returns whether the settings window should be always-on-top.</summary>
    public bool GetTopMost() => _settings?.SettingsTopMost ?? false;

    /// <summary>Stores the settings-window always-on-top preference.</summary>
    public void SetTopMost(bool topMost)
    {
        if (_settings == null) return;
        _settings.SettingsTopMost = topMost;
        SettingsManager.Save(_settings);
    }

    /// <summary>
    /// Resets all settings to their defaults and applies the changes
    /// immediately: language/theme re-applied, step size restored, brightness
    /// back to 100%. Hotkey bindings are intentionally NOT touched here (the
    /// hotkeys page has its own "clear all" action).
    /// </summary>
    public void ResetSettings()
    {
        if (_settings == null) return;

        // 重置会清除禁用状态（含持久化的 DisableUntil）：先解除暂停，
        // 否则下面的 SetBrightness/SetTemperature 会被 _paused 拦截 no-op，
        // 重置后画面纹丝不动。手动重置不清除（与"关闭"选项语义一致）。
        if (_disableActive)
        {
            _disableActive = false;
            _gamma?.SetPaused(false);
        }
        _settings.DisableUntil = null;

        _settings.LastBrightness = 1.0f;
        _settings.LastTemperature = GammaController.DEFAULT_TEMPERATURE;
        _settings.StepSize = 0.05f;
        _settings.WheelEnabled = true;
        _settings.SettingsTopMost = false;
        _settings.InvertScroll = false;
        _settings.ShowOverlay = true;
        _settings.OverlayDurationMs = 1500;
        _settings.Language = Language.System;
        _settings.Theme = ThemeMode.System;
        _settings.PopupTheme = ThemeMode.System;
        _settings.ColorTemperatureEnabled = false;
        _settings.TemperatureStepSize = GammaController.DEFAULT_TEMPERATURE_STEP;
        _settings.MinTemperature = GammaController.MIN_TEMPERATURE;
        _settings.MaxTemperature = GammaController.MAX_TEMPERATURE;
        _settings.AllHotKeysEnabled = true;
        _settings.StartupEnabled = null; // first-run: follow the registry
        // 时间调整：重置为默认（关、手动模式、默认目标值、默认过渡）。
        _settings.SolarAdjustEnabled = false;
        _settings.SolarManualMode = true;
        _settings.ManualSunriseMinutes = 440;
        _settings.ManualSunsetMinutes = 990;
        _settings.SolarLatitude = 39.9042;
        _settings.SolarLongitude = 116.4074;
        _settings.SolarLocationSet = false;
        _settings.DayTemperature = 6600f;
        _settings.DayBrightness = 1.0f;
        _settings.NightTemperature = 3900f;
        _settings.NightBrightness = 0.85f;
        _settings.TransitionMinutes = 0;
        _settings.SolarManuallyOverridden = false;
        _settings.BrightnessSmooth = true;
        _settings.TemperatureSmooth = true;
        _settings.GammaSelfHealEnabled = true;
        _settings.PauseInFullscreenEnabled = true;
        SettingsManager.Save(_settings);

        // Apply language + themes immediately (same as startup).
        Localization.Setting = _settings.Language;
        Localization.Current = Localization.Resolve(_settings.Language).Effective;
        ThemeManager.Apply(_settings.Theme);
        ThemeManager.ApplyPopupTheme(_settings.PopupTheme);

        // Restore step size everywhere.
        if (_gamma != null) _gamma.StepSize = _settings.StepSize;
        if (_popup != null) _popup.StepSize = _settings.StepSize;
        if (_gamma != null) _gamma.TemperatureStepSize = _settings.TemperatureStepSize;
        if (_popup != null) _popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
        // Restore temperature range everywhere.
        if (_gamma != null) { _gamma.MinTemperature = _settings.MinTemperature; _gamma.MaxTemperature = _settings.MaxTemperature; }
        if (_popup != null) { _popup.MinTemperature = _settings.MinTemperature; _popup.MaxTemperature = _settings.MaxTemperature; }

        // Re-register hotkeys (bindings unchanged; this just refreshes
        // whatever is currently bound).
        RegisterHotkeys();

        // Back to full brightness (and neutral color temperature).
        // ResetSettings restored BrightnessSmooth/TemperatureSmooth = true,
        // so this transition follows the smooth switches.
        bool resetSmoothB = _settings.BrightnessSmooth;
        bool resetSmoothT = _settings.TemperatureSmooth;
        if (resetSmoothB || resetSmoothT)
        {
            StartSmoothTransition(1.0f, GammaController.DEFAULT_TEMPERATURE, resetSmoothB, resetSmoothT);
        }
        else
        {
            _gamma?.SetBrightness(1.0f);
            _gamma?.SetTemperature(GammaController.DEFAULT_TEMPERATURE);
        }
        _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);

        // 时间调整已重置为关闭，停止调度器。
        ApplySolarScheduler();
    }

    /// <summary>
    /// Clears ALL hotkey bindings (increase/decrease brightness and power
    /// off) and re-registers (i.e. unregisters) them. Used by the "clear
    /// all" button on the hotkeys page.
    /// </summary>
    public void ClearAllHotkeys()
    {
        if (_settings == null) return;
        _settings.IncreaseBrightnessHotKey = "";
        _settings.DecreaseBrightnessHotKey = "";
        _settings.PowerOffHotKey = "";
        _settings.IncreaseTemperatureHotKey = "";
        _settings.DecreaseTemperatureHotKey = "";
        // Keep the enable switches on (they only gate an existing binding).
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Stores the OSD visibility preference.</summary>
    public void SetShowOverlay(bool show)
    {
        if (_settings == null) return;
        _settings.ShowOverlay = show;
        SettingsManager.Save(_settings);
    }

    /// <summary>Returns the current hotkey for increasing brightness ("" if not bound).</summary>
    public string GetIncreaseBrightnessHotKey() => _settings?.IncreaseBrightnessHotKey ?? "";

    /// <summary>Returns whether the increase-brightness hotkey is enabled.</summary>
    public bool GetIncreaseBrightnessHotKeyEnabled() => _settings?.IncreaseBrightnessHotKeyEnabled ?? true;

    /// <summary>Sets whether the increase-brightness hotkey is enabled (re-registers).</summary>
    public void SetIncreaseBrightnessHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseBrightnessHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Returns the current hotkey for decreasing brightness ("" if not bound).</summary>
    public string GetDecreaseBrightnessHotKey() => _settings?.DecreaseBrightnessHotKey ?? "";

    /// <summary>Returns whether the decrease-brightness hotkey is enabled.</summary>
    public bool GetDecreaseBrightnessHotKeyEnabled() => _settings?.DecreaseBrightnessHotKeyEnabled ?? true;

    /// <summary>Sets whether the decrease-brightness hotkey is enabled (re-registers).</summary>
    public void SetDecreaseBrightnessHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseBrightnessHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for increasing brightness and registers it globally.</summary>
    public bool SetIncreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.IncreaseBrightnessHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.IncreaseBrightnessHotKey = newValue;
        return CommitHotKey("IncBrightness", newValue, previous,
            v => _settings.IncreaseBrightnessHotKey = v,
            _settings.IncreaseBrightnessHotKeyEnabled);
    }

    /// <summary>Sets the hotkey for decreasing brightness and registers it globally.</summary>
    public bool SetDecreaseBrightnessHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.DecreaseBrightnessHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.DecreaseBrightnessHotKey = newValue;
        return CommitHotKey("DecBrightness", newValue, previous,
            v => _settings.DecreaseBrightnessHotKey = v,
            _settings.DecreaseBrightnessHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for turning off the display ("" if not bound).</summary>
    public string GetPowerOffHotKey() => _settings?.PowerOffHotKey ?? "";

    /// <summary>Returns whether the power-off hotkey is enabled.</summary>
    public bool GetPowerOffHotKeyEnabled() => _settings?.PowerOffHotKeyEnabled ?? true;

    /// <summary>Sets whether the power-off hotkey is enabled (re-registers).</summary>
    public void SetPowerOffHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.PowerOffHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for turning off the display and registers it globally.</summary>
    public bool SetPowerOffHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.PowerOffHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.IncreaseTemperatureHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.PowerOffHotKey = newValue;
        return CommitHotKey("PowerOff", newValue, previous,
            v => _settings.PowerOffHotKey = v,
            _settings.PowerOffHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for increasing color temperature ("" if not bound).</summary>
    public string GetIncreaseTemperatureHotKey() => _settings?.IncreaseTemperatureHotKey ?? "";

    /// <summary>Returns whether the increase-temperature hotkey is enabled.</summary>
    public bool GetIncreaseTemperatureHotKeyEnabled() => _settings?.IncreaseTemperatureHotKeyEnabled ?? true;

    /// <summary>Sets whether the increase-temperature hotkey is enabled (re-registers).</summary>
    public void SetIncreaseTemperatureHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.IncreaseTemperatureHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for increasing color temperature and registers it globally.</summary>
    public bool SetIncreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.IncreaseTemperatureHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.DecreaseTemperatureHotKey))
            return false;
        _settings.IncreaseTemperatureHotKey = newValue;
        return CommitHotKey("IncTemperature", newValue, previous,
            v => _settings.IncreaseTemperatureHotKey = v,
            _settings.IncreaseTemperatureHotKeyEnabled);
    }

    /// <summary>Returns the current hotkey for decreasing color temperature ("" if not bound).</summary>
    public string GetDecreaseTemperatureHotKey() => _settings?.DecreaseTemperatureHotKey ?? "";

    /// <summary>Returns whether the decrease-temperature hotkey is enabled.</summary>
    public bool GetDecreaseTemperatureHotKeyEnabled() => _settings?.DecreaseTemperatureHotKeyEnabled ?? true;

    /// <summary>Sets whether the decrease-temperature hotkey is enabled (re-registers).</summary>
    public void SetDecreaseTemperatureHotKeyEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.DecreaseTemperatureHotKeyEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Returns whether ALL hotkeys are enabled (master switch).</summary>
    public bool GetAllHotKeysEnabled() => _settings?.AllHotKeysEnabled ?? true;

    /// <summary>
    /// Sets the master switch: when false, no hotkey is registered regardless
    /// of its individual enabled flag. Re-registers on change.
    /// </summary>
    public void SetAllHotKeysEnabled(bool enabled)
    {
        if (_settings == null || _trayIcon == null) return;
        _settings.AllHotKeysEnabled = enabled;
        SettingsManager.Save(_settings);
        RegisterHotkeys();
    }

    /// <summary>Sets the hotkey for decreasing color temperature and registers it globally.</summary>
    public bool SetDecreaseTemperatureHotKey(string hotkey)
    {
        if (_settings == null || _trayIcon == null) return false;
        string previous = _settings.DecreaseTemperatureHotKey;
        string newValue = hotkey ?? "";
        if (IsTakenByAnother(newValue,
                _settings.IncreaseBrightnessHotKey,
                _settings.DecreaseBrightnessHotKey,
                _settings.PowerOffHotKey,
                _settings.IncreaseTemperatureHotKey))
            return false;
        _settings.DecreaseTemperatureHotKey = newValue;
        return CommitHotKey("DecTemperature", newValue, previous,
            v => _settings.DecreaseTemperatureHotKey = v,
            _settings.DecreaseTemperatureHotKeyEnabled);
    }

    /// <summary>
    /// True when the non-empty combo equals one of the other slots current
    /// bindings (case-insensitive). Used to reject binding the same combo
    /// to two actions.
    /// </summary>
    private static bool IsTakenByAnother(string hotkey, params string[] others)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        foreach (var o in others)
        {
            if (!string.IsNullOrWhiteSpace(o) &&
                string.Equals(o, hotkey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a bound slot is actually registered after the last
    /// RegisterHotkeys() run. Unbound slots always count as active.
    /// </summary>
    private bool HotKeyActive(string slot, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return _hotKeyRegistration.TryGetValue(slot, out bool ok) && ok;
    }

    /// <summary>
    /// Saves and re-registers, then verifies the new binding is active. On
    /// failure restores the previous binding, re-saves, re-registers and
    /// returns false. Used by the hotkey setters to roll back conflicts.
    /// </summary>
    private bool CommitHotKey(string slot, string newValue, string previous, Action<string> apply, bool enabled)
    {
        SettingsManager.Save(_settings!);
        RegisterHotkeys();
        if (!enabled || _settings?.AllHotKeysEnabled == false || HotKeyActive(slot, newValue)) return true;
        apply(previous);
        SettingsManager.Save(_settings!);
        RegisterHotkeys();
        return false;
    }

    /// <summary>
    /// Suspends ALL hotkeys while the user is recording a new combo in
    /// the settings window, so the new binding can be typed without
    /// triggering any existing hotkey. Re-registration happens on
    /// resume.
    /// </summary>
    public void SuspendAllHotKeys()
    {
        if (_hotKeysSuspended) return;
        _hotKeysSuspended = true;
        RegisterHotkeys();
    }

    /// <summary>
    /// Clears the suspension and re-registers all hotkeys. Safe to call
    /// even when nothing is suspended.
    /// </summary>
    public void ResumeAllHotKeys()
    {
        if (!_hotKeysSuspended) return;
        _hotKeysSuspended = false;
        RegisterHotkeys();
    }

    /// <summary>True while hotkeys are suspended (a recording is active).</summary>
    private bool HotKeysSuspended => _hotKeysSuspended;

    /// <summary>
    /// Registers all enabled hotkeys. When a hotkey recording is
    /// active (HotKeysSuspended), nothing is registered so the user
    /// can type the new combo without triggering existing bindings.
    /// </summary>
    private void RegisterHotkeys()
    {
        var hks = _trayIcon?.HotKeyService;
        if (hks == null) return;

        // Unregister all first (hotkeys may conflict with each other when changed)
        hks.UnregisterAll();
        _hotKeyRegistration.Clear();

        // Increase brightness hotkey
        var inc = _settings?.IncreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(inc) && _settings?.IncreaseBrightnessHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["IncBrightness"] = hks.TryRegister(inc, () =>
            {
                // 与滚轮同源：弹窗打开时直接调弹窗（不唤出 OSD），关闭时走 gamma+OSD
                // 全屏/禁用暂停期间：与滚轮一致，完全忽略（不调节、不弹 OSD）。
                if (_fullscreenPaused || _disableActive) return;
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(1);
                }
                else
                {
                    _gamma?.AdjustBrightness(_gamma?.StepSize ?? GammaController.DEFAULT_STEP);
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1.0f);
                    SaveSettings();
                }
            }, out _);
        }

        // Decrease brightness hotkey
        var dec = _settings?.DecreaseBrightnessHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(dec) && _settings?.DecreaseBrightnessHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["DecBrightness"] = hks.TryRegister(dec, () =>
            {
                if (_fullscreenPaused || _disableActive) return;
                if (_popup != null && _popup.IsShown)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustBrightness(-(_gamma?.StepSize ?? GammaController.DEFAULT_STEP));
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    if (_settings?.ShowOverlay != false) _overlay?.Show(_gamma?.CurrentBrightness ?? 1.0f);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1.0f);
                    SaveSettings();
                }
            }, out _);
        }

        // Power off display hotkey
        var powerOff = _settings?.PowerOffHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(powerOff) && _settings?.PowerOffHotKeyEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["PowerOff"] = hks.TryRegister(powerOff, () =>
            {
                // Broadcast SC_MONITORPOWER with lParam=2 (full power off).
                // Same code path as the popup's power button.
                NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                    new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(2));
            }, out _);
        }

        // Increase temperature hotkey (ignored while color temperature is disabled)
        var incTemp = _settings?.IncreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(incTemp) && _settings?.IncreaseTemperatureHotKeyEnabled != false && _settings?.ColorTemperatureEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["IncTemperature"] = hks.TryRegister(incTemp, () =>
            {
                if (_fullscreenPaused || _disableActive) return;
                if (_settings?.ColorTemperatureEnabled != true) return; // 色温关闭时忽略
                // 与滚轮同源：弹窗打开且处于色温模式时直接调弹窗；否则走 gamma
                if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
                {
                    _popup.AdjustByWheel(1);
                }
                else
                {
                    _gamma?.AdjustTemperature(_gamma?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP);
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            }, out _);
        }

        // Decrease temperature hotkey (ignored while color temperature is disabled)
        var decTemp = _settings?.DecreaseTemperatureHotKey ?? "";
        if (!string.IsNullOrWhiteSpace(decTemp) && _settings?.DecreaseTemperatureHotKeyEnabled != false && _settings?.ColorTemperatureEnabled != false && !HotKeysSuspended && _settings?.AllHotKeysEnabled != false)
        {
            _hotKeyRegistration["DecTemperature"] = hks.TryRegister(decTemp, () =>
            {
                if (_fullscreenPaused || _disableActive) return;
                if (_settings?.ColorTemperatureEnabled != true) return; // 色温关闭时忽略
                if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
                {
                    _popup.AdjustByWheel(-1);
                }
                else
                {
                    _gamma?.AdjustTemperature(-(_gamma?.TemperatureStepSize ?? GammaController.DEFAULT_TEMPERATURE_STEP));
                    OnManualAdjustment();
                    if (_gamma != null) _popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
                    _trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1.0f, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE, _settings?.ColorTemperatureEnabled ?? false);
                    SaveSettings();
                }
                TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? GammaController.DEFAULT_TEMPERATURE);
            }, out _);
        }
    }

    /// <summary>
    /// 用户手动调亮度/色温时调用：若时间调整调度正在运行，则停止调度并
    /// 持久化手动接管标志（重启软件也保持），直到关闭再开启总开关才恢复。
    /// </summary>
    private void OnManualAdjustment()
    {
        if (_settings == null || _solarScheduler == null) return;
        if (!_solarScheduler.IsRunning) return;
        _solarScheduler.Stop();
        _settings.SolarManuallyOverridden = true;
        SettingsManager.Save(_settings);
    }

    private void SaveSettings()
    {
        if (_settings != null && _gamma != null)
        {
            _settings.LastBrightness = _gamma.CurrentBrightness;
            // 色温关闭时保留 LastTemperature（此时 gamma 已是 6600K 中性），
            // 以便下次开启色温时恢复用户上次设置的色温值。
            if (_settings.ColorTemperatureEnabled)
                _settings.LastTemperature = _gamma.CurrentTemperature;
            SettingsManager.Save(_settings);
        }
    }

    public void Dispose()
    {
        SaveSettings();

        _solarScheduler?.Dispose();
        _solarScheduler = null;

        _systemMonitor?.Dispose();
        _systemMonitor = null;

        _popupAnchorTimer?.Stop();
        _popupAnchorTimer?.Dispose();
        _popupAnchorTimer = null;

        _mouseHook?.Dispose();
        _popup?.Dispose();
        _gamma?.Dispose();
        _overlay?.Dispose();
        _trayIcon?.Dispose();

        _smoothTimer?.Stop();
        _smoothTimer?.Dispose();
        _smoothTimer = null;

        _fullscreenAnimTimer?.Stop();
        _fullscreenAnimTimer?.Dispose();
        _fullscreenAnimTimer = null;

        _disableTimer?.Stop();
        _disableTimer?.Dispose();
        _disableTimer = null;

        _disableAnimTimer?.Stop();
        _disableAnimTimer?.Dispose();
        _disableAnimTimer = null;
    }
}
