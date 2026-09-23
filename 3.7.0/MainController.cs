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
	private bool _fullscreenAnimRampUsed;

	private bool _disableActive;

	/// <summary>应用白名单暂停源（设计稿 §16）——与 L0 禁用 / L1 全屏并列的第三个暂停源。</summary>
	private bool _whitelistPaused;

	/// <summary>白名单评估定时器（每 1 s 一次，见 <see cref="EvaluateWhitelistPause"/>）。</summary>
	private Timer? _whitelistTimer;

	/// <summary>白名单暂停前记录的应用值（恢复时平滑回到它）。</summary>
	/// <summary>全局暂停过渡（StartFullscreenTransition）的上一帧进度 —— 用于限制单帧推进量。</summary>
	private double _fullscreenAnimLastProgress;
	private float _whitelistBrightnessBefore = 1f;
	private float _whitelistTemperatureBefore = 6600f;
	/// <summary>逐屏白名单（2026-09-16 定稿）：当前"被白名单暂停"的屏集合（EDID 键，纯运行时）。</summary>
	private HashSet<string> _wlPausedScreens = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>逐屏过渡动画：每屏一份 from→to。仅动画期间非空。</summary>
	private readonly Dictionary<string, WlScreenAnim> _wlAnims = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>本次逐屏过渡是否为"全局暂停（白名单）进入"⇒ 收尾时全屏回原生。</summary>
	/// <summary>本次逐屏过渡是否为"全局暂停退出"⇒ 收尾时才解除 _paused 并重放设定值。</summary>
	private Timer? _wlAnimTimer;

	// ==================================================================
	//  全屏「按显示器生效」（2026-09-19 定稿）
	//  —— 只暂停「全屏窗口所在的那块屏」，其余屏保持设定值。
	//     修的是既有缺陷：副屏全屏 ⇒ 主屏色彩也被抹回原生（RULES_DETAIL §Z9.1）。
	// ==================================================================

	/// <summary>当前「被全屏暂停」的屏集合（EDID 键，**纯运行时**，绝不落盘）。
	/// 与 <see cref="_wlPausedScreens"/> 互斥 —— 只要本集合非空，白名单整体让位
	/// （见 <see cref="EvaluateWhitelistPause"/>），故同一时刻只有一个功能持有逐屏暂停。</summary>
	private readonly HashSet<string> _fsPausedScreens = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>全屏逐屏过渡动画：每屏一份 from→to。与白名单的 <see cref="_wlAnims"/> **各自独立** ——
	/// 虽然两者互斥、不会同时活跃，但"让位"路径会调 <see cref="StopWhitelistAnim"/>()，
	/// 若共用一套字典就会被连带清空（动画卡在中间帧）。</summary>
	private readonly Dictionary<string, WlScreenAnim> _fsAnims = new(StringComparer.OrdinalIgnoreCase);

	private Timer? _fsAnimTimer;

	/// <summary>逐屏过渡动画的每屏状态。</summary>
	private sealed class WlScreenAnim
	{
		public float FromB, FromT, ToB, ToT;
		public bool ToNative;
		public DateTime Start;
	}

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
	private bool _disableAnimRampUsed;

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
	/// <summary>F1（2026-09-17）：累计"跳过同值过渡"的次数（只读探针 Biz_SmoothSkips）。</summary>
	private int _smoothSkipCount;
	/// <summary>F2：本次过渡是否走"ramp 空间插值"（false ⇒ 回退到旧的参数插值）。</summary>
	private bool _rampAnimUsed;
	/// <summary>F2/F3：本次过渡的实际帧数（用于日志验证"帧数是否与标称对齐"）。</summary>
	private int _smoothFrameCount;
	/// <summary>F1：跳过同值过渡的累计次数 —— 对外可观测，便于自动化断言"真的短路了"。</summary>
	public int SmoothSkipCount => _smoothSkipCount;

	private DateTime _smoothStartTime;

	private float _smoothStartBright;

	private float _smoothTargetBright;

	private float _smoothStartTemp;

	private float _smoothTargetTemp;

	/// <summary>
	/// `_smoothTargetBright/Temp` 是否已被"最后一次请求"填过（见 F5 修复说明）。
	/// 未初始化时两轴字段是 0f，不能当作目标使用 ⇒ 回退 `_gamma.Current*`。
	/// </summary>
	private bool _smoothTargetValid;

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
		// L2（2026-09-16 用户拍板）：登记"当前正在运行的这一份"并做一次 GC。
		// **必须排在 IntegrityChecker.RunCheck() 之前** —— 自启仲裁靠"谁的 LastRunUtc
		// 更新"分胜负，先把自己的时间刷新到"现在"，才会按用户定的
		// 「运行时间为主、版本号为辅」把自启判给最后用过的那一份。
		KnownInstalls.RegisterSelf();
		IntegrityChecker.RunCheck();
		_settings = SettingsManager.Load();
		_settings.TransitionMinutes = Math.Clamp(_settings.TransitionMinutes, 0, 60);
		Localization.Setting = _settings.Language;
		Localization.Current = Localization.Resolve(_settings.Language).Effective;
		ThemeManager.Apply(_settings.Theme);
		ThemeManager.ApplyPopupTheme(_settings.PopupTheme);
		_trayIcon = new TrayIconManager();
		// P0：托盘注册现在是「可失败 + 后台退避重试」的异步过程，图标可能在
		// Initialize 之后才就绪。必须**先订阅再 Initialize** —— Initialize 内部若首次
		// NIM_ADD 即成功会同步触发 OnTrayIconReady，订阅晚一步就漏掉那次事件。
		// 此处补一次 tooltip：首试失败时下方那次 UpdateTooltip 打在未注册的图标上，
		// 若不在就绪时补，图标将永远没有 tooltip。
		// （P1 的托盘可见性写入阶梯同样挂在本事件上，见设计稿 §6.5。）
		_trayIcon.OnTrayIconReady += (_, _) => UpdateTrayTooltip();
		// P1/P2″：图标就绪后做一次幂等的常驻校验（含 TaskbarCreated 重注册之后）
		_trayIcon.OnTrayIconReady += (_, _) => ApplyTrayVisibilityPolicy();
		_trayIcon.Initialize();
		_trayIcon.OnUninstallRequested += OnUninstallRequested;
		_trayIcon.OnSettingsRequested += OnSettingsRequested;
		_trayIcon.OnLeftClickRequested += OnLeftClickRequested;
		_trayIcon.OnContextMenuOpening += OnContextMenuOpening;
		_trayIcon.OnTrayDpiChanged += OnTrayDpiChanged;
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
		// ------------------------------------------------------------------
		// ⛔ U02 修复（2026-09-21，实测 + 日志双证）：`ProbeRampFloorIfNeeded()` 原先
		// **只写在 else（非逐屏）分支里** ⇒ 逐屏模式下启动**永不采纳**配置里的
		// `RampFloorNeeded`（`GammaController.RampFloorNeeded` 是 static、默认 false）
		// ⇒ 配置落盘 True 但运行时恒 false ⇒ 严格驱动机器（本机 RTX 4070）
		// **每次重启后下限保护都失效**，表现为每次开机都要重新「被动学习」一次。
		// ⇒ 上移到 if/else **之前**：两个分支都要读配置。**位置必须仍在第一次写 ramp
		// 之前** —— if 分支的 `ApplyUnifiedTarget`（下方）就会写 ramp。
		ProbeRampFloorIfNeeded();
		if (_settings.PerMonitorEnabled)
		{
			// 播种统一种子：新屏/无记忆屏以最后全局值为起点（沿用旧启动语义）。
			// 该种子同时是"进入独立控制的统一基准"：之后若关闭独立控制，所有屏
			// 平滑回到这个基准（等价于"开启独立控制前的亮度/色温"）。
			float entryB = _settings.LastBrightness;
			float entryT = _settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f;
			_perMonitorEntryBrightness = entryB;
			_perMonitorEntryTemperature = entryT;
			_gamma.ApplyUnifiedTarget(entryB, entryT);
			_gamma.PerMonitorEnabled = true;
			_gamma.ReconcileDisplayStates();
			RestoreSavedDisplayStates();
		}
		else
		{
			_gamma.PerMonitorEnabled = false;
			// （`ProbeRampFloorIfNeeded()` 已上移到 if/else 之前 —— U02 修复 2026-09-21）
		ApplyStartupGamma();
		}
		_overlay = new BrightnessOverlay();
		_overlay.OpacityPercent = _settings.OverlayOpacityPercent;   // OSD 用户透明度（构造中 _settings 已就绪）
				// 2026-09-16：OSD 多行滑轨的冻结锁（与弹窗 IsDisableActive 同一判据）。
				_overlay.IsDisableActive = () => IsUiPaused();
		_overlay.OnBrightnessChanged += OnOverlayBrightnessChanged;
		_overlay.OnRowBrightnessChanged += OnOverlayRowBrightnessChanged;
		_mouseHook = new GlobalMouseHook(_trayIcon, _gamma, _overlay)
		{
			IsInvertedScroll = () => _settings?.InvertScroll ?? false,
			IsOverlayEnabled = () => _settings?.ShowOverlay ?? true,
			IsWheelEnabled = () => _settings?.WheelEnabled ?? true,
			IsColorTemperatureEnabled = () => _settings?.ColorTemperatureEnabled ?? false,
			// 2026-09-16：补 _whitelistPaused —— 此前白名单暂停时托盘滚轮仍能调，OSD 照弹。
			IsPaused = () => IsUiPaused(),
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
		// 暂停/禁用期间锁定弹窗交互，语义与"全局禁用""全屏暂停"一致。
		// 2026-09-15：补 _whitelistPaused —— 之前白名单暂停时弹窗不受锁，
		// 数值与可交互状态都和实际不符（用户反馈）。
		_popup.IsDisableActive = () => _disableActive || _fullscreenPaused || _whitelistPaused;
		_popup.StepSize = _settings.StepSize;
		_popup.TemperatureStepSize = _settings.TemperatureStepSize;
		_popup.MinTemperature = _settings.MinTemperature;
		_popup.MaxTemperature = _settings.MaxTemperature;
		_popup.TemperatureEnabled = _settings.ColorTemperatureEnabled;
		// 2026-09-19（F18 同款修正，**启动初始化处**）：弹窗必须按"独立控制的**实际生效状态**"渲染。
		// 单屏时设置页把「独立控制」**显示为关并锁定**（只改 UI、不改配置值 —— 保留用户偏好），
		// 若这里仍用 `settings.PerMonitorEnabled`，**启动那一刻**弹窗就会按逐屏模式渲染
		// ⇒ 用户截图实证：设置页里 G5c II 是「关」，左键弹窗却仍显示「G5c II 85%」（带屏幕名的逐屏行）。
		// 单屏时"独立控制"与统一控制等价 ⇒ 统一按统一模式渲染。
		// （运行时更新处已有同款判断；上一轮只改了那里、漏了这里。）
		_popup.PerMonitorEnabled = _settings.PerMonitorEnabled && _gamma.GetDisplayIds().Count >= 2;
		_popup.OnDisplayRowChanged += OnPopupDisplayRowChanged;
		_popup.OnPerMonitorWheel += OnPopupPerMonitorWheel;
		_popup.OnBrightnessChanged += OnPopupBrightnessChanged;
		_popup.OnTemperatureChanged += OnPopupTemperatureChanged;
		_popup.OnShownChanged += OnPopupShownChanged;
		_mouseHook.SetPopup(_popup);
		UpdateTrayTooltip();
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
		// F4a（2026-09-17）：统一化动画（关闭独立控制的 1.2s 逐屏过渡）期间，
		// 禁止 Solar 的 2 秒 tick 写屏 —— 否则它会把动画的一帧抹平，
		// 屏幕出现 1 帧下探（用户实测 Δ26530/Δ27170）。动画结束后下次 tick 照常对齐。
		// F13（2026-09-19）：停用生效时 Solar **也绝不写屏**（第二道保险）——
		// 即使运行中 Solar 仍在跑（例如停用期间用户手动拨了「时间调整」总开关），
		// 也不能绕过「功能停用」把屏幕写回去。停用解除后 `OnDisableResumed()` →
		// `ApplySolarScheduler()` 会立即 Tick 对齐，最终态不变。
		_solarScheduler.SuppressWrites = () => _unifyActive || _disableActive;
		_solarScheduler.BrightnessChanged += delegate(object? _, float v)
		{
			SyncPopupFromUi();   // 方案 B：Solar 步进期间若处于暂停，界面显示 100%/6600K
			this.BrightnessChanged?.Invoke(this, v);
		};
		_solarScheduler.TemperatureChanged += delegate(object? _, float v)
		{
			SyncPopupFromUi();   // 方案 B：同上
			this.TemperatureChanged?.Invoke(this, v);
		};
		// ------------------------------------------------------------------
		// F13（2026-09-19）：修「功能停用 + 时间调整同时生效 ⇒ 重启闪一下」。
		//
		// 根因 = **顺序**：原先 `_solarScheduler.Start()`（内部会**立即 Tick 一次**）排在
		// `RestoreDisableState()` **之前** ⇒ Solar 在 `_paused` 与 `_disableActive`
		// 都还是 false 的窗口里**真的把屏幕写成太阳目标**（如夜间 85%/3900K），
		// 随后 RestoreDisableState 才置 `_disableActive` + `SetPaused(true)` +
		// `ApplyPausedState()` ⇒ 各屏 `ResetGamma()` 回原生 100%/6600K
		// ⇒ 两次互相矛盾的瞬时写 = 一次**可见闪屏**（幅度 = 太阳目标与原生之差）。
		//
		// 修法（两步，互相独立）：
		//   ① 把 `RestoreDisableState()` 提到 Solar 创建**之前**，让停用状态**先确立**；
		//   ② 停用生效时**压根不启动** Solar 调度 —— 与 `ApplyDisable()` 里
		//      `_solarScheduler?.Stop()` 的既有语义一致。停用解除后由
		//      `OnDisableResumed()` → `ApplySolarScheduler()` 重新拉起并立即 Tick，
		//      **最终态不变**。
		//   （+ 第三道保险：`SuppressWrites` 已加 `_disableActive`，见上。）
		// ⚠ `RestoreDisableState()` 内部的 `_solarScheduler?.Stop()` 此刻字段尚为 null，
		//   用 `?.` 安全。
		// ------------------------------------------------------------------
		RestoreDisableState();
		if (_settings!.SolarAdjustEnabled && !_settings!.SolarManuallyOverridden && !_disableActive)
		{
			_solarScheduler.Start();
		}
		if (showSettingsOnStart)
		{
			Application.Idle += OnIdleShowSettings;
		}
		// P5：启动自动化桥（命名管道 + L1 动作注册表）。放在 Initialize 末尾 ——
		// 保证命令到达时各组件已装配完毕；锚点用托盘消息窗（归属 UI 线程且已有句柄）。
		AutomationBridge.Instance.Start(_trayIcon.UiMarshalAnchor);
		// P5/B1：预建托盘菜单窗体（隐藏）—— 让 Tray_* 动作在启动后立即可用，
		// 不必等用户先右键一次托盘图标。再补两个"打开/关闭菜单"的宿主动作。
		_trayIcon.EnsureMenuForm();
		AutomationBridge.Register(new AutomationAction("Tray_OpenMenu", "item",
			Click: () => _trayIcon.ShowMenuForAutomation()));
		AutomationBridge.Register(new AutomationAction("Tray_CloseMenu", "item",
			Click: () => _trayIcon.HideMenuForAutomation()));
		RegisterBusinessActions();
		// P5（2026-09-14）：把**左键弹窗**与 **OSD 浮窗** 的滑轨也接入自动化，
		// 补齐"滑轨无死角"（此前只有设置页滑轨有 ID，两个自绘浮窗的滑轨只能靠坐标点击）。
		// 注册的是控件自身的值入口，与用户拖动同一条代码；需在 UI 线程（此处即是）。
		_popup.RegisterAutomation();
		_overlay.RegisterAutomation();

		// 应用白名单：每 1 s 评估一次"白名单进程是否存在可见顶层窗口"（设计稿 §16.5）。
		// 与全屏暂停并列的第三个暂停源；只在 L0/L1 未接管时自行 SetPaused（见 EvaluateWhitelistPause）。
		_whitelistTimer = new Timer { Interval = 1000 };
		// 2026-09-19：每 tick 先清理全屏逐屏里"已离线"的屏（否则它们会一直占着
		// CountControlledDisplays() 的分母，把 allPaused 判错）。
		_whitelistTimer.Tick += (_, _) => { PruneFsPausedScreens(); EvaluateWhitelistPause(); PersistRampFloorIfLearned(); };
		_whitelistTimer.Start();
		// ------------------------------------------------------------------
		// F25（2026-09-20）：**启动时立刻评估一次白名单**，不等那 1 秒的首次 tick。
		//
		// 用户实测（03:02）：「开启白名单后 → 02:55 闪；03:01 重启应用⇒闪；03:02 重启⇒闪」
		// 与 02:42 日志完全吻合：
		//   [02:42:14.832] 写原生  → [02:42:14.950] push 85%（设定值）
		//   → [02:42:15.961] whitelist=True → [02:42:15.964] 过渡回原生
		// ⇒ **白名单评估比"恢复设定值"晚了一拍**：先亮 85%、约 1 秒后才被拽回原生
		//   ⇒ 用户观感就是"闪一下"。立即评估可把这个窗口从 ~1000ms 压到 ~0。
		// ⚠️ 本修法**只用白名单自己的判据**，不引入第二套预判逻辑
		//   （F22 那种"两套判据算同一件事"的做法已被证明会打架）。
		// ------------------------------------------------------------------
		EvaluateWhitelistPause();

		// 启动期预热扫描（用户 2026-09-15 建议）：总开关已开时，应用一启动就在**后台**扫一次
		// 候选并缓存。这样设置窗口打开白名单页时立刻就是完整列表，不会出现
		// "显示正在扫描但一直不加载、必须手动点刷新"（用户实测截图）。
		WarmUpWhitelistScan();
	}

	/// <summary>白名单候选缓存：启动期预热扫描 / 设置页刷新后写入；设置页构建时直接取用。</summary>
	public List<WhitelistCandidate>? CachedWhitelistCandidates { get; private set; }

	public void SetCachedWhitelistCandidates(List<WhitelistCandidate>? list)
		=> CachedWhitelistCandidates = list;

	/// <summary>启动期预热扫描（仅当白名单总开关为开）。后台线程，不阻塞启动。</summary>
	public void WarmUpWhitelistScan()
	{
		if (!GetAppWhitelistEnabled()) return;
		System.Threading.Tasks.Task.Run(() =>
		{
			try
			{
				List<WhitelistCandidate> list = AppWhitelistService.CollectCandidates();
				CachedWhitelistCandidates = list;
				OpLog.Log($"[whitelist] warmup scan -> {list.Count} candidates");
			}
			catch (Exception ex)
			{
				OpLog.LogEx("[whitelist] warmup scan failed", ex);
			}
		});
	}

	/// <summary>P5：弹窗当前是否显示（自动化回读用，app.ping 会带上）。</summary>
	public bool IsPopupShown => _popup != null && _popup.IsShown;

	/// <summary>
	/// P5/B2：注册业务动作层 —— 让脚本<b>不经过任何 UI 控件</b>也能驱动核心功能。
	///
	/// 与 L1 控件动作（<c>Gen_* / Bri_* …</c>）的分工：
	///   * L1 测「点这个开关有没有反应」（UI 层）；
	///   * B2 测「功能本身是否正确」（业务层）—— 直接调用与滚轮 / 弹窗 / 热键
	///     <b>完全相同</b>的业务方法，是端到端回归的基石。
	///
	/// 亮度 / 色温走 <c>GammaController.SetBrightness / SetTemperature</c>，与滚轮调节
	/// 同一条代码（含平滑、逐屏、Enabled 冻结语义）。
	/// 「弹窗显示」复用 <see cref="OnLeftClickRequested"/> —— 它内部已处理托盘图标矩形
	/// 取不到时的降级（回退到屏幕底部中央），自动化不必自己算坐标。
	/// </summary>
	private void RegisterBusinessActions()
	{
		AutomationBridge.Register(new AutomationAction("Biz_Brightness", "value",
			Get: () => Math.Round((_gamma?.CurrentBrightness ?? 0f) * 100f).ToString(),
			Set: v =>
			{
				// P2 修复（2026-09-14 逻辑审查）：改走真实用户路径（与弹窗/挡位/热键同一条
				// 代码：含 OnManualAdjustment、平滑过渡、Solar 覆写语义、落盘与事件广播）。
				// 此前直接调 _gamma.SetBrightness 且不落盘 —— LastBrightness 不更新（重启回退旧值），
				// 且在 Solar 运行中会被下一 tick 覆盖、不算"手动接管"。
				if (int.TryParse(v, out int pct))
				{
					SetBrightnessLevel(Math.Clamp(pct, 0, 100) / 100f);
				}
			}));

		AutomationBridge.Register(new AutomationAction("Biz_Temperature", "value",
			Get: () => Math.Round(_gamma?.CurrentTemperature ?? 0f).ToString(),
			Set: v =>
			{
				// 改走**真实用户路径** SetColorTemperature（与弹窗滑块/热键同一条代码：
				// 含 OnManualAdjustment、平滑过渡、Solar 覆写语义），写后立即落盘。
				// 此前直接调 _gamma.SetTemperature 且不落盘 —— LastTemperature 不更新，
				// "重新开启色温时恢复存储值"就没有可恢复的值（2026-09-14 排查发现）。
				if (int.TryParse(v, out int k) && _settings?.ColorTemperatureEnabled == true)
				{
					SetColorTemperature(Math.Clamp(k, 3300, 10000));
					SaveSettings();
				}
			}));

		AutomationBridge.Register(new AutomationAction("Biz_DisplayIds", "value",
			Get: () => string.Join("|", GetDisplayIds())));

		AutomationBridge.Register(new AutomationAction("Biz_MonitorCount", "value",
			Get: () => GetDisplayIds().Count.ToString()));

		// 弹窗 / OSD：复用生产路径。弹窗走 OnLeftClickRequested（内含托盘矩形取不到
		// 时的降级），OSD 走 ShowOverlayForDisplays（多屏行 / 单行自适应）。
		AutomationBridge.Register(new AutomationAction("Popup_Toggle", "item",
			Click: () => OnLeftClickRequested(this, EventArgs.Empty)));
		AutomationBridge.Register(new AutomationAction("Popup_Hide", "item",
			Click: () => { _popup?.Dismiss(); _overlay?.Hide(); }));
		AutomationBridge.Register(new AutomationAction("Osd_Flash", "item",
			Click: () => ShowOverlayForDisplays()));

		// P1：托盘常驻。Get 返回的是**用户偏好**（单一事实源 = settings.json），
		// 注册表里的真实状态用 TrayVisibilityService.ReadPromoted()（测试时直接读注册表更可靠）。
		AutomationBridge.Register(new AutomationAction("Biz_TrayPromoted", "value",
			Get: () => GetKeepTrayIconVisible() ? "true" : "false",
			Set: v => SetKeepTrayIconVisible(v == "true" || v == "1" || v == "on"),
			Click: () => SetKeepTrayIconVisible(!GetKeepTrayIconVisible())));

		// 应用白名单（设计稿 §16）：候选集 / 白名单内容 / 总开关 / 暂停状态。
		// 用途：B1/B2 阶段在**没有 UI** 的情况下也能完整验证（用户要求"接口无死角"）。
		AutomationBridge.Register(new AutomationAction("Biz_WhitelistCandidates", "value",
			Get: () => JsonSerializer.Serialize(GetWhitelistCandidates())));
		AutomationBridge.Register(new AutomationAction("Biz_Whitelist", "value",
			Get: () => string.Join(";", GetAppWhitelist()),
			Set: v => SetAppWhitelist(string.IsNullOrWhiteSpace(v)
				? Array.Empty<string>()
				: v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))));
		AutomationBridge.Register(new AutomationAction("Biz_WhitelistEnabled", "value",
			Get: () => GetAppWhitelistEnabled() ? "true" : "false",
			Set: v => SetAppWhitelistEnabled(v == "true" || v == "1" || v == "on")));
		// 多屏诊断：逐屏 state vs 实际 ramp（2026-09-16，用于检测看不见的第二屏）。
				AutomationBridge.Register(new AutomationAction("Biz_MonitorActual", "value",
					Get: () => string.Join("\n", GetMonitorDiagnostics())));

		AutomationBridge.Register(new AutomationAction("Biz_WhitelistPaused", "value",
			Get: () => IsWhitelistPaused() ? "true" : "false"));

		// L2/L3（2026-09-16）：安装登记表 + 自启仲裁结果，供测试断言。
		// 逐条格式：Path|Version|LastRun|Kind（共 N 行，按最后运行时间倒序）
		AutomationBridge.Register(new AutomationAction("Biz_KnownInstalls", "value",
			Get: () => KnownInstalls.DescribeForDiagnostics()));
		// 只读：当前 Run 键指向哪份 + 仲裁会怎么判（"keep"=不动 / "switch"=应抢回）
		AutomationBridge.Register(new AutomationAction("Biz_StartupTarget", "value",
			Get: () => KnownInstalls.DescribeArbitration()));
	// 逐屏白名单（2026-09-16）探针：①用户开关 ②门控是否满足 ③当前被暂停的屏
	AutomationBridge.Register(new AutomationAction("Biz_WhitelistPerMonitor", "value",
		Get: () => GetAppWhitelistPerMonitor() ? "true" : "false"));
	AutomationBridge.Register(new AutomationAction("Biz_WhitelistPerMonitorGateOk", "value",
		Get: () => IsWhitelistPerMonitorGateOk() ? "true" : "false"));
	// 驱动下限保护（2026-09-17）：当前生效值 + 强制重新探测（换驱动后可用）
	AutomationBridge.Register(new AutomationAction("Biz_RampFloorNeeded", "value",
		Get: () => GammaController.RampFloorNeeded ? "true" : "false"));
	// F1（2026-09-17）：跳过同值过渡的累计次数 —— 用于自动化断言"短路真的生效了"。
	AutomationBridge.Register(new AutomationAction("Biz_SmoothSkips", "value",
		Get: () => SmoothSkipCount.ToString()));
	AutomationBridge.Register(new AutomationAction("Biz_ProbeRampFloor", "action",
		Set: v =>
		{
			bool? acc = _gamma?.ProbeRampFloorLimit() ?? null;
			if (acc.HasValue)
			{
				GammaController.RampFloorNeeded = !acc.Value;
				if (_settings != null)
				{
					_settings.RampFloorNeeded = !acc.Value;
					SettingsManager.Save(_settings);
				}
			}
		}));

	// 多屏独立控制总开关（逐屏白名单的门控依赖它；此前无业务动作可驱动，只能靠 UI 控件）
	AutomationBridge.Register(new AutomationAction("Biz_PerMonitor", "value",
		Get: () => GetPerMonitorEnabled() ? "true" : "false",
		Set: v => SetPerMonitorEnabled(v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))));
	AutomationBridge.Register(new AutomationAction("Biz_WhitelistPausedScreens", "value",
		Get: () => string.Join("|", GetWhitelistPausedScreens())));
	// 业务动作：开关逐屏白名单（测试用；等价于设置页拨开关）
	AutomationBridge.Register(new AutomationAction("Biz_SetWhitelistPerMonitor", "action",
		Set: v => SetAppWhitelistPerMonitor(v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))));

	// 全屏「按显示器生效」（2026-09-19 定稿）探针：①用户开关 ②门控是否满足 ③当前被全屏暂停的屏
	// ④程序化切换（**不受 UI 门控限制** —— 测试需要构造门控不满足的场景）
	AutomationBridge.Register(new AutomationAction("Biz_FullscreenPerMonitor", "value",
		Get: () => GetFullscreenPerMonitor() ? "true" : "false"));
	AutomationBridge.Register(new AutomationAction("Biz_FullscreenPerMonitorGateOk", "value",
		Get: () => IsFullscreenPerMonitorGateOk() ? "true" : "false"));
	AutomationBridge.Register(new AutomationAction("Biz_FullscreenPausedScreens", "value",
		Get: () => string.Join("|", GetFullscreenPausedScreens())));

        // ---- P5 只读探针（2026-09-21 用户要求）：三类「外部读不到」的 UI 状态（只读） ----
        AutomationBridge.Register(new AutomationAction("Biz_Tip_FsPerMonitor", "value",
            Get: () => SettingsForm.TipOf("Gen_FsPerMonitorToggle")));
        AutomationBridge.Register(new AutomationAction("Biz_Tip_WhitelistPerMonitor", "value",
            Get: () => SettingsForm.TipOf("Wl_PerMonitorToggle")));
        AutomationBridge.Register(new AutomationAction("Biz_Tip_MonitorsPerMonitor", "value",
            Get: () => SettingsForm.TipOf("Mon_PerMonitorToggle")));
        AutomationBridge.Register(new AutomationAction("Biz_Tip_SolarSlider", "value",
            Get: () => SettingsForm.TipOf("Sol_DayTempSlider")));
        AutomationBridge.Register(new AutomationAction("Biz_Tip_TrayVisible", "value",
            Get: () => SettingsForm.TipOf("Gen_TrayVisibleToggle")));
        AutomationBridge.Register(new AutomationAction("Biz_ScrollMetrics_General", "value",
            Get: () => SettingsForm.ScrollMetrics("general")));
        AutomationBridge.Register(new AutomationAction("Biz_ScrollMetrics_Whitelist", "value",
            Get: () => SettingsForm.ScrollMetrics("whitelist")));
        AutomationBridge.Register(new AutomationAction("Biz_TrayTip", "value",
            Get: () => _trayIcon?.CurrentTipText ?? ""));
	AutomationBridge.Register(new AutomationAction("Biz_SetFullscreenPerMonitor", "action",
		Set: v => SetFullscreenPerMonitor(v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))));

		// 方案 B（2026-09-16）：界面显示值 + 功能停用（L0）开关。
		// 用途：无 UI 也能断言"暂停期间界面显示 100%/6600K、解除后回到设定值"
		// ——Biz_Brightness 读的是 gamma 内部状态（暂停期间为**保留值**），
		// 与界面显示值在暂停期间必然不同，两个都暴露才能对比验证。
		AutomationBridge.Register(new AutomationAction("Biz_UiBrightness", "value",
			Get: () => Math.Round(UiBrightness * 100f).ToString()));
		AutomationBridge.Register(new AutomationAction("Biz_UiTemperature", "value",
			Get: () => Math.Round(UiTemperature).ToString()));
		AutomationBridge.Register(new AutomationAction("Biz_UiPaused", "value",
			Get: () => IsUiPaused() ? "true" : "false"));
		// 托盘 tooltip 最近一次落进去的数值（"pct/K"）。托盘 tip 文本对外部不可读，
		// 只能靠这个回读来断言 tooltip 有没有跟上界面显示值（2026-09-16 用户反馈项）。
		AutomationBridge.Register(new AutomationAction("Biz_TrayTooltip", "value",
			Get: () => _lastTooltipPct + "/" + _lastTooltipK));
		// Set："" / "perm" / "forever" → 永久停用；"0" / "off" / "false" → 解除；其余按分钟数。
		AutomationBridge.Register(new AutomationAction("Biz_Disable", "value",
			Get: () => IsDisableActive() ? "true" : "false",
			Set: v =>
			{
				string s = (v ?? "").Trim().ToLowerInvariant();
				if (s is "perm" or "forever" or "on" or "true") SetDisable(null);
				else if (s is "0" or "off" or "false" or "none") SetDisable(TimeSpan.Zero);
				else if (double.TryParse(s, out double min))
					SetDisable(min <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(min));
			}));
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
		// ⚠️ 兜底值必须与 `AppSettings.PauseInFullscreenEnabled` 的构造默认一致（false）。
		//    2026-09-18 前这里是 `true` ⇒ 设置页/重置的取值与"新装默认"不符。
		return _settings?.PauseInFullscreenEnabled ?? false;
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
			else if (_fsPausedScreens.Count > 0)
			{
				// 2026-09-19 逐屏：先按逐屏语义收干净（各屏平滑写回自己的设定值）。
				ClearFullscreenPerMonitorPause(reapply: true);
			}
			else if (_fullscreenPaused)
			{
				StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
			}
		}
	}

	/// <summary>
	/// 被动学习的结果落盘：`GammaController` 在"写屏被驱动拒绝"时会自动启用驱动下限保护，
	/// 这里把它持久化（1s 粒度足够；只在真的翻转时写一次盘）。
	/// </summary>
	private void PersistRampFloorIfLearned()
	{
		if (_settings == null) return;
		if (!GammaController.RampFloorLearned) return;
		GammaController.RampFloorLearned = false;
		_settings.RampFloorNeeded = GammaController.RampFloorNeeded;
		SettingsManager.Save(_settings);
		OpLog.Log($"[gamma] 驱动下限保护已落盘：RampFloorNeeded={GammaController.RampFloorNeeded}");
	}

	/// <summary>
	/// 启动时探测该机驱动是否需要「每通道 ramp 峰值 ≥ 32768」下限保护（只探一次并落盘）。
	/// 判别器 = (32768/32768/32000)：本机 RTX 4070 596.36 会整份拒收，笔记本 1650 Ti 591.86 接受。
	/// 探测会短暂闪一下屏（写测试 LUT 后由紧随其后的 ApplyStartupGamma 复位）。
	/// </summary>
	private void ProbeRampFloorIfNeeded()
	{
		if (_settings == null || _gamma == null) return;

		if (_settings.RampFloorNeeded.HasValue)
		{
			GammaController.RampFloorNeeded = _settings.RampFloorNeeded.Value;
			OpLog.Log($"[gamma] 驱动下限保护 = {GammaController.RampFloorNeeded}（读自配置，未重新探测）");
			return;
		}

		// ⚠ 不再主动探测（探测必须写测试 LUT ⇒ 屏幕会闪一下）。
		// 改为**被动学习**：`GammaController` 在"真实写屏被驱动拒绝"时自动启用保护（见 LearnRampFloor）。
		// 未探测过的机器默认「不需要保护」⇒ 宽松驱动（如 1650Ti）立即拿到完整色温；
		// 严格驱动（如 4070）在第一次写入被拒时当场学会，用户只会看到一次"没反应"，随后自动修正。
		OpLog.Log("[gamma] 驱动下限保护：未探测过，默认不启用（等待被动学习）");
	}

	/// <summary>
	/// F24-a（2026-09-22 23:55）：**交接期重申 ramp** —— 由 <c>Program.StartHandoverKeepAlive()</c>
	/// 每 100 ms 调用一次，直到本进程被新进程的 singleton 逻辑 kill（或 3 s 上限自退）。
	///
	/// 为什么是"反复重申"而不是"退出前写一次"：
	///   用户毫秒级 ramp 监控 × 应用日志交叉验证（`_devtools/B_prime实施报告_20260922.md` §12）显示：
	///   · OS 改缩放后把 ramp **复位为原生**（监控 `41.784` 那一下**日志无任何写屏** ⇒ 外部来源）；
	///   · 且 OS 重配会**反复**复位（14 s 内 4 次）⇒ 单次写入极易落在重配进行中被覆盖；
	///   · 新进程要到 `42.330` 才开始写屏 ⇒ 中间 **581 ms 硬停在全亮**。
	///   实测 F23② 的"只写一次"正是因此无效；**必须周期性重申**，任意一次落在重配之后即修正。
	///
	/// 与 <see cref="GammaController.ReapplyAllDisplaysForTopologyChange"/> 同路径：
	/// 尊重全局暂停（写原生）/ 逐屏暂停（写原生）/ 停用（不写），且**禁用驱动下限的被动学习**
	/// —— 此刻 `_displays` 可能含已失效 DC，写失败会被误学成「驱动有下限」并永久落盘。
	///
	/// 尽力而为：本方法在重启交接流程内调用，**绝不能抛异常**（否则会打断交接定时器）。
	/// </summary>
	public void ReapplyGammaBeforeRestart()
	{
		try
		{
			_gamma?.ReapplyAllDisplaysForTopologyChange();
		}
		catch
		{
			// 交接期尽力而为：写屏失败不影响交接（新进程启动后仍会正常恢复）。
		}
	}

	private void ApplyStartupGamma()
	{
		if (_settings == null || _gamma == null)
		{
			return;
		}
		// ------------------------------------------------------------------
		// F12（2026-09-18）：「每次打开软件都闪一下」的修复 —— Solar 活跃时本方法**不写屏**。
		//
		// 背景（用户 09-18 10:18 报，外部 1000+ 帧采样实测 `100%→85%→100%`，
		//       全程 ~40ms、下探 15%）：
		//   启动路径上有**两次互相矛盾的瞬时写**：
		//     ① 本方法原本：`flag = SolarAdjustEnabled && !SolarManuallyOverridden` 为真时
		//        **强制 smooth=false** ⇒ 立即写 `LastBrightness/LastTemperature`
		//        （用户配置 `LastBrightness=0.85` 是夜间遗留值）；
		//     ② 紧随其后（`Initialize` 尾段）`_solarScheduler.Start()` 内部**立即 `Tick()`**，
		//        而 `MaxStep(transition<=0)` 返回 0 ⇒ `MoveToward` 直达 ⇒ 立即写 **Solar 目标**。
		//   ⇒ `Last*` ≠ Solar 当前目标时，屏幕先被写成 85% 再被写回 100% = 一次可见的闪。
		//
		//   实测三组对照（`_devtools/probe_startup_flash.py`）：
		//     · Solar 活跃 + LastB=0.85 ⇒ 反转 15% ⇒ **闪**
		//     · Solar 活跃 + LastB=1.0（= Solar 目标）⇒ 反转 0% ⇒ 不闪
		//     · SolarManuallyOverridden=True ⇒ 只有一次写屏 ⇒ 不闪
		//   ⇒ 触发条件就是「Solar 活跃 且 Last* ≠ Solar 当前目标」，与色温修复无关
		//     （改动前的 `0110` 产物结果逐字相同；该代码与 **3.6.0 逐字相同** ⇒ 长期潜伏，
		//      此前被 `SolarManuallyOverridden=True` 掩盖）。
		//
		// 修法：Solar 活跃时**直接返回**，这一次写屏完全交给 Solar 的首次 Tick
		//      （它写出的最终目标与原来 ② 相同）⇒ 只剩一次写屏，**最终态不变**。
		//      ⚠️ 本方法只有一个调用点（`Initialize` 的非独立控制分支），
		//        Solar **不活跃**时行为逐字不变。
		//
		// ⏭️ 遗留（另行决策，本次不动）：Solar 目标 ≠ 原生时（如夜间 85%/3900K），
		//    首次 Tick 仍是**瞬时**到位（`TransitionMinutes=0` 的既有语义）。
		//    "启动时 Solar 生效也走平滑"属另一项产品决策，需改 `SolarScheduler` 的首次 Tick。
		// ------------------------------------------------------------------
		bool solarActive = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
		if (solarActive)
		{
			OpLog.Log("[startup] Solar 活跃 ⇒ 跳过 ApplyStartupGamma 的 Last* 写屏（F12）" +
			          $"；LastB={_settings.LastBrightness * 100f:0}% LastT={_settings.LastTemperature:0}K" +
			          " 交给 Solar 首次 Tick，避免两次反向瞬时写 = 启动闪");
			return;
		}
		float lastBrightness = _settings.LastBrightness;
		float targetTemp = (_settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f);
		// ------------------------------------------------------------------
		// ⛔ F23 已回退（2026-09-22 23:10，用户实测判定）。**保持与 2148 完全一致。**
		//
		// 曾改为 `smoothBright: false, smoothTemp: false`（一次性写到位），以为能省掉 1.2s。
		// 用户实测外部 ramp 监控（8ms 粒度）**证伪**该假设：
		//   `R255 36699↔65535` 反复横跳，100% 停留 147ms / **1185ms** / 212ms / **1310ms**。
		// 根因：省掉动画后，新进程只能**一次性**写；而 OS 的重配要持续约 1~2s、期间会把 ramp
		//   反复复位为原生 ⇒ 单次写入落地前后仍有**硬停**在 100% 的窗口，观感是
		//   「亮度恢复到 100% 一段时间才跳回设定值」。
		//   而 1200ms 平滑动画 = **40 帧 × 30ms 的持续重写**，本身就是一套"自愈重试"
		//   ⇒ 任意一帧落在 OS 重配结束之后即可修正 ⇒ 观感是"闪 1~2 下"。
		// ⇒ **动画不是开销，是重试覆盖**。用户对照结论：2148（有动画）可接受；去掉动画后不可接受。
		// ⚠️ 同时 F23②（`ReapplyGammaBeforeRestart`）也已回退：它在 `[restart]` 后 2ms 写入，
		//    而那时 OS 重配仍在进行 ⇒ 实测被丢弃（日志显示要到新进程首次写屏才真正落地）
		//    ⇒ 无收益且多一次写屏，一并撤掉以保持与 2148 一致。
		// ⛔ 今后若要再压这段空窗，方向是**延长重试覆盖**（交接期持续重申 ramp），
		//    而不是把它改成一次性写入 —— 见 `_devtools/B_prime实施报告_20260922.md` §11.1。
		// ------------------------------------------------------------------
		StartSmoothTransition(lastBrightness, targetTemp, _settings.BrightnessSmooth, _settings.TemperatureSmooth);
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

	/// <summary>
	/// F5（2026-09-18 修复）：单轴命令需把**另一轴**的目标一并提交给
	/// <see cref="StartSmoothTransition"/>；但"另一轴的当前值"必须区分两种情形 ——
	/// 该轴**正在过渡中**时（<c>_smoothTimer != null</c>），它"已提交的内部状态"
	/// （<c>Current*</c>）**仍是旧值**（要等末帧才提交）⇒ 直接传 <c>Current*</c> 会把它
	/// **打回旧值**。
	///
	/// 实测（`rapid_switch_states` B2 段，3/3 复现）：设亮度 60% 后 300ms 内设色温
	/// ⇒ 亮度被打回 30%；日志铁证：
	/// <code>
	/// set Brightness:60    → [smooth] start 30% -> 60%
	/// set Temperature:5000 → [smooth] start brightness 60% -> 30%（立即）
	/// </code>
	/// ⇒ 过渡在飞时改取**该次过渡的目标**（<c>_smoothTarget*</c>）；
	/// 无过渡在飞时两者等价 ⇒ **行为不变**（这也是"只在动画在飞时才改变"的保证）。
	/// </summary>
	// ------------------------------------------------------------------
	// ⛔ 修复（2026-09-22，用户实测「低亮度下改缩放，重启后亮度/色温回 100%/6600K」）：
	//    原先**无条件**优先返回 `_smoothTarget*`。但「直接写屏」的路径 —— 弹窗滑轨
	//    （OnPopupBrightnessChanged/OnPopupTemperatureChanged）、弹窗逐屏行、OSD 滑轨、
	//    OSD 逐屏、SetDisplayBrightness/Temperature —— 只调 `_gamma.Set*` 而**不维护**
	//    `_smoothTarget*`。而启动时 ApplyStartupGamma 的 F1 短路分支会把
	//    `_smoothTarget*` 置成**配置里的旧值** 且 `_smoothTargetValid = true`
	//    ⇒ 那些路径下它一直是陈旧值 ⇒ `SaveSettings()` 反复把旧值写回
	//    ⇒ **用户设定永不落盘**、重启（含改缩放自动重启）后回退旧值。
	//    实测铁证：13 次重启全部 `[smooth] start: brightness 0%|24% -> 100%`，
	//    且 settings.json 长度恒为 1825 B（若写入 0.24 会多 3 字节）。
	//
	//    判据改为「**仅在过渡在飞时**（`_smoothTimer != null`）才用 `_smoothTarget*`」：
	//      · 过渡中：`Current*` 是插值中的中间值，不能当目标 ⇒ 仍用 `_smoothTarget*`；
	//      · 过渡结束：定时器已先置 null（见 OnSmoothTick 的 `num >= 1.0` 分支），
	//        且 `SetBrightness/SetTemperature/EndRampTransition` 已提交最终值
	//        ⇒ `Current*` 即目标，直接采用。
	//    ⭐ 顺带修掉 F5 注释里「另一轴把本轴一并写回旧值」的跨轴陈旧问题。
	// ------------------------------------------------------------------
	private float PendingOrCurrentBrightness()
		=> _smoothTimer != null && _smoothTargetValid
			? _smoothTargetBright
			: (_gamma?.CurrentBrightness ?? 1f);

	private float PendingOrCurrentTemperature()
		=> _smoothTimer != null && _smoothTargetValid
			? _smoothTargetTemp
			: (_gamma?.CurrentTemperature ?? 6600f);

	private void StartSmoothTransition(float targetBright, float targetTemp, bool smoothBright, bool smoothTemp, Action? done = null)
	{
		if (_gamma == null)
		{
			return;
		}
		// ------------------------------------------------------------------
		// F1（2026-09-17）：同值过渡短路 —— 消灭"净变化为 0 的过渡伪影"。
		// 背景：过渡的起点**不是屏幕上那份 ramp**，而是把屏幕 ramp 反算成参数后再用
		// `BuildGammaRamp` 重建的 ramp；这一对映射**不可往返**（identity 被反算成 6558K，
		// 而 BuildGammaRamp(6558K) 的蓝乘子 ≈0.9866）。于是即便目标与屏幕**完全相同**，
		// 也会出现「首帧硬跳偏暖 1.34%(Δ876) → 20 帧慢走 → 末帧硬跳弹回(Δ640)」，
		// 净变化为 0 却人眼可见 —— 用户 2026-09-17 报的"开关闪 / 启动闪"即此。
		// 判据必须在 **ramp 空间**：参数相等不代表 ramp 相等，ramp 相等才是真的没变化。
		// ⚠️ 仅在**没有进行中的过渡**时短路（`_smoothTimer == null`）：过渡在飞时起点是
		//    飞行中的实际值，"屏幕已等于目标"推不出"正在跑的过渡无意义" ⇒ 保守走原路径。
		// ------------------------------------------------------------------
		if (_smoothTimer == null && _gamma.IsTargetRampOnScreen(targetBright, targetTemp))
		{
			_smoothSkipCount++;
			OpLog.Log($"[smooth] skip: 目标 ramp 已等于屏幕当前 ramp（净变化 0）⇒ 不写屏；" +
					  $"保持 brightness {targetBright * 100f:0}% temperature {targetTemp:0}K" +
					  $"（本次请求 {(smoothBright ? "平滑" : "立即")}/{(smoothTemp ? "平滑" : "立即")}）" +
					  $"；累计跳过 {_smoothSkipCount} 次");
			_gamma.CommitTargetStateWithoutWrite(targetBright, targetTemp);
			// ⛔ F5 修复（v2）：短路分支也必须更新"最后请求的目标" ——
			//    否则 `_smoothTarget*` 会停留在**更早**的目标上，而 PendingOrCurrent*
			//    正以它为依据 ⇒ 会把另一轴写成过期值（引入新错）。
			_smoothTargetBright = targetBright;
			_smoothTargetTemp = targetTemp;
			_smoothTargetValid = true;
			if (smoothBright)
			{
				this.BrightnessChanged?.Invoke(this, _gamma.CurrentBrightness);
			}
			if (smoothTemp)
			{
				this.TemperatureChanged?.Invoke(this, _gamma.CurrentTemperature);
			}
			SaveSettings();
			UpdateTrayTooltip();
			_smoothDone = null;
			done?.Invoke();
			return;
		}
		if (!smoothBright && !smoothTemp)
		{
			_gamma.ApplyUnifiedTarget(targetBright, targetTemp);
			// ⛔ F5 修复（v2）：同 F1 短路分支 —— "两轴都立即"也要更新"最后请求的目标"。
			_smoothTargetBright = targetBright;
			_smoothTargetTemp = targetTemp;
			_smoothTargetValid = true;
			done?.Invoke();
			return;
		}
		// ⛔ 回归修复（2026-09-19，A/B 定案）：下面这两句是**单轴**写屏
		//    （`SetBrightness`/`SetTemperature` 各自 `ApplyGamma` 一次，写出**整份** ramp）。
		//    单轴写屏带的是另一轴的 `_current*`，而过渡在飞时 `_current*` 是**陈旧值**
		//    （只在 `EndRampTransition` 正常走完时提交）⇒ 会把**另一轴也一步写到位**。
		//    实测（§2-D 快速交叉，`permute_test.py --only 2`）：
		//      `[gamma/apply] uni curB=100 curT=3900` ⇒ 屏幕一步 Δ=23281；
		//      色温平滑退化为 `3900K -> 3900K` 空转（起点已被写死）。
		//    旧代码没露出来只因 `targetBright` 恰等于 `_currentBrightness`（同值早退、
		//    不写屏）—— **侥幸，不是设计保障**。
		//    修法：先把"平滑轴"的内部状态对齐到**屏幕实际值**（纯内存、不写屏），
		//    随后那次单轴写屏带上的另一轴即屏幕真值 ⇒ 该轴无变化、不会跳。
		if (smoothTemp && !smoothBright)
		{
			// 立即轴 = 亮度，平滑轴 = 色温
			_gamma.CommitTargetStateWithoutWrite(targetBright, _gamma.ReadCurrentTemperature());
		}
		else if (smoothBright && !smoothTemp)
		{
			// 立即轴 = 色温，平滑轴 = 亮度
			_gamma.CommitTargetStateWithoutWrite(_gamma.ReadCurrentBrightness(), targetTemp);
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
		_smoothTargetValid = true;      // F5 修复（v2）：标记"最后请求的目标"已可用
		_smoothTargetTemp = targetTemp;
		_smoothBrightActive = smoothBright;
		_smoothTempActive = smoothTemp;
		_smoothStartTime = DateTime.Now;
		_smoothDone = done;
		_smoothFrameCount = 0;
		// ------------------------------------------------------------------
		// F2（2026-09-17）：采样"屏幕真实 ramp"作为过渡起点，改走 **ramp 空间插值**。
		// 必须在上面"未启用平滑的轴已立即写到位"**之后**采样 —— 起点 ramp 才会包含
		// 该轴的最终值，保持原有"该轴立即变化"的语义。
		// 返回 0（屏不可写 / 读不到 ramp）⇒ 回退到旧的参数插值路径，行为完全不变。
		// ------------------------------------------------------------------
		_rampAnimUsed = _gamma.BeginRampTransition(targetBright, targetTemp, SmoothDurationMs) > 0;
		int nominalFrames = (int)Math.Ceiling(SmoothDurationMs / (double)SmoothTickMs);
		// P5 日志：平滑是否接管、从哪到哪、用哪条路径、标称帧数 —— 全部可从日志判定。
		OpLog.Log($"[smooth] start: brightness {_smoothStartBright * 100f:0}% -> {targetBright * 100f:0}%" +
				  $"（{(smoothBright ? "平滑" : "立即")}）；" +
				  $"temperature {_smoothStartTemp:0}K -> {targetTemp:0}K（{(smoothTemp ? "平滑" : "立即")}）；" +
				  $"{SmoothDurationMs}ms/{SmoothTickMs}ms tick（标称 {nominalFrames} 帧）；" +
				  $"起点={( _rampAnimUsed ? "屏幕真实 ramp（ramp 空间插值）" : "参数反算（回退）")}");
		if (_smoothTimer == null)
		{
			_smoothTimer = new Timer
			{
				Interval = SmoothTickMs
			};
			_smoothTimer.Tick += OnSmoothTick;
		}
		_smoothTimer.Start();
	}

	/// <summary>
	/// 取消在途的平滑过渡：停表 + 丢弃未完成的 done 回调 + 释放 ramp 独占窗口。
	/// **不写屏、不改任何目标值** —— 屏幕停在当前那一帧（下一次过渡会从该帧重新采样起点）。
	/// 用于「必须以屏幕当前真实值为基准重新决策」的场景（Bug A：重开色温总开关）。
	/// ⚠️ 与 `OnManualAdjustment` 的区别：后者还负责「接管 Solar」，本方法只做动画收尾。
	/// </summary>
	private void CancelInFlightSmooth()
	{
		if (_smoothTimer != null)
		{
			_smoothTimer.Stop();
			_smoothTimer.Dispose();
			_smoothTimer = null;
		}
		_smoothDone = null;
		_rampAnimUsed = false;
		_gamma?.CancelRampTransition();
	}

	private void OnSmoothTick(object? sender, EventArgs e)
	{
		_smoothFrameCount++;
		// F3：时长用常量（原来是硬编码 1200.0 / Interval=30），保证帧数与标称一致。
		double num = (DateTime.Now - _smoothStartTime).TotalMilliseconds / (double)SmoothDurationMs;
		if (num >= 1.0)
		{
			num = 1.0;
		}
		double num2 = EaseOutCubic(num);
		if (_rampAnimUsed)
		{
			// F2：逐屏 ramp 空间插值。f=0 首帧 == 屏幕真实 ramp（零跳变）；
			// f=1 末帧 == 目标 ramp（精确命中，不再有末帧硬跳）。
			_gamma?.StepRampTransition(_smoothTargetBright, _smoothTargetTemp, num2);
		}
		else if (_smoothBrightActive || _smoothTempActive)
		{
			if (_smoothBrightActive)
			{
				_gamma?.SetBrightness((float)((double)_smoothStartBright + (double)(_smoothTargetBright - _smoothStartBright) * num2));
			}
			if (_smoothTempActive)
			{
				_gamma?.SetTemperature((float)((double)_smoothStartTemp + (double)(_smoothTargetTemp - _smoothStartTemp) * num2));
			}
		}
		if (num >= 1.0)
		{
			_smoothTimer?.Stop();
			_smoothTimer?.Dispose();
			_smoothTimer = null;
			if (_smoothBrightActive)
			{
				OpLog.Log($"[smooth] done: brightness {_smoothTargetBright * 100f:0}%");
			}
			if (_smoothTempActive)
			{
				OpLog.Log($"[smooth] done: temperature {_smoothTargetTemp:0}K");
			}
			OpLog.Log($"[smooth] done: 实际 {_smoothFrameCount} 帧（标称 {(int)Math.Ceiling(SmoothDurationMs / (double)SmoothTickMs)} 帧）");
			if (_rampAnimUsed)
			{
				// F3（2026-09-17）：末帧 f=1 已精确等于目标 ramp ⇒ 只同步内部 state，
				// **不再"落定写入补齐"**（原实现这一步会造成末帧 Δ640 的硬跳）。
				_gamma?.EndRampTransition(_smoothTargetBright, _smoothTargetTemp);
				// 自检兜底：极端情况（最后一帧被跳过等）末帧可能未命中 ⇒ 全量重写一次。
				// 正常情况下这一步不会触发（不产生额外写屏）。
				if (_gamma != null && !_gamma.IsTargetRampOnScreen(_smoothTargetBright, _smoothTargetTemp))
				{
					OpLog.Log("[smooth] 末帧自检未命中目标 ramp ⇒ ReapplyAllDisplays 兜底一次");
					_gamma.ReapplyAllDisplays();
				}
			}
			else
			{
				if (_smoothBrightActive)
				{
					_gamma?.SetBrightness(_smoothTargetBright);
				}
				if (_smoothTempActive)
				{
					_gamma?.SetTemperature(_smoothTargetTemp);
				}
			}
			_rampAnimUsed = false;
			if (_smoothBrightActive)
			{
				this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
			}
			if (_smoothTempActive)
			{
				this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
			}
			SaveSettings();
			UpdateTrayTooltip();
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
			_popup?.ShowAbove(UiBrightness, UiTemperature, rectangle2);
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
			SyncPopupFromUi();  // 方案 B：弹窗打开时按界面显示值校准一次
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

	/// <summary>
	/// 托盘 tooltip（方案 B，2026-09-16）：统一走**界面显示值** —— 三个暂停源生效时
	/// 显示屏幕实际生效值（100%/6600K），否则显示设定值。多屏独立控制下原先各调用点
	/// 散着读 _gamma.Current*，暂停期间与屏幕实况脱节（用户反馈）。
	/// </summary>
	private void UpdateTrayTooltip(float? brightness = null, float? temperatureK = null)
	{
	float brightness2 = brightness ?? UiBrightness;
	float temperatureK2 = temperatureK ?? UiTemperature;
		bool showTemperature = _settings?.ColorTemperatureEnabled ?? false;
		// 记录最近一次落进 tooltip 的数值（托盘 tip 文本对外部不可读，
		// 自动化只能靠这个回读来断言"tooltip 有没有跟上"，见 Biz_TrayTooltip）。
		_lastTooltipPct = (int)Math.Round(brightness2 * 100);
		_lastTooltipK = (int)Math.Round(temperatureK2);
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
	// P1：托盘常驻写入阶梯（600/1500/4000/10000ms，任一次成功即停）
	private static readonly int[] TrayVisDelaysMs = { 600, 1500, 4000, 10000 };
	// 阶梯耗尽后的**长周期轮询**：
	// 实测（2026-09-14 全新安装）证实 —— explorer 只在"图标注册发生在任务栏初始化期间
	// （TaskbarCreated）"时才建 NotifyIconSettings 条目；程序在 explorer 稳态运行时
	// 安装/首次启动，条目**不会**出现（等 45s、100s 都没有）。此时任何写入都是无对象
	// 的 no-op。条目要等到 explorer 重建任务栏（重启资源管理器 / 注销重登）才出现，
	// 所以阶梯放弃后继续低频轮询，条目一出现就写入 —— 用户无需再手动拨一次开关。
	private const int TrayVisSlowPollMs = 30000;
	private const int TrayVisSlowPollMax = 20;      // 约 10 分钟
	private System.Windows.Forms.Timer? _trayVisTimer;
	private int _trayVisStep;
	private int _trayVisSlowPoll;

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
		// P5c 修复（2026-09-14）：补 _fullscreenPaused —— 与弹窗 UI 锁（IsDisableActive）语义一致。
		if (IsUiPaused()) return;
		OnManualAdjustment();
		float b = brightness;
		QueueAdjust("popupB", () =>
		{
			_gamma?.SetBrightness(b);
			UpdateTrayTooltip();
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
			SaveSettings();
		});
	}

	private void OnPopupTemperatureChanged(object? sender, float kelvin)
	{
		// P5c 修复（2026-09-14）：补 _fullscreenPaused —— 与弹窗 UI 锁（IsDisableActive）语义一致。
		if (IsUiPaused()) return;
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
		// P5c 修复（2026-09-14）：补 _fullscreenPaused。
		if (IsUiPaused() || _gamma == null || !_gamma.PerMonitorEnabled) return;
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
			UpdateTrayTooltip();
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? b);
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? k);
			SaveSettings();
		});
	}

	private void OnPopupPerMonitorWheel(int sign)
	{
		// P5c 修复（2026-09-14）：补 _fullscreenPaused。
		if (IsUiPaused() || _gamma == null || !_gamma.PerMonitorEnabled) return;
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
		UpdateTrayTooltip();
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
			// 方案 B 多屏版（2026-09-16）：暂停期间每屏都应显示"屏幕实际生效值"
			// （ramp 已被重置为原生），而不是 gamma 内部保留值 —— 否则行滑轨会
			// 停在暂停前的 85%，与屏幕实况脱节（用户反馈的错觉来源）。
			float uiRowB = IsUiPaused() ? 1f : displayState.Brightness;
			float uiRowT = IsUiPaused() ? 6600f : displayState.Temperature;
			string displayNameFor = GetDisplayNameFor(displayId);
			list.Add(new BrightnessPopup.DisplayRowData(displayId, displayNameFor, uiRowB, uiRowT, enabled));
		}
		return list;
	}

	private void ShowOverlayForDisplays()
	{
		// 2026-09-16 修正：OSD 是**查看**入口不是调节入口 —— 通用设置里开了就该能唤出。
		// 暂停期间照常显示，只是显示的是"屏幕实际生效值"（下面的行数据已走 Ui 值），
		// 且行滑轨仍被 Frozen 锁住改不动。
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
				list.Add(new BrightnessOverlay.DisplayRow(displayId, IsUiPaused() ? 1f : displayState.Brightness, enabled));
			}
			_overlay.ShowDisplays(list);
		}
		else
		{
			// 方案 B（2026-09-16）：单行 OSD 也走界面显示值 —— 暂停期间显示 100%。
			_overlay.Show(UiBrightness);
		}
	}

	private void OnOverlayBrightnessChanged(object? sender, float brightness)
	{
		// P5c 修复（2026-09-14）：补 _fullscreenPaused。
		if (!IsUiPaused())
		{
			OnManualAdjustment();
			float b = brightness;
			QueueAdjust("osdB", () =>
			{
				_gamma?.SetBrightness(b);
				UpdateTrayTooltip();
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
		// P5c 修复（2026-09-14）：补 _fullscreenPaused。
		if (IsUiPaused() || _gamma == null || !_gamma.PerMonitorEnabled) return;
		OnManualAdjustment();
		string id = edidId;
		float b = brightness;
		QueueAdjust("osdRow|" + edidId, () =>
		{
			_gamma.SetBrightness(id, b);
			UpdateTrayTooltip();
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
			UpdateTrayTooltip();
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
				SyncPopupFromUi();  // 方案 B：导入配置解除了停用，界面回到设定值
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
			SyncPopupFromUi();  // 方案 B：暂停标志复位，界面回到设定值
			StartFullscreenTransition(_fullscreenBrightnessBefore, _fullscreenTemperatureBefore, exit: true);
		}
		RegisterHotkeys();
		// 独立控制随导入配置同步到运行时（gamma/弹窗行模式），避免"总开关显示开、
		// 弹窗仍是单行"的脱节；关闭状态则统一写屏。
		if (_settings.PerMonitorEnabled)
		{
			ApplyPerMonitorFromSettings();
		}
		else
		{
			_gamma?.ApplyUnifiedTarget(_settings.LastBrightness,
				_settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f);
			if (_popup != null)
			{
				_popup.PerMonitorEnabled = false;
				_popup.SetDisplays(BuildDisplayRows());
			}
		}
		UpdateTrayTooltip();
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
			// P5c 修复（2026-09-14）：冻结罩（禁用/全屏）期间不得改值，也不得误触发
			// OnManualAdjustment（那会把 Solar 停掉却写不进值）。与弹窗/OSD 早退语义一致。
			if (IsUiPaused()) return;
			OnManualAdjustment();
			bool flag = _settings != null && _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
			AppSettings? settings = _settings;
			if (settings != null && settings.BrightnessSmooth && !flag)
			{
				StartSmoothTransition(brightness, PendingOrCurrentTemperature(), smoothBright: true, smoothTemp: false);
			}
			else
			{
				_gamma.SetBrightness(brightness);
				// F5 补（2026-09-20 实测 C16）：平滑关时也必须维护 `_smoothTarget*`。
				//   该字段的声明语义是「**最后一次请求**的目标」（见字段注释），
				//   而它此前只在 `StartSmoothTransition` 内被写 ⇒ 平滑关这条路径会留下
				//   **陈旧目标**：后续另一轴的 `StartSmoothTransition(PendingOrCurrentXxx(), …)`
				//   会把这个轴一并写回旧值。实测现象：亮平滑关 + 亮度设 10% 后调色温 ⇒
				//   亮度被拉回 25%（`[smooth] start: brightness 10% -> 25%（立即）`）。
				_smoothTargetBright = brightness;
				_smoothTargetTemp = PendingOrCurrentTemperature();
				_smoothTargetValid = true;
				SaveSettings();
			}
			// 挡位切换不弹 OSD：平滑开启时 OSD 此刻读到的是动画起点旧值
			// （如切 75% 却显示 100%），随后又自行消失，观感突兀
			// （用户 2026-09-04 反馈）。挡位选择本身有下拉/设置窗反馈，
			// 无需 OSD 确认；滚轮/热键等实时调节路径仍照常显示 OSD。
			UpdateTrayTooltip();
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
		// ===== 诊断（只加日志，不改任何行为）=====
		// 目的：用户报"打开独立控制总开关出现一次跳变" ⇒ 需要知道
		//   ① 是谁调用的（UI 开关 / 重置 / 导入 / 启动路径）；
		//   ② 调用瞬间"设定值"与"屏幕实际值"是否脱节（脱节 ⇒ 无过渡的播种必然跳变）。
		OpLog.Log($"[permon] SetPerMonitorEnabled({enabled}) caller={Stack2()} " +
		          $"settingPerMon={_settings?.PerMonitorEnabled} " +
		          $"curB={(_gamma?.CurrentBrightness ?? -1f) * 100f:0} curT={_gamma?.CurrentTemperature ?? -1f:0}K " +
		          $"gammaPaused={_gamma?.IsPaused} uiPaused={IsUiPaused()}");
		// 2026-09-19：门控依赖「独立控制开」⇒ 关闭它时，全屏逐屏必须立即收干
		//（`_gamma.ResetDisplayStates()` 会把 `Paused` 一并抹掉，本侧集合却不知情）。
		// reapply=false：随后的「统一化动画」会接管写屏，本处只解除标志、不抢写。
		if (!enabled) ClearFullscreenPerMonitorPause(reapply: false);
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
				// F5-同类（2026-09-18）：`Current*` 只在过渡**正常走完**时提交 ⇒
				// 过渡在飞/被打断时是**滞后值**（上面 1764 行只处理了"暂停期"这一种滞后）
				// ⇒ 改用「最后请求的目标」，与 F5 同源同修法。
				float entryB = PendingOrCurrentBrightness();
				float entryT = PendingOrCurrentTemperature();
				
				// ⚠ 暂停期间 `Current*`（统一种子）是**陈旧值**：暂停时 SetBrightness/SetTemperature
				// 会被 `if (_paused) return;` 静默丢弃，种子停在暂停前那一刻。若此刻开启独立控制，
				// `ResetDisplayStates(entryB, entryT)` 会把**所有屏的 state 整体毒化成该陈旧值**
				// （实测：全局白名单暂停刚释放、退出动画还在飞时切独立控制 ⇒ state 被写成 100%/6600K，
				// 之后用户再设 5000K 也进不去 state，屏幕被写回原生 —— 主回归 6 项失败的根因）。
				// ⇒ 暂停中改用"暂停前的目标值"作种子（即退出动画正在回到的那个值）。
				if (_gamma.IsPaused || _fullscreenAnimTimer != null)   // 含"暂停退出动画在飞"（实测正是这个时机会被毒化）
				{
					if (_whitelistBrightnessBefore >= 0f)
					{
						entryB = _whitelistBrightnessBefore;
						entryT = _whitelistTemperatureBefore;
					}
					else if (_fullscreenBrightnessBefore >= 0f)
					{
						entryB = _fullscreenBrightnessBefore;
						entryT = _fullscreenTemperatureBefore;
					}
					OpLog.Log($"[gamma] 暂停中开启独立控制：改用暂停前目标值作种子 " +
					            $"({entryB * 100f:0}%/{entryT:0}K)");
				}
				_perMonitorEntryBrightness = entryB;
				_perMonitorEntryTemperature = entryT;
				_settings.PerMonitorEnabled = true;
				_gamma.PerMonitorEnabled = true;
				_gamma.ResetDisplayStates(entryB, entryT);
				SyncGammaEnabledFromSettings(); // 停用标记仍以设置记录为准
				// 诊断：本 ON 路径**没有任何平滑过渡** —— 各屏 state 被一次性置为种子值，
				// 下一次写屏即按该值输出。若此刻屏幕实际值 ≠ 种子值，观感就是"一步跳变"。
				OpLog.Log($"[permon] ON 播种：各屏 state 直接置为 {entryB * 100f:0}%/{entryT:0}K（无过渡）" +
				          $" 屏实际读数={_gamma.ReadCurrentBrightness() * 100f:0}%/{_gamma.ReadCurrentTemperature():0}K");
			}
			else
			{
				// 关闭独立控制：统一目标 = **当前统一基准 `Current*`**（F8）。
				// ⚠ 必须在翻转 `PerMonitorEnabled` **之前**读取（否则 Average* 会退化成读陈旧的统一种子）。
				// F8（2026-09-17 晚）：**不再用「进入独立控制时的基准」当统一目标** —— 那是**历史快照**，
				// 一旦期间 Solar / 滚轮 / 弹窗 / 手动改过统一值，它就与权威值脱节。
				// 实测（人工可复现）：Solar 夜间（85%）时开过「每屏独立」（entry=85%/6600K）⇒
				// 关掉 Solar（屏幕回 100%/6600K）⇒ 再关「每屏独立」⇒ 动画把屏**拉回 85%**，
				// 留下「85% 亮度 + 6600K」的半应用状态（`[unify] OFF 目标 targetB=85% ... curB=100%`）。
				// ⇒ 改用 **`Current*`（当前统一基准）**：
				//   · 进入独立控制那一刻 entry 就是从 `Current*` 读的 ⇒ 只有权威值真变过才会分叉；
				//   · 逐屏拖动只写 `_displayStates`、**不动 `Current*`** ⇒
				//     "关闭每屏独立会丢弃逐屏微调"的既有语义**不变**；
				//   · 滚轮/弹窗/Solar 走统一 API（会更新 `Current*`）⇒ 跟随它们才符合用户预期。
				// F5-同类（2026-09-18）：同上 —— 用"最后请求的目标"，
				// 避免过渡在飞时把统一目标取成滞后值而把屏拉回旧值。
				float targetB = PendingOrCurrentBrightness();
				float targetT = PendingOrCurrentTemperature();
				// F4b（2026-09-17）：Solar 驱动中 ⇒ 统一目标**必须**取 Solar 当前目标。
				// 「进入独立控制时的基准」在 Solar 改过值之后就是**过期值**：实测基准 100%/6600K、
				// Solar 夜间 85%/3900K ⇒ 统一化动画把屏推到 100%/6600K，Solar 下一次 tick（≤2s）
				// 又把它拉回 85%/3900K（外部采样 Δ27170 硬跳）。
				// Solar 在跑 = 它是亮度/色温的权威；开关独立控制只改"控制粒度"，不改权威。
				bool solarDriving = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden
				                    && _solarScheduler != null;
				if (solarDriving)
				{
					var (solB, solT) = _solarScheduler!.GetCurrentTargets();
					targetB = solB;
					// 色温总开关关闭时 Solar 不驱动色温（保持中性），别把日/夜色温引进来。
					if (_settings.ColorTemperatureEnabled) targetT = solT;
				}
				// 诊断（2026-09-17）：关闭独立控制的统一目标从哪来、当前屏幕与 state 各是什么。
				// 实测症状：关闭瞬间有 1 帧跌回 (85%/3900K) 再跳回 ⇒ 需要知道是谁在翻转后抢先写屏。
				OpLog.Log($"[unify] OFF 目标 targetB={targetB * 100f:0}% targetT={targetT:0}K " +
						  $"来源={(solarDriving ? "Solar 当前目标（权威）" : "当前统一基准 Current*（F8）")} " +
						  $"entryB={_perMonitorEntryBrightness * 100f:0}% entryT={_perMonitorEntryTemperature:0}K " +
						  $"curB={_gamma.CurrentBrightness * 100f:0}% curT={_gamma.CurrentTemperature:0}K " +
						  $"smoothB={_settings.BrightnessSmooth} smoothT={_settings.TemperatureSmooth} uiPaused={IsUiPaused()}");
				OpLog.Log($"[unify] OFF 前置快照：{DescribeDisplayStates()}");
				_settings.PerMonitorEnabled = false;
				bool smoothB = _settings.BrightnessSmooth && !IsUiPaused();
				bool smoothT = _settings.TemperatureSmooth && !IsUiPaused();
				if (!smoothB && !smoothT)
				{
					_gamma.PerMonitorEnabled = false;
					_gamma.ApplyUnifiedTarget(targetB, targetT);
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
			UpdateTrayTooltip();
			this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
			this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
		}
	}

	/// <summary>
	/// 诊断辅助（2026-09-17）：把"逐屏 state"与"屏幕实际读数"打在一起。
	/// 用途：关闭独立控制出现 1 帧下探时，判断究竟是 state 与屏幕脱节、还是被外部路径抢先写屏。
	/// </summary>
	private string DescribeDisplayStates()
	{
		if (_gamma == null) return "（无 gamma）";
		var parts = new List<string>();
		foreach (var id in _gamma.GetDisplayIds())
		{
			var st = _gamma.GetDisplayState(id);
			var (actB, actT) = _gamma.ReadActualFor(id);
			int slash = id.LastIndexOf('\\');
			string shortId = slash >= 0 && slash + 1 < id.Length ? id.Substring(slash + 1) : id;
			parts.Add($"{shortId}[state={st.Brightness * 100f:0}%/{st.Temperature:0}K " +
					  $"实际={actB * 100f:0}%/{actT:0}K en={st.Enabled} ps={st.Paused}]");
		}
		return string.Join(" ", parts);
	}

	private void BeginDisablePerMonitorAnimation(float targetB, float targetT, bool smoothB, bool smoothT)
	{
		if (_unifyActive) return;
		_unifyTickCount = 0;
		_unifyStart.Clear();
		foreach (string displayId in _gamma!.GetDisplayIds())
		{
			var st = _gamma.GetDisplayState(displayId);
			_unifyStart[displayId] = (st.Brightness, st.Temperature);
		}
		if (_unifyStart.Count == 0)
		{
			_gamma.PerMonitorEnabled = false;
			_gamma.ApplyUnifiedTarget(targetB, targetT);
			return;
		}
		_unifyActive = true;
		_unifyTargetB = targetB;
		_unifyTargetT = targetT;
		_unifySmoothB = smoothB;
		_unifySmoothT = smoothT;
		_unifyStartTime = DateTime.Now;
		// 诊断（2026-09-17）：逐屏起点参数（来自 state，**不是**屏幕真实 ramp）——
		// 若 state 与屏幕脱节，首帧就会跳。实测疑似点之一。
		var startParts = new List<string>();
		foreach (var kv in _unifyStart)
		{
			int sl = kv.Key.LastIndexOf('\\');
			startParts.Add($"{(sl >= 0 ? kv.Key.Substring(sl + 1) : kv.Key)}={kv.Value.Brightness * 100f:0}%/{kv.Value.Temperature:0}K");
		}
		OpLog.Log($"[unify] 动画起点：{string.Join(" ", startParts)} ⇒ 目标 {targetB * 100f:0}%/{targetT:0}K " +
				  $"（smoothB={smoothB} smoothT={smoothT}）屏实际={DescribeDisplayStates()}");
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

	private int _unifyTickCount;

	private void OnUnifyTick(object? sender, EventArgs e)
	{
		_unifyTickCount++;
		double num = (DateTime.Now - _unifyStartTime).TotalMilliseconds / (double)SmoothDurationMs;
		if (num >= 1.0) num = 1.0;
		double f = EaseOutCubic(num);
		// 诊断（2026-09-17）：只记首帧 / 每 8 帧 / 末帧，控制日志量。
		bool traceTick = _unifyTickCount == 1 || _unifyTickCount % 8 == 0 || num >= 1.0;
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
			if (traceTick)
			{
				var st = _gamma?.GetDisplayState(kv.Key);
				int sl = kv.Key.LastIndexOf('\\');
				OpLog.Log($"[unify/tick] #{_unifyTickCount} f={f:0.000} " +
						  $"{(sl >= 0 ? kv.Key.Substring(sl + 1) : kv.Key)} state={st?.Brightness * 100f:0}%/{st?.Temperature:0}K");
			}
		}
		if (num >= 1.0) CompleteDisablePerMonitorAnimation();
	}

	private void CompleteDisablePerMonitorAnimation()
	{
		CancelUnifyAnimation();
		AppSettings? settings = _settings;
		if (settings == null || _gamma == null) return;
		// 诊断（2026-09-17）：这一步是「1 帧下探」的头号嫌疑 ——
		// 翻转 PerMonitorEnabled 后，逐屏动画不再写屏，而"统一模式的 _current*"此刻可能仍是
		// solar 写的旧值（85%/3900K）⇒ 任何在 SetBrightness/SetTemperature 之前插进来的
		// 全量写屏都会把那一对旧值打到屏幕上。三个标尺把"谁在什么时候写"钉死。
		OpLog.Log($"[unify] Complete 进入：curB={_gamma.CurrentBrightness * 100f:0}% curT={_gamma.CurrentTemperature:0}K " +
				  $"targetB={_unifyTargetB * 100f:0}% targetT={_unifyTargetT:0}K perMon={_gamma.PerMonitorEnabled} " +
				  $"栅格={DescribeDisplayStates()}");
		_gamma.PerMonitorEnabled = false;
		// F4c（2026-09-17）：改成**一次**写屏。原来先 SetBrightness 再 SetTemperature，
		// 第一句用的是"目标亮度 + **陈旧色温**"（陈旧值来自 Solar 在独立模式下的写入）
		// ⇒ 实测写出一张错色温 ramp（curB=100 curT=3900 R255=65535 B255=41476），
		// 2ms 后才被第二句纠正。ApplyUnifiedTarget 先对齐两个分量再只写一次。
		OpLog.Log($"[unify] Complete ① 翻转 perMon=false（翻转前 curB={_gamma.CurrentBrightness * 100f:0}% " +
				  $"curT={_gamma.CurrentTemperature:0}K）");
		_gamma.ApplyUnifiedTarget(_unifyTargetB, _unifyTargetT);
		OpLog.Log($"[unify] Complete ② 统一写屏一次（{_unifyTargetB * 100f:0}%/{_unifyTargetT:0}K）之后：" +
				  $"curB={_gamma.CurrentBrightness * 100f:0}% curT={_gamma.CurrentTemperature:0}K " +
				  $"栅格={DescribeDisplayStates()}");
		SettingsManager.Save(settings);
		_popup!.PerMonitorEnabled = false;
		_popup?.SetDisplays(BuildDisplayRows());
		UpdateTrayTooltip();
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
			// 立即生效：用最新名称重建弹窗行数据（重命名无需重启即反映到
			// 弹窗/OSD；OSD 行在每次显示时实时取名）。旧 RefreshDisplayNames
			// 只刷新缓存副本、不会读取新名，故改为整组 SetDisplays。
			if (_popup != null) _popup.SetDisplays(BuildDisplayRows());
		}
	}

	public string GetDisplaySystemName(string edidId)
	{
		return GetDisplayNameFor(edidId);
	}

	/// <summary>原始显示名（不含自定义改名）：EDID 友好名 → 型号段 → ID。
	/// 供"显示器信息"列表左列展示原厂名，与中间的自定义名对照。</summary>
	public string GetDisplayOriginalName(string edidId)
	{
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
			// P5a 修复（2026-09-14）：同值重复调用直接早退 —— 否则会重复执行“交回 Solar 接管 /
			// 恢复 LastTemperature”，在事件重复或自动化重复调用时可能非预期地改变画面。
			// “重开色温总开关 = 交回 Solar”的语义只对 关→开 成立。
			if (_settings.ColorTemperatureEnabled == enabled)
			{
				return;
			}
			_settings.ColorTemperatureEnabled = enabled;
			// ------------------------------------------------------------------
			// Bug A（2026-09-17 用户报：「时间调整与色温平滑生效时，重开色温总开关，色温不会
			// 平滑变化」）。实测根因（`_devtools/repro_bugA.py`，A1 快 / A2 慢 对照）：
			//   OFF 起了 1200ms 平滑（3900K→6600K）后，0.59s 内又点 ON ⇒ 此处读到的
			//   `_gamma.CurrentTemperature` **仍是动画开始前的 3900K**（ramp 过渡只在
			//   `EndRampTransition` 才提交内部 state）⇒ |Δ|=0 ⇒ `tempSmooth=false` ⇒
			//   既不启新过渡、又不取消旧过渡 ⇒ 旧动画照旧跑到 6600K，随后
			//   `ApplySolarScheduler()` 把屏**瞬时**拉回 3900K ⇒ 末帧硬跳 Δ22254
			//   （日志：`[smooth] done: temperature 6600K` 之后 20ms 即 `curT=3900`）。
			// 修法：① 丢弃在途过渡（停表 + 丢 done 回调 + 释放独占窗口，均不写屏）；
			//       ② 基准取**屏幕真实读数**（走 ramp 反算，动画途中也给出真实值）。
			//          新过渡的起点由 `BeginRampTransition` 重新采样屏幕 ramp，
			//          天然「从当前帧接着走」，不会跳。
			// ------------------------------------------------------------------
			CancelInFlightSmooth();
			float num = _gamma?.ReadCurrentTemperature() ?? _gamma?.CurrentTemperature ?? 6600f;
			// 目标值抉择（2026-09-14 用户指出的逻辑修正）：
			//   * Solar 活跃（启用且未被手动接管）→ **由 Solar 接管**：直接以当前时刻的
			//     调度目标为终点（如夜间 3900K）。若仍恢复 LastTemperature，会出现
			//     "先恢复旧值 5600、再被 Solar 慢慢拉走"的中间态，观感即"恢复错了值"。
			//   * Solar 未启用/已被手动接管 → 恢复 LastTemperature（既有语义）。
			//   * 关闭 → 中性 6600K。
			float num2;
			if (enabled)
			{
				bool solarEnabled = _settings.SolarAdjustEnabled;
				if (solarEnabled && _solarScheduler != null)
				{
					// 重新开启色温总开关 = 用户要求数值**交回 Solar 管理**：
					// 清除手动接管标记，并直接以当前时刻的调度目标为终点
					// （白天 6600K / 夜间 3900K），不再先恢复 LastTemperature
					// 造成"先 5600 再被慢慢拉走"的中间态（2026-09-14 用户定稿语义）。
					_settings.SolarManuallyOverridden = false;
					var (solarB, solarT) = _solarScheduler.GetCurrentTargets();
					num2 = solarT;
					OpLog.Log($"[colorTemp] enable: Solar takeover -> target {num2:0}K (override cleared)");
				}
				else
				{
					num2 = _settings.LastTemperature;
					OpLog.Log($"[colorTemp] enable: restore LastTemperature {num2:0}K");
				}
			}
			else
			{
				num2 = 6600f;
				OpLog.Log("[colorTemp] disable: neutral 6600K");
			}
			// P5b 修复（2026-09-14）：平滑门控统一为 !_disableActive && !_fullscreenPaused。
			bool tempSmooth = _settings.TemperatureSmooth && !IsUiPaused() && Math.Abs(num - num2) >= 50f;
			if (tempSmooth)
			{
				// 2026-09-16 修复（用户实测报告：时间调整开启时关/开色温总开关会跳变）：
				// **平滑期间不得同步 ApplySolarScheduler()**。SolarScheduler.Tick() 在
				// TransitionMinutes=0 时 maxStep=0 ⇒ 瞬时到位，会把刚起步的 1200ms 动画
				// 一步覆盖（实测色温比单步 0.033 → 0.349），随后动画 tick 又拉回 ⇒ 跳变。
				// 改为动画结束后再让 Solar 接手 —— 那时画面已在同一目标值，不会互相覆盖。
				StartSmoothTransition(PendingOrCurrentBrightness(), num2, smoothBright: false, smoothTemp: true,
					delegate
					{
						ApplySolarScheduler();
					});
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
			UpdateTrayTooltip();
			if (!tempSmooth)
			{
				ApplySolarScheduler();
			}
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
		if (_popup != null)
		{
			_popup.MinTemperature = minK;
			_popup.MaxTemperature = maxK;
		}
		if (_gamma == null)
		{
			return;
		}
		if (!_settings.ColorTemperatureEnabled)
		{
			_gamma.MinTemperature = minK;
			_gamma.MaxTemperature = maxK;
			return;
		}
		float currentTemperature = _gamma.CurrentTemperature;
		float num = Math.Clamp(currentTemperature, minK, maxK);
		// P5b 修复（2026-09-14）：平滑门控统一为 !_disableActive && !_fullscreenPaused。
		if (_settings.TemperatureSmooth && !IsUiPaused() && Math.Abs(currentTemperature - num) >= 50f)
		{
			// 2026-09-16 修复（实测：修改上下限时画面"一步跳变"）：
			// GammaController.SetTemperature() 会把入参 Math.Clamp 到**gamma 当前的**
			// Min/MaxTemperature。若在此处先把新范围落进 gamma，则过渡动画的中间值
			// （如 5000→6000 之间的每一帧）全部被新范围 [6000,10000] 钳成 6000，
			// **第一帧就等于终点** ⇒ 屏幕跳变（日志却显示走了 1200ms 平滑）。
			// 改法：新范围推迟到过渡结束后再落（设置与弹窗已即时更新，不受影响）。
			StartSmoothTransition(PendingOrCurrentBrightness(), num, smoothBright: false, smoothTemp: true, delegate
			{
				if (_gamma != null)
				{
					_gamma.MinTemperature = minK;
					_gamma.MaxTemperature = maxK;
				}
			});
		}
		else
		{
			_gamma.MinTemperature = minK;
			_gamma.MaxTemperature = maxK;
			_gamma.SetTemperature(num);
		}
	}

	public void SetColorTemperature(float kelvin)
	{
		AppSettings? settings = _settings;
		if (settings != null && settings.ColorTemperatureEnabled)
		{
			// P5c 修复（2026-09-14）：冻结罩（禁用/全屏）期间不得改值，也不得误触发
			// OnManualAdjustment（那会把 Solar 停掉却写不进值）。在 setter 级收口，
			// 使“设置页控件是否按全屏锁定”这一层不再有实际影响。
			if (IsUiPaused()) return;
			OnManualAdjustment();
			bool flag = settings.SolarAdjustEnabled && !settings.SolarManuallyOverridden;
			if (settings.TemperatureSmooth && !flag)
			{
				StartSmoothTransition(PendingOrCurrentBrightness(), kelvin, smoothBright: false, smoothTemp: true);
			}
			else
			{
				_gamma?.SetTemperature(kelvin);
				// F5 补（对称）：与亮度侧同理 —— 平滑关也要维护 `_smoothTarget*`，
				//   否则之后调亮度会把色温一并写回陈旧目标。
				_smoothTargetTemp = kelvin;
				_smoothTargetBright = PendingOrCurrentBrightness();
				_smoothTargetValid = true;
				this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
				SaveSettings();
			}
			if (_gamma != null && _popup != null)
			{
				SyncPopupFromUi();
			}
			UpdateTrayTooltip();
		}
	}

	// ------------------------------------------------------------------
	//  方案 B（2026-09-16）：界面显示值 —— 暂停期间显示"屏幕实际生效值"
	// ------------------------------------------------------------------
	// 背景：三个暂停源（功能停用 / 全屏暂停 / 应用白名单）生效时，gamma 内部
	// 状态被**原样保留**（供解除后重放），屏幕却被重置为原生 ramp。此前所有界面
	// 数值都直接读 gamma 内部状态，于是出现"屏幕是 100%/6600K，界面却显示
	// 80%/4000K"的脱节（用户反馈）。
	//
	// 约定：暂停期间界面显示恒为 100% / 6600K。这里**不**用 ReadCurrentBrightness /
	// ReadCurrentTemperature 反读 ramp —— 线性 ramp 反解色温只有 ~6560K
	// （Tanner Helland 在 6600K 处要求 green 乘子 >1 才可逆，实际被 clamp 到 1），
	// 会显示成 6560K，比"显示设定值"更不可解释。

	private int _lastTooltipPct = -1;
	private int _lastTooltipK = -1;

	/// <summary>三个暂停源是否有任一生效（与弹窗交互锁 <c>_popup.IsDisableActive</c> 同判据）。</summary>
	public bool IsUiPaused() => _disableActive || _fullscreenPaused || _whitelistPaused;

	/// <summary>界面显示用亮度（UI 0..1）：暂停期间为屏幕实际生效值 1.0，否则为设定值。</summary>
	public float UiBrightness => IsUiPaused() ? 1f : (_gamma?.CurrentBrightness ?? 1f);

	/// <summary>界面显示用色温（K）：暂停期间为屏幕实际生效值 6600K，否则为设定值。</summary>
	public float UiTemperature => IsUiPaused() ? 6600f : (_gamma?.CurrentTemperature ?? 6600f);

	/// <summary>
	/// 把界面显示值推给左键弹窗（方案 B 的唯一入口）。
	/// 弹窗不可见时 SyncFromGamma 只落值不重绘，因此可在任意状态变化点安全调用。
	/// </summary>
	private float _lastUiPushB = -1f;
	private float _lastUiPushT = -1f;

	private void SyncPopupFromUi()
	{
		float b = UiBrightness;
		float t = UiTemperature;
		// 只在"推给界面的值真的变了"时落日志（调用点很密：Solar 步进/热键/弹窗打开都会走这里），
		// 便于实测回答"界面此刻为什么是这个数"。正式版 OpLog 为空实现，零开销。
		if (Math.Abs(b - _lastUiPushB) > 0.0001f || Math.Abs(t - _lastUiPushT) > 0.5f)
		{
			_lastUiPushB = b;
			_lastUiPushT = t;
			OpLog.Log($"[ui] push B={b * 100f:0}% T={t:0}K paused={IsUiPaused()} " +
					  $"(disable={_disableActive} fullscreen={_fullscreenPaused} whitelist={_whitelistPaused})");
		}
		_popup?.SyncFromGamma(b, t);
		// 托盘 tooltip 一并跟上（2026-09-16）：三个暂停源切换后 tooltip 也要显示
		// 屏幕实际生效值，否则"悬停看一眼"得到的还是暂停前的旧值。
		UpdateTrayTooltip();
	}

	/// <summary>当前色温（K，界面用）。语义同 <see cref="UiTemperature"/>：暂停期间取屏幕实际生效值。</summary>
	public float GetCurrentTemperature()
	{
		return UiTemperature;
	}

	/// <summary>当前亮度（0..1，界面用）。语义同 <see cref="UiBrightness"/>：暂停期间取屏幕实际生效值。</summary>
	public float GetCurrentBrightness()
	{
		return UiBrightness;
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
		return _settings?.ManualSunriseMinutes ?? 480;
	}

	public int GetManualSunsetMinutes()
	{
		return _settings?.ManualSunsetMinutes ?? 1080;
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

	public void SetSolarAdjustEnabled(bool enabled)
	{
		// 诊断（只加日志）：时间调整总开关每次"真变值"都会写屏（日间值 / 当前时刻调度目标），
		// 记录调用者与当前值，用于定位"未点击也跳变"。
		OpLog.Log($"[solar] SetSolarAdjustEnabled({enabled}) caller={Stack2()} current={_settings?.SolarAdjustEnabled}");
		if (_settings == null)
		{
			return;
		}
		// P5a 修复（2026-09-14）：同值重复调用直接早退 —— 否则会重复执行“清接管 / 重置到
		// 白天目标”，在事件重复或自动化重复调用时可能非预期地改变画面。
		// “重开时间调整 = 交回 Solar”的语义只对 关→开 成立。
		if (_settings.SolarAdjustEnabled == enabled)
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
			// P5b 修复（2026-09-14）：平滑门控统一为 !_disableActive && !_fullscreenPaused。
			// 2026-09-16 修复（用户实测报告）：**色温总开关关闭时，开启时间调整不得改动色温**。
			// 此前这里直接把 Solar 的目标色温 tt 交给 StartSmoothTransition，而它内部会
			// 直接 _gamma.SetTemperature(...)，于是绕过了色温总开关（SolarScheduler.Tick()
			// 本身是有 if (_settings.ColorTemperatureEnabled) 判据的，唯独这条开启路径漏了）。
			var (tb, tt) = _solarScheduler?.GetCurrentTargets() ?? (1f, 6600f);
			float tempTarget = _settings.ColorTemperatureEnabled ? tt : 6600f;
			bool tempSmoothEff = temperatureSmooth && _settings.ColorTemperatureEnabled;
			if ((brightnessSmooth || temperatureSmooth) && !IsUiPaused())
			{
				StartSmoothTransition(tb, tempTarget, brightnessSmooth, tempSmoothEff, delegate
				{
					AppSettings? settings = _settings;
					if (settings != null && settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden)
					{
						_solarScheduler?.Start();
						UpdateTrayTooltip();
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
		_gamma?.ApplyUnifiedTarget(dayBrightness, num);
		SaveSettings();
		SyncPopupFromUi();
		this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? dayBrightness);
		this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? num);
		UpdateTrayTooltip();
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
		// P1 修复（2026-09-14 逻辑审查）：必须同时判 SolarManuallyOverridden。
		// 手动接管后（Overridden=1、调度器已停）拖动 Solar 页“昼/夜目标”滑块，
		// 原逻辑会经 ApplyNowInstant 立即把画面拉到 Solar 目标并保持，丢弃用户手动值，
		// 破坏“手动接管持久、直到重开总开关/重开时间调整”的契约。
		if (settings != null && settings.SolarAdjustEnabled && !settings.SolarManuallyOverridden)
		{
			_solarScheduler?.ApplyNowInstant();
		}
	}

	/// <summary>
	/// 诊断辅助（只读）：返回最近 4 层调用者的 "类型.方法" 串。
	/// 用于把"谁触发了这次写屏"直接写进日志，避免再靠猜。
	/// </summary>
	private static string Stack2()
	{
		try
		{
			var st = new System.Diagnostics.StackTrace(1, false);
			var sb = new System.Text.StringBuilder();
			int n = Math.Min(4, st.FrameCount);
			for (int i = 0; i < n; i++)
			{
				var m = st.GetFrame(i)?.GetMethod();
				if (m == null) continue;
				sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
				if (i < n - 1) sb.Append(" <- ");
			}
			return sb.ToString();
		}
		catch { return "?"; }
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
				UpdateTrayTooltip();
			}
		};
		t.Start();
	}

	/// <summary>
	/// F25-C（2026-09-23）：拓扑处理的**串行化门**。
	///
	/// 两条事件源都可能进到 <see cref="OnDisplayChanged"/>：
	///   ① `SystemEventMonitor.DisplayChanged`（隐藏窗口 WndProc，走 UI 线程）；
	///   ② `DisplaySettingsChanged` → `Program.RequestAutoRestart` → `HandleTopologyChangedWithoutRestart`
	///      —— `Microsoft.Win32.SystemEvents` 在**自己的专用线程**上抛事件。
	/// ⇒ 两者可**并发**进入。而 `RefreshDisplays()` 里是「先 Dispose 旧 DC → 再装新 DC」，
	///    两个实例交叠会**互相 Dispose 对方刚装上的 DC**，随后写屏就写到失效 DC 上
	///    ⇒ 驱动丢回原生 ⇒ **闪**。
	/// ⇒ 用一把门把整段串行化（后到者等前者跑完，然后自己重新枚举一遍 —— 结果仍然正确）。
	/// ⛔ 不要与 `GammaController._lock` 形成环：本门只包 `OnDisplayChanged`，
	///    而门内调用的 `_gamma.*` 只取它自己的 `_lock`；重试回调只取 `_lock`、不取本门。
	/// </summary>
	private readonly object _topologyGate = new();

	private void OnDisplayChanged()
	{
		lock (_topologyGate)
		{
			OnDisplayChangedCore();
		}
	}

	private void OnDisplayChangedCore()
	{
		if (_gamma != null)
		{
			AppSettings? settings = _settings;
			if (settings != null && settings.GammaSelfHealEnabled && !_fullscreenPaused)
			{
				// ------------------------------------------------------------------
				// F22（2026-09-22）①：**先救屏，再枚举**（补救写屏前置）。
				//
				// 现象（用户实测）：「低亮度下拔插显示器会闪一下，但画面稳定后能维持
				//   亮度，色温也是如此」。定量取证见 `_devtools/低亮度拔插屏闪烁_20260922.md`：
				//   · app **从未**写过 100% —— 空窗内所有 `[gamma/apply]` 的 curB 都是 0；
				//   · 是 Windows/驱动在拓扑变化时把 ramp **复位为原生**，而补救写屏太晚：
				//     实测 `WM_DISPLAYCHANGE` → 首次**成功**写屏 = 0.9~1.5 s（低亮度两次），
				//     个别窗口 7.2 / 9.7 s，快窗口仅 9~300 ms ⇒ **双峰**；
				//   · 根因：`RefreshDisplays()`（重新枚举 + 新建 DC，实测 ~0.9 s）排在
				//     `ReapplyAllDisplays()` **之前** ⇒ 补救被推后到枚举之后。
				// 修法：把补救写屏**前置**。此刻 `_displays` 仍是变化前的集合，写它们已足以
				//   覆盖"看得见的闪"（真机屏本来就在集合里；实测被驱动拒收的也正是真机屏
				//   SAC2466，幽灵屏 UGRFFFF 反而成功）。
				//   ⚠ 该次写屏**禁用 LearnRampFloor**：集合里可能含刚被拔掉、DC 已失效的屏，
				//     写入必失败 —— 不设防会把这次假失败学成「驱动有下限」并永久落盘。
				//   新插入的屏由下面的 `RefreshDisplays()` 播种 + 第二次 `ReapplyAllDisplays()` 覆盖。
				// ------------------------------------------------------------------
				_gamma.ReapplyAllDisplaysForTopologyChange();

				// ------------------------------------------------------------------
				// F25-B（2026-09-23）：启动**后台**重试。
				//
				// 背景（用户实测「物理拔出欺骗器时会出现数次闪屏」）：
				//   拔欺骗器会产生**一串** `WM_DISPLAYCHANGE`（实测 00:28:57 / 00:29:05 /
				//   00:29:13 / 00:29:17 / 00:29:30 / 00:30:03 / 00:30:12，间隔 2~5 s），
				//   每次都会：① 驱动把 ramp 复位；② 上面这次"立即写"在拓扑 teardown 期
				//   **被驱动拒收**（`[gamma] FAILED` + `ok=False`，写的是对的值）；
				//   ③ 然后卡在下一行 `RefreshDisplays()`（`Monitor.GetAll()` 实测 **4.33 s**）
				//   —— 这 4.3 s 内**没有任何重试**（F25-A 之前连锁都拿不到）
				//   ⇒ 屏幕以原生亮度停留 4.3 s，一串事件 = 好几次闪。
				//
				// 修法：起一个**有界**的后台重申循环（100 ms × 30 = 3 s），拓扑一稳、
				//   驱动一接受就立刻写回，**不必等 `Monitor.GetAll()` 结束**。
				// ⛔ 必须是后台线程：UI 线程此刻正阻塞在 `RefreshDisplays()` 里，
				//    WinForms Timer 根本不会 tick（同一坑曾导致启动动画丢 428 ms 的帧）。
				// ⛔ 走 `ReapplyAllDisplaysForTopologyChange()`：尊重暂停/停用 +
				//    **禁用驱动下限的被动学习**（churn 期写失败绝不能污染该状态）。
				// ------------------------------------------------------------------
				StartTopologyRetry();

				_gamma.RefreshDisplays();
				// ------------------------------------------------------------------
				// F21（2026-09-20）：拓扑变化后**立即按当前设定值重写一遍**。
				//
				// 用户对照实测：控制面板禁用/启用**不闪**、手动**拔出**不闪、
				//   只有手动**插入**会闪 1~2 下。
				// 成因：插入新屏时 Windows/驱动会把 ramp 重置为**原生**；
				//   原来这里只做 `RefreshDisplays()` + 开关同步，**不写 gamma**
				//   ⇒ 要等 `SolarScheduler` 下一拍（≤2s）才写回暖色 ⇒ 中间那段就是"闪"。
				// `ReapplyAllDisplays()` 在**全局暂停**时写原生（F14 已保证），
				//   所以停用/全屏/白名单期间调它是安全的。
				// F22 起它同时承担"给新插入屏补写"的职责（见上）。
				// ------------------------------------------------------------------
				_gamma.ReapplyAllDisplays();
				SyncGammaEnabledFromSettings();
				UpdateTrayTooltip();
			}
		}
	}

	/// <summary>
	/// 2026-09-20（F20）：供 <c>Program</c> 在「**显示拓扑变化但 DPI 未变**」时调用（拔插屏）。
	/// 只刷新显示器列表 + 重应用 gamma，**绝不重启进程**。
	///
	/// 背景（用户 01:24 报「关掉显示器瞬间闪屏：闪一下恢复正常色温，又迅速变回暖色」）：
	///   `Program.cs` 的 `DisplaySettingsChanged` 原先**一律**走 `RequestAutoRestart()`，
	///   于是拔屏会让整个进程自杀重启 ⇒ 新进程启动时先写原生(100%/6600K) ⇒
	///   直到 `SolarScheduler` 下一拍（实测隔了 **9 秒**）才写回暖色 ⇒ 就是那一次"闪"。
	///   实测日志（01:22:16~26）时间戳完整吻合。
	/// </summary>
	// ==================================================================
	// F25-B（2026-09-23）：拓扑变化后的**后台**重申（非 UI 线程）
	// ==================================================================

	/// <summary>F25-B：重申间隔。</summary>
	/// <summary>F25-B：重申间隔 **30 ms**（原 100 ms）。</summary>
	/// <remarks>
	/// ⭐ 为什么从 100 改到 30（2026-09-23 02:11 实测）：
	///   英伟达面板模拟插拔的监控数据显示，一次"跳原生"的**全亮停留**是 15~203 ms。
	///   其中 15 ms 就被纠正的那些是**其它路径**（F22 立即写 / 刷新后的 `ReapplyAllDisplays()`）接住的；
	///   而落到**重试拍子**上的那些，就被 100 ms 的间隔拖成了 94 / 109 / 125 / 203 ms。
	///   ⇒ 60 Hz 下 100 ms ≈ 6 帧（肉眼可辨），30 ms ≈ 2 帧（基本不可辨）。
	/// ⛔ 并发度仍由 `_topologyRetryInFlight` 钳在 1；卡顿期间该拍会跳过，间隔再密也不会打群。
	/// ⛔ 上限仍由 `_topologyRetryUntilUtc`（墙钟 3000 ms）兜住，不会无限写屏。
	/// </remarks>
	private const int TopologyRetryIntervalMs = 30;

	/// <summary>F25-B：重申窗口 **3000 ms**（**按墙钟**，不按次数）。</summary>
	private const int TopologyRetryWindowMs = 3000;

	/// <summary>
	/// F25-B：重申窗口的**截止时刻**（`DateTime.UtcNow`）。
	///
	/// ⛔ 为什么改成墙钟而不是"30 次计数"（2026-09-23 01:32 实测）：
	///    英伟达面板模拟插拔时，单次 `SetDeviceGammaRamp` 会在驱动里**卡住 3.8~5.4 s**
	///    （证据：5 次事件里有 3 次的"首次被接受的写屏"间隔 = 3871 / 3807 / 5416 ms，
	///     期间还各自被拒收 1~3 次）。此时若按**次数**计预算：那一拍把后续 243 拍全部
	///    挡在"在飞"标志外，30 次的预算也在这几秒内**被跳过的拍耗尽** ⇒ 重试在最需要时
	///    **被自己掐掉**。
	///    ⇒ 改按**墙钟** 3 s：跳过不消耗预算；只要仍在窗口内，每 100 ms 都会再试一次。
	/// </summary>
	private DateTime _topologyRetryUntilUtc = DateTime.MinValue;

	/// <summary>
	/// F25-B：后台重申定时器。
	/// ⛔ 必须是 `System.Threading.Timer`：本类顶部有 `using Timer = System.Windows.Forms.Timer;`，
	///    裸写 `Timer` 会拿到 **UI 线程**定时器，而调用它的那一刻 UI 线程正阻塞在
	///    `RefreshDisplays()`（`Monitor.GetAll()` 实测 **4.33 s**）里 ⇒ 一帧都不会 tick。
	///    （同一坑此前已导致启动动画丢掉 428 ms 的帧。）
	/// </summary>
	private System.Threading.Timer? _topologyRetryTimer;

	/// <summary>
	/// F25-B：**在飞标志**（0=空闲 / 1=有一次重试正在写屏）。
	///
	/// ⛔ 为什么必须有它（2026-09-23 01:11 实测探针抓到）：
	///    `System.Threading.Timer` 在**回调耗时超过周期**时会**并发重入**（把回调排到线程池）。
	///    本方法一次要写 2 块屏、且要抢 `_lock`，耗时经常 &gt; 100 ms ⇒ 30 个回调**同时**压在锁上
	///    ⇒ 现象：① 期间一条日志都没有（都还没走完）⇒ 看起来像"重试没触发"；
	///    ② 锁一释放就**集中排空**（实测 28 条在 397 ms 内，间隔从 38 ms 递减到 ~0 ms）；
	///    ③ `#n/30` 编号乱序（日志打在写屏**之后**，所以日志顺序 ≠ 递减顺序）。
	///    ⇒ 更糟的是：几十个并发 `SetDeviceGammaRamp` 会与 `RefreshDisplays()` 正在 Dispose 的 DC
	///      撞在一起，可能写到失效 DC 而被驱动丢回原生 ⇒ **自己制造闪烁**。
	/// ⇒ 用一个"在飞"标志把并发度**钳到 1**：上一拍没写完就**直接跳过**这一拍（不排队）。
	/// </summary>
	private int _topologyRetryInFlight;

	/// <summary>
	/// F25-B：启动/续期一次有界重申。每次拓扑变化都调用；重复调用会重置计数与周期。
	/// </summary>
	private void StartTopologyRetry()
	{
		_topologyRetryUntilUtc = DateTime.UtcNow.AddMilliseconds(TopologyRetryWindowMs);
		if (_topologyRetryTimer == null)
		{
			_topologyRetryTimer = new System.Threading.Timer(
				_ => OnTopologyRetryTick(), null,
				TopologyRetryIntervalMs, TopologyRetryIntervalMs);
		}
		else
		{
			_topologyRetryTimer.Change(TopologyRetryIntervalMs, TopologyRetryIntervalMs);
		}
	}

	/// <summary>
	/// F25-B：一次重申。到上限即停表。
	/// 走 <see cref="GammaController.ReapplyAllDisplaysForTopologyChange"/>：节奏与 F22 完全一致
	/// （尊重全局/逐屏暂停与停用 + **禁用驱动下限的被动学习** —— churn 期写失败绝不能
	/// 被学成「本机驱动有下限」并永久落盘）。
	///
	/// ⛔ 诊断（2026-09-23 01:05）：用户复测时发现**某几轮拓扑变化后 2.87 s 内一条
	///    `[gamma/apply]` 都没有**，而事后又出现"28 条在 390 ms 内密集倾泻"（队列排空的形态）。
	///    但源码核对确认 F25-A（`Monitor.GetAll()` 在锁外）与 F25-B 均已生效 ⇒ **无法从日志唯一归因**。
	///    因此本方法**每次必打一行**，把"是否触发 / 有几块屏 / 是否暂停 / 是否在过渡独占窗口 /
	///    是否有异常"全部记下来，用实测替代推测。
	/// </summary>
	private void OnTopologyRetryTick()
	{
		// ⛔ 并发度钳到 1（见 `_topologyRetryInFlight` 注释）：
		//    上一拍还在写屏就**直接跳过这一拍**，绝不排队 —— 排队就是"惊群"的成因。
		if (System.Threading.Interlocked.CompareExchange(ref _topologyRetryInFlight, 1, 0) != 0)
		{
			OpLog.Log("[f25b] 上一拍仍在写屏 ⇒ 跳过本拍（防惊群）");
			return;
		}
		try
		{
			// ⛔ 按**墙钟**判定窗口（见 `_topologyRetryUntilUtc` 注释）：跳过/卡住都不消耗预算，
			//    只要还在 3 s 窗口内就继续每 100 ms 试一次 —— 驱动卡顿时这才顶得住。
			if (DateTime.UtcNow > _topologyRetryUntilUtc)
			{
				_topologyRetryTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
				OpLog.Log($"[f25b] 重申 窗口 {TopologyRetryWindowMs} ms 已到 ⇒ 停表");
				return;
			}
			int n = _gamma?.DisplayCount ?? -1;
			bool paused = _gamma?.IsPaused ?? false;
			bool trans = _gamma?.IsRampTransitionActive ?? false;
			_gamma?.ReapplyAllDisplaysForTopologyChange();
			long leftMs = (long)(_topologyRetryUntilUtc - DateTime.UtcNow).TotalMilliseconds;
			OpLog.Log($"[f25b] 重申 剩余{leftMs}ms displays={n} paused={paused} rampTrans={trans}");
		}
		catch (Exception ex)
		{
			// ⛔ 这里以前是空 catch ⇒ 若每次都抛异常，会出现"重试 30 次却一条日志都没有"的
			//    静默失效，正是本次要查的形态。现在必须留痕。
			OpLog.Log($"[f25b] 重申 异常：{ex.GetType().Name}: {ex.Message}");
		}
		finally
		{
			System.Threading.Interlocked.Exchange(ref _topologyRetryInFlight, 0);
		}
	}

	public void HandleTopologyChangedWithoutRestart() => OnDisplayChanged();

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

	/// <summary>
	/// 按当前 _settings 把"显示器独立控制"整体同步到运行时（gamma/弹窗），
	/// 语义与启动路径一致（见 Initialize 中 PerMonitorEnabled 分支）：
	///  * 开：播种统一种子 → gamma 进独立模式 → 恢复逐屏记忆（亮度/色温/停用）；
	///  * 关：gamma 回统一模式，全部屏以种子值播种启用。
	/// 用于"重置设置"与"导入设置"之后，避免出现 settings 与运行时/弹窗脱节
	/// （例如导入了独立开启的配置，但弹窗仍是单行；重置后残留停用屏等）。
	/// </summary>
	private void ApplyPerMonitorFromSettings()
	{
		if (_settings == null || _gamma == null) return;
		float entryB = _settings.LastBrightness;
		float entryT = _settings.ColorTemperatureEnabled ? _settings.LastTemperature : 6600f;
		_perMonitorEntryBrightness = entryB;
		_perMonitorEntryTemperature = entryT;
		if (_settings.PerMonitorEnabled)
		{
			_gamma.ApplyUnifiedTarget(entryB, entryT);
			_gamma.PerMonitorEnabled = true;
			_gamma.ReconcileDisplayStates();
			RestoreSavedDisplayStates();
		}
		else
		{
			_gamma.PerMonitorEnabled = false;
			_gamma.ResetDisplayStates(entryB, entryT);
			_gamma.ApplyUnifiedTarget(entryB, entryT);
		}
		if (_popup != null)
		{
			// 2026-09-19（D5 同步修正）：弹窗必须按"独立控制的**实际生效状态**"渲染，
			// 不能只看 `settings.PerMonitorEnabled`。
			// 单屏时设置页把「独立控制」开关**显示为关并锁定**（只改 UI、不改配置值 —— 保留用户偏好），
			// 于是 `settings.PerMonitorEnabled` 仍可能是 true ⇒ 弹窗照旧按**逐屏模式**渲染
			// ⇒ 用户看到「设置页里那块屏是关的，左键弹窗里却还开着（有屏幕名、在逐屏行里）」。
			// 单屏时"独立控制"本来就无意义（与统一控制等价）⇒ 统一按**统一模式**渲染。
			bool perMonEffective = _settings.PerMonitorEnabled && _gamma.GetDisplayIds().Count >= 2;
			_popup.PerMonitorEnabled = perMonEffective;
			_popup.SetDisplays(BuildDisplayRows());
		}
	}

	private void OnFullscreenEntered(IntPtr hMonitor)
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

		// ------------------------------------------------------------------
		// 2026-09-19（定稿 §四）：逐屏分支。
		// 门控 = 全屏暂停开 && 独立控制开 && 受控屏≥2 ⇒ 不满足时**原样走下面的全局路径**
		// （逐字不变 ⇒ 零回归）。句柄拿不到 / 对不上任何屏时也**保守退化为全局**
		// —— 宁可多暂停一块，也不能漏暂停（行为等价于改动前）。
		// ------------------------------------------------------------------
		if (IsFullscreenPerMonitorGateOk())
		{
			string? edid = EdidForMonitorHandle(hMonitor);
			if (edid != null)
			{
				EnterFullscreenPerMonitor(edid);
				return;
			}
			OpLog.Log($"[fs/permon] 未能将 HMONITOR=0x{hMonitor.ToInt64():X} 映射到显示器 ⇒ 退化为全局暂停");
		}

		bool flag = _fullscreenAnimTimer != null && _fullscreenAnimExit;
		if (!_fullscreenPaused || flag)
		{
			if (!_fullscreenPaused)
			{
				_fullscreenBrightnessBefore = PendingOrCurrentBrightness();
				_fullscreenTemperatureBefore = PendingOrCurrentTemperature();
				_fullscreenPaused = true;
				_gamma?.SetPaused(paused: true);
			}
			StartFullscreenTransition(1f, 6600f);
		}
	}

	private void OnFullscreenExited(IntPtr hMonitor)
	{
		// 逐屏：先收干本功能自己的暂停集合，再做"离开 S"的逐屏过渡。
		// ⚠ 必须放在 _fullscreenPaused 判断**之前** —— 逐屏模式下它可能因
		//   "全部受控屏都被暂停"而为 true，走全局路径会把过渡写成单一起点 ⇒ 闪变。
		if (_fsPausedScreens.Count > 0)
		{
			ExitFullscreenPerMonitor();
			return;
		}
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

	// ==================================================================
	//  全屏逐屏（2026-09-19 定稿 §四）：实现细节
	// ==================================================================

	/// <summary>是否有**任何**一块屏因全屏被暂停 —— 白名单"让位"的唯一判据。</summary>
	public bool AnyFullscreenPaused() => _fsPausedScreens.Count > 0;

	/// <summary>当前被全屏暂停的屏集合（自动化/诊断用）。</summary>
	public string[] GetFullscreenPausedScreens() => _fsPausedScreens.ToArray();

	/// <summary>HMONITOR → EDID 键；对不上任何在线屏时返回 null。
	/// 零新增 API：HMONITOR 由 `Monitor.GetAll()` 的 `EnumDisplayMonitors` 回调顺手带出
	/// （`Monitor.HMonitor`），直接比对即可 —— 连 `MONITORINFOEX.szDevice` 都不需要。
	/// ⚠ 不是 `DeviceContext.Handle`（那是 HDC）。</summary>
	private static string? EdidForMonitorHandle(IntPtr hMonitor)
	{
		if (hMonitor == IntPtr.Zero) return null;
		foreach (Monitor m in Monitor.GetAll())
		{
			if (m.HMonitor == hMonitor) return m.EdidId;
		}
		return null;
	}

	/// <summary>
	/// 进入某块屏的「全屏暂停」。
	/// ⚠ 必须**先让白名单即时让位**，不能等它 1 s 的 tick —— 否则在这 1 s 窗口里
	///   白名单的 `ClearWhitelistPerMonitorPause()` 会把我们刚置上的 `Paused` 一并解除
	///   （它会遍历自己的 `_wlPausedScreens` 无条件 `SetDisplayPaused(id,false)`）。
	///   即时让位后白名单集合已空、动画已停 ⇒ 它的下一次 tick 走到让位分支时
	///   `_wlPausedScreens.Count == 0 && !hadAnim` ⇒ 直接 return，不触碰任何 Paused。
	/// </summary>
	private void EnterFullscreenPerMonitor(string edid)
	{
		if (_gamma == null) return;

		ClearWhitelistPerMonitorPause(reapply: false);

		List<string> enter = new();
		List<string> left = new();
		foreach (string id in _fsPausedScreens)
		{
			if (!string.Equals(id, edid, StringComparison.OrdinalIgnoreCase)) left.Add(id);
		}
		if (!_fsPausedScreens.Contains(edid)) enter.Add(edid);

		_fsPausedScreens.Clear();
		_fsPausedScreens.Add(edid);

		// 与逐屏白名单 §7.3 同款：**全部受控屏都被暂停**才等价"全局暂停"
		// （此时 UI 读数理应回落 100%/6600K）；部分暂停时必须保持 false，
		// 否则 `UiBrightness` 会回落 100%，与"显示可控屏平均"冲突。
		bool allPaused = _fsPausedScreens.Count >= CountControlledDisplays();
		_fullscreenPaused = allPaused;
		SyncPopupFromUi();

		OpLog.Log($"[fs/permon] enter=[{string.Join(",", enter)}] left=[{string.Join(",", left)}] " +
		          $"allPaused={allPaused} hmon-edid={edid}");
		StartFullscreenPerMonitorTransition(enter, left);
	}

	/// <summary>全屏窗口消失 ⇒ 清空暂停集合并把各屏平滑写回**生效前的值**。</summary>
	private void ExitFullscreenPerMonitor()
	{
		if (_gamma == null) return;
		List<string> left = _fsPausedScreens.ToList();
		_fsPausedScreens.Clear();
		_fullscreenPaused = false;
		SyncPopupFromUi();
		OpLog.Log($"[fs/permon] exit left=[{string.Join(",", left)}]");

		// ------------------------------------------------------------------
		// F22（2026-09-20）：**退出全屏前先看白名单会不会立刻接管**。
		//
		// 用户实测（02:13:43~45 日志 + 外部 ramp 采样完全吻合）：
		//   [43.334] [fs/permon] exit          → 启动「恢复到设定值」过渡（屏从原生日渐回暖色）
		//   [44.333] [ui] push … whitelist=True → 白名单 1s 后接管，又启动「回到原生」过渡
		//   ⇒ 两次**方向相反**的过渡先后跑 ⇒ 观感「闪一下（先变暖）再变回原生」；
		//     且第二次起手时打断第一次 ⇒ 采样里出现 Δ27170 的**硬跳**。
		//
		// 修法：若白名单**此刻就会暂停**（同一应用仍在白名单里且可见），
		//   就**不要**恢复设定值 —— 屏幕本来就该是原生，直接把暂停移交给白名单。
		//   （此时屏上已是原生 ⇒ 白名单那侧即使启动过渡也是"净变化 0"，F17 会跳过写屏。）
		// ------------------------------------------------------------------
		if (WhitelistWillTakeOver())
		{
			OpLog.Log("[fs/permon] exit：白名单会立刻接管 ⇒ 跳过「恢复设定值」过渡，直接移交");
			EvaluateWhitelistPause();
			return;
		}

		StartFullscreenPerMonitorTransition(new List<string>(), left);
	}

	/// <summary>
	/// F22：白名单**此刻**是否会暂停（判据与 <see cref="EvaluateWhitelistPause"/> 的 want 完全一致）。
	/// 用于"全屏退出 / 功能停用退出时预判白名单接管"，避免两次反向过渡。
	///
	/// <paramref name="ignoreDisable"/>：默认 false —— 功能停用（L0）生效时白名单本来就要让位，
	/// 此时问"白名单会不会接管"没有意义、直接返回 false。
	/// 但**停用正在退出的那一刻**需要问这个问题（那时 `_disableActive` 还是 true），
	/// 故允许调用方传 true 跳过这道自检（见 <see cref="ApplyDisable"/> 的 F26 分支）。
	/// </summary>
	private bool WhitelistWillTakeOver(bool ignoreDisable = false)
	{
		AppSettings? s = _settings;
		if (s == null) return false;
		if (!ignoreDisable && _disableActive) return false;     // L0 优先，不归白名单管
		if (!s.AppWhitelistEnabled) return false;
		if (s.AppWhitelist == null || s.AppWhitelist.Count == 0) return false;
		try
		{
			var set = new HashSet<string>(s.AppWhitelist, StringComparer.OrdinalIgnoreCase);
			return AppWhitelistService.AnyVisibleWindowFor(set);
		}
		catch { return false; }
	}

	/// <summary>
	/// 全屏逐屏过渡：进入的屏平滑到原生；离开的屏平滑写回**生效前的值**。
	/// "生效前的值"无需额外快照 —— `Paused` 期间该屏 state 被 `IsWritable` 挡住写不进去，
	/// 故 `state.Brightness / Temperature` 天然等于进入暂停前的值（与逐屏白名单 §11.1 同结论）。
	/// </summary>
	private void StartFullscreenPerMonitorTransition(List<string> entered, List<string> left)
	{
		if (_gamma == null || _settings == null) return;
		if (entered.Count == 0 && left.Count == 0) return;

		bool smoothB = _settings.BrightnessSmooth;
		bool smoothT = _settings.TemperatureSmooth;
		DateTime now = DateTime.Now;

		foreach (string id in entered)
		{
			_gamma.SetDisplayPaused(id, true);      // 立即置位：该屏的写值路径此刻起冻结
			// ⚠ 起点必须取**屏幕实际值**（ramp 反算），**不能**取 `state`：
			//   若该屏此刻正被白名单暂停（屏幕已是原生、state 仍是设定值），取 state
			//   会让首帧把它写回设定值 ⇒ 肉眼"跳一下"再平滑回原生（实测同类问题见 §Z21）。
			//   取实际值还天然吸收了"白名单动画正在飞、屏幕停在中间帧"的情形。
			(float cb, float ct) = _gamma.ReadActualFor(id);
			if (float.IsNaN(cb) || float.IsNaN(ct))
			{
				DisplayState fallback = _gamma.GetDisplayState(id);
				cb = fallback.Brightness;
				ct = fallback.Temperature;
			}
			PushFsAnim(id, cb, ct, 1f, 6600f, true, now);
		}
		foreach (string id in left)
		{
			DisplayState st = _gamma.GetDisplayState(id);
			PushFsAnim(id, 1f, 6600f, st.Brightness, st.Temperature, false, now);
			_gamma.SetDisplayPaused(id, false);     // 恢复受控
		}

		if (!smoothB && !smoothT)
		{
			// 无平滑设置 ⇒ 瞬时落定（与既有全局路径 !flag && !flag2 分支同语义）
			foreach (string id in entered) _gamma.ResetDisplayNative(id);
			FinishFsTransition();
			return;
		}
		_fsAnimTimer ??= CreateFsAnimTimer();
		_fsAnimTimer.Start();
	}

	/// <summary>登记/覆盖某屏的全屏过渡（若该屏正在过渡中，从它**当前实际帧值**接着走，避免跳变）。</summary>
	private void PushFsAnim(string id, float fromB, float fromT, float toB, float toT, bool toNative, DateTime now)
	{
		if (_fsAnims.TryGetValue(id, out WlScreenAnim? prev))
		{
			double p = (now - prev.Start).TotalMilliseconds / WhitelistAnimMs;
			if (p > 1.0) p = 1.0;
			double e = EaseOutCubic(p);
			fromB = prev.FromB + (prev.ToB - prev.FromB) * (float)e;
			fromT = prev.FromT + (prev.ToT - prev.FromT) * (float)e;
		}
		_fsAnims[id] = new WlScreenAnim { FromB = fromB, FromT = fromT, ToB = toB, ToT = toT, ToNative = toNative, Start = now };
		SyncAnimatingDisplays();
	}

	private Timer CreateFsAnimTimer()
	{
		var timer = new Timer { Interval = 30 };
		timer.Tick += OnFsAnimTick;
		return timer;
	}

	private void OnFsAnimTick(object? sender, EventArgs e)
	{
		if (_gamma == null || _settings == null) { StopFsAnim(); return; }

		bool smoothB = _settings.BrightnessSmooth;
		bool smoothT = _settings.TemperatureSmooth;
		DateTime now = DateTime.Now;
		bool allDone = true;

		foreach (KeyValuePair<string, WlScreenAnim> kvp in _fsAnims.ToList())
		{
			WlScreenAnim a = kvp.Value;
			double p = (now - a.Start).TotalMilliseconds / WhitelistAnimMs;
			if (p >= 1.0) p = 1.0; else allDone = false;
			double ease = EaseOutCubic(p);
			float b = smoothB ? a.FromB + (a.ToB - a.FromB) * (float)ease : a.ToB;
			float t = smoothT ? a.FromT + (a.ToT - a.FromT) * (float)ease : a.ToT;
			_gamma.ApplyFrameToDisplay(kvp.Key, b, t);
			if (p >= 1.0)
			{
				if (a.ToNative) _gamma.ResetDisplayNative(kvp.Key);
				_fsAnims.Remove(kvp.Key);
				SyncAnimatingDisplays();
			}
		}

		if (!allDone || _fsAnims.Count > 0) return;
		StopFsAnim();
		FinishFsTransition();
	}

	private void FinishFsTransition()
	{
		_gamma?.ReapplyAllDisplays();
		OpLog.Log("[fs/permon] 逐屏过渡完成");
	}

	private void StopFsAnim()
	{
		_fsAnimTimer?.Stop();
		_fsAnimTimer?.Dispose();
		_fsAnimTimer = null;
		_fsAnims.Clear();
		SyncAnimatingDisplays();
	}

	/// <summary>清空全屏逐屏暂停（关开关 / 门控失效 / 屏幕离线时调用）。
	/// `reapply=false` 用于**让位**（写屏交给接管方的动画，避免抢写造成闪动）。</summary>
	private void ClearFullscreenPerMonitorPause(bool reapply)
	{
		if (_gamma == null) return;
		bool hadAnim = _fsAnims.Count > 0;
		StopFsAnim();
		if (_fsPausedScreens.Count == 0 && !hadAnim)
		{
			_fullscreenPaused = false;
			return;
		}
		List<string> wasPaused = _fsPausedScreens.ToList();
		foreach (string id in wasPaused) _gamma.SetDisplayPaused(id, false);
		_fsPausedScreens.Clear();
		_fullscreenPaused = false;
		SyncPopupFromUi();
		if (reapply && wasPaused.Count > 0)
		{
			// 恢复也要**平滑**（当作"离开 S"），不能瞬时 ReapplyAllDisplays
			// —— 否则用户点开关时会看到屏幕跳一下。
			StartFullscreenPerMonitorTransition(new List<string>(), wasPaused);
		}
		else if (reapply)
		{
			_gamma.ReapplyAllDisplays();
		}
		OpLog.Log("[fs/permon] 逐屏暂停已清空（reapply=" + reapply + "）");
	}

	/// <summary>每 tick 幂等清理：把已离线（拔线/禁用）的屏从暂停集合里剔除，
	/// 否则它们会一直占着 `CountControlledDisplays()` 的分母，让 `allPaused` 判错。</summary>
	private void PruneFsPausedScreens()
	{
		if (_fsPausedScreens.Count == 0 || _gamma == null) return;
		var live = new HashSet<string>(_gamma.GetDisplayIds(), StringComparer.OrdinalIgnoreCase);
		List<string> dead = _fsPausedScreens.Where(id => !live.Contains(id)).ToList();
		if (dead.Count == 0) return;
		foreach (string id in dead) _fsPausedScreens.Remove(id);
		_fullscreenPaused = _fsPausedScreens.Count > 0 && _fsPausedScreens.Count >= CountControlledDisplays();
		OpLog.Log("[fs/permon] 移除已离线的暂停屏 [" + string.Join(",", dead) + "]");
	}

	/// <summary>用户偏好（持久化）。门控不满足时功能仍锁定，但偏好保留。</summary>
	public bool GetFullscreenPerMonitor() => _settings?.FullscreenPerMonitor ?? false;

	/// <summary>门控（定稿 §二）：全屏自动暂停开 && 独立控制开 && 受控屏 ≥ 2。
	/// 不满足 ⇒ 功能锁定（UI 置灰）且**不生效**，但不重置用户偏好。</summary>
	public bool IsFullscreenPerMonitorGateOk()
	{
		if (_settings == null) return false;
		if (!_settings.PauseInFullscreenEnabled) return false;   // 本功能自身的开关
		return IsFullscreenPerMonitorHostReady();
	}

	/// <summary>门控中**与自身开关无关**的部分：独立控制开 && 受控屏 ≥ 2。
	/// UI 用它决定子开关是否置灰（置灰时仍保留用户偏好）。</summary>
	public bool IsFullscreenPerMonitorHostReady() => IsPerMonitorFeatureHostReady();

	/// <summary>「按显示器生效」类功能的**共用**宿主判据（逐屏白名单 / 全屏逐屏 同款）：
	/// 独立控制开 && 受控屏 ≥ 2。</summary>
	public bool IsPerMonitorFeatureHostReady()
	{
		if (_settings == null || _gamma == null) return false;
		if (!_settings.PerMonitorEnabled) return false;
		return HasTwoControlledDisplays();
	}

	/// <summary>
	/// 受控屏是否 ≥ 2（**单一条件**，供 UI 区分"独立控制没开"与"受控屏不足"两种锁定原因）。
	///
	/// 2026-09-20（用户报）：全屏「按显示器生效」子开关的悬停提示原来是**固定文案**
	/// 「显示器独立控制开关没开」⇒ 门控已满足（开关已解锁）时仍显示这句错话。
	/// 要按原因给提示，就必须能把 <see cref="IsPerMonitorFeatureHostReady"/> 的两个条件**分开问**。
	/// </summary>
	public bool HasTwoControlledDisplays() => CountControlledDisplays() >= 2;

	/// <summary>
	/// 把「全局全屏暂停」**就地收窄**为「逐屏暂停」—— 用户在全屏进行中才打开「按显示器生效」子开关的场景。
	/// 照搬逐屏白名单的既有模式：**先把所有受控屏播种进暂停集合，再解除全局 `_paused`**。
	/// 否则在 `SetPaused(false)` 到"过渡把各屏写到目标值"之间存在空窗，周期性全量写屏会把屏幕
	/// 写成设定值、随即又被动画拉回 ⇒ 观感"闪一下"（正是白名单 §13 实测抓到的那条）。
	/// </summary>
	private void NarrowGlobalFullscreenToPerMonitor(IntPtr hMonitor)
	{
		if (_gamma == null || _settings == null) return;
		string? edid = EdidForMonitorHandle(hMonitor);
		if (edid == null)
		{
			OpLog.Log("[fs/permon] 收窄失败：拿不到全屏所在屏的 EDID ⇒ 保持全局暂停");
			return;
		}

		ClearWhitelistPerMonitorPause(reapply: false);

		List<string> seeded = new();
		foreach (string sid in _gamma.GetDisplayIds())
		{
			if (_settings.MonitorStates != null &&
			    _settings.MonitorStates.TryGetValue(sid, out MonitorState? ms) && ms != null && !ms.Enabled)
				continue;   // 手动停用的屏不参与（保持冻结）
			seeded.Add(sid);
			_gamma.SetDisplayPaused(sid, true);   // 此刻它们**本来全是原生** ⇒ 标 Paused 只是继续写原生
		}
		_gamma.SetPaused(false);

		_fsPausedScreens.Clear();
		_fsPausedScreens.Add(edid);
		_fullscreenPaused = _fsPausedScreens.Count >= CountControlledDisplays();
		SyncPopupFromUi();

		// 全屏那块屏**留在暂停里**（无需过渡，屏上就是原生）；其余屏从原生平滑恢复到各自设定值。
		List<string> left = seeded.Where(s => !string.Equals(s, edid, StringComparison.OrdinalIgnoreCase)).ToList();
		OpLog.Log($"[fs/permon] 全局暂停收窄为逐屏：enter={edid} left=[{string.Join(",", left)}]");
		StartFullscreenPerMonitorTransition(new List<string>(), left);
	}

	/// <summary>切换用户偏好；立即生效（不等事件/定时器）。</summary>
	public void SetFullscreenPerMonitor(bool enabled)
	{
		if (_settings == null) return;
		_settings.FullscreenPerMonitor = enabled;
		SaveSettings();
		if (!enabled)
		{
			// 关掉子开关 ⇒ 立刻收干净（平滑写回设定值），不留"半暂停"残影。
			ClearFullscreenPerMonitorPause(reapply: true);
		}
		else if (_fullscreenPaused)
		{
			// 打开时若当前**正处于全屏**：就地收窄，否则要等下一次前台变化才会重新评估
			//（用户会觉得"开了没反应"）。
			NarrowGlobalFullscreenToPerMonitor(_systemMonitor?.CurrentFullscreenMonitor ?? IntPtr.Zero);
		}
		UpdateTrayTooltip();
	}

	private void StartFullscreenTransition(float targetBright, float targetTemp, bool exit = false,
	                                 float fromBright = float.NaN, float fromTemp = float.NaN)
	{
		if (_gamma == null)
		{
			return;
		}
		SyncPopupFromUi();  // 方案 B：暂停/恢复动画开始，界面按当前暂停状态刷新
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
				SyncPopupFromUi();  // 方案 B：暂停标志复位，界面回到设定值
			}
			_gamma?.ApplyPausedState();
			return;
		}
		_fullscreenAnimSmoothB = flag;
		_fullscreenAnimSmoothT = flag2;
		_fullscreenAnimTargetBright = targetBright;
		_fullscreenAnimTargetTemp = targetTemp;
		// ⚠ 起点默认取"读某块屏的 ramp 反算"的值。逐屏模式下各屏值不同，若被读到的那块
		// 屏恰好是"已暂停=原生"，起点就会凭空变成原生 ⇒ 目标屏被一帧写成原生（实测跳变，
		// Δ=0.2074/0.2212 一帧走完 94%；且原生 ramp 会被反算成 6558K 而非 6600K）。
		// ⇒ 逐屏场景调用方应显式传入"跳过暂停屏后的平均值"（Current* 已是该语义）。
		_fullscreenAnimStartBright = float.IsNaN(fromBright) ? _gamma.ReadCurrentBrightness() : fromBright;
		_fullscreenAnimStartTemp = float.IsNaN(fromTemp) ? _gamma.ReadCurrentTemperature() : fromTemp;
		// 诊断（正式版 OpLog 为空实现，零开销）：把"动画实际用的起点"打出来，
		// 用于判定"欺骗器屏首帧直接落在原生附近"到底是不是起点取值错误。
		// 诊断增强（只加字段，不改行为）：把"读到起点那一刻的全部相关状态"打出来。
		// 用于回答"退出路径的起点为何是 6558K 而 curT 是 5889K"——是否是 _paused 未解除
		// 导致 ReapplyAllDisplays 未写屏（屏幕仍是原生）⇒ 读 ramp 反算得到 6558K。
		OpLog.Log($"[pauseAnim] start from={_fullscreenAnimStartBright * 100f:0}%/{_fullscreenAnimStartTemp:0}K " +
		            $"target={targetBright * 100f:0}%/{targetTemp:0}K exit={exit} " +
		            $"argB={fromBright} argT={fromTemp} " +
		            $"curB={_gamma.CurrentBrightness * 100f:0} curT={_gamma.CurrentTemperature:0}K " +
		            $"gammaPaused={_gamma.IsPaused} uiPaused={IsUiPaused()} " +
		            $"wlPaused={_whitelistPaused} wlSet={_wlPausedScreens.Count} anims={_wlAnims.Count} " +
		            $"readB={_gamma.ReadCurrentBrightness() * 100f:0} readT={_gamma.ReadCurrentTemperature():0}K");
		if (!flag)
		{
			_gamma?.ApplyPausedFrame(targetBright, _gamma.ReadCurrentTemperature());
		}
		if (!flag2)
		{
			_gamma?.ApplyPausedFrame(_gamma.ReadCurrentBrightness(), targetTemp);
		}
		// ------------------------------------------------------------------
		// F7（2026-09-17 晚）：本路径也并入 **ramp 空间**（与 StartSmoothTransition / F6 同构）。
		// 修的是实测缺陷：勾选/取消一行白名单（且该 exe 在前台）时，日志给出
		//   `[pauseAnim] start from=100%/6558K target=100%/6600K ... readT=6558K`
		// ⇒ 起点是 `ReadCurrent*` 的**反算值**（identity 反算成 6558K 而非 6600K）
		//   ⇒ 首帧写成一帧「偏暖 1.34%」（Δ≈899）；末步又跨过 `GetBlueMultiplier` 在 6600K
		//   的 0.966% 阶跃（ΔB255=633）⇒ 净变化 0 的「下探 → 缓回 → 弹回」，用户看到的是
		//   「勾选白名单时闪一下」。
		//   ramp 空间插值：f=0 == 屏幕真实 ramp（零跳变）；f=1 == 目标 ramp（精确命中）。
		// ------------------------------------------------------------------
		_fullscreenAnimRampUsed = (_gamma?.BeginRampTransition(targetBright, targetTemp, 1200,
		                                                        allowWhilePaused: true) ?? 0) > 0;
		if (_fullscreenAnimRampUsed)
		{
			OpLog.Log($"[pauseAnim] ramp 空间过渡 exit={exit} -> " +
			          $"{targetBright * 100f:0}%/{targetTemp:0}K（起点取屏幕真实 ramp）");
		}
		else
		{
			OpLog.Log($"[pauseAnim] 无可用 ramp 起点 ⇒ 回退参数插值 exit={exit} " +
			          $"start={_fullscreenAnimStartBright * 100f:0}%/{_fullscreenAnimStartTemp:0}K -> " +
			          $"{targetBright * 100f:0}%/{targetTemp:0}K");
		}
		_fullscreenAnimExit = exit;
		_fullscreenAnimStartTime = DateTime.Now;
		// ⚠ 全局暂停过渡会写**所有**屏 ⇒ 必须把全部屏登记为"动画独占写屏"，
		// 否则周期性的 out-of-band 全量写屏（实测 ~2s 一次）会把动画一帧抹平 ——
		// 这正是 B2a 实测 Δ=0.2129/0.2266（一帧走完 94%）的成因。
		_gamma?.SetAnimatingDisplays(_gamma.GetDisplayIds());
		_fullscreenAnimLastProgress = 0;   // 每次新过渡都从头计（配合单帧上限）
		// F7：ramp 空间下**不**额外写 f=0「首帧」—— 起点 ramp 就是刚从屏幕读出来的，重写同一张表
		// 只会带来 ±1 个单位的往返误差（落下一个 Δ1 亚可见帧，被 burst 判据当成「已落定」）。
		// 只有回退到参数插值时才需要把屏幕钉在已知起点上。
		if (!_fullscreenAnimRampUsed)
		{
			_gamma?.ApplyPausedFrame(_fullscreenAnimStartBright, _fullscreenAnimStartTemp);
		}
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
		
		// ⚠ 单帧进度上限：`OnFullscreenSmoothTick` 的首帧可能因为 UI 线程被阻塞
		//（--auto 命令处理 / SaveSettings / 弹窗刷新）而迟到 400ms+，此时按 elapsed
		// 算出的进度已过 40%，**一帧就把屏幕拉过大半**——用户看到的就是"跳变"
		//（实测：单步 Δ=0.2067 vs 总跨度 0.2202，即一帧走完 94%）。
		// 这里把每帧推进限制在 平滑tick/总时长（30/1200 = 1/40），卡顿只会让动画变慢，
		// 绝不会跳过头。对全局白名单暂停 / 全屏暂停等所有调用方一并生效。
		double _maxStep = (double)30 / 1200.0;
		if (num > _fullscreenAnimLastProgress + _maxStep) num = _fullscreenAnimLastProgress + _maxStep;
		_fullscreenAnimLastProgress = num;
		if (num >= 1.0)
		{
			num = 1.0;
		}
		double num2 = EaseOutCubic(num);
		float brightness = (_fullscreenAnimSmoothB ? ((float)((double)_fullscreenAnimStartBright + (double)(_fullscreenAnimTargetBright - _fullscreenAnimStartBright) * num2)) : _fullscreenAnimTargetBright);
		float temperature = (_fullscreenAnimSmoothT ? ((float)((double)_fullscreenAnimStartTemp + (double)(_fullscreenAnimTargetTemp - _fullscreenAnimStartTemp) * num2)) : _fullscreenAnimTargetTemp);
		if (_fullscreenAnimRampUsed)
		{
			// F7：ramp 空间逐帧插值。`ignoreAnimating: true` 必需 —— 本路径已把全部屏登记为
			// 动画独占（见 StartFullscreenTransition 里的 SetAnimatingDisplays），否则一帧都不写。
			_gamma?.StepRampTransition(_fullscreenAnimTargetBright, _fullscreenAnimTargetTemp, num2,
			                           allowWhilePaused: true, ignoreAnimating: true);
		}
		else
		{
			_gamma?.ApplyPausedFrame(brightness, temperature);
		}
		if (!(num >= 1.0))
		{
			return;
		}
		_fullscreenAnimTimer?.Stop();
		_fullscreenAnimTimer?.Dispose();
		_fullscreenAnimTimer = null;
		_gamma?.SetAnimatingDisplays(null);   // 过渡结束：恢复常规写屏
		if (_fullscreenAnimRampUsed)
		{
			// F7：末帧(f=1)已精确等于目标 ramp ⇒ 只关独占窗口，**不再额外写屏**、不提交 state
			//（保住暂停期间「保留值」的语义；否则收尾那次写原生就是末帧硬跳的来源）。
			_fullscreenAnimRampUsed = false;
			_gamma?.EndRampTransition(_fullscreenAnimTargetBright, _fullscreenAnimTargetTemp,
			                          commitState: false);
		}
		OpLog.Log($"[pausedAnim] done: brightness {_fullscreenAnimTargetBright * 100f:0}% " +
				  $"temp {_fullscreenAnimTargetTemp:0}K exit={_fullscreenAnimExit}");
		if (_fullscreenAnimExit)
		{
			_fullscreenPaused = false;
			if (!_disableActive)
			{
				_gamma?.SetPaused(paused: false);
			}
			SyncPopupFromUi();  // 方案 B：暂停标志复位，界面回到设定值
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
				_disableBrightnessBefore = PendingOrCurrentBrightness();
				_disableTemperatureBefore = PendingOrCurrentTemperature();
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
			else if (WhitelistWillTakeOver(ignoreDisable: true))
			{
				// ------------------------------------------------------------------
				// F26（2026-09-20 用户实测）：与全屏退出（F22）**同款**问题。
				//
				// 用户场景：开了白名单 + 功能停用，再关闭功能停用时——
				//   先按 `_disableTemperatureBefore` 平滑恢复设定值（4200K），
				//   1~2 秒后白名单接管又把它拉回原生（6600K）
				//   ⇒ **两次方向相反的过渡** ⇒ 观感"先变暖又变白"。
				// 实测日志（14:01）：
				//   `[14:01:16.008] [disableAnim] ramp 空间过渡 exit=True start=100%/6600K -> 100%/4200K`
				//   `[14:01:18.219] [whitelist/permon] entered=[SAC2466]`   ← 2 秒后被拉回原生
				//
				// 修法：白名单**此刻就会接管** ⇒ 不恢复设定值，直接把暂停移交给白名单。
				//   屏上此刻是原生（停用期间写的就是原生）⇒ 白名单"原生→原生"净变化 0
				//   ⇒ F17 会跳过写屏，**视觉零变化**。
				//
				// ⚠️ 绝不能在此调 `_gamma.ApplyPausedState()`：它在 `_paused == false` 时走
				//   `ApplyGamma()`，会把屏幕**立刻写成设定值（4200K）**——正是要避免的那一跳。
				//   也不能只 `SetPaused(false)` 就完事：逐屏白名单分支**不动全局 `_paused`**，
				//   若不清掉 L0 留下的 `_paused = true`，后续所有写值都会被吞掉。
				// ------------------------------------------------------------------
				_disableActive = false;
				OpLog.Log("[disable] exit：白名单会立刻接管 ⇒ 跳过「恢复设定值」过渡，直接移交");
				_gamma.SetPaused(paused: false);          // 解除 L0 的全局冻结
				ApplySolarScheduler();                    // 恢复被停用停止的 Solar 调度
				UpdateDisableTimer();
				SyncPopupFromUi();
				EvaluateWhitelistPause();                 // 让白名单立刻接管（屏上已是原生）
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
		SyncPopupFromUi();  // 方案 B：停用解除，界面回到设定值
	}

	private void StartDisableTransition(float targetBright, float targetTemp, bool exit, Action? done)
	{
		if (_gamma == null)
		{
			return;
		}
		SyncPopupFromUi();  // 方案 B：暂停/恢复动画开始，界面按当前暂停状态刷新
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
		// ------------------------------------------------------------------
		// F6（2026-09-17）：把「停用 / 恢复」过渡**并入 ramp 空间**（与 StartSmoothTransition 的
		// F1/F2/F3 同一套机制）。此前这里是**参数空间**插值，实测两处必然闪（RULES_DETAIL §O2）：
		//   ① 起点用 `ReadCurrentTemperature()` 反算 identity 得到 **6558K**（不是 6600K）
		//      ⇒ 首帧把屏幕写成一帧「偏暖 1.34%」的下探（实测 ΔB255≈879），随后再慢慢爬回来；
		//      ⚠️ 2026-09-18 上午已修 `RampToTemperature` 的分支判据 ⇒ identity 现反算 **6600K**，
		//         该下探的**来源**不复存在。本段仍走 ramp 空间插值（另一处诱因是下面的 6600K 阶跃，
		//         与反算是否准确无关），所以这里的机制**不因此回退**。
		//   ② 末步从 6599.x 跨到 6600 会**一步穿过 `GetBlueMultiplier` 在 6600K 的 0.966% 阶跃**
		//      （公式值 0.99039 vs `>=6600 ⇒ 1.0` 的短路）⇒ 单帧 ΔB255=633 的硬跳；
		//      再叠加收尾 `ApplyPausedState()` 写原生，就成了「下探 → 缓回 → 弹回」——
		//      净变化 0 却人眼可见（用户报的「开关闪」）。
		//   ramp 空间插值：f=0 == 屏幕真实 ramp（零跳变）；f=1 == 目标 ramp（精确命中）；
		//   参数→ramp 的阶跃被摊平到整段动画里。
		// ⭐ 再叠加 F1「同值短路」：在 **ramp 空间**比较，目标已等于屏幕 ⇒ 一次都不写。
		//   停用时的目标就是「无调节」（1.0/6600K = 原生），屏幕常在原生 ⇒ 直接归零（零写屏）。
		// ------------------------------------------------------------------
		_disableAnimRampUsed = false;
		// ⚠️ 用 `?.` + `== true`：字段的可空流分析无法跨 `SyncPopupFromUi()` 证明 `_gamma` 非空
		//    （直接 `_gamma.X` 会报 CS8602 警告；本项目要求 0 警告）。
		bool rampOnScreen = _gamma?.IsTargetRampOnScreen(targetBright, targetTemp,
		                                                 allowWhilePaused: true) == true;
		if (rampOnScreen)
		{
			OpLog.Log($"[disableAnim] skip: 目标 ramp 已等于屏幕当前 ramp（净变化 0）⇒ 不写屏；" +
			          $"exit={exit} 目标 {targetBright * 100f:0}%/{targetTemp:0}K");
			_gamma?.ApplyPausedState();
			done?.Invoke();
			return;
		}
		_disableAnimRampUsed = (_gamma?.BeginRampTransition(targetBright, targetTemp, 1200,
		                                                  allowWhilePaused: true) ?? 0) > 0;
		if (_disableAnimRampUsed)
		{
			// ⚠️ 这里**不**额外写 f=0「首帧」：起点 ramp 是刚从屏幕读出来的，f=0 写它等于重写同一张表；
			//    而「读→写」会带 ±1 个单位的往返误差，反而落下一个 Δ1 的亚可见帧（实测被 burst 判据
			//    当成「已落定」，把紧随其后的正常首帧误判成硬跳）。首帧 tick 迟到也无妨：
			//    `_rampTransitionUntil` 已开窗独占写屏，期间没有别的路径会动屏幕。
			OpLog.Log($"[disableAnim] ramp 空间过渡 exit={exit} " +
			          $"start={_disableAnimStartBright * 100f:0}%/{_disableAnimStartTemp:0}K -> " +
			          $"{targetBright * 100f:0}%/{targetTemp:0}K");
		}
		else
		{
			OpLog.Log($"[disableAnim] 无可用 ramp 起点 ⇒ 回退参数插值 exit={exit} " +
			          $"start={_disableAnimStartBright * 100f:0}%/{_disableAnimStartTemp:0}K -> " +
			          $"{targetBright * 100f:0}%/{targetTemp:0}K");
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
		if (_disableAnimRampUsed)
		{
			// F6：ramp 空间逐帧插值（f=0 == 屏幕真实 ramp；f=1 == 目标 ramp，精确命中）
			_gamma?.StepRampTransition(_disableAnimTargetBright, _disableAnimTargetTemp, num2,
			                           allowWhilePaused: true);
		}
		else
		{
			_gamma?.ApplyPausedFrame(brightness, temperature);
		}
		if (num >= 1.0)
		{
			_disableAnimTimer?.Stop();
			_disableAnimTimer?.Dispose();
			_disableAnimTimer = null;
			Action? disableAnimDone = _disableAnimDone;
			_disableAnimDone = null;
			if (_disableAnimRampUsed)
			{
				// 末帧(f=1)已精确等于目标 ramp ⇒ 只关独占窗口、**不再额外写屏**。
				// 这正是原来「恢复值 → 原生 → 恢复值」两次大跳的来源。
				bool wasExit = _disableAnimExit;
				_disableAnimRampUsed = false;
				_gamma?.EndRampTransition(_disableAnimTargetBright, _disableAnimTargetTemp,
				                          commitState: false);
				if (!wasExit)
				{
					// 进入暂停：兜底对齐原生（f=1 已是原生 ⇒ 零变化；若末帧因异常没走到，这里保底）
					_gamma?.ApplyPausedState();
				}
			}
			else
			{
				_gamma?.ApplyPausedState();
			}
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
				_disableBrightnessBefore = PendingOrCurrentBrightness();
				_disableTemperatureBefore = PendingOrCurrentTemperature();
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
			// ------------------------------------------------------------------
			// 2026-09-18 按用户给的「恢复出厂清单」补全/修正（此前这些字段要么
			// 完全没被重置、要么默认值与清单不符）：
			//   · 两个透明度、托盘常驻、白名单整块 —— 原来**未被重置** ⇒ 重置后仍
			//     保留用户旧值（"恢复出厂"货不对板）。
			//   · 全屏暂停 true→false（源码注释本就写着"默认关闭，用户明确要求"，
			//     而且它一旦生效会让调节**静默失效**）。
			//   · 快捷键总开关 true→false（热键**绑定本身**不重置 —— 热键页有
			//     单独的"恢复默认"按钮，属另一条复位路径）。
			//   · 日出/日落 440/990(07:20/16:30) → 480/60(08:00/01:00)。
			// ------------------------------------------------------------------
			_settings.OverlayOpacityPercent = 70;
			_settings.PopupOpacityPercent = 90;
			_settings.KeepTrayIconVisible = true;
			_settings.Language = Language.System;
			_settings.Theme = ThemeMode.System;
			_settings.PopupTheme = ThemeMode.System;
			_settings.ColorTemperatureEnabled = false;
			_settings.TemperatureStepSize = 100f;
			_settings.MinTemperature = 3300f;
			_settings.MaxTemperature = 10000f;
			_settings.AllHotKeysEnabled = false;
			// 开机自启 → 关（用户 2026-09-18 定）。⚠️ 只写配置，不动 Run 键：
			// 若注册表里本有自启，下次启动会被 IntegrityChecker 的"安装器代写"分支采纳回 true
			// （详见 AppSettings.StartupEnabled 注释）。要"真关"需在此补 StartupManager.SetStartup(false)。
			_settings.StartupEnabled = false;
			_settings.SolarAdjustEnabled = false;
			_settings.SolarManualMode = true;
			_settings.ManualSunriseMinutes = 480;
			_settings.ManualSunsetMinutes = 1080;
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
			// 2026-09-19：全屏「按显示器生效」子开关 → 关（与构造默认 / getter 兜底一致，§Y）。
			_settings.FullscreenPerMonitor = false;
			_settings.PauseInFullscreenEnabled = false;
			// 应用白名单整块复位（用户清单："应用白名单→关"）：总开关关、
			// 勾选清空、逐屏白名单关。
			_settings.AppWhitelistEnabled = false;
			_settings.AppWhitelist = new List<string>();
			_settings.AppWhitelistPerMonitor = false;
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
			// 显示器相关一并还原（此前漏了：重置后若残留"停用/冻结"的屏，会永远
			// 不响应调节，显示器页看起来"失灵"）。独立控制关、逐屏状态与自定义名
			// 清空后整体同步运行时（统一模式 + 100%/6600K 播种全部屏）。
			if (_unifyActive) CancelUnifyAnimation();
			_settings.PerMonitorEnabled = false;
			_settings.MonitorStates?.Clear();
			_settings.MonitorNames?.Clear();
			ApplyPerMonitorFromSettings();
			bool brightnessSmooth = _settings.BrightnessSmooth;
			bool temperatureSmooth = _settings.TemperatureSmooth;
			if (brightnessSmooth || temperatureSmooth)
			{
				StartSmoothTransition(1f, 6600f, brightnessSmooth, temperatureSmooth);
			}
			else
			{
				_gamma?.ApplyUnifiedTarget(1f, 6600f);
			}
			UpdateTrayTooltip();
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
						if (!IsUiPaused())
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
									SyncPopupFromUi();
								}
								ShowOverlayForDisplays();
								UpdateTrayTooltip();
								this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
								SaveSettings();
							}
						}
						else
						{
							// 2026-09-16：暂停期间热键不改值（冻结罩），但仍唤出 OSD
							// 展示"屏幕实际生效值" —— 否则用户按了热键像坏了。
							OpLog.Log("[hotkey] paused -> OSD only (no write)");
							ShowOverlayForDisplays();
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
						if (!IsUiPaused())
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
									SyncPopupFromUi();
								}
								ShowOverlayForDisplays();
								UpdateTrayTooltip();
								this.BrightnessChanged?.Invoke(this, _gamma?.CurrentBrightness ?? 1f);
								SaveSettings();
							}
						}
						else
						{
							// 2026-09-16：暂停期间热键不改值（冻结罩），但仍唤出 OSD
							// 展示"屏幕实际生效值" —— 否则用户按了热键像坏了。
							OpLog.Log("[hotkey] paused -> OSD only (no write)");
							ShowOverlayForDisplays();
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
							if (!IsUiPaused())
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
											SyncPopupFromUi();
										}
										UpdateTrayTooltip();
										SaveSettings();
									}
									this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
								}
							}
							else
							{
								// 2026-09-16：暂停期间热键不改值（冻结罩），但仍唤出 OSD
								// 展示"屏幕实际生效值" —— 否则用户按了热键像坏了。
								OpLog.Log("[hotkey] paused -> OSD only (no write)");
								ShowOverlayForDisplays();
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
			if (!IsUiPaused())
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
							SyncPopupFromUi();
						}
						UpdateTrayTooltip();
						SaveSettings();
					}
					this.TemperatureChanged?.Invoke(this, _gamma?.CurrentTemperature ?? 6600f);
				}
			}
			else
			{
				// 2026-09-16：暂停期间热键不改值（冻结罩），但仍唤出 OSD
				// 展示"屏幕实际生效值" —— 否则用户按了热键像坏了。
				OpLog.Log("[hotkey] paused -> OSD only (no write)");
				ShowOverlayForDisplays();
			}
		}, out error);
	}

	// ==================================================================
	//  应用白名单（设计稿 §16）
	// ==================================================================

	/// <summary>
	/// 白名单暂停源的每 1 s 评估。判据（§16.5）：白名单集合中存在"可见、未最小化、
	/// 且过 §16.4 过滤"的顶层窗口 → 暂停。用它而不是"前台窗口"，是因为停靠/边缘唤出
	/// 的窗口不一定夺取前台（QQ 场景）。
	/// 只影响"是否写 gamma"（复用 gamma 的 _paused），**不改变 Solar 调度状态**。
	/// </summary>
	private void EvaluateWhitelistPause()
	{
		if (_settings == null || _gamma == null) return;

		// L0/L1 拥有暂停权时让位：它们负责 SetPaused 与过渡动画，白名单不去抢同一个 flag。
		// （L0/L1 结束后 1 s 内本方法会按需重新暂停。）
		// ⚠ 2026-09-19（全屏逐屏，定稿 §4.4）：让位判据由 `_fullscreenPaused` 改为 `AnyFullscreenPaused()`。
		//   逐屏化后 `_fullscreenPaused` 只在**全部受控屏都被全屏暂停**时才为 true；
		//   若继续用它，副屏全屏（部分暂停）时白名单**不会让位** ⇒ 两个功能同时持有
		//   `DisplayState.Paused`，各自的"清空"路径会把对方的暂停一并解除。
		if (_disableActive || AnyFullscreenPaused())
		{
			_whitelistPaused = false;
			SyncPopupFromUi();  // 方案 B：白名单让位 L0/L1，界面沿用它们的暂停值
			ClearWhitelistPerMonitorPause(reapply: false);
			return;
		}

		bool want = false;
		if (_settings.AppWhitelistEnabled && _settings.AppWhitelist.Count > 0)
		{
			// 按"归并键"匹配：同一软件的窗口进程/托盘进程只要勾一次就全覆盖（§16.7）
			HashSet<string> set = AppWhitelistService.MakeGroupKeySet(_settings.AppWhitelist);
			// 2026-09-16 逐屏白名单：门控满足（独立控制开 && 受控屏>=2 && 开关开）时走逐屏分支。
			// 门控不满足 ⇒ 完全不进这里，行为与既有全局白名单**逐字节一致**（零回归）。
			if (IsWhitelistPerMonitorGateOk())
			{
				if (_whitelistPaused)
				{
					// 从"全局模式"切进逐屏模式：必须先解除全局 _paused，否则所有写值被它吞掉
					_whitelistPaused = false;
					// ⚠ 这里**不能** ReapplyAllDisplays()（瞬时写屏）—— 那会让屏幕先"跳"一下
					// （用户实测反馈的跳变）。改为只解除标志，由紧随其后的逐屏过渡动画接管：
					// S 内的屏目标就是原生（屏上已是原生 ⇒ 视觉无变化），S 外的屏平滑恢复。
					SyncPopupFromUi();

					// ★ 全局暂停刚解除：此刻**所有受控屏都是原生**，而 S 之外的屏需要从原生
					//   "平滑恢复"回自己的设定值。但此时 _wlPausedScreens 是空的 ⇒ 只靠
					//   entered/left 差集会算出 left=∅，副屏拿不到过渡动画，会被下一次全量写屏
					//   **瞬时**写成设定值（实测：一帧跳完 Δ=0.1924，即用户反馈的跳变）。
					//   ⇒ 先把所有受控屏播种进暂停集合，让 left = 受控屏 − S 承接过渡。
					//   （S 内的屏本来就是原生且将保持 Paused，播种不影响它们。）
					foreach (string sid in _gamma.GetDisplayIds())
					{
						if (_settings.MonitorStates != null &&
						    _settings.MonitorStates.TryGetValue(sid, out MonitorState? ms) && ms != null && !ms.Enabled)
							continue;   // 手动停用的屏不参与（保持冻结）
						_wlPausedScreens.Add(sid);
						// ★ 同时把 gamma 侧的 Paused 也置上：此刻这些屏**本来就都是原生**（刚从全局暂停
						//   解除），标成 Paused 只会让它们继续写原生；否则在"下一 tick 算出真正的 S"之前
						//   存在一个空窗 —— 该屏既不 Paused 也不在动画里，会被周期性的全量写屏写成设定值，
						//   随即又被动画拉回原生，观感就是"闪一下"（用户 2026-09-16 报告：开「按显示器生效」时跳变）。
						_gamma.SetDisplayPaused(sid, true);
					}

					// ★ 解除全局暂停必须放在**播种之后**：否则在 `SetPaused(false)` 到"下一 tick 算出真正的 S"
					//   之间存在空窗 —— 这些屏既不 Paused、也不在动画里，会被线程池上的周期性写屏
					//   写成设定值，随即又被动画拉回原生 ⇒ 观感是"闪一下"
					//   （实测：主屏单步 Δ=0.2303 而净跨度 0.0000 —— 尖峰后回归，正是用户报告的"跳一下"）。
					_gamma.SetPaused(false);
				}
				EvaluateWhitelistPerMonitor(set);
				UpdateTrayTooltip();
				return;
			}
			want = AppWhitelistService.AnyVisibleWindowFor(set);
		}

		// ⚠ 必须在 `want == _whitelistPaused` 提前返回**之前**清逐屏暂停 ——
		// 否则"部分暂停"（此时 _whitelistPaused 恰为 false）遇到 want=false 时会
		// false==false 直接 return，逐屏暂停永远残留、屏幕卡在原生（用户实测反馈的 bug）。
		// reapply 条件化：紧接着要全局暂停（want=true）时不写屏 —— 屏上本就该是原生，
		// 全局动画"原生→原生"无观感变化；避免"先恢复成设定值再平滑回原生"的跳变。
		// ⚠ 必须在调用**之前**捕获：`ClearWhitelistPerMonitorPause` 内部会把 `_whitelistPaused` 置 false，
		// 若直接用 `want == _whitelistPaused` 判"无变化"，就会变成 false==false ⇒ **提前 return**
		// ⇒ 跳过恢复路径 ⇒ 全局 `_paused` 永不解除（屏幕卡原生、连 SetTemperature 都被吞）。
		// 实测：全局白名单关闭后 `_paused` 卡住 ⇒ 后续设色温无效（主回归 6 项失败）。
		bool prevWhitelistPaused = _whitelistPaused;
		ClearWhitelistPerMonitorPause(reapply: !want);

		if (want == _whitelistPaused && prevWhitelistPaused == want) return;
		_whitelistPaused = want;
		SyncPopupFromUi();  // 方案 B：白名单暂停状态变化，界面切到/切回实际生效值
		if (want)
		{
			// 与全屏暂停**同款**处理：先置暂停，再平滑过渡到"无调节"（1.0 / 6600K）。
			// 之前只做了 SetPaused + ApplyPausedState（瞬时），所以观感是跳变（用户反馈）。
			_whitelistBrightnessBefore = PendingOrCurrentBrightness();
			_whitelistTemperatureBefore = PendingOrCurrentTemperature();
			_gamma.SetPaused(true);
			StartFullscreenTransition(1f, 6600f);
		}
		else
		{
			// 恢复：平滑回到暂停前的值；动画收尾时由 OnFullscreenSmoothTick 的 exit 分支解除暂停。
			// 逐屏过渡（各屏从原生 → 暂停前的设定值）
			// 逐屏过渡（各屏从原生 → 暂停前的设定值）
			StartFullscreenTransition(_whitelistBrightnessBefore, _whitelistTemperatureBefore, exit: true);
		}
		OpLog.Log($"[whitelist] paused={want}");
		UpdateTrayTooltip();
	}

	/// <summary>
	/// 多屏诊断（2026-09-16）：逐屏返回「屏幕实际生效值」(读 ramp) 与「gamma 内部 state」(想要的值)。
	/// 显卡欺骗器 / 接了但不亮的副屏只能靠 ramp 这个客观口径检测。
	/// 每行格式：EDID|stateB=|stateT=|actualB=|actualT=|enabled=|paused=（亮度为 0..100）。
	/// </summary>
	public List<string> GetMonitorDiagnostics()
	{
		var list = new List<string>();
		if (_gamma == null) return list;
		bool paused = IsUiPaused();
// 2026-09-16 诊断：附上每个输出的物理分辨率/主屏标记 —— 用于识别"幻影输出"
// （拔掉物理连接后 Windows 仍报 Active 的残留输出，其画布通常异常）。
var allMon = Monitor.GetAll();
		foreach (string id in _gamma.GetDisplayIds())
		{
			var st = _gamma.GetDisplayState(id);
			var actual = _gamma.ReadActualFor(id);
var mm = allMon.FirstOrDefault(x => string.Equals(x.EdidId, id, StringComparison.OrdinalIgnoreCase));
string phys = mm == null ? "phys=? primary=?" : ($"phys={mm.PhysicalWidthPx}x{mm.PhysicalHeightPx} primary={mm.IsPrimary}");
			list.Add(id + "|stateB=" + (st.Brightness * 100f).ToString("0") +
				"|stateT=" + st.Temperature.ToString("0") +
				"|actualB=" + (actual.Brightness * 100f).ToString("0") +
				"|actualT=" + actual.Temperature.ToString("0") +
"|enabled=" + st.Enabled + "|paused=" + paused + "|perpaused=" + st.Paused +
				"|fsperpaused=" + _fsPausedScreens.Contains(id) + "|" + phys);
		}
		return list;
	}

	// ==================================================================
	//  逐屏白名单（2026-09-16 第三轮定稿）
	// ==================================================================

	/// <summary>用户偏好（持久化）。门控不满足时功能仍锁定，但偏好保留。</summary>
	public bool GetAppWhitelistPerMonitor() => _settings?.AppWhitelistPerMonitor ?? false;

	/// <summary>
	/// 门控（定稿 §7.1）：开关开 && 独立控制开 && 受控屏 >= 2。
	/// 不满足 ⇒ 功能锁定（UI 置灰）且**不生效**，但**不重置用户偏好** —— 条件恢复后自动回到原状。
	/// </summary>
	public bool IsWhitelistPerMonitorGateOk()
	{
		if (_settings == null) return false;
		if (!_settings.AppWhitelistPerMonitor) return false;  // 本功能自身的开关
		return IsWhitelistPerMonitorHostReady();
	}

	/// <summary>
	/// 门控中**与自身开关无关**的部分：独立控制开 && 受控屏 >= 2。
	/// UI 用它决定子开关是否置灰（置灰时仍保留用户偏好）。
	/// </summary>
	public bool IsWhitelistPerMonitorHostReady()
	{
		// 2026-09-19：抽出共用判据（`IsPerMonitorFeatureHostReady`）——
		// 「全屏按显示器生效」的门控与此**逐字一致**（用户 2026-09-19 拍板）。
		return IsPerMonitorFeatureHostReady();
	}

	/// <summary>
	/// 受控屏数：只统计当前**在线**且未被手动停用的屏。
	/// ⚠ 不能用 MonitorStates.Count —— 它含已拔屏的陈旧条目（见记忆「多屏/幻影显示器」）。
	/// </summary>
	private int CountControlledDisplays()
	{
		if (_gamma == null) return 0;
		int n = 0;
		foreach (string id in _gamma.GetDisplayIds())
		{
			if (_settings?.MonitorStates != null &&
			_settings.MonitorStates.TryGetValue(id, out MonitorState? ms) && ms != null && !ms.Enabled)
			continue;   // 手动停用的屏不算"受控屏"
			n++;
		}
		return n;
	}

	/// <summary>切换用户偏好；立即生效（不等 1 s tick）。</summary>
	public void SetAppWhitelistPerMonitor(bool enabled)
	{
		if (_settings == null) return;
		_settings.AppWhitelistPerMonitor = enabled;
		SaveSettings();
		EvaluateWhitelistPause();
		UpdateTrayTooltip();
	}

	/// <summary>当前被逐屏暂停的屏（自动化/诊断用）。</summary>
	public string[] GetWhitelistPausedScreens() => _wlPausedScreens.ToArray();

	/// <summary>
	/// 逐屏白名单评估（定稿 §3 粘滞状态机）：
	/// - 无合格白名单窗口（无启用应用 / 全部最小化 / 全关）⇒ 清空暂停集（功能不生效）
	/// - 有窗口 且 存在"完全落入"的屏 ⇒ 切换暂停集
	/// - 有窗口 但 谁都不完全落入（**跨屏拖动中**）⇒ 保持不动
	/// ⚠ 前两者与第三者语义不同，必须分开判断 —— 否则拖动中窗口一离开原屏的完整范围，
	///   原屏就会提前恢复（与"完全拖到另一屏才变"的决策冲突）。
	/// </summary>
	private void EvaluateWhitelistPerMonitor(HashSet<string> groupKeys)
	{
		if (_gamma == null) return;

		(bool anyWindow, HashSet<string> hit, HashSet<string> overlap) =
				AppWhitelistService.GetHitScreens(groupKeys, Monitor.GetAll());

		HashSet<string> next;
		if (!anyWindow) next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		else if (hit.Count > 0) next = hit;
		else
		{
			// 窗口既不"完全落入"任何屏 —— 要么跨在中缝（真跨屏），要么悬在屏外
			// （例如下边框垂到屏幕下方，此时窗口在视觉上仍"在该屏上"）。
			// ⚠ 无条件保持 S 是不行的：用户把窗口从副屏**拖回主屏**、但底边悬空时，
			//   它已与副屏**毫无交集**，却仍被判定为"跨屏中"⇒ 副屏一直暂停、主屏不生效
			//   （用户 2026-09-16 实测报告，已复现：窗口 [60,960..960,1560]、主屏底 1440）。
			// ⇒ 只有当窗口与"当前暂停的屏"**彻底断开**（无任何交集）时才兜底切换；
			//   只要还碰着任一旧屏（真跨屏拖动中）→ 保持不动（用户 Q6：完全拖到另一屏才切换）。
			bool stillTouchingPaused = false;
			foreach (string pid0 in _wlPausedScreens)
			{
				if (overlap.Contains(pid0)) { stillTouchingPaused = true; break; }
			}
			if (!stillTouchingPaused && _wlPausedScreens.Count > 0 && overlap.Count > 0)
			{
				next = new HashSet<string>(overlap, StringComparer.OrdinalIgnoreCase);
				goto switchScreens;
			}
			ReassertPerMonitorPause();   // 真跨屏中：保持 S，但幂等补挂
			return;
		}

		switchScreens:
		if (next.SetEquals(_wlPausedScreens))
		{
			// ⚠ 集合没变**不代表暂停还在**！`GammaController.ResetDisplayStates()`（关闭/重开独立
			// 控制时调用）、`RefreshDisplays()` 等路径会**重建 `_displayStates`**，把 `Paused`
			// 标志一起抹掉，而本侧 `_wlPausedScreens` 毫不知情 —— 只比对集合永远发现不了，
			// 屏幕会静默停在设定值上（实测抓到过 `perpaused=False` 的完整链条）。
			// ⇒ 每 tick 幂等补挂；只有"真的从 false 变 true"才需要补写屏。
			ReassertPerMonitorPause();
			return;
		}

		List<string> entered = next.Except(_wlPausedScreens, StringComparer.OrdinalIgnoreCase).ToList();
		List<string> left = _wlPausedScreens.Except(next, StringComparer.OrdinalIgnoreCase).ToList();
		_wlPausedScreens = new HashSet<string>(next, StringComparer.OrdinalIgnoreCase);

		// IsUiPaused 语义（定稿 §7.3）：全部受控屏都被暂停时才等价"全局暂停"，否则必须保持 false，
		// 否则 UiBrightness 会回落 100%，与"显示可控屏平均值"冲突。
		bool allPaused = next.Count > 0 && next.Count >= CountControlledDisplays();
		if (allPaused != _whitelistPaused)
		{
			_whitelistPaused = allPaused;
			SyncPopupFromUi();
		}

		OpLog.Log($"[whitelist/permon] entered=[{string.Join(",", entered)}] left=[{string.Join(",", left)}] allPaused={allPaused}");
		StartWhitelistPerMonitorTransition(entered, left);
	}

	/// <summary>
	/// 幂等补挂：把 S 中的屏重新置为 Paused，并**检测 + 修复失同步**。
	/// 背景：外部路径（`ResetDisplayStates` / `RefreshDisplays` / 开关独立控制）会重建
	/// `_displayStates`，把 `Paused` 一起清掉；本侧集合不变 ⇒ 只比对集合发现不了。
	/// 修复用**平滑**过渡（起点取该屏 state 值 —— 失同步时屏上正好就是它）。
	/// </summary>
	private void ReassertPerMonitorPause()
	{
		if (_gamma == null || _wlPausedScreens.Count == 0) return;

		List<string> repaired = new List<string>();
		foreach (string id in _wlPausedScreens)
		{
			if (_gamma.SetDisplayPaused(id, true)) repaired.Add(id);   // 同值返回 false ⇒ 无变化
		}
		if (repaired.Count == 0) return;

		OpLog.Log("[whitelist/permon] 检测到 gamma 侧暂停标志丢失，补挂 [" + string.Join(",", repaired) + "]");
		bool smoothB = _settings?.BrightnessSmooth ?? false;
		bool smoothT = _settings?.TemperatureSmooth ?? false;
		DateTime now = DateTime.Now;
		foreach (string id in repaired)
		{
			DisplayState st = _gamma.GetDisplayState(id);
			PushAnim(id, st.Brightness, st.Temperature, 1f, 6600f, true, now);
		}
		if (smoothB || smoothT)
		{
			_wlAnimTimer ??= CreateWhitelistAnimTimer();
			_wlAnimTimer.Start();
		}
		else
		{
			foreach (string id in repaired) _gamma.ResetDisplayNative(id);
			FinishWhitelistTransition();
		}
	}

	/// <summary>
	/// 逐屏过渡（定稿 §6）：进入 S 的屏平滑到原生；离开 S 的屏平滑写回**生效前的值**。
	/// "生效前的值"无需额外快照 —— Paused 期间该屏 state 被 IsWritable 挡住写不进去，
	/// 故 state.Brightness/Temperature 天然等于进入暂停前的值（停用屏与正常屏同一路径）。
	/// </summary>
	private void StartWhitelistPerMonitorTransition(List<string> entered, List<string> left)
	{
		if (_gamma == null || _settings == null) return;
		if (entered.Count == 0 && left.Count == 0) return;

		bool smoothB = _settings.BrightnessSmooth;
		bool smoothT = _settings.TemperatureSmooth;
		DateTime now = DateTime.Now;

		foreach (string id in entered)
		{
			_gamma.SetDisplayPaused(id, true);     // 立即置位：该屏的写值路径此刻起冻结
			DisplayState st = _gamma.GetDisplayState(id);
			PushAnim(id, st.Brightness, st.Temperature, 1f, 6600f, true, now);
		}
		foreach (string id in left)
		{
			DisplayState st = _gamma.GetDisplayState(id);
			PushAnim(id, 1f, 6600f, st.Brightness, st.Temperature, false, now);
			_gamma.SetDisplayPaused(id, false);    // 恢复受控
		}

		if (!smoothB && !smoothT)
		{
			// 无平滑设置 ⇒ 瞬时落定（与既有全局路径的 !flag && !flag2 分支同语义）
			foreach (string id in entered) _gamma.ResetDisplayNative(id);
			FinishWhitelistTransition();
			return;
		}

		_wlAnimTimer ??= CreateWhitelistAnimTimer();
		_wlAnimTimer.Start();
	}

	/// <summary>把"动画独占写屏"的屏集合同步给 gamma（过渡开始/更新/结束时调用）。
	/// 2026-09-19：改为**两套动画的并集** —— 逐屏白名单与全屏逐屏各有独立字典，
	/// 虽然两者互斥、不会同时活跃，但并集能保证任何交错时序下都不会漏登记
	/// （漏登记的屏会被周期性全量写屏抹平动画帧）。</summary>
	private void SyncAnimatingDisplays()
	{
		if (_gamma == null) return;
		if (_wlAnims.Count == 0 && _fsAnims.Count == 0)
		{
			_gamma.SetAnimatingDisplays(null);
			return;
		}
		var ids = new HashSet<string>(_wlAnims.Keys, StringComparer.OrdinalIgnoreCase);
		foreach (string k in _fsAnims.Keys) ids.Add(k);
		_gamma.SetAnimatingDisplays(ids);
	}

	/// <summary>登记/覆盖某屏的过渡（若该屏正在过渡中，从它**当前实际帧值**接着走，避免跳变）。</summary>
	private void PushAnim(string id, float fromB, float fromT, float toB, float toT, bool toNative, DateTime now)
	{
		if (_wlAnims.TryGetValue(id, out WlScreenAnim? prev))
		{
			double p = (now - prev.Start).TotalMilliseconds / WhitelistAnimMs;
			if (p > 1.0) p = 1.0;
			double e = EaseOutCubic(p);
			fromB = prev.FromB + (prev.ToB - prev.FromB) * (float)e;
			fromT = prev.FromT + (prev.ToT - prev.FromT) * (float)e;
		}
		_wlAnims[id] = new WlScreenAnim { FromB = fromB, FromT = fromT, ToB = toB, ToT = toT, ToNative = toNative, Start = now };
		SyncAnimatingDisplays();
	}

	private const double WhitelistAnimMs = 1200.0;

	private Timer CreateWhitelistAnimTimer()
	{
		var timer = new Timer { Interval = 30 };
		timer.Tick += OnWhitelistAnimTick;
		return timer;
	}

	private void OnWhitelistAnimTick(object? sender, EventArgs e)
	{
		if (_gamma == null || _settings == null) { StopWhitelistAnim(); return; }

		bool smoothB = _settings.BrightnessSmooth;
		bool smoothT = _settings.TemperatureSmooth;
		DateTime now = DateTime.Now;
		bool allDone = true;

		foreach (KeyValuePair<string, WlScreenAnim> kvp in _wlAnims.ToList())
		{
			WlScreenAnim a = kvp.Value;
			double p = (now - a.Start).TotalMilliseconds / WhitelistAnimMs;
			if (p >= 1.0) p = 1.0; else allDone = false;
			double ease = EaseOutCubic(p);
			float b = smoothB ? a.FromB + (a.ToB - a.FromB) * (float)ease : a.ToB;
			float t = smoothT ? a.FromT + (a.ToT - a.FromT) * (float)ease : a.ToT;
			_gamma.ApplyFrameToDisplay(kvp.Key, b, t);
			if (p >= 1.0)
			{
				if (a.ToNative) _gamma.ResetDisplayNative(kvp.Key);    // 原生落定（与 display.ResetGamma 一致）
				_wlAnims.Remove(kvp.Key);
				SyncAnimatingDisplays();
			}
		}

		if (!allDone || _wlAnims.Count > 0) return;
		StopWhitelistAnim();
		FinishWhitelistTransition();
	}

	/// <summary>过渡收尾：按 state 重写全部屏（Paused→原生 / 停用→冻结 / 其余→state）。</summary>
	private void FinishWhitelistTransition()
	{
		_gamma?.ReapplyAllDisplays();
		OpLog.Log("[whitelist/permon] 逐屏过渡完成");
	}

	private void StopWhitelistAnim()
	{
		_wlAnimTimer?.Stop();
		_wlAnimTimer?.Dispose();
		_wlAnimTimer = null;
		_wlAnims.Clear();
		SyncAnimatingDisplays();
	}

	/// <summary>
	/// 清空逐屏暂停。reapply=false 用于"让位 L0/L1"（写屏交给它们的全局暂停，避免抢写造成闪动）；
	/// reapply=true 用于门控失效/关闭逐屏开关（**不写屏**而是按 state 还原，否则画面停在原生）。
	/// </summary>
	private void ClearWhitelistPerMonitorPause(bool reapply)
	{
		if (_gamma == null) return;
		// ⚠ 必须先记下"是否有动画在飞"：StopWhitelistAnim() 会清空 _wlAnims，
		// 而"只出不进"的过渡（窗口最小化 → next=∅、left 非空）恰好是
		// _wlPausedScreens 已空、动画却仍在飞的情形。若此时直接 return，
		// 屏幕会**永久卡在中间帧**（推演发现的真 bug）。
		bool hadAnim = _wlAnims.Count > 0;
		StopWhitelistAnim();
		if (_wlPausedScreens.Count == 0 && !hadAnim) return;
		List<string> wasPaused = _wlPausedScreens.ToList();
		foreach (string id in wasPaused) _gamma.SetDisplayPaused(id, false);
		_wlPausedScreens.Clear();
		if (_whitelistPaused)
		{
			_whitelistPaused = false;
			SyncPopupFromUi();
		}
		if (reapply && wasPaused.Count > 0)
		{
			// ⚠ 恢复也要**平滑**（当作"离开 S"），不能瞬时 ReapplyAllDisplays ——
			// 否则用户点开关时会看到屏幕跳一下（实测反馈）。
			StartWhitelistPerMonitorTransition(new List<string>(), wasPaused);
		}
		else if (reapply)
		{
			_gamma.ReapplyAllDisplays();   // 只剩"动画中途被打断"的情形需要收敛
		}
		OpLog.Log("[whitelist/permon] 逐屏暂停已清空（reapply=" + reapply + "）");
	}
	public bool GetAppWhitelistEnabled() => _settings?.AppWhitelistEnabled ?? false;

	public void SetAppWhitelistEnabled(bool enabled)
	{
		if (_settings == null || _settings.AppWhitelistEnabled == enabled) return;
		_settings.AppWhitelistEnabled = enabled;
		SettingsManager.Save(_settings);
	}

	public IReadOnlyList<string> GetAppWhitelist() => _settings?.AppWhitelist ?? new List<string>();

	/// <summary>覆盖整份白名单（自动规范化 + OrdinalIgnoreCase 去重）。</summary>
	public void SetAppWhitelist(IEnumerable<string> paths)
	{
		if (_settings == null) return;
		List<string> normalized = AppWhitelistService.MakePathSet(paths).ToList();
		_settings.AppWhitelist = normalized;
		SettingsManager.Save(_settings);
	}

	public bool IsWhitelistPaused() => _whitelistPaused;

	/// <summary>L1 全屏暂停是否**等价全局**（逐屏化后 = 全部受控屏都被全屏暂停时才为 true）。
	/// 供 UI 判断"写值入口是否冻结"；判"是否要让位给全屏"请用 <see cref="AnyFullscreenPaused"/>。</summary>
	public bool IsFullscreenPaused() => _fullscreenPaused;

	/// <summary>刷新候选集（两个数据源合并去重，§16.3）。</summary>
	public List<WhitelistCandidate> GetWhitelistCandidates() => AppWhitelistService.CollectCandidates();

	private void OnManualAdjustment()
	{
		// P3 修复（2026-09-14 逻辑审查）：
		//  (i) 条件由“调度器正在运行”放宽到“Solar 已启用” —— 覆盖“开启 Solar 的 1200ms
		//      平滑过渡窗口内调度器尚未 Start”这一段；否则窗口内的手动调整不算接管，
		//      过渡结束时的 done 回调仍会 Start 调度器并把用户刚设的值覆盖掉。
		//  (ii) 手动输入时取消在途的平滑动画 —— 否则动画每 30ms 回写插值，会把用户刚
		//      拖出的值覆盖，直到动画结束（此前动画优先级高于手动输入）。
		if (_settings != null && _solarScheduler != null && (_solarScheduler.IsRunning || _settings.SolarAdjustEnabled))
		{
			_solarScheduler.Stop();
			_settings.SolarManuallyOverridden = true;
			SettingsManager.Save(_settings);
		}
		// 取消在途平滑：清定时器 + 丢弃未完成的 done 回调（避免“动画中改道”后旧回调仍触发）。
		if (_smoothTimer != null)
		{
			_smoothTimer.Stop();
			_smoothTimer.Dispose();
			_smoothTimer = null;
			_smoothDone = null;
		}
	}

	/// <summary>
	/// 开机自启现状（以注册表为准，与设置窗开关的显示逻辑一致）。
	/// </summary>
	public bool GetStartupEnabled()
	{
		return StartupManager.IsStartupEnabled();
	}

	/// <summary>
	/// 切换开机自启。**必须经由此方法** —— <see cref="StartupManager.SetStartup"/> 只写
	/// 注册表与磁盘，而本控制器持有的 <c>_settings</c> 副本里 <c>StartupEnabled</c> 仍是旧值；
	/// 退出时 <see cref="Dispose"/> → <see cref="SaveSettings"/> 会把**整个** _settings 写回，
	/// 把用户这次改动覆盖掉。更糟的是下次启动的 IntegrityChecker 会按被覆盖的旧值判定
	/// 「设置与注册表不一致」，反过来把 Run 键改回去 —— 于是开关开、关两个方向都会失效。
	/// </summary>
	public bool SetStartupEnabled(bool enabled)
	{
		StartupManager.SetStartup(enabled);
		if (_settings != null)
		{
			_settings.StartupEnabled = enabled;
			SettingsManager.Save(_settings);
		}
		return StartupManager.IsStartupEnabled();
	}

	/// <summary>P1：托盘图标常驻显示的用户偏好（单一事实源 = settings.json）。</summary>
	public bool GetKeepTrayIconVisible() => _settings?.KeepTrayIconVisible ?? true;

	/// <summary>
	/// P1：切换「托盘图标常驻显示」。
	/// 拨到「开」→ 走写入阶梯（条目可能尚未生成，600ms → 1.5s → 4s → 10s）；
	/// 拨到「关」→ 立即写一次（失败也只记日志，条目未生成是安全 no-op）。
	/// 必须经由此方法改 <see cref="AppSettings.KeepTrayIconVisible"/> 并落盘
	/// —— 同缺陷 #4 的教训：直接改控件/注册表会让退出时 SaveSettings 覆盖掉。
	/// </summary>
	public bool SetKeepTrayIconVisible(bool value)
	{
		// 用户动作本身必须留痕：否则无法确认"UI 事件有没有走到业务层"。
		OpLog.Log($"[TrayVisibility] 用户设置 KeepTrayIconVisible: " +
				  $"{(_settings?.KeepTrayIconVisible.ToString() ?? "<null>")} -> {value}" +
				  (value ? "（开启：走写入阶梯）" : "（关闭：立即写 IsPromoted=0）"));
		if (_settings != null)
		{
			_settings.KeepTrayIconVisible = value;
			SettingsManager.Save(_settings);
		}
		if (value)
		{
			StartTrayVisibilityLadder();
		}
		else
		{
			TrayVisibilityService.TrySetPromoted(false);
		}
		return _settings?.KeepTrayIconVisible ?? value;
	}

	/// <summary>P1：托盘常驻写入阶梯 600ms → 1.5s → 4s → 10s（累计约 16s），任一次成功即停。</summary>
	private void StartTrayVisibilityLadder()
	{
		if (_trayVisTimer == null)
		{
			_trayVisTimer = new System.Windows.Forms.Timer { Interval = TrayVisDelaysMs[0] };
			_trayVisTimer.Tick += OnTrayVisibilityTick;
		}
		else
		{
			_trayVisTimer.Stop();
		}
		_trayVisStep = 0;
		_trayVisSlowPoll = 0;
		_trayVisTimer.Interval = TrayVisDelaysMs[0];
		_trayVisTimer.Start();
		OpLog.Log("[TrayVisibility] 写入阶梯启动，节拍(ms)：" + string.Join(" / ", TrayVisDelaysMs));
	}

	/// <summary>
	/// P1：写入阶梯结束（true = 成功写入；false = 阶梯耗尽仍未成功）。
	/// 设置窗用它更新非模态提示 —— <b>提示必须等阶梯出结果</b>，绝不能在拨动开关的瞬间
	/// 同步读注册表：那时阶梯首拍（600ms）还没跑，读到的必然是旧值，会把"已生效"误报成
	/// "未生效"（2026-09-13 实测踩到：IsPromoted 实际已是 1，提示却说未生效）。
	/// </summary>
	public event Action<bool>? TrayVisibilityLadderFinished;

	private void OnTrayVisibilityTick(object? sender, EventArgs e)
	{
		_trayVisTimer?.Stop();
		bool slowPhase = _trayVisStep >= TrayVisDelaysMs.Length;
		OpLog.Log(slowPhase
			? $"[TrayVisibility] 长周期轮询 第 {_trayVisSlowPoll}/{TrayVisSlowPollMax} 次尝试写入…"
			: $"[TrayVisibility] ladder 第 {_trayVisStep + 1}/{TrayVisDelaysMs.Length} 拍" +
			  $"（本拍延迟 {TrayVisDelaysMs[_trayVisStep]}ms）尝试写入…");
		if (TrayVisibilityService.TrySetPromoted(true))
		{
			OpLog.Log("[TrayVisibility] ladder 写入成功 -> 停止");
			TrayVisibilityLadderFinished?.Invoke(true);   // 成功即停
			return;
		}
		_trayVisStep++;
		if (_trayVisStep < TrayVisDelaysMs.Length)
		{
			// 阶梯未完，继续下一拍
			if (_trayVisTimer != null)
			{
				_trayVisTimer.Interval = TrayVisDelaysMs[_trayVisStep];
				_trayVisTimer.Start();
			}
			return;
		}

		// 阶梯耗尽：先通知 UI（显示"条目尚未生成"提示），然后**转长周期轮询** ——
		// 条目可能要等 explorer 重建任务栏才出现，届时自动写入，不必等下次启动、
		// 也不必用户再手动拨一次开关。
		OpLog.Log("[TrayVisibility] ladder 耗尽（约 16s）仍未写入成功 -> 转长周期轮询" +
				  $"（每 {TrayVisSlowPollMs / 1000}s 一次，最多 {TrayVisSlowPollMax} 次）");
		TrayVisibilityLadderFinished?.Invoke(false);
		if (_trayVisSlowPoll >= TrayVisSlowPollMax)
		{
			OpLog.Log("[TrayVisibility] 长周期轮询也结束，本次会话放弃（下次启动 / TaskbarCreated 会再试）");
			return;
		}
		_trayVisSlowPoll++;
		if (_trayVisTimer != null)
		{
			_trayVisTimer.Interval = TrayVisSlowPollMs;
			_trayVisTimer.Start();
		}
	}

	/// <summary>
	/// P1/P2″：托盘图标注册成功后的<b>幂等校验</b>（§6.8 定稿）。
	/// 只在「与设定不符」时才写 —— 运行期零干预、尊重用户在系统托盘里的手动拖动；
	/// 本次会话尊重，下次启动恢复开关设定。开销仅一次注册表读。
	/// P2″：总开关被用户明确关闭时，无论偏好如何都<b>强制常驻</b>（防图标彻底消失且无法自救），
	/// 但<b>不改</b> KeepTrayIconVisible 的存储值。
	/// </summary>
	private void ApplyTrayVisibilityPolicy()
	{
		// 带上触发原因，才能把这次判定和 explorer 侧的动作对应上
		// （首次注册 / TaskbarCreated 重建 / 重试阶梯成功，三者含义完全不同）。
		OpLog.Log($"[TrayVisibility] policy: 触发（OnTrayIconReady，原因={_trayIcon?.LastReadyReason ?? "(未知)"}）");
		bool forceOn = TrayVisibilityService.IsOverflowMenuExplicitlyOff();
		bool keep = _settings?.KeepTrayIconVisible ?? true;
		bool? actual = TrayVisibilityService.ReadPromoted();
		bool want = forceOn || keep;
		OpLog.Log($"[TrayVisibility] policy: 用户偏好 KeepTrayIconVisible={keep}；" +
				  $"总开关强制常驻(P2″)={forceOn}；注册表实际={TrayVisibilityService.Describe(actual)}；" +
				  $"期望={want}");
		if (want && actual != true)
		{
			StartTrayVisibilityLadder();
		}
		else
		{
			// 这条必须有：否则事后分不清"跑过且判定一致"与"压根没跑到"。
			OpLog.Log("[TrayVisibility] policy: 状态一致，无需写入（本次不产生任何注册表写操作）");
		}
	}

	private void SaveSettings()
	{
		if (_settings == null || _gamma == null)
		{
			return;
		}
		// ⛔ 修复（2026-09-19，用户报）：Solar 接管期间**不更新** Last* ——
		//    否则 Solar 的自动值（夜间 85%/3900K）会被当成“用户上次设的值”记住；
		//    而 `SetColorTemperatureEnabled(true)` 在 **Solar 关**时走 `num2 = LastTemperature`
		//    ⇒ 关掉 Solar 后一开色温就跳回 3900K（实测新旧产物均复现）。
		//    判据与既有代码一致（2553 行）：Solar 开且未被手动接管 = Solar 在写值。
		bool solarTakingOver = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
		if (!solarTakingOver)
		{
			_settings.LastBrightness = PendingOrCurrentBrightness();
			if (_settings.ColorTemperatureEnabled)
			{
				_settings.LastTemperature = PendingOrCurrentTemperature();
			}
		}
		if (_settings.PerMonitorEnabled)
		{
			IReadOnlyDictionary<string, DisplayState> allDisplayStates = _gamma.GetAllDisplayStates();
			Dictionary<string, MonitorState> dictionary = new Dictionary<string, MonitorState>(_settings.MonitorStates ?? new Dictionary<string, MonitorState>());
			// ⛔ 修复（2026-09-19，系统性源码审查）：与同一次修的 Last* 完全同源——
			//    逐屏模式下，Solar 的自动值（夜间 85%/3900K）会被统一版 SetBrightness/SetTemperature
			//    写进各屏 state，再被这里落盘 ⇒ 关 Solar / 下次启动会恢复成夜间值。
			//    ⇒ Solar 接管期间**保留上次落盘值**；Enabled 照常（用户在 UI 显式设的，与 Solar 无关）。
			bool solarTakingOverPerMonitor = _settings.SolarAdjustEnabled && !_settings.SolarManuallyOverridden;
			foreach (KeyValuePair<string, DisplayState> item in allDisplayStates)
			{
				bool hasPrev = dictionary.TryGetValue(item.Key, out var value);
				dictionary[item.Key] = new MonitorState
				{
					Enabled = (!hasPrev || value!.Enabled),
					Brightness = (solarTakingOverPerMonitor && hasPrev) ? value!.Brightness : item.Value.Brightness,
					Temperature = (solarTakingOverPerMonitor && hasPrev) ? value!.Temperature : item.Value.Temperature
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
		// 先关自动化桥：否则管道仍可接收命令，却已没有可用的 UI 锚点
		AutomationBridge.Instance.Dispose();
		// L2：正常退出时再刷一次"最后运行时刻"，让判据更贴合"用户最后用到什么时候"
		// （长时间挂机后关机的场景）。放在最前面 —— 后面若某步抛异常也不该丢掉这次记录。
		KnownInstalls.TouchSelf();
		SaveSettings();
		_solarScheduler?.Dispose();
		_whitelistTimer?.Stop();
		_whitelistTimer?.Dispose();
		_whitelistTimer = null;
		_solarScheduler = null;
		_systemMonitor?.Dispose();
		_systemMonitor = null;
		_popupAnchorTimer?.Stop();
		_popupAnchorTimer?.Dispose();
		_popupAnchorTimer = null;
		// F25-B：后台重申定时器（非 UI 线程，必须显式停掉，否则回调可能在退出过程中仍触发写屏）
		_topologyRetryTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
		_topologyRetryTimer?.Dispose();
		_topologyRetryTimer = null;
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
		_gamma?.SetAnimatingDisplays(null);   // 过渡结束：恢复常规写屏
		_unifyTimer?.Stop();
		_unifyTimer?.Dispose();
		_unifyTimer = null;
		_disableTimer?.Stop();
		_disableTimer?.Dispose();
		_disableTimer = null;
		_disableAnimTimer?.Stop();
		_disableAnimTimer?.Dispose();
		_disableAnimTimer = null;
		_gamma?.CancelRampTransition();   // F6：顺手取消在途 ramp 过渡
		_adjustFlushTimer?.Stop();
		_adjustFlushTimer?.Dispose();
		_adjustFlushTimer = null;

		// P1：托盘常驻写入阶梯的 Timer 也要停掉
		_trayVisTimer?.Stop();
		_trayVisTimer?.Dispose();
		_trayVisTimer = null;
	}
}
