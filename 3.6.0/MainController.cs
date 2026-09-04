using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace GammaBrightnessTool;

// ============================================================================
// MainController —— 组件总协调（反编译产物，维护注意）
// ----------------------------------------------------------------------------
// 本文件由反编译重建而来：早期版本的源代码注释（约 495 行）在还原过程中
// 丢失，现有注释以"本行代码做什么"为主，已无法复原原始逐段说明。此前一次
// 误改事故现场保留在 _devtools/_MainController_broken_1219.cs。
//
// 职责概览：
//   - 组装各组件（托盘/全局鼠标钩子/gamma/弹窗/OSD/日出日落调度/全屏与禁用
//     状态动画/系统事件监视），并把 UI 事件翻译成 gamma 与设置操作；
//   - 3.6.0：多显示器独立控制——GetDisplayIds/GetDisplayState/SetDisplayEnabled/
//     SetDisplayName/GetDisplaySystemName 等公开给设置页与弹窗/OSD 使用；
//   - 拖动合帧：滑轨的亮度/色温写入经 QueueAdjust/FlushAdjusts（24ms UI
//     Timer）合并后统一执行 gamma + tooltip + SaveSettings + 事件广播，
//     绝对值为语义、只保留每键最新值，勿在别处绕过该时序；
//   - 命名链路（3.6.0）：GetDisplaySystemName → GetDisplayNameFor →
//     自定义名(MonitorNames) → EDID 友好名(Monitor.GetEdidFriendlyName,
//     DisplayConfig) → EDID 型号段回退。
//
// 警告清零约定（2026-09-03 收尾）：本文件已 0 警告；CS8600/8602/8604 修复时
// 一律用可空标注/局部判空/明确「启动期必非空」的 !，不改变运行时行为。
// ============================================================================
public sealed class MainController : IDisposable
{
	private TrayIconManager? _trayIcon;

	private GlobalMouseHook? _mouseHook;

	private GammaController? _gamma;

	private BrightnessOverlay? _overlay;

	private BrightnessPopup? _popup;

	private AppSettings? _settings;

	private readonly Dictionary<string, bool> _hotKeyRegistration = new Dictionary<string, bool>();

	private bool _hotKeysSuspended;

	private Timer? _popupAnchorTimer;

	private static readonly TimeSpan PopupAnchorInterval = TimeSpan.FromMilliseconds(200.0);

	private SolarScheduler? _solarScheduler;

	private SystemEventMonitor? _systemMonitor;

	private bool _fullscreenPaused;

	private float _fullscreenBrightnessBefore = 1f;

	private float _fullscreenTemperatureBefore = 6600f;

	private Timer? _fullscreenAnimTimer;

	private DateTime _fullscreenAnimStartTime;

	private float _fullscreenAnimStartBright;

	private float _fullscreenAnimTargetBright;

	private float _fullscreenAnimStartTemp;

	private float _fullscreenAnimTargetTemp;

	private bool _fullscreenAnimExit;

	private bool _fullscreenAnimSmoothB;

	private bool _fullscreenAnimSmoothT;

	private bool _disableActive;

	private float _disableBrightnessBefore = 1f;

	private float _disableTemperatureBefore = 6600f;

	private Timer? _disableTimer;

	private Timer? _disableAnimTimer;

	private DateTime _disableAnimStartTime;

	private float _disableAnimStartBright;

	private float _disableAnimTargetBright;

	private float _disableAnimStartTemp;

	private float _disableAnimTargetTemp;

	private bool _disableAnimExit;

	private Action? _disableAnimDone;

	private bool _disableAnimSmoothB;

	private bool _disableAnimSmoothT;

	// 关闭独立控制(per-monitor→统一)的平滑过渡：各屏从自己的当前值缓动到
	// 统一目标（平均），完成后才翻转 gamma.PerMonitorEnabled 并整批写屏。
	private Timer? _unifyTimer;

	private DateTime _unifyStartTime;

	private bool _unifyActive;

	private bool _unifySmoothB;

	private bool _unifySmoothT;

	private float _unifyTargetB;

	private float _unifyTargetT;

	private readonly Dictionary<string, (float Brightness, float Temperature)> _unifyStart = new();

	// 进入独立控制瞬间的"统一基准"亮度/色温（当时屏幕实际显示的统一值）。
	// 关闭独立控制时所有屏平滑回到这个基准，而不是回到各屏当前的平均值。
	// -1 = 尚未捕获（回退用平均值）。
	private float _perMonitorEntryBrightness = -1f;

	private float _perMonitorEntryTemperature = -1f;

	private Timer? _smoothTimer;

	private DateTime _smoothStartTime;

	private float _smoothStartBright;

	private float _smoothTargetBright;

	private float _smoothStartTemp;

	private float _smoothTargetTemp;

	private bool _smoothBrightActive;

	private bool _smoothTempActive;

	private const int SmoothDurationMs = 1200;

	private const int SmoothTickMs = 30;

	private Action? _smoothDone;

	private bool HotKeysSuspended => _hotKeysSuspended;

	public event EventHandler<float>? TemperatureChanged;

	public event EventHandler<float>? BrightnessChanged;

	public void Initialize(bool silent, bool showSettingsOnStart = false)
	{
		IntegrityChecker.RunCheck();
		_settings = SettingsManager.Load();
		_settings.TransitionMinutes = Math.Clamp(_settings.TransitionMinutes, 0, 60);
		Localization.Setting = _settings.Language;
		Localization.Current = Localization.Resolve(_settings.Language).Effective;
		ThemeManager.Apply(_settings.Theme);
		ThemeManager.ApplyPopupTheme(_settings.PopupTheme);
		_trayIcon = new TrayIconManager();
		_trayIcon.Initialize();
		_trayIcon.OnUninstallRequested += OnUninstallRequested;
		_trayIcon.OnSettingsRequested += OnSettingsRequested;
		_trayIcon.OnLeftClickRequested += OnLeftClickRequested;
		_trayIcon.OnContextMenuOpening += OnContextMenuOpening;
		_trayIcon.OnTrayDpiChanged += OnTrayDpiChanged;
		_trayIcon.OnIconRectChanged += OnIconRectChanged;
		_trayIcon.DisableSolarEnabled = () => _settings?.SolarAdjustEnabled ?? false;
		_trayIcon.DisableGetRemaining = () => GetDisableRemaining();
		_trayIcon.DisableGetUntil = () => GetDisableUntil();
		_trayIcon.DisableSolarActive = () => IsSolarDisableActive();
		_trayIcon.DisableIsDaytime = () => IsDaytimeNow();
		_trayIcon.OnDisableRequested += OnDisableRequested;
		_gamma = new GammaController();
		_gamma.Initialize();
		_gamma.StepSize = _settings.StepSize;
		_gamma.TemperatureStepSize = _settings.TemperatureStepSize;
		_gamma.MinTemperature = _settings.MinTemperature;
		_gamma.MaxTemperature = _settings.MaxTemperature;
		// 3.6.0 顺序修正（Bug4）：独立控制开启时先恢复逐屏记忆（含停用标记），
		// 再决定是否走统一平滑。若沿用"先 ApplyStartupGamma(统一平滑) 再恢复
		// 逐屏状态"，平滑定时器尾段会把已恢复的各屏值覆盖成统一目标，导致
		// 开机后逐屏亮度/色温记忆失效（仅平滑开启时发生）。
		if (_settings.PerMonitorEnabled)
		{
			// 播种统一种子：新屏/无记忆屏以最后全局值为起点（沿用旧启动语义）。
			// 该种子同时是"进入独立控制的统一基准"：之后若关闭独立控制，所有屏
			// 平滑回到这个基准（等价于"开启独立控制前的亮度/色温"）。
			float entryB = _settings.LastBrightness;
			float entryT = _settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f;
			_perMonitorEntryBrightness = entryB;
			_perMonitorEntryTemperature = entryT;
			_gamma.SetBrightness(entryB);
			_gamma.SetTemperature(entryT);
			_gamma.PerMonitorEnabled = true;
			_gamma.ReconcileDisplayStates();
			RestoreSavedDisplayStates();
		}
		else
		{
			_gamma.PerMonitorEnabled = false;
			ApplyStartupGamma();
		}
		_overlay = new BrightnessOverlay();
		_overlay.OpacityPercent = _settings.OverlayOpacityPercent;   // OSD 用户透明度（构造中 _settings 已就绪）
		_overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;
		_overlay.OnRowBrightnessChanged += OnOverlayRowBrightnessChanged;
		_mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
		{
			IsInvertedScroll = () => _settings?.InvertScroll ?? false,
			IsOverlayEnabled = () => _settings?.ShowOverlay ?? true,
			IsWheelEnabled = () => _settings?.WheelEnabled ?? true,
			IsColorTemperatureEnabled = () => _settings?.ColorTemperatureEnabled ?? false,
			IsPaused = () => _fullscreenPaused || _disableActive,
			ShowOverlay = ShowOverlayForDisplays,
			OnUserAdjustment = delegate
			{
				OnManualAdjustment();
				this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
				this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
			}
		};
		_mouseHook.Install();
		_popup = new BrightnessPopup();
		_popup.OpacityPercent = _settings.PopupOpacityPercent;   // 左键弹窗用户透明度（构造中 _settings 已就绪）
		_popup.IsDisableActive = () => _disableActive || _fullscreenPaused;
		_popup.StepSize = _settings.StepSize;
		_popup.TemperatureStepSize = _settings.TemperatureStepSize;
		_popup.MinTemperature = _settings.MinTemperature;
		_popup.MaxTemperature = _settings.MaxTemperature;
		_popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
		_popup.PerMonitorEnabled = _settings.PerMonitorEnabled;
		_popup.OnDisplayRowChanged += OnPopupDisplayRowChanged;
		_popup.OnPerMonitorWheel += OnPopupPerMonitorWheel;
		_popup.OnBrightnessChanged += OnPopupBrightnessChanged;
		_popup.OnTemperatureChanged += OnPopupTemperatureChanged;
		_popup.OnShownChanged += OnPopupShownChanged;
		_mouseHook.SetPopup(_popup);
		_trayIcon.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
		RegisterHotkeys();
		if (_settings != null && (_settings.GammaSelfHealEnabled || _settings.PauseInFullscreenEnabled))
		{
			EnsureSystemMonitor();
			if (_settings.PauseInFullscreenEnabled)
			{
				_systemMonitor?.RefreshFullscreenState();
			}
		}
		_solarScheduler = new SolarScheduler(_gamma, _settings!);
		_solarScheduler.BrightnessChanged += delegate(object? _, float v)
		{
			_popup?.SyncFromGamma(v, _gamma?.CurrentTemperature ?? 6600f);
			this.BrightnessChanged?.Invoke(this, v);
		};
		_solarScheduler.TemperatureChanged += delegate(object? _, float v)
		{
			_popup?.SyncFromGamma(_gamma?.CurrentBrightness ?? 1f, v);
			this.TemperatureChanged?.Invoke(this, v);
		};
		if (_settings!.SolarAdjustEnabled && !_settings!.SolarManuallyOverridden)
		{
			_solarScheduler.Start();
		}
		RestoreDisableState();
		if (showSettingsOnStart)
		{
			Application.Idle += OnIdleShowSettings;
		}
	}

	public bool GetBrightnessSmooth()
	{
		return _settings?.BrightnessSmooth ?? true;
	}

	public bool GetTemperatureSmooth()
	{
		return _settings?.TemperatureSmooth ?? true;
	}

	public void SetBrightnessSmooth(bool enabled)
	{
		if (_settings != null)
		{
			_settings.BrightnessSmooth = enabled;
			SettingsManager.Save(_settings);
		}
	}

	public void SetTemperatureSmooth(bool enabled)
	{
		if (_settings != null)
		{
			_settings.TemperatureSmooth = enabled;
			SettingsManager.Save(_settings);
		}
	}

