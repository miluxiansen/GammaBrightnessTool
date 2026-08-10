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
            MessageBox.Show(
                $"{Localization.Get("StartupFailed")}: {ex.Message}",
                Localization.Get("Error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
