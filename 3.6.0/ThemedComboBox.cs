using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GammaBrightnessTool;

/// <summary>
/// Fully custom dropdown control (replaces the WinForms ComboBox).
///
/// Why: the native ComboBox popup (ComboLBox) owns its scrollbar — a themed
/// WS_VSCROLL that stutters on first drag and renders its track dead-black in
/// an owner-drawn list (Win11 OS behavior, unfixable from user code). Since we
/// may add more languages/options later, extending the list height forever is
/// not a real fix. This control draws its own box AND its own popup list with
/// a custom scrollbar (same 6px style as ThemeScrollPanel), so scrolling is
/// fully under our control and never stutters.
///
/// API-compatible with the old ThemedComboBox: Items.Add(string),
/// SelectedIndex (with SelectedIndexChanged), ApplyTheme(background,
/// foreground), SetParentBackground(color), CornerRadius, DropDownHeight.
/// </summary>
public sealed class ThemedComboBox : Control
{
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private Color _fieldBg = Color.White;
    private Color _textColor = Color.Black;
    private Color _parentBg = Color.White;
    private DropdownListPopup? _popup;
    // 下拉框固定字体（Point 单位）。DPI 变化时 WinForms 会把显式设置的
    // Point 字体缩放（GetScaledFont，10pt→12.5pt @125%），主控件绘制（Font）
    // 与下拉弹窗绘制（_owner.Font）都会受影响；FontChanged 处理器把字体
    // 拉回此固定实例，与设置页选项文字保持一致。
    private static readonly Font FixedFont = new Font("Segoe UI", 10F);

    /// <summary>Corner radius of the closed combo box body.</summary>
    public int CornerRadius { get; set; } = 6;

    /// <summary>Kept for API compatibility; the popup height is computed
    /// from item count and the working area instead.</summary>
    public int DropDownHeight { get; set; } = 120;

    /// <summary>Display items.</summary>
    public List<string> Items => _items;

    /// <summary>Currently selected index, or -1 when nothing is selected.
    /// Setting the same value does not raise SelectedIndexChanged.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int v = value < 0 || value >= _items.Count ? -1 : value;
            if (v == _selectedIndex) return;
            _selectedIndex = v;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Text of the current selection ("" when none).</summary>
    public string SelectedText => _selectedIndex >= 0 ? _items[_selectedIndex] : "";

    private string? _displayText;
    /// <summary>
    /// Optional display text shown while the dropdown is closed, overriding
    /// the selected item's label (e.g. showing the live brightness/temperature
    /// value instead of the nearest preset). The open list still shows the
    /// original item labels. Set to null to disable.
    /// </summary>
    public string? DisplayText
    {
        get => _displayText;
        set
        {
            if (_displayText == value) return;
            _displayText = value;
            Invalidate();
        }
    }

    /// <summary>List background color (popup uses it for its surface).</summary>
    internal Color FieldBg => _fieldBg;

    /// <summary>所在卡片/页面的背景色（下拉弹窗的空余区域用它填充，避免深色下
    /// 弹出列表底部露出与选项同色的突兀矩形色带）。</summary>
    internal Color ParentBg => _parentBg;

    /// <summary>
    /// Forces a re-selection of the currently selected item: temporarily
    /// resets to -1 and back so SelectedIndexChanged fires even when the
    /// user clicks the already-selected entry (e.g. re-applying the
    /// current brightness level / temperature preset).
    /// </summary>
    public void ReapplySelection()
    {
        if (_selectedIndex < 0) return;
        int idx = _selectedIndex;
        SelectedIndex = -1;  // clear via property
        SelectedIndex = idx; // re-select via property: fires SelectedIndexChanged
    }

    public event EventHandler? SelectedIndexChanged;

