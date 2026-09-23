using System.Diagnostics;

#if DEBUG
namespace GammaBrightnessTool;

/// <summary>
/// Lightweight file logger for DPI/popup debugging.
/// Writes to %TEMP%\GammaBrightnessPopup.log.
/// Only compiled in DEBUG builds.
/// </summary>
internal static class PopupDebug
{
    private static readonly object _lock = new();
    private static readonly string _path;

    static PopupDebug()
    {
        _path = Path.Combine(Path.GetTempPath(), "GammaBrightnessPopup.log");
    }

    public static void Log(string format, params object[] args)
    {
        try
        {
            lock (_lock)
            {
                string line = (args == null || args.Length == 0)
                    ? format
                    : string.Format(format, args);
                File.AppendAllText(_path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { /* best-effort */ }
    }

    public static void Clear() { try { File.WriteAllText(_path, ""); } catch { } }
}
#endif
