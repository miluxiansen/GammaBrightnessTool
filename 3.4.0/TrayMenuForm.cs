namespace GammaBrightnessTool;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

/// <summary>
/// Self-drawn tray context menu window, replacing the native Win32 popup
/// menu so the whole surface (background, hover highlight, check marks,
/// separators) follows the app theme with full control over rendering.
/// </summary>
internal sealed class TrayMenuForm : Form
{
    public event EventHandler? OnSettingsRequested;
    public event EventHandler? OnUninstallRequested;
    public event EventHandler? OnRestartRequested;
    public event EventHandler? OnExitRequested;

    private enum EntryKind { Item, Submenu, Separator }

    internal enum SubmenuKind { Language }

    private sealed class Entry
    {
        public EntryKind Kind;
        public string? Text;
        public SubmenuKind Sub;
        public Action? Action;
    }

    private readonly List<Entry> _entries = new();
    private int _hoverIndex = -1;
    private int _itemH;
    private int _sepH;

    private TraySubMenu? _subMenu;

    // Close-on-outside-click: global low-level mouse hook.
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;

    // Close-on-app-switch: poll the foreground window; if it changes from
    // the one active when the menu opened (Alt+Tab, clicking another app),
    // close. The no-activate menu never becomes foreground itself.
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 100 };
    private IntPtr _initialForeground = IntPtr.Zero;

    // Layout paddings (logical pixels; text is laid out with the system menu
    // font which the OS already DPI-scales).
    private const int LeftPad = 14;
    private const int RightPad = 24; // reserve room for the submenu arrow

    // Theme palette (matches the original owner-drawn native menu).
    private static bool Dark => ThemeManager.IsDark;
    private static Color MenuBg => Dark ? Color.FromArgb(30, 30, 30) : Color.White;
    private static Color MenuHover => Dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251);
    private static Color MenuText => Dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);
    private static Color MenuTextSelected => Dark ? Color.White : Color.FromArgb(20, 20, 20);
    private static Color MenuBorder => Dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);
    private static Color SeparatorColor => Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);

    public TrayMenuForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BuildEntries();

        _foregroundTimer.Tick += (_, _) =>
        {
            if (!Visible || IsDisposed)
            {
                _foregroundTimer.Stop();
                return;
            }
            IntPtr fg = GetForegroundWindow();
            if (fg != _initialForeground && fg != Handle)
            {
                CloseMenu();
            }
        };
    }

    private void BuildEntries()
    {
        _entries.Clear();
        _entries.Add(new Entry { Kind = EntryKind.Submenu, Text = Localization.Get("Language"), Sub = SubmenuKind.Language });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Text = Localization.Get("Settings"), Action = () => OnSettingsRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Text = Localization.Get("RestartApp"), Action = () => OnRestartRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Text = Localization.Get("Uninstall"), Action = () => OnUninstallRequested?.Invoke(this, EventArgs.Empty) });
        _entries.Add(new Entry { Kind = EntryKind.Separator });
        _entries.Add(new Entry { Kind = EntryKind.Item, Text = Localization.Get("Exit"), Action = () => OnExitRequested?.Invoke(this, EventArgs.Empty) });
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    /// <summary>Size the menu and show it with its top-left at the given point,
    /// clamped into the working area. Also installs the outside-click hook and
    /// the foreground watcher.</summary>
    public void ShowAt(Point screenPt)
    {
        // Row height follows the system menu font height (already DPI-scaled
        // by the OS), matching the original owner-drawn native menu and
        // avoiding any manual DPI multiplication (double-scaling bug).
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        _sepH = Math.Max(6, _itemH / 3);
        BackColor = MenuBg;

        // Rebuild so language labels and the current-language check mark
        // reflect the live selection each time the menu opens.
        BuildEntries();

        var sz = ComputeSize();
        var screen = Screen.FromPoint(screenPt);
        var wa = screen.WorkingArea;
        // Keep the menu glued to the cursor like the native TrackPopupMenu:
        // top-left at the cursor, flipping up/left when it would cross the
        // working-area edge (never move it to a detached position).
        int x = screenPt.X;
        int y = screenPt.Y;
        if (x + sz.Width > wa.Right) x = screenPt.X - sz.Width;
        if (y + sz.Height > wa.Bottom) y = screenPt.Y - sz.Height;
        if (x < wa.Left) x = wa.Left;
        if (y < wa.Top) y = wa.Top;

        // Bring to topmost so the menu stays above other windows, matching
        // the native TrackPopupMenu behavior (which creates a topmost
        // popup). WS_EX_NOACTIVATE keeps it from stealing focus.
        SetWindowPos(Handle, HWND_TOPMOST, x, y, sz.Width, sz.Height, SWP_NOACTIVATE);
        Show();
        // Re-assert position + topmost after Show: the shell may have
        // re-arranged a brand-new window while it was being created.
        SetWindowPos(Handle, HWND_TOPMOST, x, y, sz.Width, sz.Height, SWP_NOACTIVATE);

        // Clip the window to a rounded rect so the four corners are truly
        // transparent (DWM corner rounding alone leaves square backdrop
        // corners on some systems/configurations).
        Region?.Dispose();
        using (var rp = ThemedComboBox.RoundedRect(new Rectangle(0, 0, sz.Width, sz.Height), 8))
            Region = new Region(rp);

        InstallMouseHook();
        _initialForeground = GetForegroundWindow();
        _foregroundTimer.Start();

        // Highlight the entry under the cursor (if the menu opened with the
        // cursor already on an item) without expanding any submenu — the
        // user must move the mouse to expand, matching the native menu.
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || !Visible) return;
            var p = PointToClient(Cursor.Position);
            if (ClientRectangle.Contains(p))
            {
                _hoverIndex = HitIndex(p.Y);
                Invalidate();
            }
        }));
    }

    private Size ComputeSize()
    {
        int maxW = 0;
        foreach (var e in _entries)
        {
            if (e.Text == null) continue;
            int w = TextRenderer.MeasureText(e.Text, SystemFonts.MenuFont!).Width;
            if (w > maxW) maxW = w;
        }
        int width = maxW + LeftPad + RightPad;
        int height = 2;
        foreach (var e in _entries)
        {
            height += e.Kind == EntryKind.Separator ? _sepH : _itemH;
        }
        return new Size(width, height);
    }

    private int HitIndex(int y)
    {
        int ty = 1;
        for (int i = 0; i < _entries.Count; i++)
        {
            int h = _entries[i].Kind == EntryKind.Separator ? _sepH : _itemH;
            if (y >= ty && y < ty + h && _entries[i].Kind != EntryKind.Separator) return i;
            ty += h;
        }
        return -1;
    }

    private int ItemTop(int index)
    {
        int ty = 1;
        for (int i = 0; i < index && i < _entries.Count; i++)
        {
            ty += _entries[i].Kind == EntryKind.Separator ? _sepH : _itemH;
        }
        return ty;
    }

    private void UpdateHover(int idx)
    {
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
        Invalidate();

        var entry = (idx >= 0 && idx < _entries.Count) ? _entries[idx] : null;
        if (entry != null && entry.Kind == EntryKind.Submenu)
        {
            ShowSubmenuFor(idx);
        }
        else
        {
            CloseSubMenu();
        }
    }

    private void ShowSubmenuFor(int entryIndex)
    {
        var entry = _entries[entryIndex];
        if (_subMenu != null && _subMenu.Kind == entry.Sub) return;

        CloseSubMenu();

        var sub = new TraySubMenu(entry.Sub);
        sub.OnItemActivated += (_, _) => BeginInvoke(CloseMenu);

        var cur = Localization.Setting;
        sub.AddChecked(Localization.Get("LangSystem"), cur == Language.System, () => Program.Instance?.ChangeLanguage(Language.System));
        sub.AddChecked(Localization.Get("LangSC"), cur == Language.SimplifiedChinese, () => Program.Instance?.ChangeLanguage(Language.SimplifiedChinese));
        sub.AddChecked(Localization.Get("LangTC"), cur == Language.TraditionalChinese, () => Program.Instance?.ChangeLanguage(Language.TraditionalChinese));
        sub.AddChecked(Localization.Get("LangEN"), cur == Language.English, () => Program.Instance?.ChangeLanguage(Language.English));
        sub.AddChecked(Localization.Get("LangJA"), cur == Language.Japanese, () => Program.Instance?.ChangeLanguage(Language.Japanese));
        sub.AddChecked(Localization.Get("LangKO"), cur == Language.Korean, () => Program.Instance?.ChangeLanguage(Language.Korean));
        sub.AddChecked(Localization.Get("LangDE"), cur == Language.German, () => Program.Instance?.ChangeLanguage(Language.German));
        sub.AddChecked(Localization.Get("LangFR"), cur == Language.French, () => Program.Instance?.ChangeLanguage(Language.French));
        sub.AddChecked(Localization.Get("LangES"), cur == Language.Spanish, () => Program.Instance?.ChangeLanguage(Language.Spanish));
        sub.AddChecked(Localization.Get("LangRU"), cur == Language.Russian, () => Program.Instance?.ChangeLanguage(Language.Russian));

        var size = sub.ComputeSize();
        int y = PointToScreen(new Point(0, ItemTop(entryIndex))).Y;
        int x = Right - 2;
        var work = Screen.FromPoint(new Point(x, y)).WorkingArea;
        if (x + size.Width > work.Right)
            x = Left - size.Width + 2;
        // Keep the submenu reachable from the parent item: shift it up so its
        // bottom hugs the working area instead of flipping entirely above the
        // parent (which breaks the hover path from the parent item into it).
        if (y + size.Height > work.Bottom)
            y = work.Bottom - size.Height;
        if (y < work.Top)
            y = work.Top;
        sub.ShowAt(new Point(x, y), size);
        _subMenu = sub;
    }

    private void CloseSubMenu()
    {
        _subMenu?.Dispose();
        _subMenu = null;
    }

    private void CloseMenu()
    {
        _foregroundTimer.Stop();
        UninstallMouseHook();
        CloseSubMenu();
        if (!IsDisposed)
            Hide();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseProc = MouseHookCallback;
            _mouseHook = NativeMethods.SetWindowsHookEx(14, _mouseProc, NativeMethods.GetModuleHandle(null), 0u);
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == 0x0201 || msg == 0x0204) // WM_LBUTTONDOWN | WM_RBUTTONDOWN
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.x, info.pt.y);
                bool inMain = Bounds.Contains(pt);
                bool inSub = _subMenu != null && _subMenu.Visible && _subMenu.Bounds.Contains(pt);
                if (!inMain && !inSub)
                    BeginInvoke(CloseMenu);
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdateHover(HitIndex(e.Y));
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int num = HitIndex(e.Y);
            if (num >= 0 && num < _entries.Count && _entries[num].Kind == EntryKind.Item)
            {
                Action? action = _entries[num].Action;
                CloseMenu();
                action?.Invoke();
            }
        }
        base.OnMouseUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(MenuBg))
        using (var path = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            g.FillPath(brush, path);

        int y = 1;
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Kind == EntryKind.Separator)
            {
                int lineY = y + _sepH / 2;
                using var sepPen = new Pen(SeparatorColor);
                g.DrawLine(sepPen, 8, lineY, Width - 8, lineY);
                y += _sepH;
                continue;
            }

            var rect = new Rectangle(1, y, Width - 2, _itemH);
            bool hover = i == _hoverIndex;
            if (hover)
            {
                using var hoverBrush = new SolidBrush(MenuHover);
                g.FillRectangle(hoverBrush, rect);
            }
            var foreColor = hover ? MenuTextSelected : MenuText;
            TextRenderer.DrawText(g, entry.Text ?? "", SystemFonts.MenuFont,
                new Rectangle(LeftPad, y, Width - LeftPad - RightPad, _itemH), foreColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            if (entry.Kind == EntryKind.Submenu)
                TextRenderer.DrawText(g, "\u25B8", SystemFonts.MenuFont,
                    new Rectangle(Width - RightPad + 4, y, 20, _itemH), foreColor,
                    TextFormatFlags.VerticalCenter);
            y += _itemH;
        }

        using var pen = new Pen(MenuBorder);
        using var borderPath = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        g.DrawPath(pen, borderPath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _foregroundTimer.Dispose();
            UninstallMouseHook();
            CloseSubMenu();
        }
        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

