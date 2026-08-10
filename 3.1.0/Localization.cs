namespace GammaBrightnessTool;
using System.Globalization;

public enum Language
{
    // IMPORTANT: these values are persisted as numbers in settings.json.
    // Only append new languages at the END; never reorder or insert,
    // or saved settings will map to the wrong language.
    SimplifiedChinese,
    TraditionalChinese,
    English,
    Japanese,
    Korean,
    German,
    French,
    Spanish,
    Russian,
    /// <summary>
    /// Pseudo-language meaning "follow the system UI language". Never used
    /// as Localization.Current; it is resolved to a concrete language at
    /// startup (or when the user picks it).
    /// </summary>
    System
}
public enum ThemeMode
{
    // Persisted as numbers in settings.json; only append at the end.
    System,
    Dark,
    Light
}

public static class Localization
{
    private static readonly Dictionary<string, Dictionary<Language, string>> _strings = new()
    {
        ["TrayTooltip"] = new()
        {
            [Language.SimplifiedChinese] = "亮度 {0}%",
            [Language.TraditionalChinese] = "亮度 {0}%",
            [Language.English] = "Brightness {0}%",
            [Language.Japanese] = "明るさ {0}%",
            [Language.Korean] = "밝기 {0}%",
            [Language.German] = "Helligkeit {0}%",
            [Language.French] = "Luminosité {0}%",
            [Language.Spanish] = "Brillo {0}%",
            [Language.Russian] = "Яркость {0}%",
            [Language.System] = ""
        },
        ["PowerOffDisplay"] = new()
        {
            [Language.SimplifiedChinese] = "息屏",
            [Language.TraditionalChinese] = "息屏",
            [Language.English] = "Sleep",
            [Language.Japanese] = "画面オフ",
            [Language.Korean] = "화면 끄기",
            [Language.German] = "Bildschirm aus",
            [Language.French] = "Veille",
            [Language.Spanish] = "Apagar pantalla",
            [Language.Russian] = "Выкл. экран",
            [Language.System] = ""
        },
        ["PowerOffDisplayTip"] = new()
        {
            [Language.SimplifiedChinese] = "关闭显示器",
            [Language.TraditionalChinese] = "關閉顯示器",
            [Language.English] = "Turn off display",
            [Language.Japanese] = "モニターの電源を切る",
            [Language.Korean] = "모니터 끄기",
            [Language.German] = "Monitor ausschalten",
            [Language.French] = "Éteindre le moniteur",
            [Language.Spanish] = "Apagar el monitor",
            [Language.Russian] = "Выключить монитор",
            [Language.System] = ""
        },
        ["BrightnessLevels"] = new()
        {
            [Language.SimplifiedChinese] = "亮度挡位",
            [Language.TraditionalChinese] = "亮度檔位",
            [Language.English] = "Brightness Levels",
            [Language.Japanese] = "明るさレベル",
            [Language.Korean] = "밝기 수준",
            [Language.German] = "Helligkeitsstufen",
            [Language.French] = "Niveaux de luminosité",
            [Language.Spanish] = "Niveles de brillo",
            [Language.Russian] = "Уровни яркости",
            [Language.System] = ""
        },
        ["Startup"] = new()
        {
            [Language.SimplifiedChinese] = "开机启动",
            [Language.TraditionalChinese] = "開機啟動",
            [Language.English] = "Start with Windows",
            [Language.Japanese] = "起動時に開始",
            [Language.Korean] = "시작 프로그램",
            [Language.German] = "Beim Start ausführen",
            [Language.French] = "Démarrer avec Windows",
            [Language.Spanish] = "Iniciar con Windows",
            [Language.Russian] = "Запуск при загрузке",
            [Language.System] = ""
        },
        ["StartupFailed"] = new()
        {
            [Language.SimplifiedChinese] = "设置开机启动失败",
            [Language.TraditionalChinese] = "設定開機啟動失敗",
            [Language.English] = "Failed to set startup",
            [Language.Japanese] = "自動起動の設定に失敗しました",
            [Language.Korean] = "시작 프로그램 설정 실패",
            [Language.German] = "Starteinstellung fehlgeschlagen",
            [Language.French] = "Échec de la configuration du démarrage",
            [Language.Spanish] = "Error al configurar el inicio",
            [Language.Russian] = "Не удалось настроить автозапуск",
            [Language.System] = ""
        },
        ["Error"] = new()
        {
            [Language.SimplifiedChinese] = "错误",
            [Language.TraditionalChinese] = "錯誤",
            [Language.English] = "Error",
            [Language.Japanese] = "エラー",
            [Language.Korean] = "오류",
            [Language.German] = "Fehler",
            [Language.French] = "Erreur",
            [Language.Spanish] = "Error",
            [Language.Russian] = "Ошибка",
            [Language.System] = ""
        },
        ["Language"] = new()
        {
            [Language.SimplifiedChinese] = "语言",
            [Language.TraditionalChinese] = "語言",
            [Language.English] = "Language",
            [Language.Japanese] = "言語",
            [Language.Korean] = "언어",
            [Language.German] = "Sprache",
            [Language.French] = "Langue",
            [Language.Spanish] = "Idioma",
            [Language.Russian] = "Язык",
            [Language.System] = ""
        },
        ["Theme"] = new()
        {
            [Language.SimplifiedChinese] = "主题选择",
            [Language.TraditionalChinese] = "主題選擇",
            [Language.English] = "Theme",
            [Language.Japanese] = "テーマ",
            [Language.Korean] = "테마",
            [Language.German] = "Design",
            [Language.French] = "Thème",
            [Language.Spanish] = "Tema",
            [Language.Russian] = "Тема",
            [Language.System] = ""
        },
        ["ThemeSystem"] = new()
        {
            [Language.SimplifiedChinese] = "跟随系统",
            [Language.TraditionalChinese] = "跟隨系統",
            [Language.English] = "Follow System",
            [Language.Japanese] = "システムに従う",
            [Language.Korean] = "시스템 따르기",
            [Language.German] = "System folgen",
            [Language.French] = "Suivre le système",
            [Language.Spanish] = "Seguir sistema",
            [Language.Russian] = "Следовать системе",
            [Language.System] = ""
        },
        ["ThemeDark"] = new()
        {
            [Language.SimplifiedChinese] = "深色",
            [Language.TraditionalChinese] = "深色",
            [Language.English] = "Dark",
            [Language.Japanese] = "ダーク",
            [Language.Korean] = "다크",
            [Language.German] = "Dunkel",
            [Language.French] = "Sombre",
            [Language.Spanish] = "Oscuro",
            [Language.Russian] = "Тёмная",
            [Language.System] = ""
        },
        ["ThemeLight"] = new()
        {
            [Language.SimplifiedChinese] = "浅色",
            [Language.TraditionalChinese] = "淺色",
            [Language.English] = "Light",
            [Language.Japanese] = "ライト",
            [Language.Korean] = "라이트",
            [Language.German] = "Hell",
            [Language.French] = "Clair",
            [Language.Spanish] = "Claro",
            [Language.Russian] = "Светлая",
            [Language.System] = ""
        },
        ["PopupTheme"] = new()
        {
            [Language.SimplifiedChinese] = "浮窗主题",
            [Language.TraditionalChinese] = "浮窗主題",
            [Language.English] = "Popup theme",
            [Language.Japanese] = "ポップアップテーマ",
            [Language.Korean] = "팝업 테마",
            [Language.German] = "Popup-Design",
            [Language.French] = "Thème de la fenêtre",
            [Language.Spanish] = "Tema de la ventana",
            [Language.Russian] = "Тема всплывающего окна",
            [Language.System] = ""
        },
        ["StepSize"] = new()
        {
            [Language.SimplifiedChinese] = "滚轮步进",
            [Language.TraditionalChinese] = "滾輪步進",
            [Language.English] = "Wheel step",
            [Language.Japanese] = "ホイールのステップ",
            [Language.Korean] = "휠 단계",
            [Language.German] = "Rad-Schritt",
            [Language.French] = "Pas de molette",
            [Language.Spanish] = "Paso de rueda",
            [Language.Russian] = "Шаг колеса",
            [Language.System] = ""
        },
        ["InvertScroll"] = new()
        {
            [Language.SimplifiedChinese] = "反向滚轮",
            [Language.TraditionalChinese] = "反向滾輪",
            [Language.English] = "Invert wheel",
            [Language.Japanese] = "ホイール反転",
            [Language.Korean] = "휠 반전",
            [Language.German] = "Rad umkehren",
            [Language.French] = "Inverser la molette",
            [Language.Spanish] = "Invertir rueda",
            [Language.Russian] = "Инвертировать колесо",
            [Language.System] = ""
        },
        ["ShowOverlay"] = new()
        {
            [Language.SimplifiedChinese] = "显示 OSD 浮窗",
            [Language.TraditionalChinese] = "顯示 OSD 浮窗",
            [Language.English] = "Show OSD overlay",
            [Language.Japanese] = "OSD を表示",
            [Language.Korean] = "OSD 표시",
            [Language.German] = "OSD anzeigen",
            [Language.French] = "Afficher l'OSD",
            [Language.Spanish] = "Mostrar OSD",
            [Language.Russian] = "Показывать OSD",
            [Language.System] = ""
        },
        ["StepSize5"] = new()
        {
            [Language.SimplifiedChinese] = "5%（默认）",
            [Language.TraditionalChinese] = "5%（預設）",
            [Language.English] = "5% (default)",
            [Language.Japanese] = "5%（既定）",
            [Language.Korean] = "5%（기본）",
            [Language.German] = "5 % (Standard)",
            [Language.French] = "5 % (défaut)",
            [Language.Spanish] = "5 % (predet.)",
            [Language.Russian] = "5% (по умолч.)",
            [Language.System] = ""
        },
        ["StepSize10"] = new()
        {
            [Language.SimplifiedChinese] = "10%",
            [Language.TraditionalChinese] = "10%",
            [Language.English] = "10%",
            [Language.Japanese] = "10%",
            [Language.Korean] = "10%",
            [Language.German] = "10 %",
            [Language.French] = "10 %",
            [Language.Spanish] = "10 %",
            [Language.Russian] = "10%",
            [Language.System] = ""
        },
        ["StepSize15"] = new()
        {
            [Language.SimplifiedChinese] = "15%",
            [Language.TraditionalChinese] = "15%",
            [Language.English] = "15%",
            [Language.Japanese] = "15%",
            [Language.Korean] = "15%",
            [Language.German] = "15 %",
            [Language.French] = "15 %",
            [Language.Spanish] = "15 %",
            [Language.Russian] = "15%",
            [Language.System] = ""
        },
        ["StepSize20"] = new()
        {
            [Language.SimplifiedChinese] = "20%",
            [Language.TraditionalChinese] = "20%",
            [Language.English] = "20%",
            [Language.Japanese] = "20%",
            [Language.Korean] = "20%",
            [Language.German] = "20 %",
            [Language.French] = "20 %",
            [Language.Spanish] = "20 %",
            [Language.Russian] = "20%",
            [Language.System] = ""
        },
        ["LangSC"] = new()
        {
            [Language.SimplifiedChinese] = "简体中文",
            [Language.TraditionalChinese] = "简体中文",
            [Language.English] = "Simplified Chinese",
            [Language.Japanese] = "簡体中国語",
            [Language.Korean] = "중국어(간체)",
            [Language.German] = "Chinesisch (vereinfacht)",
            [Language.French] = "Chinois simplifié",
            [Language.Spanish] = "Chino simplificado",
            [Language.Russian] = "Китайский (упрощ.)",
            [Language.System] = ""
        },
        ["LangTC"] = new()
        {
            [Language.SimplifiedChinese] = "繁體中文",
            [Language.TraditionalChinese] = "繁體中文",
            [Language.English] = "Traditional Chinese",
            [Language.Japanese] = "繁体中国語",
            [Language.Korean] = "중국어(번체)",
            [Language.German] = "Chinesisch (traditionell)",
            [Language.French] = "Chinois traditionnel",
            [Language.Spanish] = "Chino tradicional",
            [Language.Russian] = "Китайский (традиц.)",
            [Language.System] = ""
        },
        ["LangEN"] = new()
        {
            [Language.SimplifiedChinese] = "English",
            [Language.TraditionalChinese] = "English",
            [Language.English] = "English",
            [Language.Japanese] = "英語",
            [Language.Korean] = "영어",
            [Language.German] = "Englisch",
            [Language.French] = "Anglais",
            [Language.Spanish] = "Inglés",
            [Language.Russian] = "Английский",
            [Language.System] = ""
        },
        ["LangJA"] = new()
        {
            [Language.SimplifiedChinese] = "日本語",
            [Language.TraditionalChinese] = "日本語",
            [Language.English] = "Japanese",
            [Language.Japanese] = "日本語",
            [Language.Korean] = "일본어",
            [Language.German] = "Japanisch",
            [Language.French] = "Japonais",
            [Language.Spanish] = "Japonés",
            [Language.Russian] = "Японский",
            [Language.System] = ""
        },
        ["LangKO"] = new()
        {
            [Language.SimplifiedChinese] = "한국어",
            [Language.TraditionalChinese] = "한국어",
            [Language.English] = "Korean",
            [Language.Japanese] = "韓国語",
            [Language.Korean] = "한국어",
            [Language.German] = "Koreanisch",
            [Language.French] = "Coréen",
            [Language.Spanish] = "Coreano",
            [Language.Russian] = "Корейский",
            [Language.System] = ""
        },
        ["LangDE"] = new()
        {
            [Language.SimplifiedChinese] = "Deutsch",
            [Language.TraditionalChinese] = "Deutsch",
            [Language.English] = "German",
            [Language.Japanese] = "ドイツ語",
            [Language.Korean] = "독일어",
            [Language.German] = "Deutsch",
            [Language.French] = "Allemand",
            [Language.Spanish] = "Alemán",
            [Language.Russian] = "Немецкий",
            [Language.System] = ""
        },
        ["LangFR"] = new()
        {
            [Language.SimplifiedChinese] = "Français",
            [Language.TraditionalChinese] = "Français",
            [Language.English] = "French",
            [Language.Japanese] = "フランス語",
            [Language.Korean] = "프랑스어",
            [Language.German] = "Französisch",
            [Language.French] = "Français",
            [Language.Spanish] = "Francés",
            [Language.Russian] = "Французский",
            [Language.System] = ""
        },
        ["LangES"] = new()
        {
            [Language.SimplifiedChinese] = "Español",
            [Language.TraditionalChinese] = "Español",
            [Language.English] = "Spanish",
            [Language.Japanese] = "スペイン語",
            [Language.Korean] = "스페인어",
            [Language.German] = "Spanisch",
            [Language.French] = "Espagnol",
            [Language.Spanish] = "Español",
            [Language.Russian] = "Испанский",
            [Language.System] = ""
        },
        ["LangRU"] = new()
        {
            [Language.SimplifiedChinese] = "Русский",
            [Language.TraditionalChinese] = "Русский",
            [Language.English] = "Russian",
            [Language.Japanese] = "ロシア語",
            [Language.Korean] = "러시아어",
            [Language.German] = "Russisch",
            [Language.French] = "Russe",
            [Language.Spanish] = "Ruso",
            [Language.Russian] = "Русский",
            [Language.System] = ""
        },
        ["LangSystem"] = new()
        {
            [Language.SimplifiedChinese] = "跟随系统",
            [Language.TraditionalChinese] = "跟隨系統",
            [Language.English] = "System",
            [Language.Japanese] = "システムに従う",
            [Language.Korean] = "시스템 언어",
            [Language.German] = "System",
            [Language.French] = "Système",
            [Language.Spanish] = "Sistema",
            [Language.Russian] = "Система",
            [Language.System] = ""
        },
        ["SystemLanguageUnsupported"] = new()
        {
            [Language.SimplifiedChinese] = "系统语言不受支持，已切换到英语。",
            [Language.TraditionalChinese] = "系統語言不受支援，已切換到英語。",
            [Language.English] = "The system language is not supported. Switched to English.",
            [Language.Japanese] = "システム言語はサポートされていません。英語に切り替えました。",
            [Language.Korean] = "시스템 언어가 지원되지 않습니다. 영어로 전환했습니다.",
            [Language.German] = "Die Systemsprache wird nicht unterstützt. Zu Englisch gewechselt.",
            [Language.French] = "La langue du système n'est pas prise en charge. Passage à l'anglais.",
            [Language.Spanish] = "El idioma del sistema no es compatible. Cambiado a inglés.",
            [Language.Russian] = "Язык системы не поддерживается. Переключено на английский.",
            [Language.System] = ""
        },
        ["RestartApp"] = new()
        {
            [Language.SimplifiedChinese] = "重启软件",
            [Language.TraditionalChinese] = "重啟軟體",
            [Language.English] = "Restart App",
            [Language.Japanese] = "アプリを再起動",
            [Language.Korean] = "앱 다시 시작",
            [Language.German] = "App neu starten",
            [Language.French] = "Redémarrer l'application",
            [Language.Spanish] = "Reiniciar la aplicación",
            [Language.Russian] = "Перезапустить приложение",
            [Language.System] = ""
        },
        ["Settings"] = new()
        {
            [Language.SimplifiedChinese] = "设置",
            [Language.TraditionalChinese] = "設定",
            [Language.English] = "Settings",
            [Language.Japanese] = "設定",
            [Language.Korean] = "설정",
            [Language.German] = "Einstellungen",
            [Language.French] = "Paramètres",
            [Language.Spanish] = "Configuración",
            [Language.Russian] = "Настройки",
            [Language.System] = ""
        },
        ["SettingsTitle"] = new()
        {
            [Language.SimplifiedChinese] = "设置 - Gamma Brightness Tool",
            [Language.TraditionalChinese] = "設定 - Gamma Brightness Tool",
            [Language.English] = "Settings - Gamma Brightness Tool",
            [Language.Japanese] = "設定 - Gamma Brightness Tool",
            [Language.Korean] = "설정 - Gamma Brightness Tool",
            [Language.German] = "Einstellungen - Gamma Brightness Tool",
            [Language.French] = "Paramètres - Gamma Brightness Tool",
            [Language.Spanish] = "Configuración - Gamma Brightness Tool",
            [Language.Russian] = "Настройки - Gamma Brightness Tool",
            [Language.System] = ""
        },
        ["SettingsGeneral"] = new()
        {
            [Language.SimplifiedChinese] = "通用设置",
            [Language.TraditionalChinese] = "通用設定",
            [Language.English] = "General",
            [Language.Japanese] = "一般設定",
            [Language.Korean] = "일반 설정",
            [Language.German] = "Allgemein",
            [Language.French] = "Général",
            [Language.Spanish] = "General",
            [Language.Russian] = "Общие",
            [Language.System] = ""
        },
        ["SettingsHotkeys"] = new()
        {
            [Language.SimplifiedChinese] = "热键绑定",
            [Language.TraditionalChinese] = "熱鍵綁定",
            [Language.English] = "Hotkeys",
            [Language.Japanese] = "ホットキー",
            [Language.Korean] = "단축키",
            [Language.German] = "Tastenkürzel",
            [Language.French] = "Raccourcis clavier",
            [Language.Spanish] = "Atajos de teclado",
            [Language.Russian] = "Горячие клавиши",
            [Language.System] = ""
        },
        ["SettingsAbout"] = new()
        {
            [Language.SimplifiedChinese] = "版本信息",
            [Language.TraditionalChinese] = "版本資訊",
            [Language.English] = "About",
            [Language.Japanese] = "バージョン情報",
            [Language.Korean] = "버전 정보",
            [Language.German] = "Über",
            [Language.French] = "À propos",
            [Language.Spanish] = "Acerca de",
            [Language.Russian] = "О программе",
            [Language.System] = ""
        },
        ["SettingsPlaceholder"] = new()
        {
            [Language.SimplifiedChinese] = "此功能开发中，敬请期待",
            [Language.TraditionalChinese] = "此功能開發中，敬請期待",
            [Language.English] = "Coming soon",
            [Language.Japanese] = "この機能は開発中です",
            [Language.Korean] = "준비 중입니다",
            [Language.German] = "In Kürze verfügbar",
            [Language.French] = "Bientôt disponible",
            [Language.Spanish] = "Próximamente",
            [Language.Russian] = "Скоро появится",
            [Language.System] = ""
        },
        ["SettingsRestartApp"] = new()
        {
            [Language.SimplifiedChinese] = "重启软件",
            [Language.TraditionalChinese] = "重啟軟體",
            [Language.English] = "Restart App",
            [Language.Japanese] = "アプリを再起動",
            [Language.Korean] = "앱 다시 시작",
            [Language.German] = "App neu starten",
            [Language.French] = "Redémarrer l'application",
            [Language.Spanish] = "Reiniciar la aplicación",
            [Language.Russian] = "Перезапустить приложение",
            [Language.System] = ""
        },
        ["On"] = new()
        {
            [Language.SimplifiedChinese] = "开",
            [Language.TraditionalChinese] = "開",
            [Language.English] = "ON",
            [Language.Japanese] = "オン",
            [Language.Korean] = "켜짐",
            [Language.German] = "EIN",
            [Language.French] = "MARCHE",
            [Language.Spanish] = "ENCENDIDO",
            [Language.Russian] = "ВКЛ",
            [Language.System] = ""
        },
        ["Off"] = new()
        {
            [Language.SimplifiedChinese] = "关",
            [Language.TraditionalChinese] = "關",
            [Language.English] = "OFF",
            [Language.Japanese] = "オフ",
            [Language.Korean] = "꺼짐",
            [Language.German] = "AUS",
            [Language.French] = "ARRÊT",
            [Language.Spanish] = "APAGADO",
            [Language.Russian] = "ВЫКЛ",
            [Language.System] = ""
        },
        ["AboutVersion"] = new()
        {
            [Language.SimplifiedChinese] = "版本",
            [Language.TraditionalChinese] = "版本",
            [Language.English] = "Version",
            [Language.Japanese] = "バージョン",
            [Language.Korean] = "버전",
            [Language.German] = "Version",
            [Language.French] = "Version",
            [Language.Spanish] = "Versión",
            [Language.Russian] = "Версия",
            [Language.System] = ""
        },
        ["CheckUpdate"] = new()
        {
            [Language.SimplifiedChinese] = "检查更新",
            [Language.TraditionalChinese] = "檢查更新",
            [Language.English] = "Check for Updates",
            [Language.Japanese] = "更新を確認",
            [Language.Korean] = "업데이트 확인",
            [Language.German] = "Nach Updates suchen",
            [Language.French] = "Rechercher des mises à jour",
            [Language.Spanish] = "Buscar actualizaciones",
            [Language.Russian] = "Проверить обновления",
            [Language.System] = ""
        },
        ["AboutDescription"] = new()
        {
            [Language.SimplifiedChinese] = "通过 Gamma Ramp 调节屏幕亮度的托盘工具",
            [Language.TraditionalChinese] = "透過 Gamma Ramp 調整螢幕亮度的托盤工具",
            [Language.English] = "Tray tool that adjusts screen brightness via Gamma Ramp",
            [Language.Japanese] = "Gamma Ramp で画面の明るさを調整するトレイツール",
            [Language.Korean] = "감마 램프로 화면 밝기를 조절하는 트레이 도구",
            [Language.German] = "Tray-Tool zur Anpassung der Bildschirmhelligkeit per Gamma Ramp",
            [Language.French] = "Outil de la zone de notification pour régler la luminosité via Gamma Ramp",
            [Language.Spanish] = "Herramienta de bandeja que ajusta el brillo de la pantalla mediante Gamma Ramp",
            [Language.Russian] = "Инструмент в трее для регулировки яркости экрана через Gamma Ramp",
            [Language.System] = ""
        },
        ["Exit"] = new()
        {
            [Language.SimplifiedChinese] = "退出程序",
            [Language.TraditionalChinese] = "退出程式",
            [Language.English] = "Exit",
            [Language.Japanese] = "終了",
            [Language.Korean] = "종료",
            [Language.German] = "Beenden",
            [Language.French] = "Quitter",
            [Language.Spanish] = "Salir",
            [Language.Russian] = "Выход",
            [Language.System] = ""
        },
        ["OverlayTitle"] = new()
        {
            [Language.SimplifiedChinese] = "亮度",
            [Language.TraditionalChinese] = "亮度",
            [Language.English] = "Brightness",
            [Language.Japanese] = "明るさ",
            [Language.Korean] = "밝기",
            [Language.German] = "Helligkeit",
            [Language.French] = "Luminosité",
            [Language.Spanish] = "Brillo",
            [Language.Russian] = "Яркость",
            [Language.System] = ""
        },
        ["Uninstall"] = new()
        {
            [Language.SimplifiedChinese] = "卸载软件",
            [Language.TraditionalChinese] = "卸載軟體",
            [Language.English] = "Uninstall",
            [Language.Japanese] = "アンインストール",
            [Language.Korean] = "제거",
            [Language.German] = "Deinstallieren",
            [Language.French] = "Désinstaller",
            [Language.Spanish] = "Desinstalar",
            [Language.Russian] = "Удалить",
            [Language.System] = ""
        },
        ["UninstallTitle"] = new()
        {
            [Language.SimplifiedChinese] = "确认卸载",
            [Language.TraditionalChinese] = "確認卸載",
            [Language.English] = "Confirm Uninstall",
            [Language.Japanese] = "アンインストールの確認",
            [Language.Korean] = "제거 확인",
            [Language.German] = "Deinstallation bestätigen",
            [Language.French] = "Confirmer la désinstallation",
            [Language.Spanish] = "Confirmar desinstalación",
            [Language.Russian] = "Подтверждение удаления",
            [Language.System] = ""
        },
        ["UninstallPrompt"] = new()
        {
            [Language.SimplifiedChinese] = "确定要卸载 Gamma Brightness Tool 吗？\n\n这将删除所有程序文件和设置。",
            [Language.TraditionalChinese] = "確定要卸載 Gamma Brightness Tool 嗎？\n\n這將刪除所有程式檔案和設定。",
            [Language.English] = "Are you sure you want to uninstall Gamma Brightness Tool?\n\nThis will delete all program files and settings.",
            [Language.Japanese] = "Gamma Brightness Tool をアンインストールしますか？\n\nすべてのプログラムファイルと設定が削除されます。",
            [Language.Korean] = "Gamma Brightness Tool을 제거하시겠습니까?\n\n모든 프로그램 파일과 설정이 삭제됩니다.",
            [Language.German] = "Möchten Sie Gamma Brightness Tool wirklich deinstallieren?\n\nAlle Programmdateien und Einstellungen werden gelöscht.",
            [Language.French] = "Voulez-vous vraiment désinstaller Gamma Brightness Tool ?\n\nTous les fichiers et paramètres seront supprimés.",
            [Language.Spanish] = "¿Seguro que desea desinstalar Gamma Brightness Tool?\n\nSe eliminarán todos los archivos y la configuración.",
            [Language.Russian] = "Вы уверены, что хотите удалить Gamma Brightness Tool?\n\nВсе файлы программы и настройки будут удалены.",
            [Language.System] = ""
        }
    };

