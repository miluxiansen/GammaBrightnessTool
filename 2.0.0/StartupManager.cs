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
    /// </summary>
    public static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Application.ExecutablePath;
                string args = "--silent";
                key.SetValue(APP_NAME, $"\"{exePath}\" {args}");
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
            MessageBox.Show($"设置开机启动失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
