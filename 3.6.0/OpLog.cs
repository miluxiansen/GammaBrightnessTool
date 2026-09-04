using System.Globalization;
using System.Text;

namespace GammaBrightnessTool;

/// <summary>
/// 运行操作日志（实测诊断用）：把关键操作、输入参数与返回结果按时间顺序写入
/// %TEMP%\GammaBrightnessTool_ops.log，供实测后复盘。
///  - 线程安全（进程内 lock），UTF-8 无 BOM 追加写；
///  - 超 512 KB 自动轮转为 .old（保留最近 2 份体积）；
///  - LogThrottled：同一 key 在 intervalMs 内只落第一条，防动画/高频路径刷屏。
/// 纯日志，不影响任何业务行为；写入失败静默忽略（不干扰运行）。
/// </summary>
public static class OpLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> ThrottleMap = new();
    private static readonly int RotationBytes = 512 * 1024;

    public static string LogFilePath =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GammaBrightnessTool_ops.log");

    public static void Log(string message)
    {
        try
        {
            string line = string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss.fff}] {1}",
                DateTime.Now, message);
            lock (Gate)
            {
                RotateIfNeeded();
                System.IO.File.AppendAllText(LogFilePath, line + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 日志失败绝不打扰运行
        }
    }

    /// <summary>限流日志：同 key 在 intervalMs 内只写第一条（默认 300ms）。</summary>
    public static void LogThrottled(string key, string message, int intervalMs = 300)
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (ThrottleMap.TryGetValue(key, out long last) && now - last < intervalMs)
                return;
            ThrottleMap[key] = now;
        }
        Log(message);
    }

    /// <summary>记录一次异常（含堆栈首行）并返回，方便调用方连写业务上下文。</summary>
    public static void LogEx(string context, Exception ex)
    {
        Log(context + " => " + ex.GetType().Name + ": " + ex.Message);
        if (ex.StackTrace != null)
        {
            string first = ex.StackTrace.Replace("\r", "").Split('\n')[0].Trim();
            if (first.Length > 0) Log("    at " + first);
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new System.IO.FileInfo(LogFilePath);
            if (!fi.Exists || fi.Length < RotationBytes) return;
            string old = LogFilePath + ".old";
            try { System.IO.File.Delete(old); } catch { /* 忽略 */ }
            System.IO.File.Move(LogFilePath, old);
        }
        catch
        {
            // 轮转失败不阻断写日志
        }
    }
}