    private static Language _setting = Language.System;
    private static Language _current = Language.SimplifiedChinese;

    /// <summary>
    /// The user's language choice as stored in settings (may be System).
    /// </summary>
    public static Language Setting
    {
        get => _setting;
        set
        {
            if (_setting != value)
            {
                _setting = value;
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// The resolved, concrete language actually used for strings. Never
    /// Language.System; resolved at startup or when the user picks a language.
    /// </summary>
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

    /// <summary>
    /// Resolves the user's language choice to a concrete language and a flag
    /// indicating whether the choice maps to a supported language.
    /// </summary>
    public static (Language Effective, bool Supported) Resolve(Language setting)
    {
        if (setting != Language.System)
        {
            return (setting, true);
        }

        var sysLang = ResolveSystemLanguage();
        return sysLang.HasValue ? (sysLang.Value, true) : (Language.English, false);
    }

    /// <summary>
    /// Maps the current system UI language (CurrentUICulture) to a supported
    /// Language, or null when unsupported.
    /// </summary>
    public static Language? ResolveSystemLanguage()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // zh-TW / zh-HK / zh-MO -> Traditional Chinese, everything else
            // (zh-CN / zh-SG / plain zh) -> Simplified Chinese.
            return (name.Contains("TW") || name.Contains("HK") || name.Contains("MO"))
                ? Language.TraditionalChinese
                : Language.SimplifiedChinese;
        }
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return Language.Japanese;
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return Language.Korean;
        if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return Language.German;
        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return Language.French;
        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return Language.Spanish;
        if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return Language.Russian;
        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return Language.English;
        return null;
    }

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
