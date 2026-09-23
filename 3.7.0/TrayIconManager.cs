using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;
// ImplicitUsings 会引入 System.Threading，其中同样存在 Timer（System.Threading.Timer）；
// 显式别名消歧，写法与 MainController.cs 一致。
using Timer = System.Windows.Forms.Timer;

namespace GammaBrightnessTool;

public sealed class TrayIconManager : IDisposable
{
    public const uint ICON_ID = 1;
    public const uint WM_TRAY_CALLBACK = WM_USER + 1;
    public static readonly Guid IconGuid = new("{F1B8A3C2-5D6E-4A7F-9C8B-0E1D2F3A4B5C}");

    /// <summary>
    /// explorer 在任务栏（重新）建立时广播的系统消息 ID。
    /// 只注册一次并缓存 —— RegisterWindowMessage 是跨进程查询，勿在 WndProc 里反复调。
    /// </summary>
    private static readonly uint WM_TASKBARCREATED =
        NativeMethods.RegisterWindowMessage("TaskbarCreated");

    /// <summary>NIM_ADD 失败后的退避阶梯（ms）。走完最后一档后固定用最后一档，直到累计超时。</summary>
    private static readonly int[] RetryDelaysMs = { 500, 1000, 2000, 4000, 8000 };

    /// <summary>退避重试的累计上限（ms）。超过即判定为持续性原因，放弃且不弹框。</summary>
    private const int RetryMaxTotalMs = 30000;

    private TrayMessageWindow? _messageWindow;
    private IntPtr _iconHandle;
    private Icon? _appIcon;
    private bool _isMouseOverIcon;
    private int _lastDpi;

    private Timer? _retryTimer;
    private int _retryStep;
    private DateTime _retryStartedUtc = DateTime.MinValue;
    private DateTime _lastDpiCheck = DateTime.MinValue;

    // Icon rect cache: avoids a Shell_NotifyIconGetRect IPC round-trip on
    // every wheel tick. Invalidated on RefreshIcon / DPI change.
    private Rectangle? _cachedIconRect;
    private DateTime _iconRectCacheTime = DateTime.MinValue;
    private static readonly TimeSpan IconRectCacheTtl = TimeSpan.FromMilliseconds(200);

    // Icon recovery cooldown: when Shell_NotifyIconGetRect fails (icon
    // temporarily lost during shell refresh / DPI change), don't hammer
    // RefreshIcon; at most once per 2s.
    private DateTime _lastIconRecovery = DateTime.MinValue;
    private static readonly TimeSpan IconRecoveryCooldown = TimeSpan.FromSeconds(2);