	public bool GetGammaSelfHealEnabled()
	{
		return _settings?.GammaSelfHealEnabled ?? true;
	}

	public bool GetPauseInFullscreenEnabled()
	{
		return _settings?.PauseInFullscreenEnabled ?? true;
	}

	public void SetGammaSelfHealEnabled(bool enabled)
	{
		if (_settings != null)
		{
			_settings.GammaSelfHealEnabled = enabled;
			SettingsManager.Save(_settings);
		}
	}

	public void SetPauseInFullscreenEnabled(bool enabled)
	{
		if (_settings != null)
		{
			_settings.PauseInFullscreenEnabled = enabled;
			SettingsManager.Save(_settings);
			if (enabled)
			{
				// 运行时开启：若启动时因"默认关"未创建监听器，此刻补建并立即
				// 检测当前前台是否已处于全屏，保证无需重启即生效（Bug A）。
				EnsureSystemMonitor();
				_systemMonitor?.RefreshFullscreenState();
			}
			else if (_fullscreenPaused)
			{
				StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
			}
		}
	}

	private void ApplyStartupGamma()
	{
		if (_settings != null && _gamma != null)
		{
			float lastBrightness = _settings.LastBrightness;
			float targetTemp = (_settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f);
			bool flag = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
			StartSmoothTransition(lastBrightness, targetTemp, _settings.BrightnessSmooth && !flag, _settings.TemperatureSmooth && !flag);
		}
	}

	private void RestoreSavedDisplayStates()
	{
		if (_settings == null || _gamma == null || !_gamma.PerMonitorEnabled)
		{
			return;
		}
		Dictionary<string, MonitorState> dictionary = _settings.MonitorStates ?? new Dictionary<string, MonitorState>();
		foreach (string displayId in _gamma.GetDisplayIds())
		{
			if (dictionary.TryGetValue(displayId, out var value))
			{
				_gamma.InitializeDisplayState(displayId, Math.Clamp(value.Brightness, 0f, 1f), Math.Clamp(value.Temperature, 3300f, 10000f));
				// 恢复"停用"标记：否则 gamma 侧新建的 DisplayState 默认 Enabled=true，
				// 重启/热插拔后 UI 显示停用的屏会被重新启用并写 gamma（Bug2）。
				_gamma.SetDisplayEnabled(displayId, value.Enabled);
			}
		}
		foreach (string displayId2 in _gamma.GetDisplayIds())
		{
			DisplayState displayState = _gamma.GetDisplayState(displayId2);
			_gamma.SetBrightness(displayId2, displayState.Brightness);
			if (_settings.ColorTemperatureEnabled)
			{
				_gamma.SetTemperature(displayId2, displayState.Temperature);
			}
		}
	}

	private static double EaseOutCubic(double t)
	{
		return 1.0 - Math.Pow(1.0 - t, 3.0);
	}

	private void StartSmoothTransition(float targetBright, float targetTemp, bool smoothBright, bool smoothTemp, Action? done = null)
	{
		if (_gamma == null)
		{
			return;
		}
		if (!smoothBright && !smoothTemp)
		{
			_gamma.SetBrightness(targetBright);
			_gamma.SetTemperature(targetTemp);
			done?.Invoke();
			return;
		}
		if (!smoothBright)
		{
			_gamma.SetBrightness(targetBright);
		}
		if (!smoothTemp)
		{
			_gamma.SetTemperature(targetTemp);
		}
		_smoothStartBright = _gamma.ReadCurrentBrightness();
		_smoothStartTemp = _gamma.ReadCurrentTemperature();
		_smoothTargetBright = targetBright;
		_smoothTargetTemp = targetTemp;
		_smoothBrightActive = smoothBright;
		_smoothTempActive = smoothTemp;
		_smoothStartTime = DateTime.Now;
		_smoothDone = done;
		if (_smoothTimer == null)
		{
			_smoothTimer = new Timer
			{
				Interval = 30
			};
			_smoothTimer.Tick += OnSmoothTick;
		}
		_smoothTimer.Start();
	}

	private void OnSmoothTick(object? sender, EventArgs e)
	{
		double num = (DateTime.Now - _smoothStartTime).TotalMilliseconds / 1200.0;
		if (num >= 1.0)
		{
			num = 1.0;
		}
		double num2 = EaseOutCubic(num);
		if (_smoothBrightActive)
		{
			_gamma?.SetBrightness((float)((double)_smoothStartBright + (double)(_smoothTargetBright - _smoothStartBright) * num2));
		}
		if (_smoothTempActive)
		{
			_gamma?.SetTemperature((float)((double)_smoothStartTemp + (double)(_smoothTargetTemp - _smoothStartTemp) * num2));
		}
		if (num >= 1.0)
		{
			_smoothTimer?.Stop();
			_smoothTimer?.Dispose();
			_smoothTimer = null;
			if (_smoothBrightActive)
			{
				_gamma?.SetBrightness(_smoothTargetBright);
			}
			if (_smoothTempActive)
			{
				_gamma?.SetTemperature(_smoothTargetTemp);
			}
			if (_smoothBrightActive)
			{
				this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
			}
			if (_smoothTempActive)
			{
				this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
			}
			SaveSettings();
			_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
			Action? smoothDone = _smoothDone;
			_smoothDone = null;
			smoothDone?.Invoke();
		}
	}

	private void OnIdleShowSettings(object? sender, EventArgs e)
	{
		Application.Idle -= OnIdleShowSettings;
		OnSettingsRequested(this, EventArgs.Empty);
	}

