using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static GammaBrightnessTool.NativeMethods;

namespace GammaBrightnessTool;

/// <summary>
/// Manages global hotkey registration via RegisterHotKey / UnregisterHotKey.
/// Hotkeys are registered on a hidden message window so WM_HOTKEY
/// messages can be dispatched to the correct window handle.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly Dictionary<int, Action> _hotKeyCallbacks = new();
    private int _nextId = 1;

    public HotKeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>
    /// Parses a hotkey string (e.g. "Ctrl+Shift+Up") and registers it.
    /// Returns the assigned id, or -1 on failure (e.g. already taken by another app).
    /// </summary>
    public int Register(string hotkeyString, Action callback)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString)) return -1;

        if (!Parse(hotkeyString, out int vk, out int modifiers))
            return -1;

        int id = Interlocked.Increment(ref _nextId);
        if (!RegisterHotKey(_windowHandle, id, modifiers, vk))
        {
            Debug.WriteLine($"[HotKeyService] Failed to register {hotkeyString}: {Marshal.GetLastWin32Error()}");
            return -1;
        }

        _hotKeyCallbacks[id] = callback;
        return id;
    }

    /// <summary>
    /// Unregisters a hotkey by its assigned id.
    /// </summary>
    public void Unregister(int id)
    {
        if (id <= 0) return;
        if (_hotKeyCallbacks.Remove(id))
        {
            UnregisterHotKey(_windowHandle, id);
        }
    }

    /// <summary>
    /// Unregisters all currently held hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _hotKeyCallbacks.Keys.ToList())
        {
            UnregisterHotKey(_windowHandle, id);
        }
        _hotKeyCallbacks.Clear();
    }

    /// <summary>
    /// Called by the tray message window's WndProc when a WM_HOTKEY arrives.
    /// Dispatches to the registered callback.
    /// </summary>
    public void ProcessHotKey(int id)
    {
        if (_hotKeyCallbacks.TryGetValue(id, out var callback))
        {
            callback();
        }
    }

    /// <summary>
    /// Parses a hotkey string like "Ctrl+Shift+Up" into vk and modifiers.
    /// Returns false if the string is empty or cannot be parsed.
    /// </summary>
    public static bool Parse(string s, out int vk, out int modifiers)
    {
        vk = 0;
        modifiers = 0;

        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split('+');
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (string.IsNullOrEmpty(p)) continue;

            switch (p.ToLowerInvariant())
            {
                // Modifiers
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;

                // Virtual keys
                case "a": vk = 0x41; break;
                case "b": vk = 0x42; break;
                case "c": vk = 0x43; break;
                case "d": vk = 0x44; break;
                case "e": vk = 0x45; break;
                case "f": vk = 0x46; break;
                case "g": vk = 0x47; break;
                case "h": vk = 0x48; break;
                case "i": vk = 0x49; break;
                case "j": vk = 0x4A; break;
                case "k": vk = 0x4B; break;
                case "l": vk = 0x4C; break;
                case "m": vk = 0x4D; break;
                case "n": vk = 0x4E; break;
                case "o": vk = 0x4F; break;
                case "p": vk = 0x50; break;
                case "q": vk = 0x51; break;
                case "r": vk = 0x52; break;
                case "s": vk = 0x53; break;
                case "t": vk = 0x54; break;
                case "u": vk = 0x55; break;
                case "v": vk = 0x56; break;
                case "w": vk = 0x57; break;
                case "x": vk = 0x58; break;
                case "y": vk = 0x59; break;
                case "z": vk = 0x5A; break;

                case "0": vk = 0x30; break;
                case "1": vk = 0x31; break;
                case "2": vk = 0x32; break;
                case "3": vk = 0x33; break;
                case "4": vk = 0x34; break;
                case "5": vk = 0x35; break;
                case "6": vk = 0x36; break;
                case "7": vk = 0x37; break;
                case "8": vk = 0x38; break;
                case "9": vk = 0x39; break;

                // Navigation / function keys
                case "f1": vk = 0x70; break;
                case "f2": vk = 0x71; break;
                case "f3": vk = 0x72; break;
                case "f4": vk = 0x73; break;
                case "f5": vk = 0x74; break;
                case "f6": vk = 0x75; break;
                case "f7": vk = 0x76; break;
                case "f8": vk = 0x77; break;
                case "f9": vk = 0x78; break;
                case "f10": vk = 0x79; break;
                case "f11": vk = 0x7A; break;
                case "f12": vk = 0x7B; break;

                case "space": vk = 0x20; break;
                case "enter":
                case "return": vk = 0x0D; break;
                case "tab": vk = 0x09; break;
                case "escape":
                case "esc": vk = 0x1B; break;
                case "backspace": vk = 0x08; break;
                case "delete":
                case "del": vk = 0x2E; break;
                case "insert": vk = 0x2D; break;
                case "home": vk = 0x24; break;
                case "end": vk = 0x23; break;
                case "pageup": vk = 0x21; break;
                case "pagedown": vk = 0x22; break;

                // Arrow keys
                case "up":
                case "uparrow": vk = 0x26; break;
                case "down":
                case "downarrow": vk = 0x28; break;
                case "left":
                case "leftarrow": vk = 0x25; break;
                case "right":
                case "rightarrow": vk = 0x27; break;

                // Punctuation
                case "add":
                case "plus": vk = 0x6B; break;
                case "subtract":
                case "minus": vk = 0x6D; break;
                case "multiply": vk = 0x6A; break;
                case "divide": vk = 0x6F; break;
                case "oem_minus": vk = 0xBD; break;
                case "oem_plus": vk = 0xBB; break;
                case "oemcomma": vk = 0xBC; break;
                case "oemperiod": vk = 0xBE; break;
                case "oemopenbrackets": vk = 0xDB; break;
                case "oemclosebrackets": vk = 0xDD; break;
                case "oemsemicolon": vk = 0xBA; break;
                case "oemquotes": vk = 0xDE; break;
                case "oemquestion": vk = 0xBF; break;
                case "oemtilde": vk = 0xC0; break;

                // Numpad keys (VK_NUMPAD0..9 = 0x60..0x69).
                // Keys.ToString() renders them as "NumPad0".."NumPad9", so
                // the capture box can produce these and Parse must accept
                // them again (round-trip), otherwise the hotkey silently
                // fails to register.
                case "numpad0": vk = 0x60; break;
                case "numpad1": vk = 0x61; break;
                case "numpad2": vk = 0x62; break;
                case "numpad3": vk = 0x63; break;
                case "numpad4": vk = 0x64; break;
                case "numpad5": vk = 0x65; break;
                case "numpad6": vk = 0x66; break;
                case "numpad7": vk = 0x67; break;
                case "numpad8": vk = 0x68; break;
                case "numpad9": vk = 0x69; break;
                case "numpadadd": vk = 0x6B; break;
                case "numpadsubtract": vk = 0x6D; break;
                case "numpadmultiply": vk = 0x6A; break;
                case "numpaddivide": vk = 0x6F; break;
                case "decimal":
                case "numpaddecimal": vk = 0x6E; break;
                case "numpadseparator": vk = 0x6C; break;
                case "oemminus": vk = 0xBD; break;
                case "oemplus": vk = 0xBB; break;

                default:
                    return false;
            }
        }

        return vk != 0;
    }

    /// <summary>
    /// Formats modifier flags alone ("Ctrl + Shift"). Used by the capture
    /// box to show which modifiers are held while the main key is pending.
    /// </summary>
    public static string FormatModifiers(int modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Formats a vk + modifiers into a display string like "Ctrl + Shift + Up".
    /// Returns an empty string for an invalid combination.
    /// </summary>
    public static string Format(int vk, int modifiers)
    {
        if (vk == 0) return "";

        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");

        string keyName = vk switch
        {
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0x0D => "Enter",
            0x09 => "Tab",
            0x1B => "Escape",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x20 => "Space",
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
            0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
            0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
            0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
            0x41 => "A", 0x42 => "B", 0x43 => "C", 0x44 => "D", 0x45 => "E",
            0x46 => "F", 0x47 => "G", 0x48 => "H", 0x49 => "I", 0x4A => "J",
            0x4B => "K", 0x4C => "L", 0x4D => "M", 0x4E => "N", 0x4F => "O",
            0x50 => "P", 0x51 => "Q", 0x52 => "R", 0x53 => "S", 0x54 => "T",
            0x55 => "U", 0x56 => "V", 0x57 => "W", 0x58 => "X", 0x59 => "Y",
            0x5A => "Z",
            0x60 => "NumPad0", 0x61 => "NumPad1", 0x62 => "NumPad2", 0x63 => "NumPad3", 0x64 => "NumPad4",
            0x65 => "NumPad5", 0x66 => "NumPad6", 0x67 => "NumPad7", 0x68 => "NumPad8", 0x69 => "NumPad9",
            0x6A => "NumPadMultiply", 0x6B => "NumPadAdd", 0x6C => "NumPadSeparator",
            0x6D => "NumPadSubtract", 0x6E => "NumPadDecimal", 0x6F => "NumPadDivide",
            0xBC => "Oemcomma", 0xBD => "OemMinus", 0xBE => "OemPeriod", 0xBB => "OemPlus",
            0xDB => "OemOpenBrackets", 0xDD => "OemCloseBrackets", 0xBA => "OemSemicolon",
            0xDE => "OemQuotes", 0xBF => "OemQuestion", 0xC0 => "Oemtilde",
            _ => ((Keys)vk).ToString()
        };

        parts.Add(keyName);
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}
