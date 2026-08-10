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

    private TrayMessageWindow? _messageWindow;
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
    public bool IsMouseOverIcon => _isMouseOverIcon;

    public event EventHandler<float>? OnBrightnessSelected;
    public event EventHandler? OnMouseEnterIcon;
    public event EventHandler? OnMouseLeaveIcon;
    public event EventHandler<Language>? OnLanguageChanged;
    public event EventHandler? OnUninstallRequested;
    public event EventHandler? OnSettingsRequested;

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

    // Owner-drawn menu text storage: MF_OWNERDRAW menu items do not carry
    // their text in the menu structure; the text pointer is passed via
    // AppendMenu's lpNewItem. We pin the strings with GCHandle so the menu
    // can read them during WM_DRAWITEM, and free them after the menu closes.
    private readonly List<GCHandle> _menuPins = new();

    /// <summary>
    /// Appends an owner-drawn menu item whose text is rendered by
    /// TrayMessageWindow.WndProc (WM_DRAWITEM) so the tray menu follows the
    /// app theme (dark menu on dark theme, light menu on light theme).
    /// </summary>
    private void AppendThemeItem(IntPtr hMenu, uint flags, uint id, string text)
    {
        var gch = GCHandle.Alloc(text, GCHandleType.Pinned);
        _menuPins.Add(gch);
        AppendMenu(hMenu, MF_OWNERDRAW | flags, id, gch.AddrOfPinnedObject());
    }

    private void ClearMenuPins()
    {
        foreach (var gch in _menuPins) gch.Free();
        _menuPins.Clear();
    }

    /// <summary>
    /// Pins a string for use as a popup (submenu) item's owner-drawn text.
    /// Popup items pass the submenu handle as the item ID, so they cannot
    /// go through AppendThemeItem's id-based path; they use this pointer
    /// directly. Freed together with the other pins in ClearMenuPins.
    /// </summary>
    private IntPtr PinMenuText(string text)
    {
        var gch = GCHandle.Alloc(text, GCHandleType.Pinned);
        _menuPins.Add(gch);
        return gch.AddrOfPinnedObject();
    }

    /// <summary>
    /// Paints the entire popup menu window (padding, separator gaps, and
    /// the strip below the last item — areas owner-draw never covers) in
    /// the current theme background. Without this the menu window stays in
    /// the system color, showing white around/below the dark items.
    /// </summary>
    private void ApplyMenuTheme(IntPtr hMenu)
    {
        if (hMenu == IntPtr.Zero) return;
        bool dark = ThemeManager.IsDark;
        uint color = dark ? 0x001E1E1Eu : 0x00FFFFFFu; // COLORREF: 0x00BBGGRR
        IntPtr brush = CreateSolidBrush(color);
        if (brush == IntPtr.Zero) return;
        _menuBrushes.Add(brush);

        var mi = new MENUINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUINFO>(),
            fMask = MIM_BACKGROUND | MIM_APPLYTOSUBMENUS,
            hbrBack = brush
        };
        SetMenuInfo(hMenu, ref mi);
    }

    private readonly List<IntPtr> _menuBrushes = new();

    private void ClearMenuBrushes()
    {
        foreach (var b in _menuBrushes) DeleteObject(b);
        _menuBrushes.Clear();
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            // Paint the whole popup (including padding / area below the
            // last item) in the theme background; owner-draw alone leaves
            // those parts in the system color (white even on dark theme).
            ApplyMenuTheme(hMenu);

            // Submenus are attached with MF_POPUP so Windows provides the
            // native hover cascade (no click needed to open them). The items
            // stay owner-drawn (MF_OWNERDRAW) for the themed background/text;
            // the submenu arrow is drawn by the system and follows the DWM
            // immersive dark mode set in OnHandleCreated (white on dark,
            // black on light), so we do NOT self-draw an arrow here.
            IntPtr hSubMenu = CreatePopupMenu();
            if (hSubMenu != IntPtr.Zero)
            {
                ApplyMenuTheme(hSubMenu);
                AppendThemeItem(hSubMenu, MF_STRING, 101, "100%");
                AppendThemeItem(hSubMenu, MF_STRING, 102, "75%");
                AppendThemeItem(hSubMenu, MF_STRING, 103, "50%");
                AppendThemeItem(hSubMenu, MF_STRING, 104, "25%");
                AppendThemeItem(hSubMenu, MF_STRING, 105, "10%");
            }

            IntPtr hLangMenu = CreatePopupMenu();
            if (hLangMenu != IntPtr.Zero)
            {
                ApplyMenuTheme(hLangMenu);
                var cur = Localization.Setting;
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.System ? MF_CHECKED : MF_UNCHECKED), 310,
                    Localization.Get("LangSystem"));
                AppendMenu(hLangMenu, MF_SEPARATOR, 0, null);
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.SimplifiedChinese ? MF_CHECKED : MF_UNCHECKED), 301,
                    Localization.Get("LangSC"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.TraditionalChinese ? MF_CHECKED : MF_UNCHECKED), 302,
                    Localization.Get("LangTC"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.English ? MF_CHECKED : MF_UNCHECKED), 303,
                    Localization.Get("LangEN"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.Japanese ? MF_CHECKED : MF_UNCHECKED), 304,
                    Localization.Get("LangJA"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.Korean ? MF_CHECKED : MF_UNCHECKED), 305,
                    Localization.Get("LangKO"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.German ? MF_CHECKED : MF_UNCHECKED), 306,
                    Localization.Get("LangDE"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.French ? MF_CHECKED : MF_UNCHECKED), 307,
                    Localization.Get("LangFR"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.Spanish ? MF_CHECKED : MF_UNCHECKED), 308,
                    Localization.Get("LangES"));
                AppendThemeItem(hLangMenu, MF_STRING | (cur == Language.Russian ? MF_CHECKED : MF_UNCHECKED), 309,
                    Localization.Get("LangRU"));
            }

            // Submenu entries as native popup items (hover-cascade).
            AppendMenu(hMenu, MF_POPUP | MF_OWNERDRAW,
                hSubMenu == IntPtr.Zero ? (uint)0 : (uint)hSubMenu,
                PinMenuText(Localization.Get("BrightnessLevels")));
            AppendMenu(hMenu, MF_POPUP | MF_OWNERDRAW,
                hLangMenu == IntPtr.Zero ? (uint)0 : (uint)hLangMenu,
                PinMenuText(Localization.Get("Language")));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendThemeItem(hMenu, MF_STRING, 205, Localization.Get("Settings"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendThemeItem(hMenu, MF_STRING, 203, Localization.Get("RestartApp"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendThemeItem(hMenu, MF_STRING, 204, Localization.Get("Uninstall"));

            AppendMenu(hMenu, MF_SEPARATOR, 0, null);

            AppendThemeItem(hMenu, MF_STRING, 202, Localization.Get("Exit"));

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
                case 202: Application.Exit(); break;
                case 203: RestartApplication(); break;
                case 204: OnUninstallRequested?.Invoke(this, EventArgs.Empty); break;
                case 205: OnSettingsRequested?.Invoke(this, EventArgs.Empty); break;
                case 301: OnLanguageChanged?.Invoke(this, Language.SimplifiedChinese); break;
                case 302: OnLanguageChanged?.Invoke(this, Language.TraditionalChinese); break;
                case 303: OnLanguageChanged?.Invoke(this, Language.English); break;
                case 304: OnLanguageChanged?.Invoke(this, Language.Japanese); break;
                case 305: OnLanguageChanged?.Invoke(this, Language.Korean); break;
                case 306: OnLanguageChanged?.Invoke(this, Language.German); break;
                case 307: OnLanguageChanged?.Invoke(this, Language.French); break;
                case 308: OnLanguageChanged?.Invoke(this, Language.Spanish); break;
                case 309: OnLanguageChanged?.Invoke(this, Language.Russian); break;
                case 310: OnLanguageChanged?.Invoke(this, Language.System); break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
            ClearMenuPins();
            ClearMenuBrushes();
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
                        var font = SystemFonts.MenuFont;
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
            float scale = Math.Max(1f, SystemFonts.MenuFont.Height / 16f);
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
