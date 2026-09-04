using System.Globalization;
using System.Text;

namespace GammaBrightnessTool;

/// <summary>
/// 运行操作日志（诊断用）：把关键操作、输入参数与返回结果按时间顺序写入
/// %TEMP%\GammaBrightnessTool_ops.log，供实测后复盘。
///
/// 生效范围（编译期开关 GBT_INTERNAL_LOG，见 csproj）：
///  * Debug 构建（源码运行/调试）与显式 -p:InternalLog=true 的 Release
///    （内部测试版）→ 完整实现；
///  * 正式版 Release（未加 InternalLog）→ 全部方法为空实现：接口保留、
///    调用点不变，但零写入、零内存/CPU/磁盘开销。
///
/// 行为约束（仅内部构建下）：
///  - 线程安全（进程内 lock），UTF-8 无 BOM 追加写；
///  - 超 512 KB 自动轮转为 .old；
///  - LogThrottled：同一 key 在 intervalMs 内只落第一条，防高频路径刷屏。
/// 日志失败静默忽略，绝不干扰运行。
/// </summary>
public static class OpLog
{
    /// <summary>当前构建日志是否生效（正式版恒为 false）。</summary>
    public static bool IsEnabled =>
#if GBT_INTERNAL_LOG
        true;
#else
        false;
#endif

    public static string LogFilePath =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GammaBrightnessTool_ops.log");

#if GBT_INTERNAL_LOG

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> ThrottleMap = new();
    private static readonly int RotationBytes = 512 * 1024;

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

    /// <summary>记录一次异常（含堆栈首行）。</summary>
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

#else

    // ---- 正式版：接口保留，全部空实现（无任何 IO/内存/CPU 开销）----

    public static void Log(string message) { }

    public static void LogThrottled(string key, string message, int intervalMs = 300) { }

    public static void LogEx(string context, Exception ex) { }

#endif
}
