using Microsoft.Win32;

namespace GammaBrightnessTool;

/// <summary>
/// Manages Windows startup registry entry.
/// </summary>
public static class StartupManager
{
    private const string REGISTRY_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "GammaBrightnessTool";

    /// <summary>
    /// 自启命令行里 exe 之后的固定参数。与 Setup.iss 写 Run 键时保持一致；
    /// 写成常量是为了让"比对 Run 值"与"写入 Run 值"用的是同一份事实。
    /// </summary>
    private const string APP_ARGS = "--silent";

    /// <summary>
    /// Checks if the application is set to start with Windows.
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, false);
            return key?.GetValue(APP_NAME) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables or disables startup with Windows, and syncs the state to settings.json.
    /// Creates the Run key if it does not exist (previously the call silently
    /// failed when the key was missing, e.g. on trimmed systems).
    /// </summary>
    public static void SetStartup(bool enable)
    {
        try
        {
            // CreateSubKey opens for write and creates the key when missing.
            using var key = Registry.CurrentUser.CreateSubKey(REGISTRY_KEY);
            if (key == null) return;

            if (enable)
            {
                key.SetValue(APP_NAME, BuildCommandLine(Application.ExecutablePath));
            }
            else
            {
                key.DeleteValue(APP_NAME, false);
            }

            // 同步更新设置文件，确保注册表与配置一致
            var settings = SettingsManager.Load();
            settings.StartupEnabled = enable;
            SettingsManager.Save(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{Localization.Get("StartupFailed")}: {ex.Message}",
                Localization.Get("Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 构造自启命令行：带引号的 exe 路径 + 固定参数。
    /// 写入与比对共用此方法，避免两处各写一份格式而产生假不一致。
    /// </summary>
    public static string BuildCommandLine(string exePath) => $"\"{exePath}\" {APP_ARGS}";

    /// <summary>
    /// 读取当前自启 Run 值的命令行原文。未设置 / 类型异常 / 读取失败均返回 false。
    /// </summary>
    public static bool TryGetStartupCommandLine(out string commandLine)
    {
        commandLine = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, false);
            if (key?.GetValue(APP_NAME) is string value && value.Length > 0)
            {
                commandLine = value;
                return true;
            }
        }
        catch
        {
            // 读不到按「未设置」处理，调用方据此跳过判断
        }
        return false;
    }

    /// <summary>
    /// 从 Run 命令行里剥出可执行文件路径。兼容两种写法：
    ///   * 带引号（本程序与 Setup.iss 的写法）："D:\...\GammaBrightnessTool.exe" --silent
    ///   * 不带引号（仅手工编辑场景）：取第一个 ".exe" 之前的部分
    /// 形态无法解析（空串、引号不闭合、无 .exe 后缀）返回 false。
    /// </summary>
    public static bool TryExtractExePath(string commandLine, out string exePath)
    {
        exePath = "";
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        string s = commandLine.Trim();
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end <= 1) return false;                 // 引号不闭合 / 空引号
            exePath = s.Substring(1, end - 1).Trim();
        }
        else
        {
            int end = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return false;                  // 无 .exe，形态不明
            exePath = s.Substring(0, end + 4).Trim();
        }
        return exePath.Length > 0;
    }

    /// <summary>
    /// 该路径是否可用于「文件是否存在」判断。**只有返回 true 时才允许据此改写 Run 键**。
    ///
    /// 要求：盘符绝对路径（X:\...），不含环境变量（%VAR%）与通配符，且所在盘已就绪。
    /// 排除的形态与理由：
    ///   * UNC（\\server\share）与映射盘：连接未就绪时 File.Exists 恒为 false，
    ///     会把「暂时访问不到」误判成「文件已被删除」；
    ///   * %LOCALAPPDATA%\... 之类：File.Exists 不展开环境变量，必然误判；
    ///   * 裸文件名：依赖 PATH / 工作目录解析，同样不可靠。
    /// 这些形态只可能来自用户手工编辑 Run 值——本程序与安装器写入的恒为带引号盘符路径。
    /// </summary>
    public static bool IsSafeForExistenceCheck(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOf('%') >= 0) return false;
        if (path.IndexOfAny(new[] { '*', '?' }) >= 0) return false;

        // 盘符绝对路径：至少 3 字符（X:\），首字符字母、次字符 ':'、第三为分隔符
        if (path.Length < 3) return false;
        if (!char.IsLetter(path[0]) || path[1] != ':') return false;
        if (path[2] != '\\' && path[2] != '/') return false;

        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return new System.IO.DriveInfo(root).IsReady;
        }
        catch
        {
            return false;   // 盘符非法等 → 不冒险判断
        }
    }

    /// <summary>
    /// 静默改写自启运行项：写入的注册表值与配置改动与 <see cref="SetStartup"/> 完全一致，
    /// 唯一差别是**失败不弹框**（仅记 OpLog），返回是否写入成功。
    ///
    /// 供启动自检（IntegrityChecker）调用，遵守 3.7.0 设计稿 §3.4 的 fail-safe 定档：
    /// 启动阶段绝不弹框打断用户。
    /// </summary>
    public static bool TrySetStartupSilent(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(REGISTRY_KEY);
            if (key == null) return false;

            if (enable)
            {
                key.SetValue(APP_NAME, BuildCommandLine(Application.ExecutablePath));
            }
            else
            {
                key.DeleteValue(APP_NAME, false);
            }

            var settings = SettingsManager.Load();
            settings.StartupEnabled = enable;
            SettingsManager.Save(settings);
            return true;
        }
        catch (Exception ex)
        {
            OpLog.LogEx("[StartupManager] silent startup write failed", ex);
            return false;
        }
    }
}