    public ThemedComboBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 10F);
        FontChanged += (_, _) =>
        {
            var f = Font;
            if (f.Size != FixedFont.Size || f.Unit != FixedFont.Unit) Font = FixedFont;
        };
    }

    /// <summary>Color used to fill the area outside the rounded corners
    /// (should match the card the box sits on).</summary>
    public void SetParentBackground(Color color)
    {
        _parentBg = color;
        Invalidate();
    }

    /// <summary>Field background + text color (theme refresh entry point).
    /// Also syncs the corner fill (_parentBg): the body's rounded corners
    /// are painted with it, and leaving it stale after a theme switch shows
    /// the old theme's colour in the four corners (e.g. dark corners on a
    /// light theme).</summary>
    public void ApplyTheme(Color background, Color foreground)
    {
        _fieldBg = background;
        _parentBg = background;
        _textColor = foreground;
        Invalidate();
        if (_popup != null && _popup.Visible) _popup.Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        if (_popup != null && !_popup.IsDisposed && _popup.Visible)
        {
            _popup.ClosePopup();
            return;
        }
        OpenPopup();
    }

    private void OpenPopup()
    {
        if (_items.Count == 0) return;
        // Always build a fresh popup: Form.Close() can dispose a non-modal
        // form when its handle is gone, and reusing a closed/disposed popup
        // throws ObjectDisposedException on the next Show().
        if (_popup != null)
        {
            _popup.Dispose();
            _popup = null;
        }
        _popup = new DropdownListPopup(this);
        _popup.ShowAt(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // Fill outside the rounded body with the parent color so no white
        // square corners ever show on a dark theme.
        using (var b = new SolidBrush(_parentBg))
            g.FillRectangle(b, ClientRectangle);

        var bodyRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(bodyRect, CornerRadius);

        var bgColor = Enabled ? _fieldBg : Blend(_fieldBg, _parentBg, 0.45f);
        using (var b = new SolidBrush(bgColor))
            g.FillPath(b, path);

        var border = ThemeManager.IsDark ? Color.FromArgb(80, 80, 88) : Color.FromArgb(150, 150, 150);
        using (var pen = new Pen(border))
            g.DrawPath(pen, path);

        // Selected text (DisplayText overrides while closed).
        var fg = Enabled ? _textColor : Blend(_textColor, _parentBg, 0.45f);
        var textRect = new Rectangle(10, 0, Width - 10 - 24, Height);
        TextRenderer.DrawText(g, DisplayText ?? SelectedText, Font, textRect, fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        // Dropdown arrow.
        DrawArrow(g, fg);
    }

    private void DrawArrow(Graphics g, Color color)
    {
        int cx = Width - 11;
        int cy = Height / 2;
        using var b = new SolidBrush(color);
        g.FillPolygon(b, new[]
        {
            new PointF(cx - 4f, cy - 2f),
            new PointF(cx + 4f, cy - 2f),
            new PointF(cx, cy + 3f)
        });
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled && _popup != null && _popup.Visible) _popup.Close();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _popup?.Dispose();
        base.Dispose(disposing);
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb(
            Math.Clamp((int)(a.R + (b.R - a.R) * t), 0, 255),
            Math.Clamp((int)(a.G + (b.G - a.G) * t), 0, 255),
            Math.Clamp((int)(a.B + (b.B - a.B) * t), 0, 255));
    }
}