	private void OnLeftClickRequested(object? sender, EventArgs e)
	{
		_overlay?.Hide();
		#if DEBUG
		PopupDebug.Log("OnLeftClickRequested: BEGIN");
		#endif
		Rectangle? rectangle = _trayIcon?.GetIconRectLive();
		if (rectangle.HasValue)
		{
			#if DEBUG
			PopupDebug.Log($"OnLeftClickRequested: iconRect={rectangle.Value}");
			#endif
			AppSettings? settings = _settings;
			if (settings != null && settings.PerMonitorEnabled)
			{
				_popup?.SetDisplays(BuildDisplayRows());
			}
			_popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, rectangle.Value);
		}
		else
		{
			Point position = Cursor.Position;
			Screen screen = Screen.FromPoint(position);
			Rectangle workingArea = screen.WorkingArea;
			Rectangle rectangle2 = new Rectangle(workingArea.Left + (workingArea.Width - 120) / 2, workingArea.Bottom - 40, 120, 40);
			#if DEBUG
			PopupDebug.Log($"OnLeftClickRequested: FALLBACK cursor={position} screen={screen.Bounds} wa={workingArea} fallbackRect={rectangle2}");
			#endif
			_popup?.ShowAbove(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, rectangle2);
		}
	}

	private void OnContextMenuOpening(object? sender, EventArgs e)
	{
		_popup?.Dismiss();
		_overlay?.Hide();
	}

	private void OnTrayDpiChanged(object? sender, EventArgs e)
	{
		#if DEBUG
		PopupDebug.Log($"OnTrayDpiChanged: IsShown={_popup?.IsShown}");
		#endif
		if (_popup != null && _popup.IsShown)
		{
			Rectangle? rectangle = _trayIcon?.GetIconRectLive();
			if (rectangle.HasValue)
			{
				_popup.ReanchorTo(rectangle.Value);
			}
		}
	}

	private void OnIconRectChanged(object? sender, EventArgs e)
	{
		if (_popup != null && _popup.IsShown)
		{
			Rectangle? rectangle = _trayIcon?.GetIconRectLive();
			if (rectangle.HasValue)
			{
				_popup.ReanchorTo(rectangle.Value);
			}
		}
	}

	private void OnPopupShownChanged(object? sender, EventArgs e)
	{
		#if DEBUG
		PopupDebug.Log($"OnPopupShownChanged: IsShown={_popup?.IsShown}");
		#endif
		if (_popup != null && _popup.IsShown)
		{
			if (_popupAnchorTimer == null)
			{
				_popupAnchorTimer = new Timer
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
		if (_popup != null && _popup.IsShown)
		{
			Rectangle? rectangle = _trayIcon?.GetIconRectLive();
			if (rectangle.HasValue)
			{
				_popup.ReanchorTo(rectangle.Value);
			}
		}
	}

	private void UpdateTrayTooltip(float? brightness = null, float? temperatureK = null)
	{
		float brightness2 = brightness ?? _gamma?.CurrentBrightness ?? 1f;
		float temperatureK2 = temperatureK ?? _gamma?.CurrentTemperature ?? 6600f;
		bool showTemperature = _settings?.ColorTemperatureEnabled ?? false;
		_trayIcon?.UpdateTooltip(brightness2, temperatureK2, showTemperature);
	}

	// ------------------------------------------------------------------
	// 滑轨拖动合帧（drag coalescing）。快速拖动时 WM_MOUSEMOVE 事件流可达
	// 数百次/秒，而每次事件原实现都同步执行：gamma 写屏（每启用屏一次
	// SetDeviceGammaRamp）+ 托盘 tooltip 的 Shell_NotifyIcon IPC +
	// SaveSettings() 全量 JSON 落盘 → UI 线程被逐次拖垮，滑轨表现为卡顿。
	// 方案：把 5 个热点处理器（弹窗/OSD 主滑轨、弹窗/OSD 多屏行）改为只登记
	// "同类键最新一次" 请求，合帧到 24ms 的 UI Timer 统一执行一次。因为亮度/
	// 色温都是绝对值（非增量），丢中间值不影响最终状态；停止拖动后最迟 24ms
	// 内补最后一帧。24ms 合帧人眼不可感知，但每类键的写屏/IPC/落盘频率从
	// 数百Hz 降到 ~40Hz，拖动即恢复流畅。
	// ------------------------------------------------------------------
	private const int AdjustFlushMs = 24;
	private System.Windows.Forms.Timer? _adjustFlushTimer;
	private readonly Dictionary<string, Action> _pendingAdjusts = new();

	/// <summary>登记一次待应用的调节：同类键只保留最新值（绝对值语义）。</summary>
	private void QueueAdjust(string key, Action apply)
	{
		_pendingAdjusts[key] = apply;
		if (_adjustFlushTimer == null)
		{
			_adjustFlushTimer = new System.Windows.Forms.Timer { Interval = AdjustFlushMs };
			_adjustFlushTimer.Tick += (_, _) => FlushAdjusts();
		}
		if (!_adjustFlushTimer.Enabled)
		{
			_adjustFlushTimer.Start();
		}
	}

	/// <summary>合帧定时器到点：一次性执行各键的最新请求（每键一次）。</summary>
	private void FlushAdjusts()
	{
		_adjustFlushTimer?.Stop();
		if (_pendingAdjusts.Count == 0) return;
		Action[] batch = _pendingAdjusts.Values.ToArray();
		_pendingAdjusts.Clear();
		foreach (Action apply in batch)
		{
			try
			{
				apply();
			}
			catch
			{
				// 单键应用失败不中断同批其余键
			}
		}
	}

	private void OnPopupBrightnessChanged(object? sender, float brightness)
	{
		if (_disableActive) return;
		OnManualAdjustment();
		float b = brightness;
		QueueAdjust("popupB", () =>
		{
			_gamma?.SetBrightness(b);
			_trayIcon?.UpdateTooltip(b, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
			SaveSettings();
		});
	}

	private void OnPopupTemperatureChanged(object? sender, float kelvin)
	{
		if (_disableActive) return;
		OnManualAdjustment();
		float k = kelvin;
		QueueAdjust("popupT", () =>
		{
			_gamma?.SetTemperature(k);
			SaveSettings();
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
		});
	}

	private void OnPopupDisplayRowChanged(string edidId, float brightness, float kelvin)
	{
		if (_disableActive || _gamma == null || !_gamma.PerMonitorEnabled) return;
		OnManualAdjustment();
		string id = edidId;
		float b = brightness;
		float k = kelvin;
		QueueAdjust("row|" + edidId, () =>
		{
			DisplayState st = _gamma.GetDisplayState(id);
			if (Math.Abs(st.Brightness - b) > 0.001f)
			{
				_gamma.SetBrightness(id, b);
			}
			if (_settings != null && _settings.ColorTemperatureEnabled && Math.Abs(st.Temperature - k) > 0.5f)
			{
				_gamma.SetTemperature(id, k);
			}
			_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? k);
			SaveSettings();
		});
	}

	private void OnPopupPerMonitorWheel(int sign)
	{
		if (_disableActive || _gamma == null || !_gamma.PerMonitorEnabled) return;
		OnManualAdjustment();
		// 弹窗打开时托盘滚轮：按当前弹窗模式对所有启用屏做等步长偏移（各屏基于自己值）。
		bool temp = _popup != null && _popup.IsTemperatureMode;
		if (temp)
		{
			float dk = (_popup!.TemperatureStepSize > 0 ? _popup.TemperatureStepSize : 100f);
			_gamma.AdjustTemperature(sign * dk);
		}
		else
		{
			float ds = (_popup!.StepSize > 0 ? _popup.StepSize : 0.05f);
			_gamma.AdjustBrightness(sign * ds);
		}
		_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
		this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
		this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
		SaveSettings();
		// 原位回灌各行数值（不整表重建）：滚轮连续滚动时整表销毁/重建行控件
		// 会让弹窗每次调节都闪烁跳动。
		RefreshPopupRowValues();
	}

	/// <summary>
	/// 把 gamma 各屏最新值原位刷到弹窗行（行控件不销毁，仅更新值并重绘）。
	/// 行模式（亮度/色温）保持不变，无需走 SetDisplays 重建。
	/// </summary>
	private void RefreshPopupRowValues()
	{
		if (_popup == null || _gamma == null || !_popup.PerMonitorEnabled) return;
		foreach (string displayId in _gamma.GetDisplayIds())
		{
			DisplayState st = _gamma.GetDisplayState(displayId);
			bool enabled = true;
			if (_settings != null && _settings.MonitorStates != null &&
				_settings.MonitorStates.TryGetValue(displayId, out MonitorState? ms) && ms != null)
			{
				enabled = ms.Enabled;
			}
			_popup.SyncDisplayRow(displayId, st.Brightness, st.Temperature, enabled);
		}
	}

	private string GetDisplayNameFor(string edidId)
	{		string? displayName = GetDisplayName(edidId);
		if (displayName != null)
		{
			return displayName;
		}
		// 友好名（EDID 0xFC Monitor-Name，如 "G5c II"）优先于内部型号段（SAC2466）
		string? friendly = Monitor.GetEdidFriendlyName(edidId);
		if (!string.IsNullOrWhiteSpace(friendly))
		{
			return friendly;
		}
		string[] array = edidId.Split('\\');
		if (array.Length >= 2 && !string.IsNullOrWhiteSpace(array[1]))
		{
			return array[1];
		}
		return edidId;
	}

	private List<BrightnessPopup.DisplayRowData> BuildDisplayRows()
	{
		List<BrightnessPopup.DisplayRowData> list = new List<BrightnessPopup.DisplayRowData>();
		if (_gamma == null)
		{
			return list;
		}
		foreach (string displayId in _gamma.GetDisplayIds())
		{
			DisplayState displayState = _gamma.GetDisplayState(displayId);
			bool enabled = true;
			AppSettings? settings = _settings;
			if (settings != null && settings.MonitorStates != null &&
				settings.MonitorStates.TryGetValue(displayId, out MonitorState? value) && value != null)
			{
				enabled = value.Enabled;
			}
			string displayNameFor = GetDisplayNameFor(displayId);
			list.Add(new BrightnessPopup.DisplayRowData(displayId, displayNameFor, displayState.Brightness, displayState.Temperature, enabled));
		}
		return list;
	}

	private void ShowOverlayForDisplays()
	{
		if (_overlay == null || _gamma == null)
		{
			return;
		}
		if (_gamma.PerMonitorEnabled)
		{
			List<BrightnessOverlay.DisplayRow> list = new List<BrightnessOverlay.DisplayRow>();
			foreach (string displayId in _gamma.GetDisplayIds())
			{
				DisplayState displayState = _gamma.GetDisplayState(displayId);
				bool enabled = true;
				AppSettings? settings = _settings;
				if (settings != null && settings.MonitorStates != null &&
					settings.MonitorStates.TryGetValue(displayId, out MonitorState? value) && value != null)
				{
					enabled = value.Enabled;
				}
				list.Add(new BrightnessOverlay.DisplayRow(displayId, displayState.Brightness, enabled));
			}
			_overlay.ShowDisplays(list);
		}
		else
		{
			_overlay.Show(_gamma.CurrentBrightness);
		}
	}

	private void OnOverlayBrightnessChanged(object? sender, float brightness)
	{
		if (!_disableActive)
		{
			OnManualAdjustment();
			float b = brightness;
			QueueAdjust("osdB", () =>
			{
				_gamma?.SetBrightness(b);
				_trayIcon?.UpdateTooltip(b, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
				this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
				SaveSettings();
			});
		}
	}

	/// <summary>
	/// 多行 OSD（独立模式）中某一屏的滑轨被拖动时触发：只调那一屏（基于各自当前值）。
	/// </summary>
	private void OnOverlayRowBrightnessChanged(string edidId, float brightness)
	{
		if (_disableActive || _gamma == null || !_gamma.PerMonitorEnabled) return;
		OnManualAdjustment();
		string id = edidId;
		float b = brightness;
		QueueAdjust("osdRow|" + edidId, () =>
		{
			_gamma.SetBrightness(id, b);
			_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
			SaveSettings();
		});
	}

	private void OnSettingsRequested(object? sender, EventArgs e)
	{
		SettingsForm.ShowOrActivate();
	}

	private void OnUninstallRequested(object? sender, EventArgs e)
	{
		DialogResult dialogResult = MessageBox.Show(Localization.Get("UninstallPrompt"), Localization.Get("UninstallTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation);
		if (dialogResult == DialogResult.Yes)
		{
			PerformUninstall();
		}
	}

	private void PerformUninstall()
	{
		StartupManager.SetStartup(enable: false);
		Program.ReleaseMutex();
		string executablePath = Application.ExecutablePath;
		string fileName = Path.GetFileName(executablePath);
		string path = Path.GetDirectoryName(executablePath) ?? "";
		string text = Path.Combine(path, "unins000.exe");
		if (File.Exists(text))
		{
			OpLog.Log($"[uninstall] installed version detected ({text}) -> run silent uninstaller (AppData preserved)");
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = text,
				Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Process.Start(startInfo);
			Application.Exit();
			return;
		}
		OpLog.Log($"[uninstall] green version ({executablePath}) -> temp batch cleanup (exe-dir only, AppData preserved)");
		string text2 = Path.Combine(Path.GetTempPath(), "uninstall_" + fileName + ".bat");
		// 绿色版自卸载：只清自身残留，绝不触碰共享数据与系统级托盘缓存——
		//  * 不删 %APPDATA%\GammaBrightnessTool（settings.json 等与安装版共享，
		//    安装版卸载器明确保留；绿色版删它会把安装版配置一起清掉）；
		//  * 不 reg delete TrayNotify 的 IconStreams/PastIconsStream（全系统共享的
		//    旧式托盘历史，清理会重置所有软件的托盘图标自定义，策略同 Setup.iss）。
		// 仅删除：exe 旁的旧式绿色版配置（Load 时已迁移进 AppData）、桌面快捷方式与自身。
		string contents = $"\r\n@echo off\r\nchcp 65001 >nul\r\ntimeout /t 2 /nobreak >nul\r\ndel /f /q \"{Path.Combine(path, "settings.json")}\" 2>nul\r\ndel /f /q \"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Gamma Brightness Tool.lnk")}\" 2>nul\r\ndel /f /q \"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GammaBrightnessTool.lnk")}\" 2>nul\r\ndel /f /q \"{executablePath}\" 2>nul\r\ndel /f /q \"{text2}\" 2>nul\r\n";
		File.WriteAllText(text2, contents);
		ProcessStartInfo startInfo2 = new ProcessStartInfo
		{
			FileName = "cmd.exe",
			Arguments = "/c \"" + text2 + "\"",
			UseShellExecute = false,
			CreateNoWindow = true
		};
		Process.Start(startInfo2);
		Application.Exit();
	}

	private void OnLanguageChanged(object? sender, Language lang)
	{
		if (_settings != null)
		{
			(Language Effective, bool Supported) tuple = Localization.Resolve(lang);
			Language item = tuple.Effective;
			bool item2 = tuple.Supported;
			Localization.Setting = lang;
			Localization.Current = item;
			_settings.Language = lang;
			SettingsManager.Save(_settings);
			_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
			if (lang == Language.System && !item2)
			{
				MessageBox.Show(Localization.Get("SystemLanguageUnsupported"), Localization.Get("Error"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
		}
	}

	public void ChangeLanguage(Language lang)
	{
		OnLanguageChanged(null, lang);
	}

	public ThemeMode GetTheme()
	{
		return _settings?.Theme ?? ThemeMode.System;
	}

	public void SetTheme(ThemeMode theme)
	{
		// 主题模式只允许枚举内的 System/Dark/Light。曾出现调用方把下拉
		// SelectedIndex=-1（重选同项的中间事件）直接传入 → ThemeManager 模式
		// 被置成非法值并退化为"跟随系统"，与用户选定主题不一致时整窗闪变。
		// 非法输入直接忽略：不落盘、不应用。
		if ((int)theme < (int)ThemeMode.System || (int)theme > (int)ThemeMode.Light) return;
		if (_settings != null)
		{
			_settings.Theme = theme;
			SettingsManager.Save(_settings);
			ThemeManager.Apply(theme);
		}
	}

	public bool ExportSettings(string path)
	{
		try
		{
			string contents = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(path, contents);
			return true;
		}
		catch (Exception value)
		{
			Debug.WriteLine($"Failed to export settings: {value}");
			return false;
		}
	}

	public bool ImportSettings(string path)
	{
		try
		{
			string json = File.ReadAllText(path);
			AppSettings? appSettings = JsonSerializer.Deserialize<AppSettings>(json);
			if (appSettings == null)
			{
				return false;
			}
			appSettings.LastBrightness = Math.Clamp(appSettings.LastBrightness, 0f, 1f);
			appSettings.LastTemperature = Math.Clamp(appSettings.LastTemperature, 3300f, 10000f);
			appSettings.StepSize = Math.Clamp(appSettings.StepSize, 0.01f, 0.5f);
			appSettings.TemperatureStepSize = Math.Clamp(appSettings.TemperatureStepSize, 50f, 3000f);
			appSettings.MinTemperature = Math.Clamp(appSettings.MinTemperature, 3300f, 10000f);
			appSettings.MaxTemperature = Math.Clamp(appSettings.MaxTemperature, 3300f, 10000f);
			if (appSettings.MinTemperature >= appSettings.MaxTemperature)
			{
				appSettings.MinTemperature = 3300f;
				appSettings.MaxTemperature = 10000f;
			}
			if (_disableActive)
			{
				_disableActive = false;
				_gamma?.SetPaused(paused: false);
			}
			_settings = appSettings;
			SettingsManager.Save(_settings);
			ApplyImportedSettings();
			RestoreDisableState();
			return true;
		}
		catch (Exception value)
		{
			Debug.WriteLine($"Failed to import settings: {value}");
			return false;
		}
	}

	private void ApplyImportedSettings()
	{
		if (_settings == null)
		{
			return;
		}
		Localization.Setting = _settings.Language;
		Localization.Current = Localization.Resolve(_settings.Language).Effective;
		ThemeManager.Apply(_settings.Theme);
		ThemeManager.ApplyPopupTheme(_settings.PopupTheme);
		if (_gamma != null)
		{
			_gamma.StepSize = _settings.StepSize;
		}
		if (_popup != null)
		{
			_popup.StepSize = _settings.StepSize;
		}
		if (_gamma != null)
		{
			_gamma.TemperatureStepSize = _settings.TemperatureStepSize;
		}
		if (_popup != null)
		{
			_popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
		}
		if (_gamma != null)
		{
			_gamma.MinTemperature = _settings.MinTemperature;
			_gamma.MaxTemperature = _settings.MaxTemperature;
		}
		if (_popup != null)
		{
			_popup.MinTemperature = _settings.MinTemperature;
			_popup.MaxTemperature = _settings.MaxTemperature;
		}
		if (!_settings.PauseInFullscreenEnabled && _fullscreenPaused && !_disableActive)
		{
			_fullscreenPaused = false;
			if (!_disableActive)
			{
				_gamma?.SetPaused(paused: false);
			}
			StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
		}
		RegisterHotkeys();
		_gamma?.SetBrightness(_settings.LastBrightness);
		if (_settings.ColorTemperatureEnabled)
		{
			_gamma?.SetTemperature(_settings.LastTemperature);
		}
		else
		{
			_gamma?.SetTemperature(6600f);
		}
		_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
		ApplySolarScheduler();
	}

	public ThemeMode GetPopupTheme()
	{
		return _settings?.PopupTheme ?? ThemeMode.System;
	}

	public void SetPopupTheme(ThemeMode theme)
	{
		// 同 SetTheme：非法枚举（如 SelectedIndex=-1 传入）忽略，避免弹窗主题
		// 退化为"跟随系统"并触发闪变。
		if ((int)theme < (int)ThemeMode.System || (int)theme > (int)ThemeMode.Light) return;
		if (_settings != null)
		{
			_settings.PopupTheme = theme;
			SettingsManager.Save(_settings);
			ThemeManager.ApplyPopupTheme(theme);
		}
	}

	public int GetPopupOpacityPercent() => _settings?.PopupOpacityPercent ?? 90;
	public int GetOverlayOpacityPercent() => _settings?.OverlayOpacityPercent ?? 70;

	/// <summary>设置左键弹窗不透明度（%）并即时生效（若弹窗已显示）。</summary>
	public void SetPopupOpacityPercent(int percent)
	{
		int v = Math.Clamp(percent, 40, 100);
		if (_settings == null || _settings.PopupOpacityPercent == v) return;
		_settings.PopupOpacityPercent = v;
		SettingsManager.Save(_settings);
		if (_popup != null) _popup.OpacityPercent = v;
	}

	/// <summary>设置 OSD 浮窗不透明度（%）并即时生效。</summary>
	public void SetOverlayOpacityPercent(int percent)
	{
		int v = Math.Clamp(percent, 40, 100);
		if (_settings == null || _settings.OverlayOpacityPercent == v) return;
		_settings.OverlayOpacityPercent = v;
		SettingsManager.Save(_settings);
		if (_overlay != null) _overlay.OpacityPercent = v;
	}

	public float GetStepSize()
	{
		return _settings?.StepSize ?? 0.05f;
	}

	public void SetStepSize(float step)
	{
		if (_settings != null)
		{
			_settings.StepSize = Math.Clamp(step, 0.01f, 1f);
			if (_gamma != null)
			{
				_gamma.StepSize = _settings.StepSize;
			}
			if (_popup != null)
			{
				_popup.StepSize = _settings.StepSize;
			}
			SettingsManager.Save(_settings);
		}
	}

	public void SetBrightnessLevel(float brightness)
	{
		if (_gamma != null)
		{
			OnManualAdjustment();
			bool flag = _settings != null && _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
			AppSettings? settings = _settings;
			if (settings != null && settings.BrightnessSmooth && !flag)
			{
				StartSmoothTransition(brightness, _gamma.CurrentTemperature, smoothBright: true, smoothTemp: false);
			}
			else
			{
				_gamma.SetBrightness(brightness);
				SaveSettings();
			}
			// 挡位切换不弹 OSD：平滑开启时 OSD 此刻读到的是动画起点旧值
			// （如切 75% 却显示 100%），随后又自行消失，观感突兀
			// （用户 2026-09-04 反馈）。挡位选择本身有下拉/设置窗反馈，
			// 无需 OSD 确认；滚轮/热键等实时调节路径仍照常显示 OSD。
			_trayIcon?.UpdateTooltip(brightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? brightness);
		}
	}

	public bool GetInvertScroll()
	{
		return _settings?.InvertScroll ?? false;
	}

	public void SetInvertScroll(bool invert)
	{
		if (_settings != null)
		{
			_settings.InvertScroll = invert;
			SettingsManager.Save(_settings);
		}
	}

	public bool GetWheelEnabled()
	{
		return _settings?.WheelEnabled ?? true;
	}

	public void SetWheelEnabled(bool enabled)
	{
		if (_settings != null)
		{
			_settings.WheelEnabled = enabled;
			SettingsManager.Save(_settings);
		}
	}

	public bool GetPerMonitorEnabled()
	{
		return _settings?.PerMonitorEnabled ?? false;
	}

	public void SetPerMonitorEnabled(bool enabled)
	{
		if (_settings != null && _gamma != null && _settings.PerMonitorEnabled != enabled)
		{
			if (enabled)
			{
				// 动画中途被反向往回开：先取消未完成的统一过渡。
				if (_unifyActive) CancelUnifyAnimation();
				// 记录"进入独立控制的统一基准"（此刻屏幕实际显示的统一值），
				// 关闭独立控制时各屏平滑回到它。随后把所有屏状态重置为该真实值
				// （不用旧的 Reconcile 保留残留状态）：否则上一次独立/关闭切换
				// 留下的过时 state 会让滑轨显示的值与屏幕实况脱节
				// （如滑轨 0% 实际却是另一亮度）。
				float entryB = _gamma.CurrentBrightness;
				float entryT = _gamma.CurrentTemperature;
				_perMonitorEntryBrightness = entryB;
				_perMonitorEntryTemperature = entryT;
				_settings.PerMonitorEnabled = true;
				_gamma.PerMonitorEnabled = true;
				_gamma.ResetDisplayStates(entryB, entryT);
				SyncGammaEnabledFromSettings(); // 停用标记仍以设置记录为准
			}
			else
			{
				// 关闭独立控制：统一目标 = 进入独立控制时的基准（若从未进入过则
				// 回退启用屏平均值）。必须在翻转 PerMonitorEnabled 之前读取，
				// 否则 Average* 会退化成读陈旧的统一种子字段（Bug3）。
				float targetB = _perMonitorEntryBrightness >= 0f ? _perMonitorEntryBrightness : _gamma.AverageBrightness;
				float targetT = _perMonitorEntryTemperature >= 0f ? _perMonitorEntryTemperature : _gamma.AverageTemperature;
				_settings.PerMonitorEnabled = false;
				bool smoothB = _settings.BrightnessSmooth && !_disableActive && !_fullscreenPaused;
				bool smoothT = _settings.TemperatureSmooth && !_disableActive && !_fullscreenPaused;
				if (!smoothB && !smoothT)
				{
					_gamma.PerMonitorEnabled = false;
					_gamma.SetBrightness(targetB);
					_gamma.SetTemperature(targetT);
				}
				else
				{
					// 平滑关闭：gamma 侧仍保持 per-monitor 语义，各屏从自身当前值
					// 缓动到统一目标；动画结束后才翻转并整批写屏（避免统一写屏
					// 把不同起点硬拉到同一插值造成的跳变）。
					BeginDisablePerMonitorAnimation(targetB, targetT, smoothB, smoothT);
					return;
				}
			}
			SettingsManager.Save(_settings);
			_popup!.PerMonitorEnabled = enabled;
			// 切换后即时刷新弹窗行模式（行数据源注入）
			_popup?.SetDisplays(BuildDisplayRows());
			_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
		}
	}

	private void BeginDisablePerMonitorAnimation(float targetB, float targetT, bool smoothB, bool smoothT)
	{
		if (_unifyActive) return;
		_unifyStart.Clear();
		foreach (string displayId in _gamma!.GetDisplayIds())
		{
			var st = _gamma.GetDisplayState(displayId);
			_unifyStart[displayId] = (st.Brightness, st.Temperature);
		}
		if (_unifyStart.Count == 0)
		{
			_gamma.PerMonitorEnabled = false;
			_gamma.SetBrightness(targetB);
			_gamma.SetTemperature(targetT);
			return;
		}
		_unifyActive = true;
		_unifyTargetB = targetB;
		_unifyTargetT = targetT;
		_unifySmoothB = smoothB;
		_unifySmoothT = smoothT;
		_unifyStartTime = DateTime.Now;
		// 未启用平滑的轴在动画一开始就设到目标（保持原有"该轴立即变化"语义）。
		if (!smoothB)
		{
			foreach (var kv in _unifyStart) _gamma.SetBrightness(kv.Key, targetB);
		}
		if (!smoothT)
		{
			foreach (var kv in _unifyStart) _gamma.SetTemperature(kv.Key, targetT);
		}
		if (_unifyTimer == null)
		{
			_unifyTimer = new Timer
			{
				Interval = SmoothTickMs
			};
			_unifyTimer.Tick += OnUnifyTick;
		}
		_unifyTimer.Start();
	}

	private void OnUnifyTick(object? sender, EventArgs e)
	{
		double num = (DateTime.Now - _unifyStartTime).TotalMilliseconds / (double)SmoothDurationMs;
		if (num >= 1.0) num = 1.0;
		double f = EaseOutCubic(num);
		foreach (var kv in _unifyStart)
		{
			if (_unifySmoothB)
			{
				float b = (float)((double)kv.Value.Brightness + ((double)_unifyTargetB - (double)kv.Value.Brightness) * f);
				_gamma?.SetBrightness(kv.Key, b);
			}
			if (_unifySmoothT)
			{
				float t = (float)((double)kv.Value.Temperature + ((double)_unifyTargetT - (double)kv.Value.Temperature) * f);
				_gamma?.SetTemperature(kv.Key, t);
			}
		}
		if (num >= 1.0) CompleteDisablePerMonitorAnimation();
	}

	private void CompleteDisablePerMonitorAnimation()
	{
		CancelUnifyAnimation();
		AppSettings? settings = _settings;
		if (settings == null || _gamma == null) return;
		_gamma.PerMonitorEnabled = false;
		_gamma.SetBrightness(_unifyTargetB);
		_gamma.SetTemperature(_unifyTargetT);
		SettingsManager.Save(settings);
		_popup!.PerMonitorEnabled = false;
		_popup?.SetDisplays(BuildDisplayRows());
		_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, settings.ColorTemperatureEnabled);
		this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? _unifyTargetB);
		this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? _unifyTargetT);
	}

	private void CancelUnifyAnimation()
	{
		if (_unifyTimer != null)
		{
			_unifyTimer.Stop();
			_unifyTimer.Dispose();
			_unifyTimer = null;
		}
		_unifyActive = false;
		_unifyStart.Clear();
	}

	public (float Brightness, float Temperature, bool Enabled) GetDisplayState(string edidId)
	{
		if (_gamma == null)
		{
			return (Brightness: 1f, Temperature: 6600f, Enabled: true);
		}
		DisplayState displayState = _gamma.GetDisplayState(edidId);
		bool item = true;
		AppSettings? settings = _settings;
		if (settings != null && settings.MonitorStates != null &&
			settings.MonitorStates.TryGetValue(edidId, out MonitorState? value) && value != null)
		{
			item = value.Enabled;
		}
		return (Brightness: displayState.Brightness, Temperature: displayState.Temperature, Enabled: item);
	}

	public IReadOnlyList<string> GetDisplayIds()
	{
		return _gamma?.GetDisplayIds() ?? Array.Empty<string>();
	}

	public void SetDisplayBrightness(string edidId, float brightness)
	{
		if (_gamma != null)
		{
			OnManualAdjustment();
			_gamma.SetBrightness(edidId, brightness);
			_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? brightness);
			SaveSettings();
		}
	}

	public void SetDisplayTemperature(string edidId, float kelvin)
	{
		if (_gamma != null)
		{
			OnManualAdjustment();
			_gamma.SetTemperature(edidId, kelvin);
			_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? kelvin);
			SaveSettings();
		}
	}

	/// <summary>
	/// 设置单台显示器是否受控（独立控制模式的启用开关）。
	/// 停用屏冻结、不再被调节/重写。
	/// </summary>
	public void SetDisplayEnabled(string edidId, bool enabled)
	{
		if (_settings != null)
		{
			Dictionary<string, MonitorState> dictionary = _settings.MonitorStates ?? new Dictionary<string, MonitorState>();
			if (!dictionary.TryGetValue(edidId, out var value))
			{
				value = new MonitorState();
			}
			value.Enabled = enabled;
			dictionary[edidId] = value;
			_settings.MonitorStates = dictionary;
			// 同步 GammaController（单一事实源）：停用屏冻结、不再被调节/重写
			_gamma?.SetDisplayEnabled(edidId, enabled);
			SettingsManager.Save(_settings);
			// 即时刷新弹窗/OSD 行（停用屏置灰冻结）
			if (_settings.PerMonitorEnabled)
			{
				_popup?.SetDisplays(BuildDisplayRows());
			}
		}
	}

	public string? GetDisplayName(string edidId)
	{
		AppSettings? settings = _settings;
		if (settings != null && settings.MonitorNames != null &&
			settings.MonitorNames.TryGetValue(edidId, out string? value) && value != null)
		{
			return string.IsNullOrWhiteSpace(value) ? null : value;
		}
		return null;
	}

	public void SetDisplayName(string edidId, string? name)
	{
		if (_settings != null)
		{
			Dictionary<string, string> dictionary = _settings.MonitorNames ?? new Dictionary<string, string>();
			if (string.IsNullOrWhiteSpace(name))
			{
				dictionary.Remove(edidId);
			}
			else
			{
				dictionary[edidId] = name.Trim();
			}
			_settings.MonitorNames = dictionary;
			SettingsManager.Save(_settings);
			_popup?.RefreshDisplayNames();
		}
	}

	public string GetDisplaySystemName(string edidId)
	{
		return GetDisplayNameFor(edidId);
	}

	public float GetTemperatureStepSize()
	{
		return _settings?.TemperatureStepSize ?? 100f;
	}

	public bool GetColorTemperatureEnabled()
	{
		return _settings?.ColorTemperatureEnabled ?? false;
	}

	public void SetColorTemperatureEnabled(bool enabled)
	{
		if (_settings != null)
		{
			_settings.ColorTemperatureEnabled = enabled;
			float num = _gamma?.CurrentTemperature ?? 6600f;
			float num2 = (enabled ? _settings.LastTemperature : 6600f);
			if (_settings.TemperatureSmooth && !_disableActive && Math.Abs(num - num2) >= 50f)
			{
				StartSmoothTransition(_gamma?.CurrentBrightness ?? 1f, num2, smoothBright: false, smoothTemp: true);
			}
			else
			{
				_gamma?.SetTemperature(num2);
			}
			SettingsManager.Save(_settings);
			RegisterHotkeys();
			if (_popup != null)
			{
				_popup.TemperatureEnabled = enabled;
			}
			_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, enabled);
			ApplySolarScheduler();
		}
	}

	public void SetTemperatureStepSize(float stepK)
	{
		if (_settings != null)
		{
			_settings.TemperatureStepSize = Math.Clamp(stepK, 50f, 3000f);
			SettingsManager.Save(_settings);
			if (_gamma != null)
			{
				_gamma.TemperatureStepSize = _settings.TemperatureStepSize;
			}
			if (_popup != null)
			{
				_popup.TemperatureStepSize = _settings.TemperatureStepSize;
			}
		}
	}

	public float GetMinTemperature()
	{
		return _settings?.MinTemperature ?? 3300f;
	}

	public float GetMaxTemperature()
	{
		return _settings?.MaxTemperature ?? 10000f;
	}

	public void SetTemperatureRange(float minK, float maxK)
	{
		if (_settings == null)
		{
			return;
		}
		minK = Math.Clamp(minK, 3300f, 10000f);
		maxK = Math.Clamp(maxK, 3300f, 10000f);
		if (minK >= maxK)
		{
			return;
		}
		_settings.MinTemperature = minK;
		_settings.MaxTemperature = maxK;
		SettingsManager.Save(_settings);
		if (_gamma != null)
		{
			_gamma.MinTemperature = minK;
			_gamma.MaxTemperature = maxK;
		}
		if (_popup != null)
		{
			_popup.MinTemperature = minK;
			_popup.MaxTemperature = maxK;
		}
		if (_gamma != null && _settings.ColorTemperatureEnabled)
		{
			float currentTemperature = _gamma.CurrentTemperature;
			float num = Math.Clamp(currentTemperature, minK, maxK);
			if (_settings.TemperatureSmooth && !_disableActive && Math.Abs(currentTemperature - num) >= 50f)
			{
				StartSmoothTransition(_gamma.CurrentBrightness, num, smoothBright: false, smoothTemp: true);
			}
			else
			{
				_gamma.SetTemperature(num);
			}
		}
	}

	public void SetColorTemperature(float kelvin)
	{
		AppSettings? settings = _settings;
		if (settings != null && settings.ColorTemperatureEnabled)
		{
			OnManualAdjustment();
			bool flag = settings.SolarAdjustEnabled && !settings.SolarManuallyOverridden;
			if (settings.TemperatureSmooth && !flag)
			{
				StartSmoothTransition(_gamma?.CurrentBrightness ?? 1f, kelvin, smoothBright: false, smoothTemp: true);
			}
			else
			{
				_gamma?.SetTemperature(kelvin);
				this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
				SaveSettings();
			}
			if (_gamma != null && _popup != null)
			{
				_popup.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
			}
			_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f);
		}
	}

	public float GetCurrentTemperature()
	{
		return _gamma?.CurrentTemperature ?? 6600f;
	}

	public float GetCurrentBrightness()
	{
		return _gamma?.CurrentBrightness ?? 1f;
	}

	public bool GetSolarAdjustEnabled()
	{
		return _settings?.SolarAdjustEnabled ?? false;
	}

	public bool GetSolarManualMode()
	{
		return _settings?.SolarManualMode ?? true;
	}

	public int GetManualSunriseMinutes()
	{
		return _settings?.ManualSunriseMinutes ?? 440;
	}

	public int GetManualSunsetMinutes()
	{
		return _settings?.ManualSunsetMinutes ?? 990;
	}

	public double GetSolarLatitude()
	{
		return _settings?.SolarLatitude ?? 39.9042;
	}

	public double GetSolarLongitude()
	{
		return _settings?.SolarLongitude ?? 116.4074;
	}

	public bool GetSolarLocationSet()
	{
		return _settings?.SolarLocationSet ?? false;
	}

	public float GetDayTemperature()
	{
		return _settings?.DayTemperature ?? 6600f;
	}

	public float GetDayBrightness()
	{
		return _settings?.DayBrightness ?? 1f;
	}

	public float GetNightTemperature()
	{
		return _settings?.NightTemperature ?? 3900f;
	}

	public float GetNightBrightness()
	{
		return _settings?.NightBrightness ?? 0.85f;
	}

	public int GetTransitionMinutes()
	{
		return _settings?.TransitionMinutes ?? 0;
	}

	public bool GetSolarManuallyOverridden()
	{
		return _settings?.SolarManuallyOverridden ?? false;
	}

	public void SetSolarAdjustEnabled(bool enabled)
	{
		if (_settings == null)
		{
			return;
		}
		_settings.SolarAdjustEnabled = enabled;
		if (enabled)
		{
			_settings.SolarManuallyOverridden = false;
		}
		SettingsManager.Save(_settings);
		if (enabled)
		{
			bool brightnessSmooth = _settings.BrightnessSmooth;
			bool temperatureSmooth = _settings.TemperatureSmooth;
			if ((brightnessSmooth || temperatureSmooth) && !_disableActive)
			{
				var (tb, tt) = _solarScheduler?.GetCurrentTargets() ?? (1f, 6600f);
				StartSmoothTransition(tb, tt, brightnessSmooth, temperatureSmooth, delegate
				{
					AppSettings? settings = _settings;
					if (settings != null && settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden)
					{
						_solarScheduler?.Start();
						_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? tb, _gamma?.CurrentTemperature ?? tt, _settings?.ColorTemperatureEnabled ?? false);
					}
				});
			}
			else
			{
				ApplySolarScheduler();
			}
			return;
		}
		_solarScheduler?.Stop();
		float dayBrightness = _settings.DayBrightness;
		float num = (_settings.ColorTemperatureEnabled ? _settings.DayTemperature : 6600f);
		bool brightnessSmooth2 = _settings.BrightnessSmooth;
		bool temperatureSmooth2 = _settings.TemperatureSmooth;
		if (brightnessSmooth2 || temperatureSmooth2)
		{
			StartSmoothTransition(dayBrightness, num, brightnessSmooth2, temperatureSmooth2);
			return;
		}
		_gamma?.SetBrightness(dayBrightness);
		_gamma?.SetTemperature(num);
		SaveSettings();
		_popup?.SyncFromGamma(dayBrightness, num);
		this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? dayBrightness);
		this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? num);
		_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? dayBrightness, _gamma?.CurrentTemperature ?? num, _settings?.ColorTemperatureEnabled ?? false);
	}

	public void SetSolarManualMode(bool manual)
	{
		if (_settings != null)
		{
			_settings.SolarManualMode = manual;
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
		}
	}

	public void SetManualSunriseMinutes(int minutes)
	{
		if (_settings != null)
		{
			_settings.ManualSunriseMinutes = Math.Clamp(minutes, 0, 1439);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
		}
	}

	public void SetManualSunsetMinutes(int minutes)
	{
		if (_settings != null)
		{
			_settings.ManualSunsetMinutes = Math.Clamp(minutes, 0, 1439);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
		}
	}

	public void SetSolarLocation(double latitude, double longitude)
	{
		if (_settings != null)
		{
			_settings.SolarLatitude = latitude;
			_settings.SolarLongitude = longitude;
			_settings.SolarLocationSet = true;
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
		}
	}

	public void SetDayTemperature(float kelvin)
	{
		if (_settings != null)
		{
			_settings.DayTemperature = Math.Clamp(kelvin, 3300f, 10000f);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
			RefreshSolarNow();
		}
	}

	public void SetDayBrightness(float brightness)
	{
		if (_settings != null)
		{
			_settings.DayBrightness = Math.Clamp(brightness, 0f, 1f);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
			RefreshSolarNow();
		}
	}

	public void SetNightTemperature(float kelvin)
	{
		if (_settings != null)
		{
			_settings.NightTemperature = Math.Clamp(kelvin, 3300f, 10000f);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
			RefreshSolarNow();
		}
	}

	public void SetNightBrightness(float brightness)
	{
		if (_settings != null)
		{
			_settings.NightBrightness = Math.Clamp(brightness, 0f, 1f);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
			RefreshSolarNow();
		}
	}

	public void SetTransitionMinutes(int minutes)
	{
		if (_settings != null)
		{
			_settings.TransitionMinutes = Math.Clamp(minutes, 0, 60);
			SettingsManager.Save(_settings);
			ApplySolarScheduler();
		}
	}

	public void RefreshSolarNow()
	{
		AppSettings? settings = _settings;
		if (settings != null && settings.SolarAdjustEnabled)
		{
			_solarScheduler?.ApplyNowInstant();
		}
	}

	private void ApplySolarScheduler()
	{
		if (_solarScheduler == null || _gamma == null)
		{
			return;
		}
		AppSettings? settings = _settings;
		if (settings != null && settings.SolarAdjustEnabled && !settings.SolarManuallyOverridden)
		{
			if (!_solarScheduler.IsRunning)
			{
				_solarScheduler.Start();
			}
			else
			{
				_solarScheduler.Tick();
			}
		}
		else
		{
			_solarScheduler.Stop();
		}
	}

	/// <summary>
	/// 确保 SystemEventMonitor 已创建并接线（显示器热插拔自愈/全屏暂停共用）。
	/// 启动时按设置创建；运行中把"全屏自动暂停"从关切到开时也会延迟创建——
	/// 否则默认关闭的首启（_systemMonitor==null）在设置页开启该功能要到重启才生效。
	/// </summary>
	private void EnsureSystemMonitor()
	{
		if (_systemMonitor != null) return;
		var monitor = new SystemEventMonitor();
		monitor.Resumed += OnSystemResumed;
		monitor.DisplayChanged += OnDisplayChanged;
		monitor.FullscreenEntered += OnFullscreenEntered;
		monitor.FullscreenExited += OnFullscreenExited;
		monitor.Initialize();
		_systemMonitor = monitor;
	}

	private void OnSystemResumed()
	{
		if (_gamma == null)
		{
			return;
		}
		AppSettings? settings = _settings;
		if (settings == null || !settings.GammaSelfHealEnabled || _fullscreenPaused)
		{
			return;
		}
		Timer t = new Timer
		{
			Interval = 800
		};
		t.Tick += delegate
		{
			t.Stop();
			t.Dispose();
			if (_gamma != null && !_fullscreenPaused)
			{
				_gamma.RefreshDisplays();
				SyncGammaEnabledFromSettings();
				_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			}
		};
		t.Start();
	}

	private void OnDisplayChanged()
	{
		if (_gamma != null)
		{
			AppSettings? settings = _settings;
			if (settings != null && settings.GammaSelfHealEnabled && !_fullscreenPaused)
			{
				_gamma.RefreshDisplays();
				SyncGammaEnabledFromSettings();
				_trayIcon?.UpdateTooltip(_gamma.CurrentBrightness, _gamma.CurrentTemperature, _settings?.ColorTemperatureEnabled ?? false);
			}
		}
	}

	/// <summary>
	/// 热插拔/自愈后 gamma 会以 Enabled=true 重建各屏状态（RefreshDisplays 播种），
	/// 这里把 settings.MonitorStates 里记录的"停用"标记同步回去，避免停用屏被
	/// 重新启用并写 gamma（Bug2 热插拔路径）。
	/// </summary>
	private void SyncGammaEnabledFromSettings()
	{
		if (_settings == null || _gamma == null || !_gamma.PerMonitorEnabled) return;
		Dictionary<string, MonitorState> dictionary = _settings.MonitorStates ?? new Dictionary<string, MonitorState>();
		foreach (string displayId in _gamma.GetDisplayIds())
		{
			if (dictionary.TryGetValue(displayId, out var value))
			{
				_gamma.SetDisplayEnabled(displayId, value.Enabled);
			}
		}
	}

	private void OnFullscreenEntered()
	{
		AppSettings? settings = _settings;
		if (settings == null || !settings.PauseInFullscreenEnabled)
		{
			return;
		}
		if (_disableActive)
		{
			_fullscreenPaused = true;
			return;
		}
		bool flag = _fullscreenAnimTimer != null && _fullscreenAnimExit;
		if (!_fullscreenPaused || flag)
		{
			if (!_fullscreenPaused)
			{
				_fullscreenBrightnessBefore = _gamma?.CurrentBrightness ?? 1f;
				_fullscreenTemperatureBefore = _gamma?.CurrentTemperature ?? 6600f;
				_fullscreenPaused = true;
				_gamma?.SetPaused(paused: true);
			}
			StartFullscreenTransition(1f, 6600f);
		}
	}

	private void OnFullscreenExited()
	{
		if (_fullscreenPaused)
		{
			if (_disableActive)
			{
				_fullscreenPaused = false;
			}
			else
			{
				StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
			}
		}
	}

	private void StartFullscreenTransition(float targetBright, float targetTemp, bool exit = false)
	{
		if (_gamma == null)
		{
			return;
		}
		bool flag = _settings?.BrightnessSmooth ?? false;
		bool flag2 = _settings?.TemperatureSmooth ?? false;
		if (!flag && !flag2)
		{
			if (exit)
			{
				_fullscreenPaused = false;
				if (!_disableActive)
				{
					_gamma?.SetPaused(paused: false);
				}
			}
			_gamma?.ApplyPausedState();
			return;
		}
		_fullscreenAnimSmoothB = flag;
		_fullscreenAnimSmoothT = flag2;
		_fullscreenAnimTargetBright = targetBright;
		_fullscreenAnimTargetTemp = targetTemp;
		_fullscreenAnimStartBright = _gamma.ReadCurrentBrightness();
		_fullscreenAnimStartTemp = _gamma.ReadCurrentTemperature();
		if (!flag)
		{
			_gamma?.ApplyPausedFrame(targetBright, _gamma.ReadCurrentTemperature());
		}
		if (!flag2)
		{
			_gamma?.ApplyPausedFrame(_gamma.ReadCurrentBrightness(), targetTemp);
		}
		_fullscreenAnimExit = exit;
		_fullscreenAnimStartTime = DateTime.Now;
		if (_fullscreenAnimTimer == null)
		{
			_fullscreenAnimTimer = new Timer
			{
				Interval = 30
			};
			_fullscreenAnimTimer.Tick += OnFullscreenSmoothTick;
		}
		_fullscreenAnimTimer.Start();
	}

	private void OnFullscreenSmoothTick(object? sender, EventArgs e)
	{
		double num = (DateTime.Now - _fullscreenAnimStartTime).TotalMilliseconds / 1200.0;
		if (num >= 1.0)
		{
			num = 1.0;
		}
		double num2 = EaseOutCubic(num);
		float brightness = (_fullscreenAnimSmoothB ? ((float)((double)_fullscreenAnimStartBright + (double)(_fullscreenAnimTargetBright - _fullscreenAnimStartBright) * num2)) : _fullscreenAnimTargetBright);
		float temperature = (_fullscreenAnimSmoothT ? ((float)((double)_fullscreenAnimStartTemp + (double)(_fullscreenAnimTargetTemp - _fullscreenAnimStartTemp) * num2)) : _fullscreenAnimTargetTemp);
		_gamma?.ApplyPausedFrame(brightness, temperature);
		if (!(num >= 1.0))
		{
			return;
		}
		_fullscreenAnimTimer?.Stop();
		_fullscreenAnimTimer?.Dispose();
		_fullscreenAnimTimer = null;
		if (_fullscreenAnimExit)
		{
			_fullscreenPaused = false;
			if (!_disableActive)
			{
				_gamma?.SetPaused(paused: false);
			}
			_gamma?.ApplyPausedState();
		}
		else
		{
			_gamma?.ApplyPausedState();
		}
	}

	public DateTime? GetDisableUntil()
	{
		return _settings?.DisableUntil;
	}

	public bool IsDisableActive()
	{
		return _disableActive;
	}

	public TimeSpan? GetDisableRemaining()
	{
		DateTime? dateTime = _settings?.DisableUntil;
		if (!dateTime.HasValue)
		{
			return null;
		}
		if (dateTime.Value == DateTime.MaxValue)
		{
			return null;
		}
		TimeSpan timeSpan = dateTime.Value - DateTime.Now;
		return (timeSpan > TimeSpan.Zero) ? timeSpan : TimeSpan.Zero;
	}

	public void SetDisable(TimeSpan? duration)
	{
		if (_settings != null && _gamma != null)
		{
			if (!duration.HasValue)
			{
				_settings.DisableUntil = DateTime.MaxValue;
				SettingsManager.Save(_settings);
				ApplyDisable(disable: true);
			}
			else if (duration == TimeSpan.Zero)
			{
				_settings.DisableUntil = null;
				SettingsManager.Save(_settings);
				ApplyDisable(disable: false);
			}
			else
			{
				_settings.DisableUntil = DateTime.Now + duration.Value;
				SettingsManager.Save(_settings);
				ApplyDisable(disable: true);
			}
			UpdateDisableTimer();
		}
	}

	private void ApplyDisable(bool disable)
	{
		if (_gamma == null)
		{
			return;
		}
		if (disable)
		{
			if (!_disableActive)
			{
				_disableBrightnessBefore = _gamma.CurrentBrightness;
				_disableTemperatureBefore = _gamma.CurrentTemperature;
				_disableActive = true;
				_gamma.SetPaused(paused: true);
				_solarScheduler?.Stop();
				StartDisableTransition(1f, 6600f, exit: false, null);
			}
		}
		else if (_disableActive)
		{
			if (_fullscreenPaused)
			{
				_disableActive = false;
				UpdateDisableTimer();
			}
			else
			{
				StartDisableTransition(_disableBrightnessBefore, _disableTemperatureBefore, exit: true, OnDisableResumed);
			}
		}
	}

	private void OnDisableResumed()
	{
		_disableActive = false;
		if (!_fullscreenPaused)
		{
			_gamma?.SetPaused(paused: false);
		}
		_gamma?.ApplyPausedState();
		ApplySolarScheduler();
		UpdateDisableTimer();
	}

	private void StartDisableTransition(float targetBright, float targetTemp, bool exit, Action? done)
	{
		if (_gamma == null)
		{
			return;
		}
		bool flag = _settings?.BrightnessSmooth ?? false;
		bool flag2 = _settings?.TemperatureSmooth ?? false;
		if (!flag && !flag2)
		{
			_gamma?.ApplyPausedState();
			done?.Invoke();
			return;
		}
		_disableAnimSmoothB = flag;
		_disableAnimSmoothT = flag2;
		_disableAnimTargetBright = targetBright;
		_disableAnimTargetTemp = targetTemp;
		_disableAnimStartBright = _gamma.ReadCurrentBrightness();
		_disableAnimStartTemp = _gamma.ReadCurrentTemperature();
		if (!flag)
		{
			_gamma?.ApplyPausedFrame(targetBright, _gamma.ReadCurrentTemperature());
		}
		if (!flag2)
		{
			_gamma?.ApplyPausedFrame(_gamma.ReadCurrentBrightness(), targetTemp);
		}
		_disableAnimExit = exit;
		_disableAnimDone = done;
		_disableAnimStartTime = DateTime.Now;
		if (_disableAnimTimer == null)
		{
			_disableAnimTimer = new Timer
			{
				Interval = 30
			};
			_disableAnimTimer.Tick += OnDisableSmoothTick;
		}
		_disableAnimTimer.Start();
	}

	private void OnDisableSmoothTick(object? sender, EventArgs e)
	{
		double num = (DateTime.Now - _disableAnimStartTime).TotalMilliseconds / 1200.0;
		if (num >= 1.0)
		{
			num = 1.0;
		}
		double num2 = EaseOutCubic(num);
		float brightness = (_disableAnimSmoothB ? ((float)((double)_disableAnimStartBright + (double)(_disableAnimTargetBright - _disableAnimStartBright) * num2)) : _disableAnimTargetBright);
		float temperature = (_disableAnimSmoothT ? ((float)((double)_disableAnimStartTemp + (double)(_disableAnimTargetTemp - _disableAnimStartTemp) * num2)) : _disableAnimTargetTemp);
		_gamma?.ApplyPausedFrame(brightness, temperature);
		if (num >= 1.0)
		{
			_disableAnimTimer?.Stop();
			_disableAnimTimer?.Dispose();
			_disableAnimTimer = null;
			Action? disableAnimDone = _disableAnimDone;
			_disableAnimDone = null;
			_gamma?.ApplyPausedState();
			disableAnimDone?.Invoke();
		}
	}

	private void RestoreDisableState()
	{
		if (_settings == null || _gamma == null)
		{
			return;
		}
		DateTime? disableUntil = _settings.DisableUntil;
		if (disableUntil.HasValue)
		{
			if (disableUntil.Value <= DateTime.Now)
			{
				_settings.DisableUntil = null;
				SettingsManager.Save(_settings);
			}
			else
			{
				_disableBrightnessBefore = _gamma.CurrentBrightness;
				_disableTemperatureBefore = _gamma.CurrentTemperature;
				_disableActive = true;
				_gamma.SetPaused(paused: true);
				_gamma.ApplyPausedState();
				_solarScheduler?.Stop();
			}
			UpdateDisableTimer();
		}
	}

	private void UpdateDisableTimer()
	{
		DateTime? dateTime = _settings?.DisableUntil;
		if (dateTime.HasValue && dateTime.Value != DateTime.MaxValue && dateTime.Value > DateTime.Now)
		{
			if (_disableTimer == null)
			{
				_disableTimer = new Timer
				{
					Interval = 1000
				};
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
		if (_settings != null)
		{
			DateTime? disableUntil = _settings.DisableUntil;
			if (disableUntil.HasValue && disableUntil.Value <= DateTime.Now)
			{
				_settings.DisableUntil = null;
				SettingsManager.Save(_settings);
				_disableTimer?.Stop();
				ApplyDisable(disable: false);
			}
		}
	}

	public (TimeOnly Sunrise, TimeOnly Sunset) GetSolarSunriseSunset()
	{
		if (_settings == null)
		{
			return (Sunrise: new TimeOnly(6, 0), Sunset: new TimeOnly(18, 0));
		}
		if (_settings.SolarManualMode)
		{
			return (Sunrise: TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunriseMinutes, 0, 1439))), Sunset: TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(_settings.ManualSunsetMinutes, 0, 1439))));
		}
		return SolarTimes.Calculate(_settings.SolarLatitude, _settings.SolarLongitude, DateTime.Now);
	}

	public DateTime GetSolarDisableUntil()
	{
		DateTime now = DateTime.Now;
		var (timeOnly, timeOnly2) = GetSolarSunriseSunset();
		if (now.TimeOfDay >= timeOnly.ToTimeSpan() && now.TimeOfDay < timeOnly2.ToTimeSpan())
		{
			DateTime dateTime = now.Date + timeOnly2.ToTimeSpan();
			return (dateTime > now) ? dateTime : dateTime.AddDays(1.0);
		}
		DateTime dateTime2 = now.Date + timeOnly.ToTimeSpan();
		if (dateTime2 <= now)
		{
			dateTime2 = dateTime2.AddDays(1.0);
		}
		return dateTime2;
	}

	public bool IsSolarDisableActive()
	{
		DateTime? dateTime = _settings?.DisableUntil;
		if (!dateTime.HasValue)
		{
			return false;
		}
		(TimeOnly Sunrise, TimeOnly Sunset) solarSunriseSunset = GetSolarSunriseSunset();
		TimeOnly item = solarSunriseSunset.Sunrise;
		TimeOnly item2 = solarSunriseSunset.Sunset;
		DateTime now = DateTime.Now;
		bool flag = Math.Abs((dateTime.Value - now.Date - item.ToTimeSpan()).TotalMinutes) < 5.0;
		bool flag2 = Math.Abs((dateTime.Value - now.Date - item2.ToTimeSpan()).TotalMinutes) < 5.0;
		return flag || flag2;
	}

	public bool IsDaytimeNow()
	{
		DateTime now = DateTime.Now;
		var (timeOnly, timeOnly2) = GetSolarSunriseSunset();
		return now.TimeOfDay >= timeOnly.ToTimeSpan() && now.TimeOfDay < timeOnly2.ToTimeSpan();
	}

	private void OnDisableRequested(TimeSpan? duration)
	{
		if (duration == TimeSpan.FromSeconds(-1.0))
		{
			SetDisable(GetSolarDisableUntil() - DateTime.Now);
		}
		else
		{
			SetDisable(duration);
		}
	}

	public bool GetShowOverlay()
	{
		return _settings?.ShowOverlay ?? true;
	}

	public bool GetTopMost()
	{
		return _settings?.SettingsTopMost ?? false;
	}

	public void SetTopMost(bool topMost)
	{
		if (_settings != null)
		{
			_settings.SettingsTopMost = topMost;
			SettingsManager.Save(_settings);
		}
	}

	public void ResetSettings()
	{
		if (_settings != null)
		{
			if (_disableActive)
			{
				_disableActive = false;
				_gamma?.SetPaused(paused: false);
			}
			_settings.DisableUntil = null;
			_settings.LastBrightness = 1f;
			_settings.LastTemperature = 6600f;
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
			_settings.TemperatureStepSize = 100f;
			_settings.MinTemperature = 3300f;
			_settings.MaxTemperature = 10000f;
			_settings.AllHotKeysEnabled = true;
			_settings.StartupEnabled = null;
			_settings.SolarAdjustEnabled = false;
			_settings.SolarManualMode = true;
			_settings.ManualSunriseMinutes = 440;
			_settings.ManualSunsetMinutes = 990;
			_settings.SolarLatitude = 39.9042;
			_settings.SolarLongitude = 116.4074;
			_settings.SolarLocationSet = false;
			_settings.DayTemperature = 6600f;
			_settings.DayBrightness = 1f;
			_settings.NightTemperature = 3900f;
			_settings.NightBrightness = 0.85f;
			_settings.TransitionMinutes = 0;
			_settings.SolarManuallyOverridden = false;
			_settings.BrightnessSmooth = true;
			_settings.TemperatureSmooth = true;
			_settings.GammaSelfHealEnabled = true;
			_settings.PauseInFullscreenEnabled = true;
			SettingsManager.Save(_settings);
			Localization.Setting = _settings.Language;
			Localization.Current = Localization.Resolve(_settings.Language).Effective;
			ThemeManager.Apply(_settings.Theme);
			ThemeManager.ApplyPopupTheme(_settings.PopupTheme);
			if (_gamma != null)
			{
				_gamma.StepSize = _settings.StepSize;
			}
			if (_popup != null)
			{
				_popup.StepSize = _settings.StepSize;
			}
			if (_gamma != null)
			{
				_gamma.TemperatureStepSize = _settings.TemperatureStepSize;
			}
			if (_popup != null)
			{
				_popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
			}
			if (_gamma != null)
			{
				_gamma.MinTemperature = _settings.MinTemperature;
				_gamma.MaxTemperature = _settings.MaxTemperature;
			}
			if (_popup != null)
			{
				_popup.MinTemperature = _settings.MinTemperature;
				_popup.MaxTemperature = _settings.MaxTemperature;
			}
			RegisterHotkeys();
			bool brightnessSmooth = _settings.BrightnessSmooth;
			bool temperatureSmooth = _settings.TemperatureSmooth;
			if (brightnessSmooth || temperatureSmooth)
			{
				StartSmoothTransition(1f, 6600f, brightnessSmooth, temperatureSmooth);
			}
			else
			{
				_gamma?.SetBrightness(1f);
				_gamma?.SetTemperature(6600f);
			}
			_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
			ApplySolarScheduler();
		}
	}

	public void ClearAllHotkeys()
	{
		if (_settings != null)
		{
			_settings.IncreaseBrightnessHotKey = "";
			_settings.DecreaseBrightnessHotKey = "";
			_settings.PowerOffHotKey = "";
			_settings.IncreaseTemperatureHotKey = "";
			_settings.DecreaseTemperatureHotKey = "";
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public void SetShowOverlay(bool show)
	{
		if (_settings != null)
		{
			_settings.ShowOverlay = show;
			SettingsManager.Save(_settings);
		}
	}

	public string GetIncreaseBrightnessHotKey()
	{
		return _settings?.IncreaseBrightnessHotKey ?? "";
	}

	public bool GetIncreaseBrightnessHotKeyEnabled()
	{
		return _settings?.IncreaseBrightnessHotKeyEnabled ?? true;
	}

	public void SetIncreaseBrightnessHotKeyEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.IncreaseBrightnessHotKeyEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public string GetDecreaseBrightnessHotKey()
	{
		return _settings?.DecreaseBrightnessHotKey ?? "";
	}

	public bool GetDecreaseBrightnessHotKeyEnabled()
	{
		return _settings?.DecreaseBrightnessHotKeyEnabled ?? true;
	}

	public void SetDecreaseBrightnessHotKeyEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.DecreaseBrightnessHotKeyEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public bool SetIncreaseBrightnessHotKey(string hotkey)
	{
		if (_settings == null || _trayIcon == null)
		{
			return false;
		}
		string increaseBrightnessHotKey = _settings.IncreaseBrightnessHotKey;
		string text = hotkey ?? "";
		if (IsTakenByAnother(text, _settings.DecreaseBrightnessHotKey, _settings.PowerOffHotKey, _settings.IncreaseTemperatureHotKey, _settings.DecreaseTemperatureHotKey))
		{
			return false;
		}
		_settings.IncreaseBrightnessHotKey = text;
		return CommitHotKey("IncBrightness", text, increaseBrightnessHotKey, delegate(string v)
		{
			_settings.IncreaseBrightnessHotKey = v;
		}, _settings.IncreaseBrightnessHotKeyEnabled);
	}

	public bool SetDecreaseBrightnessHotKey(string hotkey)
	{
		if (_settings == null || _trayIcon == null)
		{
			return false;
		}
		string decreaseBrightnessHotKey = _settings.DecreaseBrightnessHotKey;
		string text = hotkey ?? "";
		if (IsTakenByAnother(text, _settings.IncreaseBrightnessHotKey, _settings.PowerOffHotKey, _settings.IncreaseTemperatureHotKey, _settings.DecreaseTemperatureHotKey))
		{
			return false;
		}
		_settings.DecreaseBrightnessHotKey = text;
		return CommitHotKey("DecBrightness", text, decreaseBrightnessHotKey, delegate(string v)
		{
			_settings.DecreaseBrightnessHotKey = v;
		}, _settings.DecreaseBrightnessHotKeyEnabled);
	}

	public string GetPowerOffHotKey()
	{
		return _settings?.PowerOffHotKey ?? "";
	}

	public bool GetPowerOffHotKeyEnabled()
	{
		return _settings?.PowerOffHotKeyEnabled ?? true;
	}

	public void SetPowerOffHotKeyEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.PowerOffHotKeyEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public bool SetPowerOffHotKey(string hotkey)
	{
		if (_settings == null || _trayIcon == null)
		{
			return false;
		}
		string powerOffHotKey = _settings.PowerOffHotKey;
		string text = hotkey ?? "";
		if (IsTakenByAnother(text, _settings.IncreaseBrightnessHotKey, _settings.DecreaseBrightnessHotKey, _settings.IncreaseTemperatureHotKey, _settings.DecreaseTemperatureHotKey))
		{
			return false;
		}
		_settings.PowerOffHotKey = text;
		return CommitHotKey("PowerOff", text, powerOffHotKey, delegate(string v)
		{
			_settings.PowerOffHotKey = v;
		}, _settings.PowerOffHotKeyEnabled);
	}

	public string GetIncreaseTemperatureHotKey()
	{
		return _settings?.IncreaseTemperatureHotKey ?? "";
	}

	public bool GetIncreaseTemperatureHotKeyEnabled()
	{
		return _settings?.IncreaseTemperatureHotKeyEnabled ?? true;
	}

	public void SetIncreaseTemperatureHotKeyEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.IncreaseTemperatureHotKeyEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public bool SetIncreaseTemperatureHotKey(string hotkey)
	{
		if (_settings == null || _trayIcon == null)
		{
			return false;
		}
		string increaseTemperatureHotKey = _settings.IncreaseTemperatureHotKey;
		string text = hotkey ?? "";
		if (IsTakenByAnother(text, _settings.IncreaseBrightnessHotKey, _settings.DecreaseBrightnessHotKey, _settings.PowerOffHotKey, _settings.DecreaseTemperatureHotKey))
		{
			return false;
		}
		_settings.IncreaseTemperatureHotKey = text;
		return CommitHotKey("IncTemperature", text, increaseTemperatureHotKey, delegate(string v)
		{
			_settings.IncreaseTemperatureHotKey = v;
		}, _settings.IncreaseTemperatureHotKeyEnabled);
	}

	public string GetDecreaseTemperatureHotKey()
	{
		return _settings?.DecreaseTemperatureHotKey ?? "";
	}

	public bool GetDecreaseTemperatureHotKeyEnabled()
	{
		return _settings?.DecreaseTemperatureHotKeyEnabled ?? true;
	}

	public void SetDecreaseTemperatureHotKeyEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.DecreaseTemperatureHotKeyEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public bool GetAllHotKeysEnabled()
	{
		return _settings?.AllHotKeysEnabled ?? true;
	}

	public void SetAllHotKeysEnabled(bool enabled)
	{
		if (_settings != null && _trayIcon != null)
		{
			_settings.AllHotKeysEnabled = enabled;
			SettingsManager.Save(_settings);
			RegisterHotkeys();
		}
	}

	public bool SetDecreaseTemperatureHotKey(string hotkey)
	{
		if (_settings == null || _trayIcon == null)
		{
			return false;
		}
		string decreaseTemperatureHotKey = _settings.DecreaseTemperatureHotKey;
		string text = hotkey ?? "";
		if (IsTakenByAnother(text, _settings.IncreaseBrightnessHotKey, _settings.DecreaseBrightnessHotKey, _settings.PowerOffHotKey, _settings.IncreaseTemperatureHotKey))
		{
			return false;
		}
		_settings.DecreaseTemperatureHotKey = text;
		return CommitHotKey("DecTemperature", text, decreaseTemperatureHotKey, delegate(string v)
		{
			_settings.DecreaseTemperatureHotKey = v;
		}, _settings.DecreaseTemperatureHotKeyEnabled);
	}

	private static bool IsTakenByAnother(string hotkey, params string[] others)
	{
		if (string.IsNullOrWhiteSpace(hotkey))
		{
			return false;
		}
		foreach (string text in others)
		{
			if (!string.IsNullOrWhiteSpace(text) && string.Equals(text, hotkey, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private bool HotKeyActive(string slot, string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return true;
		}
		bool value2;
		return _hotKeyRegistration.TryGetValue(slot, out value2) && value2;
	}

	private bool CommitHotKey(string slot, string newValue, string previous, Action<string> apply, bool enabled)
	{
		SettingsManager.Save(_settings!);
		RegisterHotkeys();
		if (enabled)
		{
			AppSettings? settings = _settings;
			if ((settings == null || settings.AllHotKeysEnabled) && !HotKeyActive(slot, newValue))
			{
				apply(previous);
				SettingsManager.Save(_settings!);
				RegisterHotkeys();
				return false;
			}
		}
		return true;
	}

	public void SuspendAllHotKeys()
	{
		if (!_hotKeysSuspended)
		{
			_hotKeysSuspended = true;
			RegisterHotkeys();
		}
	}

	public void ResumeAllHotKeys()
	{
		if (_hotKeysSuspended)
		{
			_hotKeysSuspended = false;
			RegisterHotkeys();
		}
	}

	private void RegisterHotkeys()
	{
		HotKeyService? hotKeyService = _trayIcon?.HotKeyService;
		if (hotKeyService == null)
		{
			return;
		}
		hotKeyService.UnregisterAll();
		_hotKeyRegistration.Clear();
		string text = _settings?.IncreaseBrightnessHotKey ?? "";
		string error;
		if (!string.IsNullOrWhiteSpace(text))
		{
			AppSettings? settings = _settings;
			if ((settings == null || settings.IncreaseBrightnessHotKeyEnabled) && !HotKeysSuspended)
			{
				AppSettings? settings2 = _settings;
				if (settings2 == null || settings2.AllHotKeysEnabled)
				{
					_hotKeyRegistration["IncBrightness"] = hotKeyService.TryRegister(text, delegate
					{
						if (!_fullscreenPaused && !_disableActive)
						{
							if (_popup != null && _popup.IsShown)
							{
								_popup.AdjustByWheel(1);
							}
							else
							{
								_gamma?.AdjustBrightness(_gamma?.StepSize ?? 0.05f);
								OnManualAdjustment();
								if (_gamma != null)
								{
									_popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
								}
								ShowOverlayForDisplays();
								_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
								this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
								SaveSettings();
							}
						}
					}, out error);
				}
			}
		}
		string text2 = _settings?.DecreaseBrightnessHotKey ?? "";
		if (!string.IsNullOrWhiteSpace(text2))
		{
			AppSettings? settings3 = _settings;
			if ((settings3 == null || settings3.DecreaseBrightnessHotKeyEnabled) && !HotKeysSuspended)
			{
				AppSettings? settings4 = _settings;
				if (settings4 == null || settings4.AllHotKeysEnabled)
				{
					_hotKeyRegistration["DecBrightness"] = hotKeyService.TryRegister(text2, delegate
					{
						if (!_fullscreenPaused && !_disableActive)
						{
							if (_popup != null && _popup.IsShown)
							{
								_popup.AdjustByWheel(-1);
							}
							else
							{
								_gamma?.AdjustBrightness(0f - (_gamma?.StepSize ?? 0.05f));
								OnManualAdjustment();
								if (_gamma != null)
								{
									_popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
								}
								ShowOverlayForDisplays();
								_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
								this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
								SaveSettings();
							}
						}
					}, out error);
				}
			}
		}
		string text3 = _settings?.PowerOffHotKey ?? "";
		if (!string.IsNullOrWhiteSpace(text3))
		{
			AppSettings? settings5 = _settings;
			if ((settings5 == null || settings5.PowerOffHotKeyEnabled) && !HotKeysSuspended)
			{
				AppSettings? settings6 = _settings;
				if (settings6 == null || settings6.AllHotKeysEnabled)
				{
					_hotKeyRegistration["PowerOff"] = hotKeyService.TryRegister(text3, delegate
					{
						NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, 274u, new IntPtr(61808), new IntPtr(2));
					}, out error);
				}
			}
		}
		string text4 = _settings?.IncreaseTemperatureHotKey ?? "";
		if (!string.IsNullOrWhiteSpace(text4))
		{
			AppSettings? settings7 = _settings;
			if (settings7 == null || settings7.IncreaseTemperatureHotKeyEnabled)
			{
				AppSettings? settings8 = _settings;
				if ((settings8 == null || settings8.ColorTemperatureEnabled) && !HotKeysSuspended)
				{
					AppSettings? settings9 = _settings;
					if (settings9 == null || settings9.AllHotKeysEnabled)
					{
						_hotKeyRegistration["IncTemperature"] = hotKeyService.TryRegister(text4, delegate
						{
							if (!_fullscreenPaused && !_disableActive)
							{
								AppSettings? settings14 = _settings;
								if (settings14 != null && settings14.ColorTemperatureEnabled)
								{
									if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
									{
										_popup.AdjustByWheel(1);
									}
									else
									{
										_gamma?.AdjustTemperature(_gamma?.TemperatureStepSize ?? 100f);
										OnManualAdjustment();
										if (_gamma != null)
										{
											_popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
										}
										_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
										SaveSettings();
									}
									this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
								}
							}
						}, out error);
					}
				}
			}
		}
		string text5 = _settings?.DecreaseTemperatureHotKey ?? "";
		if (string.IsNullOrWhiteSpace(text5))
		{
			return;
		}
		AppSettings? settings10 = _settings;
		if (settings10 != null && !settings10.DecreaseTemperatureHotKeyEnabled)
		{
			return;
		}
		AppSettings? settings11 = _settings;
		if ((settings11 != null && !settings11.ColorTemperatureEnabled) || HotKeysSuspended)
		{
			return;
		}
		AppSettings? settings12 = _settings;
		if (settings12 != null && !settings12.AllHotKeysEnabled)
		{
			return;
		}
		_hotKeyRegistration["DecTemperature"] = hotKeyService.TryRegister(text5, delegate
		{
			if (!_fullscreenPaused && !_disableActive)
			{
				AppSettings? settings13 = _settings;
				if (settings13 != null && settings13.ColorTemperatureEnabled)
				{
					if (_popup != null && _popup.IsShown && _popup.IsTemperatureMode)
					{
						_popup.AdjustByWheel(-1);
					}
					else
					{
						_gamma?.AdjustTemperature(0f - (_gamma?.TemperatureStepSize ?? 100f));
						OnManualAdjustment();
						if (_gamma != null)
						{
							_popup?.SyncFromGamma(_gamma.CurrentBrightness, _gamma.CurrentTemperature);
						}
						_trayIcon?.UpdateTooltip(_gamma?.CurrentBrightness ?? 1f, _gamma?.CurrentTemperature ?? 6600f, _settings?.ColorTemperatureEnabled ?? false);
						SaveSettings();
					}
					this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
				}
			}
		}, out error);
	}

	private void OnManualAdjustment()
	{
		if (_settings != null && _solarScheduler != null && _solarScheduler.IsRunning)
		{
			_solarScheduler.Stop();
			_settings.SolarManuallyOverridden = true;
			SettingsManager.Save(_settings);
		}
	}

	private void SaveSettings()
	{
		if (_settings == null || _gamma == null)
		{
			return;
		}
		_settings.LastBrightness = _gamma.CurrentBrightness;
		if (_settings.ColorTemperatureEnabled)
		{
			_settings.LastTemperature = _gamma.CurrentTemperature;
		}
		if (_settings.PerMonitorEnabled)
		{
			IReadOnlyDictionary<string, DisplayState> allDisplayStates = _gamma.GetAllDisplayStates();
			Dictionary<string, MonitorState> dictionary = new Dictionary<string, MonitorState>(_settings.MonitorStates ?? new Dictionary<string, MonitorState>());
			foreach (KeyValuePair<string, DisplayState> item in allDisplayStates)
			{
				dictionary[item.Key] = new MonitorState
				{
					Enabled = (!dictionary.TryGetValue(item.Key, out var value) || value.Enabled),
					Brightness = item.Value.Brightness,
					Temperature = item.Value.Temperature
				};
			}
			HashSet<string> liveIds = allDisplayStates.Keys.ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string item2 in dictionary.Keys.Where((string k) => !liveIds.Contains(k)).ToList())
			{
				dictionary.Remove(item2);
			}
			_settings.MonitorStates = dictionary;
		}
		SettingsManager.Save(_settings);
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
		_unifyTimer?.Stop();
		_unifyTimer?.Dispose();
		_unifyTimer = null;
		_disableTimer?.Stop();
		_disableTimer?.Dispose();
		_disableTimer = null;
		_disableAnimTimer?.Stop();
		_disableAnimTimer?.Dispose();
		_disableAnimTimer = null;
		_adjustFlushTimer?.Stop();
		_adjustFlushTimer?.Dispose();
		_adjustFlushTimer = null;
	}
}
