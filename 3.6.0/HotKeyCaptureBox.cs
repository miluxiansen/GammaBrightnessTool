using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// A text box that captures a hotkey combination. Left-clicking it starts
/// recording; modifier keys + one main key are joined with "+" (e.g.
/// "Ctrl+Shift+Up"). Delete/Backspace/Escape without modifiers clears the
/// capture (so the confirm button can commit an "unbind"). The owning form's
/// confirm (√) / cancel (×) buttons commit or revert the recording.
/// </summary>
public sealed class HotKeyCaptureBox : RoundedTextBox
{
    private bool _capturing;
    private bool _cleared;
    private int _modifiers;
    private int _mainKey;
    private string _savedValue = "";
    private string _placeholder = "";

    /// <summary>Raised when recording starts / a key is captured / commit / cancel.</summary>
    public event EventHandler? CaptureStateChanged;

    // 固定 10pt 字体：DPI 缩放会把 Font 放大，FontChanged 拉回此实例。
    private static readonly Font FixedFont = new Font("Segoe UI", 10F);

    public bool IsCapturing => _capturing;

    /// <summary>True after Delete/Backspace/Escape cleared the combo (confirm = unbind).</summary>
    public bool IsCleared => _cleared;

    /// <summary>The value committed before the current recording started.</summary>
    public string SavedValue => _savedValue;

    /// <summary>The currently captured combo ("" while none / cleared).</summary>
    public string CapturedHotKey => _mainKey != 0 ? HotKeyService.Format(_mainKey, _modifiers) : "";

    /// <summary>
    /// When the user clicks the confirm/cancel buttons, focus leaves this box
    /// first (Leave fires BEFORE the button's GotFocus/MouseDown, as verified
    /// by an event-order test). The form sets this flag from the buttons'
    /// MouseDown/GotFocus so the deferred auto-cancel (which runs after the
    /// button's events) can skip cancelling the recording.
    /// </summary>
    public bool AutoCancelSuppressed { get; set; }

    public HotKeyCaptureBox()
    {
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        Font = FixedFont;
        // DPI 变化时 WinForms 会把显式设置的 Point 字体缩放（GetScaledFont，
        // 10pt→12.5pt @125%），导致框内快捷键文字随 DPI 变大/变小；
        // FontChanged 处理器把字体拉回固定 10pt，与设置页其他文字一致。
        // 复用静态实例而非每次 new Font（旧写法每次 DPI 变化泄漏一个 GDI 字体）。
        FontChanged += (_, _) =>
        {
            var f = Font;
            if (f.Size != FixedFont.Size || f.Unit != FixedFont.Unit) Font = FixedFont;
        };
    }

    /// <summary>Applies the current theme colors (background, text, border).</summary>
    public override void ApplyTheme(Color background, Color foreground)
    {
        base.ApplyTheme(background, foreground);
        RefreshText();
    }

    /// <summary>Sets the hint text shown when no hotkey is bound.</summary>
    public void SetPlaceholder(string text)
    {
        _placeholder = text ?? "";
        RefreshText();
    }

    /// <summary>The committed hotkey string ("" = not bound).</summary>
    public string HotKey
    {
        get => _savedValue;
        set
        {
            _savedValue = value ?? "";
            RefreshText();
        }
    }

    public void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _cleared = false;
        _modifiers = 0;
        _mainKey = 0;
        Text = "…";
        Focus();
        Select(0, 0);
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Commits the given value and leaves recording mode.</summary>
    public void CommitCapture(string value)
    {
        _capturing = false;
        _cleared = false;
        _savedValue = value ?? "";
        RefreshText();
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reverts to the saved value and leaves recording mode.</summary>
    public void CancelCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _cleared = false;
        RefreshText();
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string RecordingDisplay =>
        _mainKey != 0
            ? HotKeyService.Format(_mainKey, _modifiers)
            : (HotKeyService.FormatModifiers(_modifiers).Length > 0
                ? HotKeyService.FormatModifiers(_modifiers) + " + …"
                : "…");

    private void RefreshText()
    {
        if (_capturing) return;
        bool empty = string.IsNullOrEmpty(_savedValue);
        Text = empty ? _placeholder : _savedValue;
        ForeColor = empty
            ? (ThemeManager.IsDark ? Color.FromArgb(130, 130, 130) : Color.Gray)
            : (ThemeManager.IsDark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(40, 40, 40));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            StartCapture();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnKeyDown(e);
            return;
        }

        // Suppress everything while recording so nothing is typed / beeps.
        e.Handled = true;
        e.SuppressKeyPress = true;

        var kc = e.KeyCode;

        if (kc == Keys.ControlKey || kc == Keys.LControlKey || kc == Keys.RControlKey)
        {
            _modifiers |= MOD_CONTROL;
            _cleared = false;
        }
        else if (kc == Keys.ShiftKey || kc == Keys.LShiftKey || kc == Keys.RShiftKey)
        {
            _modifiers |= MOD_SHIFT;
            _cleared = false;
        }
        else if (kc == Keys.Menu || kc == Keys.LMenu || kc == Keys.RMenu)
        {
            _modifiers |= MOD_ALT;
            _cleared = false;
        }
        else if (kc == Keys.LWin || kc == Keys.RWin)
        {
            _modifiers |= MOD_WIN;
            _cleared = false;
        }
        else if ((kc == Keys.Delete || kc == Keys.Back || kc == Keys.Escape) && _modifiers == 0)
        {
            // Clear the captured combination (confirm then = unbind).
            _modifiers = 0;
            _mainKey = 0;
            _cleared = true;
        }
        else if (kc == Keys.Tab && _modifiers == 0)
        {
            // Let Tab move focus away; the Leave handler cancels recording.
            e.Handled = false;
            e.SuppressKeyPress = false;
            base.OnKeyDown(e);
            return;
        }
        else if ((kc == Keys.Enter || kc == Keys.Space) && _modifiers == 0)
        {
            // Ignore Enter/Space without modifiers (no accidental confirm).
            base.OnKeyDown(e);
            return;
        }
        else
        {
            _mainKey = (int)kc;
            _cleared = false;
        }

        Text = RecordingDisplay;
        Select(0, 0);
        CaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        // Leave fires BEFORE the button's GotFocus/MouseDown (verified), so
        // we cannot know here whether focus moved to the confirm/cancel
        // buttons. Defer the auto-cancel to the end of the current message
        // (BeginInvoke): by then the button's MouseDown/GotFocus handlers
        // have run and set AutoCancelSuppressed if a button was clicked.
        if (_capturing)
        {
            var box = this;
            BeginInvoke(new Action(() =>
            {
                bool suppressed = box.AutoCancelSuppressed;
                box.AutoCancelSuppressed = false; // consume for next Leave
                if (box._capturing && !suppressed)
                {
                    box.CancelCapture();
                }
            }));
        }
    }
}