    public TrayIconManager()
    {
        // Tray icon glyph follows the OS theme only (dark taskbar needs a
        // white glyph, light taskbar a black one) — never the in-app theme
        // choice, so a light in-app theme on a dark system still shows a
        // white glyph. Menu DWM dark mode follows the effective app theme.
        ThemeManager.SystemThemeChanged += OnSystemThemeChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        var win = _messageWindow;
        if (win != null && !win.IsDisposed && win.IsHandleCreated && win.InvokeRequired)
        {
            // ThemeManager's 500ms registry poll runs on a thread-pool
            // timer, so this event can fire off the UI thread. UpdateIcon
            // ForTheme disposes GDI icon objects and may fall back to
            // RefreshIcon (which sleeps 100ms); marshal to the UI thread
            // so those are never touched from a pool thread.
            win.BeginInvoke(new Action(() => OnSystemThemeChanged(sender, e)));
            return;
        }
        try
        {
            UpdateIconForTheme();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIconManager] Icon refresh on OS theme change failed: {ex}");
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var win = _messageWindow;
        if (win != null && !win.IsDisposed && win.IsHandleCreated && win.InvokeRequired)
        {
            // Same thread-pool timer origin: keep DWM attribute changes on
            // the UI thread.
            win.BeginInvoke(new Action(() => OnThemeChanged(sender, e)));
            return;
        }
        try
        {
            _messageWindow?.UpdateDwmTheme();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIconManager] DWM theme update failed: {ex}");
        }
    }

    /// <summary>
    /// Swaps the tray icon image for the current theme (white glyph on dark
    /// taskbar, black on light) via NIM_MODIFY. Uses MODIFY instead of
    /// DELETE+ADD because re-registering the same GUID right after a delete
    /// can be ignored by Explorer (icon appears unchanged); MODIFY keeps the
    /// registration and just replaces the icon, which always takes effect.
    /// </summary>
    private void UpdateIconForTheme()
    {
        if (_messageWindow == null) return;

        var newIcon = IconGenerator.CreateMultiSizeTrayIcon();
        var newHandle = newIcon?.Handle ?? SystemIcons.Application.Handle;

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = WindowHandle,
            uID = ICON_ID,
            uFlags = NIF_ICON | NIF_GUID | NIF_SHOWTIP,
            hIcon = newHandle,
            guidItem = IconGuid
        };
        bool ok = Shell_NotifyIcon(NIM_MODIFY, ref nid);

        // Replace the tracked icon only after the shell accepted the swap;
        // dispose the old one (the shell keeps its own copy of the bitmap).
        if (ok)
        {
            _appIcon?.Dispose();
            _appIcon = newIcon;
            _iconHandle = newHandle;
        }
        else
        {
            // Shell could not find/modify the existing icon (e.g. it was
            // temporarily lost during a shell restart): fall back to the
            // full delete + re-add path which re-registers everything.
            newIcon?.Dispose();
            RefreshIcon();
        }
    }

    public IntPtr WindowHandle => _messageWindow?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// UI 线程 marshal 锚点（P5 自动化桥用）。
    /// 托盘消息窗本就归属 UI 线程、且已创建窗口句柄，正好用来把管道线程收到的
    /// 命令用 <c>Control.Invoke</c> 切回 UI 线程执行 —— 无需额外创建控件。
    /// 只在 <see cref="Initialize"/> 之后非空。
    /// </summary>
    public Control? UiMarshalAnchor => _messageWindow;

    /// <summary>
    /// Global hotkey registry (WM_HOTKEY dispatch). Created after the
    /// message window exists; MainController registers/unregisters keys
    /// through this instance.
    /// </summary>
    public HotKeyService? HotKeyService { get; private set; }

    public event EventHandler? OnMouseEnterIcon;
    public event EventHandler? OnMouseLeaveIcon;
    public event EventHandler? OnUninstallRequested;
    public event EventHandler? OnSettingsRequested;
    // 右键菜单“禁用”请求：null=永久禁用；TimeSpan.Zero=解除；时长=临时；-1 秒=日出/日落。
    public event Action<TimeSpan?>? OnDisableRequested;
    // 禁用菜单需要的当前状态（由 MainController 注入）。
    public Func<bool>? DisableSolarEnabled;
    public Func<TimeSpan?>? DisableGetRemaining;
    public Func<DateTime?>? DisableGetUntil;
    public Func<bool>? DisableSolarActive;
    public Func<bool>? DisableIsDaytime;

    /// <summary>
    /// Raised when the user left-clicks the tray icon (opens the persistent brightness slider popup).
    /// </summary>
    public event EventHandler? OnLeftClickRequested;

    /// <summary>
    /// Raised right before the context menu is shown (right-click).
    /// Used to dismiss the persistent popup so both never appear together.
    /// </summary>
    public event EventHandler? OnContextMenuOpening;

    /// <summary>
    /// Raised after the tray icon's DPI changed and the icon was refreshed
    /// (the icon may have moved to a new physical position). Consumers that
    /// anchor windows to the icon (the left-click brightness popup) must
    /// re-anchor on this event.
    /// </summary>
    public event EventHandler? OnTrayDpiChanged;

    /// <summary>
    /// Raised when the icon's physical rect changes (the icon moved, e.g.
    /// after a DPI change or taskbar relocation). Consumers that anchor
    /// windows to the icon (the left-click popup) re-anchor on this event.
    /// Unlike <see cref="OnTrayDpiChanged"/> (which only fires from
    /// WM_DPICHANGED / CheckMouseLeave and is unreliable for hidden windows),
    /// this is driven by active polling so it always fires while the popup
    /// is open and the icon moves.
    /// </summary>

    /// <summary>
    /// 初始化托盘：建消息窗、缓存 DPI、装配全局热键宿主，然后尝试注册图标。
    /// <b>本方法不抛异常</b> —— 注册失败只转入后台退避重试，绝不阻断启动。
    /// （原实现第三次 NIM_ADD 失败即 throw InvalidOperationException，而 Initialize()
    /// 与 MainController 调用点都没有 try/catch → explorer 未就绪时启动即崩。）
    /// </summary>
    public void Initialize()
    {
        CreateMessageWindow();
        _lastDpi = GetDpiForWindow(WindowHandle);
        HotKeyService = new HotKeyService(WindowHandle);

        if (TryCreateTrayIcon())
        {
            RaiseTrayIconReady("首次注册（NIM_ADD 成功）");
        }
        else
        {
            StartTrayIconRetryLadder();
        }
    }

    private void CreateMessageWindow()
    {
        _messageWindow = new TrayMessageWindow(this);
        // ------------------------------------------------------------------
        // F16b（2026-09-22）：**只创建句柄，不 Show**（F16 注释里写的原意本来就是这个）。
        //
        // ⛔ 原实现 `Show(); Hide();` 之所以有问题：WinForms 的 `Form.Show()` 会【激活】窗口
        //   ⇒ 抢前台（用户当前窗口失去激活 ⇒ Win11 边框由强调色变非活动色）；
        //   紧接的 `Hide()` 又让「刚拿到前台的窗口」消失 ⇒ **前台退回任务栏**；
        //   系统随后把前台还给原窗口 ⇒ 一串激活态变化 = 用户可见的「重启时其他窗口边框闪一下」。
        //   实测（60ms 采样 `GetForegroundWindow`）：重启 GBT 期间**前台被抢 3 次**，
        //   每次都伴随一次「前台落到 `Shell_TrayWnd`」。
        //
        // ⇒ 访问 `Handle` 属性即强制创建窗口句柄（内部走 base.CreateHandle → OnHandleCreated，
        //   与 Show() 路径**等价**，`UpdateDwmTheme()` 照常执行），且**完全不触碰前台**。
        //   本窗只需句柄来接收 `Shell_NotifyIcon` 回调 + 作自动化桥的 UI 锚点，**无需可见**。
        // ------------------------------------------------------------------
        _ = _messageWindow.Handle;   // 强制创建句柄（等价于 Show 的句柄创建，但不抢前台）
    }

    /// <summary>
    /// 尝试注册托盘图标。返回是否成功；<b>永不抛异常</b>。
    ///
    /// 正常路径：直接 NIM_ADD（同 GUID 复用 NotifyIconSettings 条目，"隐藏图标菜单"
    /// 开关项由 Windows 在首次 NIM_ADD+GUID 时建立并持久化，程序从不写删该注册表区，
    /// NIM_DELETE 也只移除实时图标本身）。因此正常路径零 DELETE，与历史验证过
    /// "开关项正常出现"的版本行为一致。
    ///
    /// 失败语义：三次尝试（立即 / 300ms / 按 GUID 注销残留槽位后重建）仍失败则返回
    /// false，由调用方决定是否进入退避阶梯 —— 不 throw、不弹框、不退出。
    /// </summary>
    private bool TryCreateTrayIcon()
    {
        try
        {
            _appIcon = IconGenerator.CreateMultiSizeTrayIcon();
            _iconHandle = _appIcon?.Handle ?? SystemIcons.Application.Handle;

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = WindowHandle,
                uID = ICON_ID,
                uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = _iconHandle,
                szTip = Localization.Get("TrayTooltipBrightnessOnly", 100).Replace("\n", ""),
                guidItem = IconGuid
            };

            if (!Shell_NotifyIcon(NIM_ADD, ref nid))
            {
                // 首试失败：多半是跨进程自动重启时旧实例的 GUID 槽位尚未释放。
                // 先等旧进程收尾再重试（等几帧即可，纯重试不动任何东西）。
                int err1 = Marshal.GetLastWin32Error();
                Thread.Sleep(300);
                if (!Shell_NotifyIcon(NIM_ADD, ref nid))
                {
                    int err2 = Marshal.GetLastWin32Error();
                    // 仍失败才按 GUID 精确注销残留槽位（hWnd+uID 在 GUID 注册下
                    // 匹配不到），随后立即重建。
                    var delNid = new NOTIFYICONDATA
                    {
                        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                        uFlags = NIF_GUID,
                        guidItem = IconGuid
                    };
                    Shell_NotifyIcon(NIM_DELETE, ref delNid);
                    Thread.Sleep(50);
                    if (!Shell_NotifyIcon(NIM_ADD, ref nid))
                    {
                        // 记录三个错误码，便于按 Win32 错误归类（UIPI / 无交互桌面 /
                        // 槽位残留 / 托盘被替换实现接管等）
                        OpLog.Log($"[TrayIcon] NIM_ADD failed 3x " +
                                  $"(err={err1}/{err2}/{Marshal.GetLastWin32Error()})");
                        return false;
                    }
                }
            }

            nid.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref nid);
            return true;
        }
        catch (Exception ex)
        {
            // 图标构造 / 结构体封送等异常同样不得上抛：
            // 启动期任何失败都只降级为「暂时没有图标」，不改变程序生命周期。
            OpLog.LogEx("[TrayIcon] TryCreateTrayIcon failed", ex);
            return false;
        }
    }

    /// <summary>
    /// 托盘图标注册成功（含 TaskbarCreated 后重注册）时触发。
    /// MainController 订阅此事件以重新应用托盘可见性设置（P1）。
    /// 注意：不要把 promote 逻辑挂到 <see cref="RefreshIcon"/> 后面 —— 那条路径走
    /// DELETE+ADD，3.4.0 已记录会被 Explorer 忽略；且 IsPromoted 持久在注册表，
    /// DELETE 不影响它。
    /// </summary>
    public event EventHandler? OnTrayIconReady;

    /// <summary>
    /// 最近一次「图标已就绪」的触发原因（P5 日志用）。
    /// 用于把托盘可见性的判定日志与 explorer 侧的动作（首次注册 / TaskbarCreated 重建 /
    /// 重试阶梯成功）串起来 —— 否则只看得到"策略跑了一次"，看不出是**哪一次**、为什么跑。
    /// </summary>
    internal string LastReadyReason { get; private set; } = "";

    /// <summary>
    /// 通知订阅者「图标已就绪」。订阅者异常不得影响托盘注册本身，故隔离并记日志。
    /// </summary>
    private void RaiseTrayIconReady(string reason)
    {
        LastReadyReason = reason;
        // NIM_ADD 成功是**正向事件**，原先只记失败 —— 缺了它就看不出图标到底注册上没有，
        // 也就无法把"策略为什么跑"和 explorer 侧的动作对应起来。
        OpLog.Log($"[TrayIcon] 图标已注册并就绪（原因：{reason}）");
        try
        {
            OnTrayIconReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[TrayIcon] OnTrayIconReady handler failed", ex);
        }
    }

    /// <summary>
    /// 启动（或重置）退避重试阶梯：500 → 1000 → 2000 → 4000 → 8000 ms，
    /// 之后固定 8000 ms，累计超过 <see cref="RetryMaxTotalMs"/> 即停止
    /// （不弹框、不退出、不 throw）。
    ///
    /// 用 WinForms Timer（UI 线程）而非后台线程：NIM_ADD 与消息窗口 hWnd 的归属线程
    /// 相关，必须在建消息窗的同一线程上调用；Initialize() 本身就在 UI 线程执行。
    /// </summary>
    private void StartTrayIconRetryLadder()
    {
        if (_retryTimer == null)
        {
            _retryTimer = new Timer { Interval = RetryDelaysMs[0] };
            _retryTimer.Tick += OnTrayIconRetryTick;
        }
        else
        {
            _retryTimer.Stop();
        }

        _retryStep = 0;
        _retryStartedUtc = DateTime.UtcNow;
        _retryTimer.Interval = RetryDelaysMs[0];
        _retryTimer.Start();

        OpLog.Log($"[TrayIcon] tray retry ladder started (first retry in {RetryDelaysMs[0]} ms)");
    }

    private void StopTrayIconRetryLadder()
    {
        _retryTimer?.Stop();
    }

    private void OnTrayIconRetryTick(object? sender, EventArgs e)
    {
        _retryTimer?.Stop();

        if (TryCreateTrayIcon())
        {
                OpLog.Log("[TrayIcon] tray retry succeeded, icon registered");
                RaiseTrayIconReady("重试阶梯成功（此前 NIM_ADD 曾失败）");
            return;
        }

        if ((DateTime.UtcNow - _retryStartedUtc).TotalMilliseconds >= RetryMaxTotalMs)
        {
            // 阶梯耗尽 → 判定为持续性原因（explorer 长期不可用 / UIPI / 无交互桌面 /
            // 任务栏被替换实现接管），而非开机竞态。到此为止，只记日志。
            OpLog.Log($"[TrayIcon] tray retry ladder exhausted after {RetryMaxTotalMs} ms, giving up");
            return;
        }

        _retryStep++;
        int delay = _retryStep < RetryDelaysMs.Length
            ? RetryDelaysMs[_retryStep]
            : RetryDelaysMs[RetryDelaysMs.Length - 1];
        if (_retryTimer != null)
        {
            _retryTimer.Interval = delay;
            _retryTimer.Start();
        }
    }

    /// <summary>
    /// 收到 explorer 的 TaskbarCreated 广播：任务栏已重建，实时图标全部丢失，必须重注册。
    /// 先重置阶梯再尝试一次，失败则从头跑退避。
    /// </summary>
    private void HandleTaskbarCreated()
    {
        OpLog.Log("[TrayIcon] TaskbarCreated received, re-registering icon");
        StopTrayIconRetryLadder();

        if (TryCreateTrayIcon())
        {
            RaiseTrayIconReady("TaskbarCreated 重注册（explorer 重建了任务栏）");
        }
        else
        {
            StartTrayIconRetryLadder();
        }
    }

    public void ProcessTrayMessage(uint message)
    {
        switch (message)
        {
            case 0x200:
                if (!_isMouseOverIcon)
                {
                    _isMouseOverIcon = true;
                    OnMouseEnterIcon?.Invoke(this, EventArgs.Empty);
                }
                break;
            case 0x202: // WM_LBUTTONUP - open persistent brightness slider popup
                OnLeftClickRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 0x205: // WM_RBUTTONUP - keep original context menu
                OnContextMenuOpening?.Invoke(this, EventArgs.Empty);
                ShowContextMenu();
                break;
        }
    }

    public void CheckMouseLeave()
    {
        if (!_isMouseOverIcon) return;

        if (DateTime.Now - _lastDpiCheck > TimeSpan.FromMilliseconds(500))
        {
            _lastDpiCheck = DateTime.Now;
            int currentDpi = GetDpiForWindow(WindowHandle);
            if (currentDpi != 0 && currentDpi != _lastDpi)
            {
                _lastDpi = currentDpi;
                RefreshIcon();
                _isMouseOverIcon = false;
                OnTrayDpiChanged?.Invoke(this, EventArgs.Empty);
                OnMouseLeaveIcon?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = WindowHandle,
            uID = ICON_ID,
            guidItem = IconGuid
        };

        int result = Shell_NotifyIconGetRect(ref identifier, out var iconRect);
        if (result == S_OK)
        {
            GetCursorPos(out var cursorPos);
            if (!iconRect.Contains(cursorPos))
            {
                _isMouseOverIcon = false;
                OnMouseLeaveIcon?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _isMouseOverIcon = false;
            OnMouseLeaveIcon?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RefreshIcon()
    {
        // The icon rect is stale once the icon is re-registered (it may
        // have moved, e.g. after a DPI change), so drop the cache.
        InvalidateIconRectCache();

        if (_messageWindow != null)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = WindowHandle,
                uID = ICON_ID,
                guidItem = IconGuid
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);

            System.Threading.Thread.Sleep(100);

            _appIcon?.Dispose();
            _appIcon = IconGenerator.CreateMultiSizeTrayIcon();
            _iconHandle = _appIcon?.Handle ?? SystemIcons.Application.Handle;

            nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = WindowHandle,
                uID = ICON_ID,
                uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = _iconHandle,
                szTip = Localization.Get("TrayTooltipBrightnessOnly", 100).Replace("\n", ""),
                guidItem = IconGuid
            };
            Shell_NotifyIcon(NIM_ADD, ref nid);

            nid.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref nid);
        }
    }

    /// <summary>
    /// Real-time check whether the cursor is over the tray icon, using the
    /// cursor position passed in by the caller (no extra GetCursorPos IPC).
    /// Uses a short-lived cache of the icon rect so rapid wheel scrolling
    /// does not trigger a Shell_NotifyIconGetRect round-trip per tick.
    /// </summary>
    /// <remarks>
    /// This is the wheel-path replacement for the IsMouseOverIcon state
    /// machine. The state machine is reset on DPI change (and can only be
    /// restored by a WM_MOUSEMOVE), which permanently broke wheel handling
    /// until the mouse moved. Geometric hit-testing against the current
    /// icon rect has no such stuck state: it just answers "is the cursor
    /// over the icon right now?"
    /// </remarks>
    public bool IsMouseOverIconNow(Point cursorPos)
    {
        var rect = GetIconRectCached();
        if (rect.HasValue)
        {
            return rect.Value.Contains(cursorPos);
        }

        // Icon rect unavailable (shell hiccup, icon temporarily lost).
        // Try to recover the icon (rate-limited) so the wheel starts
        // working again without requiring the user to move the mouse.
        TryRecoverIcon();
        return false;
    }

    /// <summary>
    /// Returns the cached icon rect, refreshing the cache if older than
    /// <see cref="IconRectCacheTtl"/> or invalidated by RefreshIcon.
    /// </summary>
    private Rectangle? GetIconRectCached()
    {
        if (_cachedIconRect.HasValue &&
            DateTime.Now - _iconRectCacheTime <= IconRectCacheTtl)
        {
            return _cachedIconRect;
        }

        var rect = GetIconRect();
        _cachedIconRect = rect;
        _iconRectCacheTime = DateTime.Now;
        return rect;
    }

    /// <summary>
    /// Invalidates the cached icon rect (e.g. after RefreshIcon or DPI
    /// change, when the icon may have moved).
    /// </summary>
    private void InvalidateIconRectCache()
    {
        _cachedIconRect = null;
        _iconRectCacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Recovers the tray icon after it was temporarily lost (shell restart,
    /// DPI change), rate-limited to once per <see cref="IconRecoveryCooldown"/>.
    /// </summary>
    /// <remarks>
    /// Called from the low-level mouse hook callback. RefreshIcon() contains
    /// a Thread.Sleep(100); running it synchronously inside the hook would
    /// block the whole system's mouse input for 100ms. The actual recovery
    /// is therefore deferred to the UI thread via the message window's
    /// BeginInvoke, so the hook callback returns immediately.
    /// </remarks>
    private void TryRecoverIcon()
    {
        if (DateTime.Now - _lastIconRecovery < IconRecoveryCooldown) return;

        _lastIconRecovery = DateTime.Now;

        var win = _messageWindow;
        if (win == null || win.IsDisposed || !win.IsHandleCreated)
        {
            // No message window to marshal to (startup edge case): recover
            // directly. This runs on the UI thread anyway during startup.
            RefreshIcon();
            return;
        }

        win.BeginInvoke(new Action(() =>
        {
            try
            {
                RefreshIcon();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayIconManager] Icon recovery failed: {ex}");
            }
        }));
    }

    /// <summary>
    /// Gets the screen rectangle of the tray icon with NO cache, refreshing
    /// immediately. When the rect is unavailable, triggers icon recovery
    /// (rate-limited, async) so the icon can be restored without user
    /// intervention — the same self-healing approach used by the wheel
    /// path. Returns null if still unavailable.
    /// </summary>
    public Rectangle? GetIconRectLive()
    {
        var rect = GetIconRect();
        if (!rect.HasValue)
        {
            TryRecoverIcon();
        }
        return rect;
    }

    /// <summary>
    /// Gets the screen rectangle of the tray icon, or null if unavailable.
    /// </summary>
    public Rectangle? GetIconRect()
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = WindowHandle,
            uID = ICON_ID,
            guidItem = IconGuid
        };
        if (Shell_NotifyIconGetRect(ref identifier, out var iconRect) == S_OK)
        {
            return new Rectangle(iconRect.Left, iconRect.Top, iconRect.Width, iconRect.Height);
        }
        return null;
    }

    private TrayMenuForm? _menuForm;

    /// <summary>
    /// 取得（或创建）托盘菜单窗体。P5/B1 起改为独立方法：自动化在启动时也会调用它，
    /// 让 <c>Tray_*</c> 动作不必等用户先右键一次托盘图标就已注册可用。
    /// </summary>
    internal TrayMenuForm EnsureMenuForm()
    {
        var menu = _menuForm;
        if (menu == null || menu.IsDisposed)
        {
            // Self-drawn menu window (replaces the native Win32 popup menu so
            // the whole surface follows the app theme with full rendering control).
            menu = new TrayMenuForm();
            menu.OnSettingsRequested += (s, e) => OnSettingsRequested?.Invoke(this, EventArgs.Empty);
            menu.OnUninstallRequested += (s, e) => OnUninstallRequested?.Invoke(this, EventArgs.Empty);
            menu.OnRestartRequested += (s, e) => RestartApplication();
            menu.OnExitRequested += (s, e) => Application.Exit();
            menu.IsSolarEnabled = () => DisableSolarEnabled?.Invoke() ?? false;
            menu.GetDisableRemaining = () => DisableGetRemaining?.Invoke();
            menu.GetDisableUntil = () => DisableGetUntil?.Invoke();
            menu.IsSolarDisableActive = () => DisableSolarActive?.Invoke() ?? false;
            menu.IsDaytime = () => DisableIsDaytime?.Invoke() ?? true;
            menu.OnDisableRequested = (d) => OnDisableRequested?.Invoke(d);
            _menuForm = menu;
            menu.RegisterAutomation();   // P5/B1
        }
        return menu;
    }

    private void ShowContextMenu()
    {
        var menu = EnsureMenuForm();

        GetCursorPos(out POINT cursorPos);
        SetForegroundWindow(WindowHandle);
        menu.ShowAt(new Point(cursorPos.x, cursorPos.y));
    }

    /// <summary>
    /// P5/B1：以等价于「右键托盘图标」的方式显示菜单（自动化用）。
    /// 位置取主屏工作区中央 —— 自动化环境里鼠标 / 真实托盘坐标不可控；菜单显示出来
    /// 只是供观察，动作本身走 ui.click:Tray_*，并不需要真的去点菜单。
    /// </summary>
    internal void ShowMenuForAutomation()
    {
        var menu = EnsureMenuForm();
        SetForegroundWindow(WindowHandle);
        var area = Screen.PrimaryScreen?.WorkingArea;
        menu.ShowAt(new Point(
            area == null ? 400 : area.Value.Width / 2,
            area == null ? 300 : area.Value.Height / 2));
    }

    /// <summary>P5/B1：关闭自动化打开的菜单（只隐藏不释放，避免下次重建）。</summary>
    internal void HideMenuForAutomation()
    {
        _menuForm?.Hide();
        // 菜单隐藏 = 子菜单叶子项（Tray_Level_* / Tray_Lang_* / Tray_Disable_*）已不可用，
        // 一并注销，避免陈旧 ID 残留（ShowAt 时会重新注册）。
        TrayMenuForm.ClearSubmenuActions();
    }

    /// <summary>P5 只读探针（2026-09-21）：当前托盘 tooltip 原文。
    /// 为什么需要：tooltip 只存在于 shell 侧、外部读不到 ⇒ 保存一份供自动化断言。</summary>
    public string CurrentTipText => _lastTipText;

    private string _lastTipText = "";
    public void UpdateTooltip(float brightness, float temperatureK, bool showTemperature = true)
    {
        // szTip 是 128 字符定长缓冲（含结尾 NUL）：文本超长时 ByValTStr 封送会抛
        // ArgumentException（每次滚轮刷新 tooltip 都会失败），这里统一截断到 127。
        string tip = (showTemperature
                ? Localization.Get("TrayTooltip", (int)Math.Round(brightness * 100), (int)Math.Round(temperatureK))
                : Localization.Get("TrayTooltipBrightnessOnly", (int)Math.Round(brightness * 100)))
            .Replace("\n", "");
        if (tip.Length > 127) tip = tip.Substring(0, 127);
        _lastTipText = tip;                 // P5 探针：保存当前 tooltip 原文（供自动化断言）

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = WindowHandle,
            uID = ICON_ID,
            // NIF_GUID is REQUIRED: the icon is registered by GUID, so
            // NIM_MODIFY must include NIF_GUID or the shell cannot locate
            // the icon and silently ignores the tooltip update (the
            // tooltip then keeps showing the initial 100%).
            // NIF_SHOWTIP is REQUIRED too: under NOTIFYICON_VERSION_4 the
            // shell hides the tooltip entirely when an update omits it.
            uFlags = NIF_TIP | NIF_GUID | NIF_SHOWTIP,
            guidItem = IconGuid,
            szTip = tip
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public static void RestartApplication()
    {
        Program.ReleaseMutex();
        var startInfo = new ProcessStartInfo
        {
            FileName = Application.ExecutablePath,
            UseShellExecute = true
        };
        Process.Start(startInfo);
        Application.Exit();
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        ThemeManager.SystemThemeChanged -= OnSystemThemeChanged;

        // 先停掉退避重试阶梯：否则 Dispose 之后 Timer 仍可能回调 TryCreateTrayIcon，
        // 在已释放的消息窗口上重注册图标。
        StopTrayIconRetryLadder();
        _retryTimer?.Dispose();
        _retryTimer = null;

        HotKeyService?.Dispose();
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            // 必须以 NIF_GUID 方式注销（hWnd+uID 在 GUID 注册下匹配不到）：
            // 否则跨进程自动重启时旧槽位残留 → 新进程 NIM_ADD 撞重复 GUID 崩溃。
            // 正常退出删掉实时图标即可；下次启动的 NIM_ADD 会立刻重建开关项
            // （"缺失即生成、免重启 explorer"的已验证机制）。
            uFlags = NIF_GUID,
            guidItem = IconGuid
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        _messageWindow?.Dispose();
        _appIcon?.Dispose();
        _menuForm?.Dispose();
        _menuForm = null;
    }

    private class TrayMessageWindow : Form
    {
        private readonly TrayIconManager _manager;
        private Icon? _taskbarIcon;

        public TrayMessageWindow(TrayIconManager manager)
        {
            _manager = manager;
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(1, 1);
            // ------------------------------------------------------------------
            // F16（2026-09-19）：**必须 false**。
            //
            // 本窗只是「托盘消息窗 + 自动化桥的 UI 锚点」。CreateMessageWindow() 的意图
            // 明确是"创建句柄但**不显示**"（构造函数已 `Visible = false`，调用处又是
            // `Show()` 紧接 `Hide()`）。
            //
            // 但 `ShowInTaskbar = true` 会让那次 `Show()` 在**任务栏留下一个按钮**
            // （且 `Icon = CreateTaskbarIcon()` ⇒ 深色图标）⇒ 用户实测报：
            //   「重启软件时，任务栏出现一个往上升的黑色图标，然后立马被打断并消失」。
            // 外部窗口监控实测完全吻合：`title='Gamma Brightness'`（与 `Text` 赋值一字不差）、
            // `rect=126,126 135x37`、**只存活 62ms**，且每次程序启动必现（与"功能停用"无关）。
            // ⇒ 改 false 后，`Show()`/`Hide()` 不再触碰任务栏，现象消失。
            // ⚠️ 不影响句柄与 `UiMarshalAnchor`（自动化桥只要求窗口句柄存在，与任务栏无关）。
            // ------------------------------------------------------------------
            ShowInTaskbar = false;
            Visible = false;

            _taskbarIcon = IconGenerator.CreateTaskbarIcon();
            Icon = _taskbarIcon;
            Text = "Gamma Brightness";
        }

        /// <summary>
        /// F16b（2026-09-22）：**防御性** —— 本窗永不应激活。
        /// 即使将来有人再对它 `Show()`，也不会抢前台。
        /// （修复前正是缺了这一条，才让 `Show(); Hide();` 抢走前台。）
        /// </summary>
        protected override bool ShowWithoutActivation => true;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateDwmTheme();
        }

        /// <summary>
        /// Applies (or clears) DWM immersive dark mode on the message window.
        /// Popup menus created on this thread inherit it, so the
        /// system-drawn submenu arrows / hover highlight follow the OS dark
        /// theme (white arrow on dark, black on light). Called on handle
        /// creation and again on theme change.
        /// </summary>
        public void UpdateDwmTheme()
        {
            if (!IsHandleCreated) return;
            int dark = ThemeManager.IsDark ? 1 : 0;
            // Windows 10 2004+ uses attr 19; Windows 11 uses 20. Try both.
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_10,
                ref dark, sizeof(int));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TASKBARCREATED)
            {
                // explorer 重启（崩溃自恢复 / 用户手动重启 / 任务栏重建 / 大版本升级
                // 期间 shell 多次重启）后，已注册的通知图标全部丢失 —— 图标活在
                // explorer 进程内存里，NotifyIconSettings 只存偏好、不存「当前有谁」，
                // 故 Shell 不会自动重建，必须重新 NIM_ADD。
                _manager.HandleTaskbarCreated();
            }
            else if (m.Msg == WM_TRAY_CALLBACK)
            {
                uint message = (uint)m.LParam & 0xFFFF;
                _manager.ProcessTrayMessage(message);
            }
            else if (m.Msg == WM_HOTKEY)
            {
                // Global hotkey pressed: dispatch to the HotKeyService by id.
                _manager.HotKeyService?.ProcessHotKey(m.WParam.ToInt32());
            }
            else if (m.Msg == 0x02E0)
            {
                // Hidden form: Windows does NOT send WM_DPICHANGED to hidden
                // windows. This handler is here only for completeness; in
                // practice the DPI change is detected by CheckMouseLeave
                // (polling GetDpiForWindow every 500 ms) and by the
                // MainController polling timer which drives ReanchorTo.
                _manager.RefreshIcon();
                _manager.OnTrayDpiChanged?.Invoke(_manager, EventArgs.Empty);
            }
            else if (m.Msg == WM_MEASUREITEM)
            {
                var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(m.LParam);
                if (mis.CtlType == ODT_MENU)
                {
                    // Measure the owner-drawn item: fixed height, width = text + padding.
                    IntPtr screenDc = GetDC(IntPtr.Zero);
                    try
                    {
                        using var g = Graphics.FromHdc(screenDc);
                        var text = Marshal.PtrToStringUni(mis.itemData) ?? "";
                        var font = SystemFonts.MenuFont!;
                        var size = TextRenderer.MeasureText(g, text, font);
                        // Height uses the system menu font height (already
                        // DPI-scaled by the OS) plus padding. Do NOT multiply
                        // by GetDpiForWindow: that API returns 0 on hidden
                        // windows in some contexts (scale=0 -> 22px) and the
                        // real DPI in others (28*1.75=49px), so the same build
                        // rendered 22px or 49px rows depending on timing.
                        // Font-based height is compact and consistent at any DPI.
                        int rowH = Math.Max(22, font.Height + 8);
                        mis.itemHeight = (uint)rowH;
                        // Popup (submenu) items show a system-drawn arrow on
                        // the right; reserve room so the text does not overlap it.
                        bool isPopup = mis.itemID > 0xFFFFu;
                        mis.itemWidth = (uint)(size.Width + 40 + (isPopup ? 18 : 0));
                        Marshal.StructureToPtr(mis, m.LParam, false);
                        m.Result = (IntPtr)1;
                        return;
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
            }
            else if (m.Msg == WM_DRAWITEM)
            {
                var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(m.LParam);
                if (dis.CtlType == ODT_MENU)
                {
                    DrawMenuThemeItem(dis);
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Renders an owner-drawn tray menu item according to the current
        /// app theme: dark background + white text on dark theme, light
        /// background + dark text on light theme, highlighted background
        /// for the selected (hovered) item, and a check mark for checked
        /// items (language menu selection, startup state).
        /// </summary>
        private void DrawMenuThemeItem(DRAWITEMSTRUCT dis)
        {
            bool dark = ThemeManager.IsDark;
            bool selected = (dis.itemState & ODS_SELECTED) == ODS_SELECTED;
            bool checked_ = (dis.itemState & ODS_CHECKED) == ODS_CHECKED;
            // Popup (submenu) items have itemID = submenu handle (large
            // value > 0xFFFF); plain command items use small IDs (101-310).
            // The submenu arrow is drawn by the system (DWM immersive dark
            // mode makes it white on dark theme / black on light), so we
            // only reserve right-side room for it here.
            bool isPopup = dis.itemID > 0xFFFFu;

            var rc = new Rectangle(dis.rcItem.Left, dis.rcItem.Top,
                dis.rcItem.Right - dis.rcItem.Left,
                dis.rcItem.Bottom - dis.rcItem.Top);

            // Background
            Color bg = dark
                ? (selected ? Color.FromArgb(51, 51, 55) : Color.FromArgb(30, 30, 30))
                : (selected ? Color.FromArgb(229, 241, 251) : Color.White);

            Color textColor = dark
                ? (selected ? Color.White : Color.FromArgb(232, 232, 232))
                : (selected ? Color.FromArgb(20, 20, 20) : Color.FromArgb(40, 40, 40));

            using var g = Graphics.FromHdc(dis.hDC);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bgBrush = new SolidBrush(bg))
            {
                g.FillRectangle(bgBrush, rc);
            }

            var text = Marshal.PtrToStringUni(dis.itemData) ?? "";
            // Font-height-based scale (system font is DPI-scaled by the OS),
            // consistent with MEASUREITEM; avoids GetDpiForWindow returning 0
            // on the hidden message window which made rows 22px vs 49px.
            float scale = Math.Max(1f, SystemFonts.MenuFont!.Height / 16f);
            int checkW = checked_ ? (int)(24 * scale) : 0;
            var textRect = new Rectangle(rc.Left + checkW + 8, rc.Top, rc.Width - checkW - 20, rc.Height);

            if (checked_)
            {
                using var checkBrush = new SolidBrush(textColor);
                var checkRect = new Rectangle(rc.Left + 4, rc.Top, (int)(20 * scale), rc.Height);
                TextRenderer.DrawText(g, "\u2713", SystemFonts.MenuFont, checkRect, textColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }

            // Shrink the text area to leave room for the system-drawn
            // submenu arrow on popup items.
            if (isPopup)
            {
                textRect.Width -= (int)(18 * scale);
            }

            TextRenderer.DrawText(g, text, SystemFonts.MenuFont, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _taskbarIcon?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
