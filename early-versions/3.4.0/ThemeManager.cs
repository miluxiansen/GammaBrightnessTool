using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// Resolves the effective UI theme (dark/light) from the user's theme
/// setting and (for ThemeMode.System) the Windows app theme preference
/// (HKCU\...\Themes\Personalize\AppsUseLightTheme). Raising ThemeChanged
/// lets open windows (settings form, tray menu owner) repaint immediately
/// when the user switches theme at runtime.
/// </summary>
public static class ThemeManager
{
    private static ThemeMode _mode = ThemeMode.System;
    private static ThemeMode _popupMode = ThemeMode.System;
    private static bool _watching;
    private static System.Threading.Timer? _pollTimer;
    private static int _lastSystemDark = -1; // -1 = unknown yet

    /// <summary>The user's theme choice as stored in settings.</summary>
    public static ThemeMode Mode => _mode;

    /// <summary>The popup theme choice (independent of the main UI theme).</summary>
    public static ThemeMode PopupMode => _popupMode;

    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Raised when the popup theme (PopupMode) or the effective popup
    /// dark/light state changes. The floating popups subscribe to this so
    /// they repaint independently of the main UI theme.
    /// </summary>
    public static event EventHandler? PopupThemeChanged;

    /// <summary>
    /// Raised whenever the OS dark/light preference flips, regardless of the
    /// app theme mode. Used for elements bound to the OS theme only (e.g. the
    /// tray icon glyph: dark taskbar needs a white icon, light taskbar a
    /// black one, independent of the in-app theme choice).
    /// </summary>
    public static event EventHandler? SystemThemeChanged;

    /// <summary>
    /// Applies the user's theme choice (called at startup and whenever the
    /// theme combo changes). Raises ThemeChanged only when the effective
    /// dark/light state actually changes, so rebuilds are minimal.
    /// </summary>
    public static void Apply(ThemeMode mode)
    {
        bool oldDark = IsDark;
        _mode = mode;

        // Watch for OS theme changes while in System mode so windows can
        // repaint when the user flips the Windows dark/light switch.
        EnsureWatching();

        bool newDark = IsDark;
        if (newDark != oldDark)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies the popup theme choice (independent of the main UI theme).
    /// Called at startup and whenever the popup theme combo changes.
    /// Raises PopupThemeChanged when the effective popup dark/light state
    /// actually changes.
    /// </summary>
    public static void ApplyPopupTheme(ThemeMode mode)
    {
        bool oldDark = PopupIsDark;
        _popupMode = mode;
        EnsureWatching();

        bool newDark = PopupIsDark;
        if (newDark != oldDark)
        {
            PopupThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void EnsureWatching()
    {
        if (_watching) return;
        _watching = true;

        // Channel 1: OS broadcast (fires instantly on real UI switches).
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            // Rare in services/session-0; polling below still covers us.
        }

        // Channel 2: poll the registry every 500 ms. Covers switches that
        // do not broadcast (direct registry edits, some GPO/remote flows)
        // and is fully reliable regardless of message pump state.
        _pollTimer = new System.Threading.Timer(_ => PollSystemTheme(), null, 500, 500);
    }

    private static void PollSystemTheme()
    {
        int current = SystemUsesDarkTheme() ? 0 : 1;
        int prev = Interlocked.Exchange(ref _lastSystemDark, current);
        if (prev != -1 && prev != current)
        {
            // Always notify OS-theme-bound listeners (tray icon).
            SystemThemeChanged?.Invoke(null, EventArgs.Empty);
            // Rebuild the in-app UI only when following the system theme.
            if (_mode == ThemeMode.System)
            {
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            }
            // Rebuild the floating popups when they follow the system theme.
            if (_popupMode == ThemeMode.System)
            {
                PopupThemeChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        // Always notify OS-theme-bound listeners (tray icon); rebuild the
        // in-app UI only when following the system theme. The poller dedupes
        // actual flips.
        SystemThemeChanged?.Invoke(null, EventArgs.Empty);
        if (_mode == ThemeMode.System)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
        if (_popupMode == ThemeMode.System)
        {
            PopupThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>True when the Windows app mode is dark (AppsUseLightTheme=0),
    /// independent of the app theme setting. For tray icon / taskbar-bound
    /// elements.</summary>
    public static bool SystemIsDark => SystemUsesDarkTheme();

    /// <summary>True when the UI should render dark.</summary>
    public static bool IsDark
    {
        get
        {
            if (_mode == ThemeMode.Dark) return true;
            if (_mode == ThemeMode.Light) return false;
            return SystemUsesDarkTheme();
        }
    }

    /// <summary>True when the floating popups should render dark.</summary>
    public static bool PopupIsDark
    {
        get
        {
            if (_popupMode == ThemeMode.Dark) return true;
            if (_popupMode == ThemeMode.Light) return false;
            return SystemUsesDarkTheme();
        }
    }

    // ------------------------------------------------------------------
    // Popup palette (BrightnessPopup / BrightnessOverlay / PowerTipForm).
    // Dark = current look (black bg, white text, white slider fill).
    // Light = white bg, black text, blue slider fill as requested by user.
    // ------------------------------------------------------------------

    /// <summary>Popup window background. Light mode uses a light gray (not
    /// pure white) so the popup stays visible against a white desktop.</summary>
    public static Color PopupBg => PopupIsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);

    /// <summary>Primary text color (percentage label).</summary>
    public static Color PopupText => PopupIsDark ? Color.White : Color.FromArgb(20, 20, 20);

    /// <summary>Slider track (unfilled part).</summary>
    public static Color PopupTrack => PopupIsDark ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(200, 200, 200);

    /// <summary>Slider fill (brightness level) - blue in light mode.</summary>
    public static Color PopupFill => PopupIsDark ? Color.White : Color.FromArgb(0, 120, 215);

    /// <summary>Slider thumb circle.</summary>
    public static Color PopupThumb => PopupIsDark ? Color.White : Color.FromArgb(0, 120, 215);

    /// <summary>Thumb outline pen.</summary>
    public static Color PopupThumbOutline => PopupIsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(0, 90, 170);

    /// <summary>Power button background. Light mode slightly darker than the
    /// popup background (240) so the button stands out.</summary>
    public static Color PopupBtnBg => PopupIsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(215, 215, 215);

    /// <summary>Power button border (1px, light mode only; dark keeps none).</summary>
    public static Color PopupBtnBorder => PopupIsDark ? Color.Transparent : Color.FromArgb(150, 150, 150);

    /// <summary>Power button hover background.</summary>
    public static Color PopupBtnHover => PopupIsDark ? Color.FromArgb(90, 90, 90) : Color.FromArgb(196, 210, 228);

    /// <summary>Power button pressed background.</summary>
    public static Color PopupBtnDown => PopupIsDark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(192, 208, 228);

    /// <summary>Power button icon color.</summary>
    public static Color PopupBtnIcon => PopupIsDark ? Color.White : Color.FromArgb(40, 40, 40);

    /// <summary>PowerTip tooltip background. Light mode uses the same light
    /// gray as the popup.</summary>
    public static Color TipBg => PopupIsDark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(240, 240, 240);

    /// <summary>PowerTip tooltip text color.</summary>
    public static Color TipText => PopupIsDark ? Color.White : Color.FromArgb(30, 30, 30);

    /// <summary>
    /// Reads the Windows "app mode" (dark/light) preference. Defaults to
    /// light when the value cannot be read.
    /// </summary>
    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                return v == 0; // 0 = dark
            }
        }
        catch
        {
            // Fall through to light default.
        }
        return false;
    }
}
