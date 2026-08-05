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

    public IntPtr WindowHandle => _messageWindow?.Handle ?? IntPtr.Zero;
    public bool IsMouseOverIcon => _isMouseOverIcon;

    public event EventHandler<float>? OnBrightnessSelected;
    public event EventHandler? OnMouseEnterIcon;
    public event EventHandler? OnMouseLeaveIcon;
    public event EventHandler<Language>? OnLanguageChanged;
    public event EventHandler? OnUninstallRequested;

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

        _appIcon = IconGenerator.CreateTrayIcon();
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
            case 0x202:
            case 0x205:
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
            _appIcon = IconGenerator.CreateTrayIcon();
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
            uFlags = NIF_TIP,
            guidItem = IconGuid,
            szTip = Localization.Get("TrayTooltip", (int)(brightness * 100))
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
        private int _currentDpi;
        private Icon? _taskbarIcon;

        public TrayMessageWindow(TrayIconManager manager)
        {
            _manager = manager;
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(1, 1);
            ShowInTaskbar = true;
            Visible = false;
            _currentDpi = DeviceDpi;

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
                _currentDpi = (m.WParam.ToInt32() >> 16);
                _manager.RefreshIcon();
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