/// <summary>
/// The popup list window of <see cref="ThemedComboBox"/>: a borderless,
/// non-activating, topmost form that draws its items and — when the list is
/// taller than the screen budget — a custom 6px scrollbar. Clicking outside
/// closes it (via an application message filter); a click inside selects the
/// item under the cursor.
/// </summary>
internal sealed class DropdownListPopup : Form, IMessageFilter
{
    private readonly ThemedComboBox _owner;
    private int _scrollPos;
    private int _maxScroll;
    private int _itemH;
    private int _hoverIndex = -1;
    private int _downIndex = -1;
    private bool _draggingThumb;
    private int _dragOffsetY;
    // Top-level window we are attached to; its Deactivate closes us when the
    // user clicks another app (a message filter only sees our own messages).
    private Form? _ownerForm;
    // Set on every mouse-down inside the popup (items, scrollbar).
    private DateTime _lastInsideClick = DateTime.MinValue;
    // Clicking this no-activate popup still makes the owner lose activation
    // briefly (the OS foreground handling), with WM_MOUSEACTIVATE arriving
    // before our MouseDown. So Deactivate does not close immediately: it
    // arms this timer, and 200ms later we check whether a click actually
    // landed inside us (scrollbar/item) or the user really left.
    private readonly System.Windows.Forms.Timer _deactivateTimer = new() { Interval = 200 };
    // Low-level mouse hook: the definitive "clicked outside" detector. The
    // message filter only sees our own process, and owner Deactivate stops
    // firing once the owner is already inactive, so clicks on other apps
    // after the owner lost focus would otherwise never close the popup.
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;
    // Foreground watcher: Alt+Tab / switching apps with the keyboard never
    // produces a mouse click, and once the owner is already inactive its
    // Deactivate no longer fires either. Polling the foreground window
    // catches every "user left" case regardless of how it happened.
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 100 };
    private IntPtr _lastForeground = IntPtr.Zero;
    // Max items visible without scrolling; the language list (10 items) will
    // show the custom scrollbar, shorter lists show all items.
    private const int MaxVisibleItems = 8;

    public DropdownListPopup(ThemedComboBox owner)
    {
        _owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // Not TopMost: a topmost popup floats above unrelated apps (e.g.
        // over a chat input while typing). The popup sits above the active
        // settings window naturally; clicking another app deactivates the
        // owner and closes us.
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _deactivateTimer.Tick += (_, _) =>
        {
            _deactivateTimer.Stop();
            if ((DateTime.Now - _lastInsideClick).TotalMilliseconds >= 300 && Visible)
            {
                ClosePopup();
            }
        };
        _foregroundTimer.Tick += (_, _) =>
        {
            if (!Visible || IsDisposed)
            {
                _foregroundTimer.Stop();
                return;
            }
            IntPtr fg = GetForegroundWindow();
            if (fg == _lastForeground) return;
            _lastForeground = fg;
            if (fg == Handle) return;
            if (_ownerForm != null && (fg == _ownerForm.Handle || IsOwnedBy(fg, _ownerForm.Handle))) return;
            ClosePopup();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    /// <summary>Position below (or above, when space is short) the owner and show.</summary>
    public void ShowAt(ThemedComboBox owner)
    {
        _itemH = Math.Max(20, (int)(24 * owner.DeviceDpi / 96.0f));
        int total = owner.Items.Count;
        var work = Screen.GetWorkingArea(owner);
        int maxListH = Math.Min((int)(work.Height * 0.6), MaxVisibleItems * _itemH + 4);
        int listH = Math.Min(total * _itemH + 4, maxListH);
        _maxScroll = Math.Max(0, total * _itemH - (listH - 4));
        _scrollPos = Math.Clamp(_scrollPos, 0, _maxScroll);

        var ownerRect = owner.RectangleToScreen(owner.ClientRectangle);
        // Keep the popup inside the top-level window, not just the screen:
        // with DWM rounded corners the transparent area outside the radius
        // shows whatever is underneath, so a popup sticking out of its owner
        // window exposes foreign content (desktop/other windows) as light or
        // black corner patches. Intersect the working area with the owner
        // window rect to make the corners show the owner's own background.
        var bound = work;
        if (owner.TopLevelControl is Control tl)
        {
            var tlRect = tl.RectangleToScreen(tl.ClientRectangle);
            bound = Rectangle.Intersect(work, tlRect);
            if (bound.Width < 60 || bound.Height < 60) bound = work; // fallback
        }
        int x = ownerRect.Left;
        int y = ownerRect.Bottom + 1;
        if (y + listH > bound.Bottom) y = ownerRect.Top - listH - 1;
        if (y < bound.Top) y = bound.Top;
        if (x + owner.Width > bound.Right) x = bound.Right - owner.Width;
        if (x < bound.Left) x = bound.Left;
        int finalH = Math.Min(listH, Math.Max(0, bound.Bottom - y));
        Bounds = new Rectangle(x, y, owner.Width, finalH);

        // Any pixel the custom paint does not cover (e.g. the thin border
        // corners, or a repaint gap) must show the CARD/page background
        // colour (not the list colour): a gap painted with the option colour
        // reads as an odd rectangle between the list bottom and the next
        // control on dark themes.
        BackColor = _owner.ParentBg;

        _hoverIndex = -1;
        _downIndex = -1;

        // Own the popup to the settings window: clicking the no-activate
        // popup itself (items, scrollbar) keeps the owner active (no
        // spurious Deactivate -> close), the popup stays above the owner,
        // and clicking another app still deactivates the owner ->
        // OnOwnerDeactivated closes us.
        if (owner.TopLevelControl is Form topForm)
        {
            _ownerForm = topForm;
            Owner = topForm;
            topForm.Deactivate += OnOwnerDeactivated;
        }
        Show();
        // Win11 rounds the corners of borderless TopMost windows and fills
        // the area outside the radius with black; ask for an explicit round
        // corner so the outside is transparent instead of black triangles.
        try
        {
            int pref = 2; // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { /* best-effort; harmless on older builds */ }
        Application.AddMessageFilter(this);
        InstallMouseHook();
        _foregroundTimer.Start();
    }

    /// <summary>Hide the popup and detach the click filter. Unlike Form.Close,
    /// this never disposes the form, so the owner can safely rebuild it.</summary>
    public void ClosePopup()
    {
        _deactivateTimer.Stop();
        _foregroundTimer.Stop();
        UninstallMouseHook();
        Application.RemoveMessageFilter(this);
        if (_ownerForm != null)
        {
            _ownerForm.Deactivate -= OnOwnerDeactivated;
            _ownerForm = null;
        }
        if (!IsDisposed) Hide();
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        // The click that deactivated the owner may have landed on us (items,
        // scrollbar) and its MouseDown arrives right after Deactivate; decide
        // 200ms later based on whether a click actually happened inside.
        _deactivateTimer.Stop();
        _deactivateTimer.Start();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandle(null!), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg == 0x0201 /* WM_LBUTTONDOWN */ || msg == 0x0204 /* WM_RBUTTONDOWN */)
            {
                var pt = Marshal.PtrToStructure<MouseHookPoint>(lParam);
                if (!Bounds.Contains(new Point(pt.X, pt.Y)))
                {
                    // Runs on the UI thread (hook installed there); still
                    // defer so we never close while the message is being
                    // dispatched to the popup itself.
                    BeginInvoke(ClosePopup);
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static bool IsOwnedBy(IntPtr hwnd, IntPtr owner)
    {
        IntPtr cur = hwnd;
        while (cur != IntPtr.Zero)
        {
            if (cur == owner) return true;
            cur = GetWindow(cur, 4 /* GW_OWNER */);
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        UninstallMouseHook();
        if (_ownerForm != null)
        {
            _ownerForm.Deactivate -= OnOwnerDeactivated;
            _ownerForm = null;
        }
        base.OnFormClosed(e);
    }

    /// <summary>Clicking anywhere outside the popup closes it; the message is
    /// not swallowed so the underlying control still receives the click.</summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == 0x0201 /* WM_LBUTTONDOWN */ && Visible)
        {
            if (!Bounds.Contains(Cursor.Position)) ClosePopup();
        }
        return false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = ThemeManager.IsDark;

        var bg = _owner.FieldBg;
        var pageBg = _owner.ParentBg;   // 空隙/边角用卡片底色，融入页面背景
        var hover = dark ? Color.FromArgb(78, 78, 86) : Color.FromArgb(229, 241, 251);
        var textNormal = dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40);

        // 弹窗底色 = 页面背景（列表底部任何未被行覆盖的空白/边角不再显示选项色）
        using (var b = new SolidBrush(pageBg))
            g.FillRectangle(b, ClientRectangle);

        int first = _scrollPos / _itemH;
        int visible = Math.Max(0, (Height - 4) / _itemH);
        int end = Math.Min(_owner.Items.Count, first + visible);
        for (int i = first; i < end; i++)
        {
            var rect = new Rectangle(1, 2 + (i - first) * _itemH, Width - 2, _itemH);
            bool hov = i == _hoverIndex;
            // 行背景统一为选项色；hover 行再叠高亮色（避免行间透出页面底色）
            if (hov)
            {
                using var hb = new SolidBrush(hover);
                g.FillRectangle(hb, rect);
            }
            else
            {
                using var rb = new SolidBrush(bg);
                g.FillRectangle(rb, rect);
            }
            var fg = textNormal;
            var textRect = new Rectangle(rect.Left + 8, rect.Top, rect.Width - 16, rect.Height);
            TextRenderer.DrawText(g, _owner.Items[i], _owner.Font, textRect, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        if (_maxScroll > 0) PaintScrollbar(g, dark);

        // Rounded border matching the DWM corner preference so the 1px frame
        // follows the rounded outline instead of being clipped at the corners.
        var border = dark ? Color.FromArgb(88, 88, 96) : Color.FromArgb(160, 160, 160);
        using (var pen = new Pen(border))
        using (var path = ThemedComboBox.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            g.DrawPath(pen, path);
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
        int totalH = _owner.Items.Count * _itemH;
        int visibleH = Height - 4;
        int thumbH = Math.Max(24, (int)(trackH * (double)visibleH / totalH));
        double ratio = _maxScroll > 0 ? (double)_scrollPos / _maxScroll : 0;
        int thumbY = 4 + (int)(ratio * (trackH - thumbH));
        return new Rectangle(x, thumbY, sbW, thumbH);
    }

    private bool InScrollbarArea(int x) => x > Width - 12;

    private int HitIndex(int y)
    {
        int first = _scrollPos / _itemH;
        int idx = first + (y - 2) / _itemH;
        if (idx < 0 || idx >= _owner.Items.Count) return -1;
        return idx;
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
        _lastInsideClick = DateTime.Now;
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
                _downIndex = -1;
            }
            else
            {
                _downIndex = HitIndex(e.Y);
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_draggingThumb)
        {
            _draggingThumb = false;
            return;
        }
        if (e.Button == MouseButtons.Left && _downIndex >= 0)
        {
            if (HitIndex(e.Y) == _downIndex)
            {
                // 点击已选中的项也要重新触发 SelectedIndexChanged
                // （如重新应用当前亮度挡位/色温预设）。
                if (_owner.SelectedIndex == _downIndex)
                    _owner.ReapplySelection();
                else
                    _owner.SelectedIndex = _downIndex;
                ClosePopup();
                return;
            }
        }
        _downIndex = -1;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int delta = e.Delta > 0 ? -3 * _itemH : 3 * _itemH;
        _scrollPos = Math.Clamp(_scrollPos + delta, 0, _maxScroll);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            UninstallMouseHook();
            _deactivateTimer.Dispose();
            _foregroundTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
