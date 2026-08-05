using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

public sealed class TrayIconManager : IDisposable
{
    public const uint ICON_ID = 1;
    public const uint WM_TRAY_CALLBACK = WM_USER + 1;
    public static readonly Guid IconGuid = new("{F1B8A3C2-5D6E-4A7F-9C8B-0E1D2F3A4B5C}");

    private Form? _messageWindow;
    private IntPtr _iconHandle;
    private Icon? _appIcon;
    private bool _isMouseOverIcon;
    private int _lastDpi;
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

    public IntPtr WindowHandle => _messageWindow?.Handle ?? IntPtr.Zero;
    public bool IsMouseOverIcon => _isMouseOverIcon;

    public event EventHandler<float>? OnBrightnessSelected;
    public event EventHandler? OnMouseEnterIcon;
    public event EventHandler? OnMouseLeaveIcon;
    public event EventHandler<Language>? OnLanguageChanged;
    public event EventHandler? OnUninstallRequested;

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
    public event EventHandler? OnIconRectChanged;

    public void Initialize()
    {
        CreateMessageWindow();
        _lastDpi = GetDpiForWindow(WindowHandle);
        CreateTrayIcon();
    }

    private void CreateMessageWindow()
    {
        _messageWindow = new TrayMessageWindow(this);
        _messageWindow.Show();
        _messageWindow.Hide();
    }

    private void CreateTrayIcon()
    {
        var oldNid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = WindowHandle,
            uID = ICON_ID
        };
        Shell_NotifyIcon(NIM_DELETE, ref oldNid);

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
            szTip = Localization.Get("TrayTooltip", 100).Replace("\n", ""),
            guidItem = IconGuid
        };

        if (!Shell_NotifyIcon(NIM_ADD, ref nid))
        {
            throw new InvalidOperationException("Failed to create tray icon");
        }

        nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);
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
                szTip = Localization.Get("TrayTooltip", 100).Replace("\n", ""),
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

    /// <summary>
    /// Polls the icon rect and raises <see cref="OnIconRectChanged"/> when
    /// it differs from the last seen rect. Called periodically while the
    /// popup is open so the popup follows the icon across DPI changes /
    /// taskbar moves without depending on WM_DPICHANGED delivery to a
    /// hidden message window. Returns the current rect (or null).
    /// </summary>
    public Rectangle? PollIconRect()
    {
        var rect = GetIconRect();
        if (!rect.HasValue)
        {
            TryRecoverIcon();
            return null;
        }

        if (!_lastPolledIconRect.HasValue || _lastPolledIconRect.Value != rect.Value)
        {
            _lastPolledIconRect = rect;
            OnIconRectChanged?.Invoke(this, EventArgs.Empty);
        }
        return rect;
    }

    private Rectangle? _lastPolledIconRect;

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            IntPtr hSubMenu = CreatePopupMenu();
            if (hSubMenu != IntPtr.Zero)
            {
                AppendMenu(hSubMenu, MF_STRING, 101, "100%");
                AppendMenu(hSubMenu, MF_STRING, 102, "75%");
                AppendMenu(hSubMenu, MF_STRING, 103, "50%");
                AppendMenu(hSubMenu, MF_STRING, 104, "25%");
                AppendMenu(hSubMenu, MF_STRING, 105, "10%");
                AppendMenu(hMenu, MF_POPUP, (uint)(ulong)hSubMenu, Localization.Get("BrightnessLevels"));
            }

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            bool isStartup = StartupManager.IsStartupEnabled();
            AppendMenu(hMenu, MF_STRING | (isStartup ? MF_CHECKED : MF_UNCHECKED), 201, Localization.Get("Startup"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            IntPtr hLangMenu = CreatePopupMenu();
            if (hLangMenu != IntPtr.Zero)
            {
                var cur = Localization.Current;
                AppendMenu(hLangMenu, MF_STRING | (cur == Language.SimplifiedChinese ? MF_CHECKED : MF_UNCHECKED), 301,
                    Localization.Get("LangSC"));
                AppendMenu(hLangMenu, MF_STRING | (cur == Language.TraditionalChinese ? MF_CHECKED : MF_UNCHECKED), 302,
                    Localization.Get("LangTC"));
                AppendMenu(hLangMenu, MF_STRING | (cur == Language.English ? MF_CHECKED : MF_UNCHECKED), 303,
                    Localization.Get("LangEN"));
                AppendMenu(hMenu, MF_POPUP, (uint)(ulong)hLangMenu, Localization.Get("Language"));
            }

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendMenu(hMenu, MF_STRING, 203, Localization.Get("RestartApp"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendMenu(hMenu, MF_STRING, 204, Localization.Get("Uninstall"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendMenu(hMenu, MF_STRING, 202, Localization.Get("Exit"));

            GetCursorPos(out POINT cursorPos);
            SetForegroundWindow(WindowHandle);

            int cmd = TrackPopupMenuEx(hMenu,
                TPM_LEFTALIGN | TPM_TOPALIGN | TPM_LEFTBUTTON | TPM_RETURNCMD,
                cursorPos.x, cursorPos.y, WindowHandle, IntPtr.Zero);

            switch (cmd)
            {
                case 101: OnBrightnessSelected?.Invoke(this, 1.00f); break;
                case 102: OnBrightnessSelected?.Invoke(this, 0.75f); break;
                case 103: OnBrightnessSelected?.Invoke(this, 0.50f); break;
                case 104: OnBrightnessSelected?.Invoke(this, 0.25f); break;
                case 105: OnBrightnessSelected?.Invoke(this, 0.10f); break;
                case 201: StartupManager.SetStartup(!isStartup); break;
                case 202: Application.Exit(); break;
                case 203: RestartApplication(); break;
                case 204: OnUninstallRequested?.Invoke(this, EventArgs.Empty); break;
                case 301: OnLanguageChanged?.Invoke(this, Language.SimplifiedChinese); break;
                case 302: OnLanguageChanged?.Invoke(this, Language.TraditionalChinese); break;
                case 303: OnLanguageChanged?.Invoke(this, Language.English); break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void UpdateTooltip(float brightness)
    {
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
            szTip = Localization.Get("TrayTooltip", (int)(brightness * 100)).Replace("\n", "")
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private void RestartApplication()
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
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = WindowHandle,
            uID = ICON_ID,
            guidItem = IconGuid
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        _messageWindow?.Dispose();
        _appIcon?.Dispose();
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
            ShowInTaskbar = true;
            Visible = false;

            _taskbarIcon = IconGenerator.CreateTaskbarIcon();
            Icon = _taskbarIcon;
            Text = "Gamma Brightness";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRAY_CALLBACK)
            {
                uint message = (uint)m.LParam & 0xFFFF;
                _manager.ProcessTrayMessage(message);
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
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _taskbarIcon?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
