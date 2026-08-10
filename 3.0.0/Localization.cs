namespace GammaBrightnessTool;

public enum Language
{
    SimplifiedChinese,
    TraditionalChinese,
    English
}

public static class Localization
{
    private static readonly Dictionary<string, Dictionary<Language, string>> _strings = new()
    {
        ["TrayTooltip"] = new()
        {
            [Language.SimplifiedChinese] = "亮度 {0}%",
            [Language.TraditionalChinese] = "亮度 {0}%",
            [Language.English] = "Brightness {0}%"
        },
        ["PowerOffDisplay"] = new()
        {
            [Language.SimplifiedChinese] = "息屏",
            [Language.TraditionalChinese] = "息屏",
            [Language.English] = "Sleep"
        },
        ["PowerOffDisplayTip"] = new()
        {
            [Language.SimplifiedChinese] = "关闭显示器",
            [Language.TraditionalChinese] = "關閉顯示器",
            [Language.English] = "Turn off display"
        },
        ["BrightnessLevels"] = new()
        {
            [Language.SimplifiedChinese] = "亮度挡位",
            [Language.TraditionalChinese] = "亮度檔位",
            [Language.English] = "Brightness Levels"
        },
        ["Startup"] = new()
        {
            [Language.SimplifiedChinese] = "开机启动",
            [Language.TraditionalChinese] = "開機啟動",
            [Language.English] = "Start with Windows"
        },
        ["StartupFailed"] = new()
        {
            [Language.SimplifiedChinese] = "设置开机启动失败",
            [Language.TraditionalChinese] = "設定開機啟動失敗",
            [Language.English] = "Failed to set startup"
        },
        ["Error"] = new()
        {
            [Language.SimplifiedChinese] = "错误",
            [Language.TraditionalChinese] = "錯誤",
            [Language.English] = "Error"
        },
        ["Language"] = new()
        {
            [Language.SimplifiedChinese] = "语言/Language",
            [Language.TraditionalChinese] = "語言/Language",
            [Language.English] = "Language"
        },
        ["LangSC"] = new()
        {
            [Language.SimplifiedChinese] = "简体中文",
            [Language.TraditionalChinese] = "简体中文",
            [Language.English] = "Simplified Chinese"
        },
        ["LangTC"] = new()
        {
            [Language.SimplifiedChinese] = "繁體中文",
            [Language.TraditionalChinese] = "繁體中文",
            [Language.English] = "Traditional Chinese"
        },
        ["LangEN"] = new()
        {
            [Language.SimplifiedChinese] = "English",
            [Language.TraditionalChinese] = "English",
            [Language.English] = "English"
        },
        ["RestartApp"] = new()
        {
            [Language.SimplifiedChinese] = "重启软件",
            [Language.TraditionalChinese] = "重啟軟體",
            [Language.English] = "Restart App"
        },
        ["Exit"] = new()
        {
            [Language.SimplifiedChinese] = "退出程序",
            [Language.TraditionalChinese] = "退出程式",
            [Language.English] = "Exit"
        },
        ["OverlayTitle"] = new()
        {
            [Language.SimplifiedChinese] = "亮度",
            [Language.TraditionalChinese] = "亮度",
            [Language.English] = "Brightness"
        },
        ["Uninstall"] = new()
        {
            [Language.SimplifiedChinese] = "卸载软件",
            [Language.TraditionalChinese] = "卸載軟體",
            [Language.English] = "Uninstall"
        },
        ["UninstallTitle"] = new()
        {
            [Language.SimplifiedChinese] = "确认卸载",
            [Language.TraditionalChinese] = "確認卸載",
            [Language.English] = "Confirm Uninstall"
        },
        ["UninstallPrompt"] = new()
        {
            [Language.SimplifiedChinese] = "确定要卸载 Gamma Brightness Tool 吗？\n\n这将删除所有程序文件和设置。",
            [Language.TraditionalChinese] = "確定要卸載 Gamma Brightness Tool 嗎？\n\n這將刪除所有程式檔案和設定。",
            [Language.English] = "Are you sure you want to uninstall Gamma Brightness Tool?\n\nThis will delete all program files and settings."
        }
    };

    private static Language _current = Language.SimplifiedChinese;

    public static Language Current
    {
        get => _current;
        set
        {
            if (_current != value)
            {
                _current = value;
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    public static event EventHandler? LanguageChanged;

    public static string Get(string key, params object?[] args)
    {
        if (_strings.TryGetValue(key, out var dict) && dict.TryGetValue(_current, out var text))
        {
            return args.Length > 0 ? string.Format(text, args) : text;
        }
        return key;
    }

    public static string Get(Language lang, string key, params object?[] args)
    {
        if (_strings.TryGetValue(key, out var dict) && dict.TryGetValue(lang, out var text))
        {
            return args.Length > 0 ? string.Format(text, args) : text;
        }
        return key;
    }
}