/// <summary>
/// The hover-expanded submenu of the tray menu (language).
/// Mirrors the owner-draw look of the parent menu.
/// </summary>
internal sealed class TraySubMenu : Form
{
    private sealed class Item
    {
        public string Text = "";
        public bool Checked;
        public Action? Action;
    }

    private readonly List<Item> _items = new();
    private int _hoverIndex = -1;
    private int _itemH;
    private int _scrollPos;
    private int _maxScroll;
    private bool _draggingThumb;
    private int _dragOffsetY;
    private const int MaxVisibleItems = 4;

    private const int LeftPad = 14;
    private const int RightPad = 14;
    private const int CheckPad = 20;

    private readonly TrayMenuForm.SubmenuKind _kind;

    public TrayMenuForm.SubmenuKind Kind => _kind;

    private static bool Dark => ThemeManager.IsDark;

    private static Color MenuBg => Dark ? Color.FromArgb(30, 30, 30) : Color.White;

    private static Color MenuHover => Dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(229, 241, 251);

    private static Color MenuText => Dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);

    private static Color MenuTextSelected => Dark ? Color.White : Color.FromArgb(20, 20, 20);

    private static Color MenuBorder => Dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
            return cp;
        }
    }

    public event EventHandler? OnItemActivated;

    internal TraySubMenu(TrayMenuForm.SubmenuKind kind)
    {
        _kind = kind;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void Add(string text, Action action)
    {
        _items.Add(new Item { Text = text, Action = action });
    }

    public void AddChecked(string text, bool isChecked, Action action)
    {
        _items.Add(new Item { Text = text, Checked = isChecked, Action = action });
    }

    public Size ComputeSize()
    {
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        int maxW = 0;
        foreach (var item in _items)
            maxW = Math.Max(maxW, TextRenderer.MeasureText(item.Text, SystemFonts.MenuFont).Width);
        int checkRoom = _kind == TrayMenuForm.SubmenuKind.Language ? CheckPad : 0;
        int width = maxW + LeftPad + RightPad + checkRoom;
        int total = _items.Count;
        int visible = Math.Min(total, MaxVisibleItems);
        int height = 2 + visible * _itemH;
        _maxScroll = Math.Max(0, total * _itemH - (visible * _itemH));
        _scrollPos = Math.Clamp(_scrollPos, 0, _maxScroll);
        return new Size(width, height);
    }

    public void ShowAt(Point screenPt, Size size)
    {
        _itemH = Math.Max(22, SystemFonts.MenuFont!.Height + 8);
        BackColor = MenuBg;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, screenPt.X, screenPt.Y, size.Width, size.Height, 16u);
        Show();
        // Double call: the first positions it, the second re-asserts the Z order.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, screenPt.X, screenPt.Y, size.Width, size.Height, 16u);
        Region?.Dispose();
        using (var rp = ThemedComboBox.RoundedRect(new Rectangle(0, 0, size.Width, size.Height), 8))
            Region = new Region(rp);
    }

    private int HitIndex(int y)
    {
        int idx = _scrollPos / _itemH + (y - 1) / _itemH;
        if (idx < 0 || idx >= _items.Count) return -1;
        return idx;
    }

    private void PaintScrollbar(Graphics g, bool dark)
    {
        var tr = GetThumbRect();
        var thumbColor = dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(190, 190, 190);
        using var b = new SolidBrush(thumbColor);
        using var path = ThemedComboBox.RoundedRect(tr, 2);
        g.FillPath(b, path);
    }

    private Rectangle GetThumbRect()
    {
        int sbW = 6;
        int x = Width - sbW - 3;
        int trackH = Height - 8;
        int totalH = _items.Count * _itemH;
        int visibleH = Height - 4;
        int thumbH = Math.Max(24, (int)(trackH * (double)visibleH / totalH));
        double ratio = _maxScroll > 0 ? (double)_scrollPos / _maxScroll : 0;
        int thumbY = 4 + (int)(ratio * (trackH - thumbH));
        return new Rectangle(x, thumbY, sbW, thumbH);
    }

    private bool InScrollbarArea(int x) => x > Width - 12;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int delta = e.Delta > 0 ? -3 * _itemH : 3 * _itemH;
        _scrollPos = Math.Clamp(_scrollPos + delta, 0, _maxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingThumb && _maxScroll > 0)
        {
            var tr = GetThumbRect();
            int trackH = Height - 8;
            int maxTravel = trackH - tr.Height;
            double ratio = maxTravel > 0 ? (double)(e.Y - _dragOffsetY - 4) / maxTravel : 0;
            _scrollPos = (int)Math.Round(Math.Clamp(ratio, 0, 1) * _maxScroll);
            Invalidate();
        }
        else
        {
            int idx = HitIndex(e.Y);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                Invalidate();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_maxScroll > 0 && InScrollbarArea(e.X))
            {
                var tr = GetThumbRect();
                if (e.Y >= tr.Top && e.Y <= tr.Bottom)
                {
                    // Start dragging the thumb.
                    _draggingThumb = true;
                    _dragOffsetY = e.Y - tr.Top;
                }
                else
                {
                    // Click on the gutter: page up/down.
                    int page = Height - 4;
                    _scrollPos = Math.Clamp(_scrollPos + (e.Y < tr.Top ? -page : page), 0, _maxScroll);
                    Invalidate();
                }
            }
            else
            {
                // Fire the item action on mouse DOWN (not up). The parent menu's
                // outside-click hook can close the submenu on the press itself
                // (its Bounds check races with the WM_LBUTTONUP dispatch), which
                // would otherwise swallow the click. Invoking on down guarantees
                // the language switch runs before any deferred CloseMenu.
                int idx = HitIndex(e.Y);
                if (idx >= 0)
                {
                    try
                    {
                        _items[idx].Action?.Invoke();
                    }
                    finally
                    {
                        OnItemActivated?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _draggingThumb = false;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(MenuBg))
        using (var path = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            g.FillPath(brush, path);

        bool dark = ThemeManager.IsDark;
        bool hasCheck = _kind == TrayMenuForm.SubmenuKind.Language;
        int textLeft = LeftPad + (hasCheck ? CheckPad : 0);

        int first = _scrollPos / _itemH;
        int visible = Math.Max(0, (Height - 2) / _itemH);
        int end = Math.Min(_items.Count, first + visible);
        int y = 1;
        for (int i = first; i < end; i++)
        {
            var item = _items[i];
            var rect = new Rectangle(1, y, Width - 2, _itemH);
            bool hover = i == _hoverIndex;
            if (hover)
            {
                using var hoverBrush = new SolidBrush(MenuHover);
                g.FillRectangle(hoverBrush, rect);
            }
            var foreColor = hover ? MenuTextSelected : MenuText;
            if (hasCheck && item.Checked)
                TextRenderer.DrawText(g, "\u2713", SystemFonts.MenuFont,
                    new Rectangle(16, y, CheckPad, _itemH), foreColor,
                    TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, item.Text, SystemFonts.MenuFont,
                new Rectangle(textLeft, y, Width - textLeft - RightPad, _itemH), foreColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            y += _itemH;
        }

        if (_maxScroll > 0) PaintScrollbar(g, dark);

        using var pen = new Pen(MenuBorder);
        using var borderPath = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        g.DrawPath(pen, borderPath);
    }
}
