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
        ["TemperatureMode"] = new()
        {
            [Language.SimplifiedChinese] = "色温",
            [Language.TraditionalChinese] = "色溫",
            [Language.English] = "Temp",
            [Language.Japanese] = "色温度",
            [Language.Korean] = "색온도",
            [Language.German] = "Farbtemp.",
            [Language.French] = "Temp. couleur",
            [Language.Spanish] = "Temp. color",
            [Language.Russian] = "Цвет. темп.",
            [Language.System] = ""
        },
        ["BrightnessMode"] = new()
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
        ["TemperatureValue"] = new()
        {
            [Language.SimplifiedChinese] = "色温 {0}K",
            [Language.TraditionalChinese] = "色溫 {0}K",
            [Language.English] = "Temp {0}K",
            [Language.Japanese] = "色温度 {0}K",
            [Language.Korean] = "색온도 {0}K",
            [Language.German] = "Farbtemp. {0}K",
            [Language.French] = "Temp. couleur {0}K",
            [Language.Spanish] = "Temp. color {0}K",
            [Language.Russian] = "Цвет. темп. {0}K",
            [Language.System] = ""
        },
        ["DefaultSuffix"] = new()
        {
            [Language.SimplifiedChinese] = "(默认)",
            [Language.TraditionalChinese] = "(預設)",
            [Language.English] = " (default)",
            [Language.Japanese] = "(既定)",
            [Language.Korean] = "(기본)",
            [Language.German] = " (Standard)",
            [Language.French] = " (défaut)",
            [Language.Spanish] = " (predeterminado)",
            [Language.Russian] = " (по умолчанию)",
            [Language.System] = ""
        },
        ["TrayTooltip"] = new()
        {
            [Language.SimplifiedChinese] = "亮度 {0}% / 色温 {1}K",
            [Language.TraditionalChinese] = "亮度 {0}% / 色溫 {1}K",
            [Language.English] = "Brightness {0}% / Temp {1}K",
            [Language.Japanese] = "明るさ {0}% / 色温度 {1}K",
            [Language.Korean] = "밝기 {0}% / 색온도 {1}K",
            [Language.German] = "Helligkeit {0}% / Farbtemp. {1}K",
            [Language.French] = "Luminosité {0}% / Temp. couleur {1}K",
            [Language.Spanish] = "Brillo {0}% / Temp. color {1}K",
            [Language.Russian] = "Яркость {0}% / Цвет. темп. {1}K",
            [Language.System] = ""
        },
        ["TrayTooltipBrightnessOnly"] = new()
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
            [Language.Korean] = "디스플레이 끄기",
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
            [Language.Korean] = "밝기 레벨",
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
            [Language.Korean] = "시작 시 실행",
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
            [Language.Korean] = "시작 시 실행 설정 실패",
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
            [Language.SimplifiedChinese] = "亮度滚轮步进",
            [Language.TraditionalChinese] = "亮度滾輪步進",
            [Language.English] = "Brightness wheel step",
            [Language.Japanese] = "明るさホイールのステップ",
            [Language.Korean] = "밝기 휠 단계",
            [Language.German] = "Helligkeit Rad-Schritt",
            [Language.French] = "Pas de molette luminosité",
            [Language.Spanish] = "Paso de rueda brillo",
            [Language.Russian] = "Шаг колеса яркости",
            [Language.System] = ""
        },
        ["BrightnessSmooth"] = new()
        {
            [Language.SimplifiedChinese] = "亮度平滑",
            [Language.TraditionalChinese] = "亮度平滑",
            [Language.English] = "Brightness Smoothing",
            [Language.Japanese] = "明るさスムーズ",
            [Language.Korean] = "밝기 부드럽게",
            [Language.German] = "Helligkeit glätten",
            [Language.French] = "Lissage luminosité",
            [Language.Spanish] = "Suavizado de brillo",
            [Language.Russian] = "Плавная яркость",
            [Language.System] = ""
        },
        ["TemperatureSmooth"] = new()
        {
            [Language.SimplifiedChinese] = "色温平滑",
            [Language.TraditionalChinese] = "色溫平滑",
            [Language.English] = "Color Temp Smoothing",
            [Language.Japanese] = "色温度スムーズ",
            [Language.Korean] = "색온도 부드럽게",
            [Language.German] = "Farbtemperatur glätten",
            [Language.French] = "Lissage température",
            [Language.Spanish] = "Suavizado de temperatura",
            [Language.Russian] = "Плавная цвет. темп.",
            [Language.System] = ""
        },
        ["TemperatureStepSize"] = new()
        {
            [Language.SimplifiedChinese] = "色温滚轮步进",
            [Language.TraditionalChinese] = "色溫滾輪步進",
            [Language.English] = "Temperature wheel step",
            [Language.Japanese] = "色温度ホイールのステップ",
            [Language.Korean] = "색온도 휠 단계",
            [Language.German] = "Farbtemperatur Rad-Schritt",
            [Language.French] = "Pas de molette température",
            [Language.Spanish] = "Paso de rueda temperatura",
            [Language.Russian] = "Шаг колеса цветовой температуры",
            [Language.System] = ""
        },
        ["WheelEnabled"] = new()
        {
            [Language.SimplifiedChinese] = "滚轮调节",
            [Language.TraditionalChinese] = "滾輪調節",
            [Language.English] = "Wheel adjust",
            [Language.Japanese] = "ホイール調整",
            [Language.Korean] = "휠 조절",
            [Language.German] = "Rad einstellen",
            [Language.French] = "Molette",
            [Language.Spanish] = "Rueda",
            [Language.Russian] = "Колесо",
            [Language.System] = ""
        },
        ["TemperaturePresets"] = new()
        {
            [Language.SimplifiedChinese] = "色温预设",
            [Language.TraditionalChinese] = "色溫預設",
            [Language.English] = "Temperature presets",
            [Language.Japanese] = "色温度プリセット",
            [Language.Korean] = "색온도 프리셋",
            [Language.German] = "Farbtemperatur-Voreinstellungen",
            [Language.French] = "Préréglages de température",
            [Language.Spanish] = "Ajustes predefinidos de temperatura",
            [Language.Russian] = "Пресеты цветовой температуры",
            [Language.System] = ""
        },
        ["CurrentColorTemp"] = new()
        {
            [Language.SimplifiedChinese] = "当前色温",
            [Language.TraditionalChinese] = "目前色溫",
            [Language.English] = "Current temperature",
            [Language.Japanese] = "現在の色温度",
            [Language.Korean] = "현재 색온도",
            [Language.German] = "Aktuelle Farbtemperatur",
            [Language.French] = "Température actuelle",
            [Language.Spanish] = "Temperatura actual",
            [Language.Russian] = "Текущая цветовая температура",
            [Language.System] = ""
        },
        ["TemperatureRange"] = new()
        {
            [Language.SimplifiedChinese] = "色温范围",
            [Language.TraditionalChinese] = "色溫範圍",
            [Language.English] = "Temperature range",
            [Language.Japanese] = "色温度範囲",
            [Language.Korean] = "색온도 범위",
            [Language.German] = "Farbtemperaturbereich",
            [Language.French] = "Plage de température",
            [Language.Spanish] = "Rango de temperatura",
            [Language.Russian] = "Диапазон цветовой температуры",
            [Language.System] = ""
        },
        ["TemperatureMin"] = new()
        {
            [Language.SimplifiedChinese] = "最低",
            [Language.TraditionalChinese] = "最低",
            [Language.English] = "Min",
            [Language.Japanese] = "最小",
            [Language.Korean] = "최소",
            [Language.German] = "Min.",
            [Language.French] = "Min",
            [Language.Spanish] = "Mín",
            [Language.Russian] = "Мин.",
            [Language.System] = ""
        },
        ["TemperatureMax"] = new()
        {
            [Language.SimplifiedChinese] = "最高",
            [Language.TraditionalChinese] = "最高",
            [Language.English] = "Max",
            [Language.Japanese] = "最大",
            [Language.Korean] = "최대",
            [Language.German] = "Max.",
            [Language.French] = "Max",
            [Language.Spanish] = "Máx",
            [Language.Russian] = "Макс.",
            [Language.System] = ""
        },
        ["ColorTemperatureEnabled"] = new()
        {
            [Language.SimplifiedChinese] = "色温设置",
            [Language.TraditionalChinese] = "色溫設定",
            [Language.English] = "Color temperature",
            [Language.Japanese] = "色温度",
            [Language.Korean] = "색온도 조절",
            [Language.German] = "Farbtemperatur",
            [Language.French] = "Température couleur",
            [Language.Spanish] = "Temperatura color",
            [Language.Russian] = "Цветовая температура",
            [Language.System] = ""
        },
        ["SettingsTopMost"] = new()
        {
            [Language.SimplifiedChinese] = "画面置顶",
            [Language.TraditionalChinese] = "畫面置頂",
            [Language.English] = "Always on top",
            [Language.Japanese] = "最前面に表示",
            [Language.Korean] = "항상 위에",
            [Language.German] = "Immer im Vordergrund",
            [Language.French] = "Toujours au premier plan",
            [Language.Spanish] = "Siempre al frente",
            [Language.Russian] = "Поверх всех окон",
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
            [Language.Korean] = "OSD 오버레이 표시",
            [Language.German] = "OSD anzeigen",
            [Language.French] = "Afficher l'OSD",
            [Language.Spanish] = "Mostrar OSD",
            [Language.Russian] = "Показывать OSD",
            [Language.System] = ""
        },
        ["ExportSettings"] = new()
        {
            [Language.SimplifiedChinese] = "导出设置",
            [Language.TraditionalChinese] = "匯出設定",
            [Language.English] = "Export settings",
            [Language.Japanese] = "設定をエクスポート",
            [Language.Korean] = "설정 내보내기",
            [Language.German] = "Einstellungen exportieren",
            [Language.French] = "Exporter les réglages",
            [Language.Spanish] = "Exportar ajustes",
            [Language.Russian] = "Экспорт настроек",
            [Language.System] = ""
        },
        ["ImportSettings"] = new()
        {
            [Language.SimplifiedChinese] = "导入设置",
            [Language.TraditionalChinese] = "匯入設定",
            [Language.English] = "Import settings",
            [Language.Japanese] = "設定をインポート",
            [Language.Korean] = "설정 가져오기",
            [Language.German] = "Einstellungen importieren",
            [Language.French] = "Importer les réglages",
            [Language.Spanish] = "Importar ajustes",
            [Language.Russian] = "Импорт настроек",
            [Language.System] = ""
        },
        ["ExportConfirm"] = new()
        {
            [Language.SimplifiedChinese] = "确定要导出当前设置吗？",
            [Language.TraditionalChinese] = "確定要匯出目前設定嗎？",
            [Language.English] = "Export current settings?",
            [Language.Japanese] = "現在の設定をエクスポートしますか？",
            [Language.Korean] = "현재 설정을 내보내시겠습니까?",
            [Language.German] = "Aktuelle Einstellungen exportieren?",
            [Language.French] = "Exporter les réglages actuels ?",
            [Language.Spanish] = "¿Exportar los ajustes actuales?",
            [Language.Russian] = "Экспортировать текущие настройки?",
            [Language.System] = ""
        },
        ["ExportDone"] = new()
        {
            [Language.SimplifiedChinese] = "设置已导出",
            [Language.TraditionalChinese] = "設定已匯出",
            [Language.English] = "Settings exported",
            [Language.Japanese] = "設定をエクスポートしました",
            [Language.Korean] = "설정을 내보냈습니다",
            [Language.German] = "Einstellungen exportiert",
            [Language.French] = "Réglages exportés",
            [Language.Spanish] = "Ajustes exportados",
            [Language.Russian] = "Настройки экспортированы",
            [Language.System] = ""
        },
        ["ImportConfirm"] = new()
        {
            [Language.SimplifiedChinese] = "导入设置将覆盖当前设置，确定继续吗？",
            [Language.TraditionalChinese] = "匯入設定將覆蓋目前設定，確定繼續嗎？",
            [Language.English] = "Importing will overwrite current settings. Continue?",
            [Language.Japanese] = "インポートすると現在の設定が上書きされます。続行しますか？",
            [Language.Korean] = "가져오기를 하면 현재 설정이 덮어써집니다. 계속하시겠습니까?",
            [Language.German] = "Beim Import werden die aktuellen Einstellungen überschrieben. Fortfahren?",
            [Language.French] = "L'importation remplacera les réglages actuels. Continuer ?",
            [Language.Spanish] = "La importación sobrescribirá los ajustes actuales. ¿Continuar?",
            [Language.Russian] = "Импорт перезапишет текущие настройки. Продолжить?",
            [Language.System] = ""
        },
        ["ImportDone"] = new()
        {
            [Language.SimplifiedChinese] = "设置已导入并生效",
            [Language.TraditionalChinese] = "設定已匯入並生效",
            [Language.English] = "Settings imported and applied",
            [Language.Japanese] = "設定をインポートして適用しました",
            [Language.Korean] = "설정을 가져와 적용했습니다",
            [Language.German] = "Einstellungen importiert und angewendet",
            [Language.French] = "Réglages importés et appliqués",
            [Language.Spanish] = "Ajustes importados y aplicados",
            [Language.Russian] = "Настройки импортированы и применены",
            [Language.System] = ""
        },
        ["ImportInvalid"] = new()
        {
            [Language.SimplifiedChinese] = "导入失败：文件不是有效的设置文件",
            [Language.TraditionalChinese] = "匯入失敗：檔案不是有效的設定檔案",
            [Language.English] = "Import failed: not a valid settings file",
            [Language.Japanese] = "インポート失敗：有効な設定ファイルではありません",
            [Language.Korean] = "가져오기 실패: 유효한 설정 파일이 아닙니다",
            [Language.German] = "Import fehlgeschlagen: keine gültige Einstellungsdatei",
            [Language.French] = "Échec de l'importation : fichier de réglages non valide",
            [Language.Spanish] = "Error de importación: no es un archivo de ajustes válido",
            [Language.Russian] = "Ошибка импорта: недопустимый файл настроек",
            [Language.System] = ""
        },
        ["ImportExportSettings"] = new()
        {
            [Language.SimplifiedChinese] = "导入/导出设置",
            [Language.TraditionalChinese] = "匯入/匯出設定",
            [Language.English] = "Import/export settings",
            [Language.Japanese] = "設定のインポート/エクスポート",
            [Language.Korean] = "설정 가져오기/내보내기",
            [Language.German] = "Einstellungen importieren/exportieren",
            [Language.French] = "Importer/exporter les réglages",
            [Language.Spanish] = "Importar/exportar ajustes",
            [Language.Russian] = "Импорт/экспорт настроек",
            [Language.System] = ""
        },
        ["GammaSelfHeal"] = new()
        {
            [Language.SimplifiedChinese] = "Gamma 自愈",
            [Language.TraditionalChinese] = "Gamma 自癒",
            [Language.English] = "Gamma self-heal",
            [Language.Japanese] = "ガンマ自己修復",
            [Language.Korean] = "감마 자가 복구",
            [Language.German] = "Gamma-Selbstheilung",
            [Language.French] = "Auto-réparation gamma",
            [Language.Spanish] = "Autocuración gamma",
            [Language.Russian] = "Самовосстановление гаммы",
            [Language.System] = ""
        },
        ["GammaSelfHealHint"] = new()
        {
            [Language.SimplifiedChinese] = "睡眠唤醒/显示器热插拔后自动恢复 gamma",
            [Language.TraditionalChinese] = "睡眠喚醒/顯示器熱插拔後自動恢復 gamma",
            [Language.English] = "Restore gamma after sleep/resume and monitor changes",
            [Language.Japanese] = "スリープ復帰・モニター変更後にガンマを自動復元",
            [Language.Korean] = "절전 모드 해제/모니터 변경 후 감마 자동 복원",
            [Language.German] = "Gamma nach Ruhezustand/Monitorwechsel wiederherstellen",
            [Language.French] = "Restaurer le gamma après veille/changement d'écran",
            [Language.Spanish] = "Restaurar gamma tras suspensión/cambio de monitor",
            [Language.Russian] = "Восстанавливать гамму после сна/смены монитора",
            [Language.System] = ""
        },
        ["PauseInFullscreen"] = new()
        {
            [Language.SimplifiedChinese] = "全屏自动暂停",
            [Language.TraditionalChinese] = "全螢幕自動暫停",
            [Language.English] = "Pause in fullscreen",
            [Language.Japanese] = "全画面で一時停止",
            [Language.Korean] = "전체 화면에서 일시 중지",
            [Language.German] = "Im Vollbild pausieren",
            [Language.French] = "Pause en plein écran",
            [Language.Spanish] = "Pausar en pantalla completa",
            [Language.Russian] = "Пауза в полноэкранном режиме",
            [Language.System] = ""
        },
        ["PauseInFullscreenHint"] = new()
        {
            [Language.SimplifiedChinese] = "全屏应用（游戏/视频）时暂停 gamma，退出后恢复",
            [Language.TraditionalChinese] = "全螢幕應用（遊戲/影片）時暫停 gamma，退出後恢復",
            [Language.English] = "Pause gamma in fullscreen apps (games/video), restore on exit",
            [Language.Japanese] = "全画面アプリ（ゲーム/動画）でガンマを一時停止、終了で復元",
            [Language.Korean] = "전체 화면 앱(게임/영상)에서 감마 일시 중지, 종료 시 복원",
            [Language.German] = "Gamma in Vollbild-Apps pausieren (Spiele/Video), danach wiederherstellen",
            [Language.French] = "Pause gamma en plein écran (jeux/vidéo), restauration à la sortie",
            [Language.Spanish] = "Pausar gamma en apps a pantalla completa (juegos/vídeo), restaurar al salir",
            [Language.Russian] = "Пауза гаммы в полноэкранных приложениях (игры/видео), восстановление при выходе",
            [Language.System] = ""
        },
        ["StepSize5"] = new()
        {
            [Language.SimplifiedChinese] = "5%（默认）",
            [Language.TraditionalChinese] = "5%（預設）",
            [Language.English] = "5% (default)",
            [Language.Japanese] = "5%（既定）",
            [Language.Korean] = "5%(기본)",
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
            [Language.Korean] = "중국어 간체",
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
            [Language.Korean] = "중국어 번체",
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
            [Language.Korean] = "English",
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
            [Language.Korean] = "日本語",
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
            [Language.Korean] = "Deutsch",
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
            [Language.Korean] = "Français",
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
            [Language.Korean] = "Español",
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
            [Language.Korean] = "Русский",
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
            [Language.Korean] = "시스템",
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
            [Language.Korean] = "시스템 언어가 지원되지 않습니다. 영어로 표시됩니다.",
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
            [Language.Korean] = "일반",
            [Language.German] = "Allgemein",
            [Language.French] = "Général",
            [Language.Spanish] = "General",
            [Language.Russian] = "Общие",
            [Language.System] = ""
        },
        ["SettingsHotkeys"] = new()
        {
            [Language.SimplifiedChinese] = "快捷键",
            [Language.TraditionalChinese] = "快捷鍵",
            [Language.English] = "Hotkey",
            [Language.Japanese] = "ホットキー",
            [Language.Korean] = "단축키",
            [Language.German] = "Tastenkürzel",
            [Language.French] = "Raccourci",
            [Language.Spanish] = "Atajo",
            [Language.Russian] = "Горячая клавиша",
            [Language.System] = ""
        },
        ["SettingsAbout"] = new()
        {
            [Language.SimplifiedChinese] = "版本信息",
            [Language.TraditionalChinese] = "版本資訊",
            [Language.English] = "About",
            [Language.Japanese] = "バージョン情報",
            [Language.Korean] = "정보",
            [Language.German] = "Über",
            [Language.French] = "À propos",
            [Language.Spanish] = "Acerca de",
            [Language.Russian] = "О программе",
            [Language.System] = ""
        },
        ["SettingsColorTemp"] = new()
        {
            [Language.SimplifiedChinese] = "色温设置",
            [Language.TraditionalChinese] = "色溫設定",
            [Language.English] = "Color temp",
            [Language.Japanese] = "色温度",
            [Language.Korean] = "색온도 설정",
            [Language.German] = "Farbtemperatur",
            [Language.French] = "Température couleur",
            [Language.Spanish] = "Temperatura color",
            [Language.Russian] = "Цветовая температура",
            [Language.System] = ""
        },
        ["SettingsBrightness"] = new()
        {
            [Language.SimplifiedChinese] = "亮度设置",
            [Language.TraditionalChinese] = "亮度設定",
            [Language.English] = "Brightness",
            [Language.Japanese] = "明るさ設定",
            [Language.Korean] = "밝기 설정",
            [Language.German] = "Helligkeit",
            [Language.French] = "Luminosité",
            [Language.Spanish] = "Brillo",
            [Language.Russian] = "Яркость",
            [Language.System] = ""
        },

        ["SettingsPlaceholder"] = new()
        {
            [Language.SimplifiedChinese] = "此功能开发中，敬请期待",
            [Language.TraditionalChinese] = "此功能開發中，敬請期待",
            [Language.English] = "Coming soon",
            [Language.Japanese] = "この機能は開発中です",
            [Language.Korean] = "선택하여 변경하세요…",
            [Language.German] = "In Kürze verfügbar",
            [Language.French] = "Bientôt disponible",
            [Language.Spanish] = "Próximamente",
            [Language.Russian] = "Скоро появится",
            [Language.System] = ""
        },
        ["HotkeyIncreaseBrightness"] = new()
        {
            [Language.SimplifiedChinese] = "增加亮度",
            [Language.TraditionalChinese] = "增加亮度",
            [Language.English] = "Increase Brightness",
            [Language.Japanese] = "明るさを上げる",
            [Language.Korean] = "밝기 증가",
            [Language.German] = "Helligkeit erhöhen",
            [Language.French] = "Augmenter la luminosité",
            [Language.Spanish] = "Aumentar brillo",
            [Language.Russian] = "Увеличить яркость",
            [Language.System] = ""
        },
        ["HotkeyDecreaseBrightness"] = new()
        {
            [Language.SimplifiedChinese] = "降低亮度",
            [Language.TraditionalChinese] = "降低亮度",
            [Language.English] = "Decrease Brightness",
            [Language.Japanese] = "明るさを下げる",
            [Language.Korean] = "밝기 감소",
            [Language.German] = "Helligkeit verringern",
            [Language.French] = "Diminuer la luminosité",
            [Language.Spanish] = "Disminuir brillo",
            [Language.Russian] = "Уменьшить яркость",
            [Language.System] = ""
        },
        ["HotkeyPowerOff"] = new()
        {
            [Language.SimplifiedChinese] = "熄屏",
            [Language.TraditionalChinese] = "熄屏",
            [Language.English] = "Turn Off Display",
            [Language.Japanese] = "画面をオフ",
            [Language.Korean] = "화면 끄기",
            [Language.German] = "Bildschirm ausschalten",
            [Language.French] = "Éteindre l'écran",
            [Language.Spanish] = "Apagar pantalla",
            [Language.Russian] = "Выключить экран",
            [Language.System] = ""
        },
        ["HotkeyIncreaseTemperature"] = new()
        {
            [Language.SimplifiedChinese] = "增加色温",
            [Language.TraditionalChinese] = "增加色溫",
            [Language.English] = "Increase Color Temp",
            [Language.Japanese] = "色温度を上げる",
            [Language.Korean] = "색온도 올리기",
            [Language.German] = "Farbtemperatur erhöhen",
            [Language.French] = "Augmenter la température",
            [Language.Spanish] = "Aumentar temperatura de color",
            [Language.Russian] = "Увеличить цвет. температуру",
            [Language.System] = ""
        },
        ["HotkeyDecreaseTemperature"] = new()
        {
            [Language.SimplifiedChinese] = "降低色温",
            [Language.TraditionalChinese] = "降低色溫",
            [Language.English] = "Decrease Color Temp",
            [Language.Japanese] = "色温度を下げる",
            [Language.Korean] = "색온도 내리기",
            [Language.German] = "Farbtemperatur verringern",
            [Language.French] = "Diminuer la température",
            [Language.Spanish] = "Disminuir temperatura de color",
            [Language.Russian] = "Уменьшить цвет. температуру",
            [Language.System] = ""
        },
        ["HotkeyInputPlaceholder"] = new()
        {
            [Language.SimplifiedChinese] = "点击开始录制…",
            [Language.TraditionalChinese] = "點擊開始錄製…",
            [Language.English] = "Click to record…",
            [Language.Japanese] = "クリックして録音…",
            [Language.Korean] = "단축키 입력…",
            [Language.German] = "Klicken zum Aufnehmen…",
            [Language.French] = "Cliquez pour enregistrer…",
            [Language.Spanish] = "Haz clic para grabar…",
            [Language.Russian] = "Нажмите, чтобы записать…",
            [Language.System] = ""
        },
        ["HotkeyConfirm"] = new()
        {
            [Language.SimplifiedChinese] = "确认",
            [Language.TraditionalChinese] = "確認",
            [Language.English] = "OK",
            [Language.Japanese] = "確認",
            [Language.Korean] = "확인",
            [Language.German] = "OK",
            [Language.French] = "OK",
            [Language.Spanish] = "OK",
            [Language.Russian] = "ОК",
            [Language.System] = ""
        },
        ["HotkeyCancel"] = new()
        {
            [Language.SimplifiedChinese] = "取消",
            [Language.TraditionalChinese] = "取消",
            [Language.English] = "Cancel",
            [Language.Japanese] = "キャンセル",
            [Language.Korean] = "취소",
            [Language.German] = "Abbrechen",
            [Language.French] = "Annuler",
            [Language.Spanish] = "Cancelar",
            [Language.Russian] = "Отмена",
            [Language.System] = ""
        },
        ["HotkeyConflict"] = new()
        {
            [Language.SimplifiedChinese] = "此快捷键已被占用，请换一个",
            [Language.TraditionalChinese] = "此快捷鍵已被佔用，請換一個",
            [Language.English] = "This hotkey is already in use",
            [Language.Japanese] = "このホットキーは既に使用されています",
            [Language.Korean] = "이 단축키는 이미 사용 중입니다. 다른 단축키를 입력하세요.",
            [Language.German] = "Diese Tastenkombination ist bereits belegt",
            [Language.French] = "Ce raccourci est déjà utilisé",
            [Language.Spanish] = "Este atajo ya está en uso",
            [Language.Russian] = "Эта комбинация уже используется",
            [Language.System] = ""
        },
        ["SettingsRestartApp"] = new()
        {
            [Language.SimplifiedChinese] = "重启软件",
            [Language.TraditionalChinese] = "重啟軟體",
            [Language.English] = "Restart App",
            [Language.Japanese] = "アプリを再起動",
            [Language.Korean] = "설정 저장 후 앱을 다시 시작해야 합니다.",
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
            [Language.Korean] = "켜기",
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
            [Language.Korean] = "끄기",
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
            [Language.SimplifiedChinese] = "Github",
            [Language.TraditionalChinese] = "Github",
            [Language.English] = "Github",
            [Language.Japanese] = "Github",
            [Language.Korean] = "Github에서 업데이트 확인",
            [Language.German] = "Github",
            [Language.French] = "Github",
            [Language.Spanish] = "Github",
            [Language.Russian] = "Github",
            [Language.System] = "Github"
        },
        ["Gitee"] = new()
        {
            [Language.SimplifiedChinese] = "Gitee",
            [Language.TraditionalChinese] = "Gitee",
            [Language.English] = "Gitee",
            [Language.Japanese] = "Gitee",
            [Language.Korean] = "Gitee",
            [Language.German] = "Gitee",
            [Language.French] = "Gitee",
            [Language.Spanish] = "Gitee",
            [Language.Russian] = "Gitee",
            [Language.System] = "Gitee"
        },
        ["ResetSettings"] = new()
        {
            [Language.SimplifiedChinese] = "重置设置",
            [Language.TraditionalChinese] = "重設設定",
            [Language.English] = "Reset settings",
            [Language.Japanese] = "設定をリセット",
            [Language.Korean] = "설정 초기화",
            [Language.German] = "Einstellungen zurücksetzen",
            [Language.French] = "Réinitialiser les réglages",
            [Language.Spanish] = "Restablecer ajustes",
            [Language.Russian] = "Сбросить настройки",
            [Language.System] = ""
        },
        ["ClearAllHotkeys"] = new()
        {
            [Language.SimplifiedChinese] = "一键清除",
            [Language.TraditionalChinese] = "一鍵清除",
            [Language.English] = "Clear all",
            [Language.Japanese] = "すべて解除",
            [Language.Korean] = "모든 단축키 해제",
            [Language.German] = "Alle löschen",
            [Language.French] = "Tout effacer",
            [Language.Spanish] = "Borrar todo",
            [Language.Russian] = "Очистить всё",
            [Language.System] = ""
        },
        ["ClearAllHotkeysConfirm"] = new()
        {
            [Language.SimplifiedChinese] = "确定要清除所有快捷键吗？",
            [Language.TraditionalChinese] = "確定要清除所有快捷鍵嗎？",
            [Language.English] = "Clear all hotkeys?",
            [Language.Japanese] = "すべてのショートカットを解除しますか？",
            [Language.Korean] = "모든 단축키를 해제하시겠습니까?",
            [Language.German] = "Alle Tastenkürzel löschen?",
            [Language.French] = "Effacer tous les raccourcis ?",
            [Language.Spanish] = "¿Borrar todos los atajos?",
            [Language.Russian] = "Очистить все горячие клавиши?",
            [Language.System] = ""
        },
        ["ClearAllHotkeysDone"] = new()
        {
            [Language.SimplifiedChinese] = "所有快捷键已清除",
            [Language.TraditionalChinese] = "所有快捷鍵已清除",
            [Language.English] = "All hotkeys cleared",
            [Language.Japanese] = "すべてのショートカットを解除しました",
            [Language.Korean] = "모든 단축키가 해제되었습니다.",
            [Language.German] = "Alle Tastenkürzel gelöscht",
            [Language.French] = "Tous les raccourcis effacés",
            [Language.Spanish] = "Todos los atajos borrados",
            [Language.Russian] = "Все горячие клавиши очищены",
            [Language.System] = ""
        },
        ["AllHotKeysEnabled"] = new()
        {
            [Language.SimplifiedChinese] = "启用所有快捷键",
            [Language.TraditionalChinese] = "啟用所有快捷鍵",
            [Language.English] = "Enable all hotkeys",
            [Language.Japanese] = "すべてのショートカットを有効化",
            [Language.Korean] = "모든 단축키 활성화",
            [Language.German] = "Alle Tastenkürzel aktivieren",
            [Language.French] = "Activer tous les raccourcis",
            [Language.Spanish] = "Activar todos los atajos",
            [Language.Russian] = "Включить все горячие клавиши",
            [Language.System] = ""
        },
        ["ResetConfirm"] = new()
        {
            [Language.SimplifiedChinese] = "确定要恢复所有默认设置吗？",
            [Language.TraditionalChinese] = "確定要恢復所有預設設定嗎？",
            [Language.English] = "Reset all settings to defaults?",
            [Language.Japanese] = "すべての設定を初期化しますか？",
            [Language.Korean] = "모든 설정을 초기화하시겠습니까? 이 작업은 취소할 수 없습니다.",
            [Language.German] = "Alle Einstellungen zurücksetzen?",
            [Language.French] = "Réinitialiser tous les réglages ?",
            [Language.Spanish] = "¿Restablecer todos los ajustes?",
            [Language.Russian] = "Сбросить все настройки?",
            [Language.System] = ""
        },
        ["ResetDone"] = new()
        {
            [Language.SimplifiedChinese] = "设置已重置为默认值",
            [Language.TraditionalChinese] = "設定已重設為預設值",
            [Language.English] = "Settings reset to defaults",
            [Language.Japanese] = "設定を初期化しました",
            [Language.Korean] = "설정이 초기화되었습니다. 앱을 다시 시작합니다.",
            [Language.German] = "Einstellungen wurden zurückgesetzt",
            [Language.French] = "Réglages réinitialisés",
            [Language.Spanish] = "Ajustes restablecidos",
            [Language.Russian] = "Настройки сброшены",
            [Language.System] = ""
        },
        ["AboutDescription"] = new()
        {
            [Language.SimplifiedChinese] = "通过 Gamma Ramp 调节屏幕亮度的托盘工具",
            [Language.TraditionalChinese] = "透過 Gamma Ramp 調整螢幕亮度的托盤工具",
            [Language.English] = "Tray tool that adjusts screen brightness via Gamma Ramp",
            [Language.Japanese] = "Gamma Ramp で画面の明るさを調整するトレイツール",
            [Language.Korean] = "Windows용 밝기 및 색온도 조절 도구. 드라이버 수준에서 Gamma Ramp를 조정합니다.",
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
            [Language.Korean] = "Gamma Brightness Tool을(를) 제거하시겠습니까?\n\n저장된 설정과 앱 데이터가 삭제됩니다.",


            [Language.German] = "Möchten Sie Gamma Brightness Tool wirklich deinstallieren?\n\nAlle Programmdateien und Einstellungen werden gelöscht.",
            [Language.French] = "Voulez-vous vraiment désinstaller Gamma Brightness Tool ?\n\nTous les fichiers et paramètres seront supprimés.",
            [Language.Spanish] = "¿Seguro que desea desinstalar Gamma Brightness Tool?\n\nSe eliminarán todos los archivos y la configuración.",
            [Language.Russian] = "Вы уверены, что хотите удалить Gamma Brightness Tool?\n\nВсе файлы программы и настройки будут удалены.",
            [Language.System] = ""
        },
        ["SolarAdjust"] = new()
        {
            [Language.SimplifiedChinese] = "时间调整",
            [Language.TraditionalChinese] = "時間調整",
            [Language.English] = "Time Adjustment",
            [Language.Japanese] = "時間調整",
            [Language.Korean] = "시간 조정",
            [Language.German] = "Zeitanpassung",
            [Language.French] = "Ajustement horaire",
            [Language.Spanish] = "Ajuste de tiempo",
            [Language.Russian] = "Настройка времени",
            [Language.System] = ""
        },
        ["DisableMenu"] = new()
        {
            [Language.SimplifiedChinese] = "功能停用",
            [Language.TraditionalChinese] = "功能停用",
            [Language.English] = "Disable",
            [Language.Japanese] = "無効化",
            [Language.Korean] = "비활성화",
            [Language.German] = "Deaktivieren",
            [Language.French] = "Désactiver",
            [Language.Spanish] = "Desactivar",
            [Language.Russian] = "Отключить",
            [Language.System] = ""
        },
        ["DisableOff"] = new()
        {
            [Language.SimplifiedChinese] = "关闭",
            [Language.TraditionalChinese] = "關閉",
            [Language.English] = "Off",
            [Language.Japanese] = "オフ",
            [Language.Korean] = "끄기",
            [Language.German] = "Aus",
            [Language.French] = "Éteindre",
            [Language.Spanish] = "Apagar",
            [Language.Russian] = "Выключить",
            [Language.System] = ""
        },
        ["DisablePermanent"] = new()
        {
            [Language.SimplifiedChinese] = "永久",
            [Language.TraditionalChinese] = "永久",
            [Language.English] = "Permanent",
            [Language.Japanese] = "永久",
            [Language.Korean] = "영구",
            [Language.German] = "Dauerhaft",
            [Language.French] = "Définitivement",
            [Language.Spanish] = "Permanente",
            [Language.Russian] = "Навсегда",
            [Language.System] = ""
        },
        ["Disable1Min"] = new()
        {
            [Language.SimplifiedChinese] = "1 分钟",
            [Language.TraditionalChinese] = "1 分鐘",
            [Language.English] = "1 minute",
            [Language.Japanese] = "1 分",
            [Language.Korean] = "1분",
            [Language.German] = "1 Minute",
            [Language.French] = "1 minute",
            [Language.Spanish] = "1 minuto",
            [Language.Russian] = "1 минута",
            [Language.System] = ""
        },
        ["Disable5Min"] = new()
        {
            [Language.SimplifiedChinese] = "5 分钟",
            [Language.TraditionalChinese] = "5 分鐘",
            [Language.English] = "5 minutes",
            [Language.Japanese] = "5 分",
            [Language.Korean] = "5분",
            [Language.German] = "5 Minuten",
            [Language.French] = "5 minutes",
            [Language.Spanish] = "5 minutos",
            [Language.Russian] = "5 минут",
            [Language.System] = ""
        },
        ["Disable15Min"] = new()
        {
            [Language.SimplifiedChinese] = "15 分钟",
            [Language.TraditionalChinese] = "15 分鐘",
            [Language.English] = "15 minutes",
            [Language.Japanese] = "15 分",
            [Language.Korean] = "15분",
            [Language.German] = "15 Minuten",
            [Language.French] = "15 minutes",
            [Language.Spanish] = "15 minutos",
            [Language.Russian] = "15 минут",
            [Language.System] = ""
        },
        ["Disable30Min"] = new()
        {
            [Language.SimplifiedChinese] = "30 分钟",
            [Language.TraditionalChinese] = "30 分鐘",
            [Language.English] = "30 minutes",
            [Language.Japanese] = "30 分",
            [Language.Korean] = "30분",
            [Language.German] = "30 Minuten",
            [Language.French] = "30 minutes",
            [Language.Spanish] = "30 minutos",
            [Language.Russian] = "30 минут",
            [Language.System] = ""
        },
        ["Disable1Hour"] = new()
        {
            [Language.SimplifiedChinese] = "1 小时",
            [Language.TraditionalChinese] = "1 小時",
            [Language.English] = "1 hour",
            [Language.Japanese] = "1 時間",
            [Language.Korean] = "1시간",
            [Language.German] = "1 Stunde",
            [Language.French] = "1 heure",
            [Language.Spanish] = "1 hora",
            [Language.Russian] = "1 час",
            [Language.System] = ""
        },
        ["Disable3Hours"] = new()
        {
            [Language.SimplifiedChinese] = "3 小时",
            [Language.TraditionalChinese] = "3 小時",
            [Language.English] = "3 hours",
            [Language.Japanese] = "3 時間",
            [Language.Korean] = "3시간",
            [Language.German] = "3 Stunden",
            [Language.French] = "3 heures",
            [Language.Spanish] = "3 horas",
            [Language.Russian] = "3 часа",
            [Language.System] = ""
        },
        ["Disable5Hours"] = new()
        {
            [Language.SimplifiedChinese] = "5 小时",
            [Language.TraditionalChinese] = "5 小時",
            [Language.English] = "5 hours",
            [Language.Japanese] = "5 時間",
            [Language.Korean] = "5시간",
            [Language.German] = "5 Stunden",
            [Language.French] = "5 heures",
            [Language.Spanish] = "5 horas",
            [Language.Russian] = "5 часов",
            [Language.System] = ""
        },
        ["Disable12Hours"] = new()
        {
            [Language.SimplifiedChinese] = "12 小时",
            [Language.TraditionalChinese] = "12 小時",
            [Language.English] = "12 hours",
            [Language.Japanese] = "12 時間",
            [Language.Korean] = "12시간",
            [Language.German] = "12 Stunden",
            [Language.French] = "12 heures",
            [Language.Spanish] = "12 horas",
            [Language.Russian] = "12 часов",
            [Language.System] = ""
        },
        ["Disable1Day"] = new()
        {
            [Language.SimplifiedChinese] = "一天",
            [Language.TraditionalChinese] = "一天",
            [Language.English] = "1 day",
            [Language.Japanese] = "1 日",
            [Language.Korean] = "하루",
            [Language.German] = "1 Tag",
            [Language.French] = "1 jour",
            [Language.Spanish] = "1 día",
            [Language.Russian] = "1 день",
            [Language.System] = ""
        },
        ["DisableUntilSunset"] = new()
        {
            [Language.SimplifiedChinese] = "到日落",
            [Language.TraditionalChinese] = "到日落",
            [Language.English] = "Until sunset",
            [Language.Japanese] = "日没まで",
            [Language.Korean] = "일몰까지",
            [Language.German] = "Bis Sonnenuntergang",
            [Language.French] = "Jusqu'au coucher du soleil",
            [Language.Spanish] = "Hasta el atardecer",
            [Language.Russian] = "До заката",
            [Language.System] = ""
        },
        ["DisableUntilSunrise"] = new()
        {
            [Language.SimplifiedChinese] = "到日出",
            [Language.TraditionalChinese] = "到日出",
            [Language.English] = "Until sunrise",
            [Language.Japanese] = "日の出まで",
            [Language.Korean] = "일출까지",
            [Language.German] = "Bis Sonnenaufgang",
            [Language.French] = "Jusqu'au lever du soleil",
            [Language.Spanish] = "Hasta el amanecer",
            [Language.Russian] = "До восхода",
            [Language.System] = ""
        },
        ["DisableActiveStatus"] = new()
        {
            [Language.SimplifiedChinese] = "已停用 {0}",
            [Language.TraditionalChinese] = "已停用 {0}",
            [Language.English] = "Disabled {0}",
            [Language.Japanese] = "無効化 {0}",
            [Language.Korean] = "비활성화 {0}",
            [Language.German] = "Deaktiviert {0}",
            [Language.French] = "Désactivé {0}",
            [Language.Spanish] = "Desactivado {0}",
            [Language.Russian] = "Отключено {0}",
            [Language.System] = ""
        },
        ["SolarAdjustEnabled"] = new()
        {
            [Language.SimplifiedChinese] = "按日出日落自动调节",
            [Language.TraditionalChinese] = "按日出日落自動調節",
            [Language.English] = "Adjust by sunrise/sunset",
            [Language.Japanese] = "日の出・日の入りで自動調整",
            [Language.Korean] = "일출·일몰에 맞춰 자동 조절",
            [Language.German] = "Nach Sonnenauf-/untergang anpassen",
            [Language.French] = "Ajuster selon le lever/coucher du soleil",
            [Language.Spanish] = "Ajustar según amanecer/atardecer",
            [Language.Russian] = "Настройка по восходу/закату",
            [Language.System] = ""
        },
        ["SolarMode"] = new()
        {
            [Language.SimplifiedChinese] = "模式",
            [Language.TraditionalChinese] = "模式",
            [Language.English] = "Mode",
            [Language.Japanese] = "モード",
            [Language.Korean] = "모드",
            [Language.German] = "Modus",
            [Language.French] = "Mode",
            [Language.Spanish] = "Modo",
            [Language.Russian] = "Режим",
            [Language.System] = ""
        },
        ["SolarModeManual"] = new()
        {
            [Language.SimplifiedChinese] = "手动设置时间",
            [Language.TraditionalChinese] = "手動設定時間",
            [Language.English] = "Manual times",
            [Language.Japanese] = "手動で時刻設定",
            [Language.Korean] = "수동 시간 설정",
            [Language.German] = "Manuelle Zeiten",
            [Language.French] = "Heures manuelles",
            [Language.Spanish] = "Horas manuales",
            [Language.Russian] = "Вручную",
            [Language.System] = ""
        },
        ["SolarModeLocation"] = new()
        {
            [Language.SimplifiedChinese] = "获取物理位置",
            [Language.TraditionalChinese] = "獲取物理位置",
            [Language.English] = "Physical location",
            [Language.Japanese] = "位置を取得",
            [Language.Korean] = "실제 위치 가져오기",
            [Language.German] = "Physischer Standort",
            [Language.French] = "Position physique",
            [Language.Spanish] = "Ubicación física",
            [Language.Russian] = "Геолокация",
            [Language.System] = ""
        },
        ["SolarManualSunrise"] = new()
        {
            [Language.SimplifiedChinese] = "日出时间",
            [Language.TraditionalChinese] = "日出時間",
            [Language.English] = "Sunrise",
            [Language.Japanese] = "日の出",
            [Language.Korean] = "일출",
            [Language.German] = "Sonnenaufgang",
            [Language.French] = "Lever du soleil",
            [Language.Spanish] = "Amanecer",
            [Language.Russian] = "Восход",
            [Language.System] = ""
        },
        ["SolarManualSunset"] = new()
        {
            [Language.SimplifiedChinese] = "日落时间",
            [Language.TraditionalChinese] = "日落時間",
            [Language.English] = "Sunset",
            [Language.Japanese] = "日の入り",
            [Language.Korean] = "일몰",
            [Language.German] = "Sonnenuntergang",
            [Language.French] = "Coucher du soleil",
            [Language.Spanish] = "Atardecer",
            [Language.Russian] = "Закат",
            [Language.System] = ""
        },
        ["SolarGetLocation"] = new()
        {
            [Language.SimplifiedChinese] = "获取位置",
            [Language.TraditionalChinese] = "獲取位置",
            [Language.English] = "Get location",
            [Language.Japanese] = "位置を取得",
            [Language.Korean] = "위치 가져오기",
            [Language.German] = "Standort abrufen",
            [Language.French] = "Obtenir la position",
            [Language.Spanish] = "Obtener ubicación",
            [Language.Russian] = "Получить позицию",
            [Language.System] = ""
        },
        ["SolarLocationHint"] = new()
        {
            [Language.SimplifiedChinese] = "点击后通过 IP 自动获取经纬度",
            [Language.TraditionalChinese] = "點擊後通過 IP 自動獲取經緯度",
            [Language.English] = "Click to auto-detect coordinates via IP",
            [Language.Japanese] = "クリックでIPから座標を自動取得",
            [Language.Korean] = "클릭 시 IP로 좌표 자동 감지",
            [Language.German] = "Klicken, um Koordinaten per IP zu ermitteln",
            [Language.French] = "Cliquez pour détecter les coordonnées par IP",
            [Language.Spanish] = "Haz clic para detectar coordenadas por IP",
            [Language.Russian] = "Нажмите для автоопределения координат по IP",
            [Language.System] = ""
        },
        ["SolarLocationFailed"] = new()
        {
            [Language.SimplifiedChinese] = "获取位置失败，请检查网络连接后重试。",
            [Language.TraditionalChinese] = "獲取位置失敗，請檢查網路連線後重試。",
            [Language.English] = "Failed to get location. Please check your network and try again.",
            [Language.Japanese] = "位置の取得に失敗しました。ネットワークを確認して再試行してください。",
            [Language.Korean] = "위치를 가져오지 못했습니다. 네트워크를 확인하고 다시 시도하세요.",
            [Language.German] = "Standort konnte nicht abgerufen werden. Bitte Netzwerk prüfen und erneut versuchen.",
            [Language.French] = "Échec de l'obtention de la position. Vérifiez le réseau et réessayez.",
            [Language.Spanish] = "No se pudo obtener la ubicación. Compruebe la red e inténtelo de nuevo.",
            [Language.Russian] = "Не удалось получить местоположение. Проверьте сеть и повторите.",
            [Language.System] = ""
        },
        ["SolarLocationGot"] = new()
        {
            [Language.SimplifiedChinese] = "已获取位置：{0}, {1}",
            [Language.TraditionalChinese] = "已獲取位置：{0}, {1}",
            [Language.English] = "Location: {0}, {1}",
            [Language.Japanese] = "位置を取得：{0}, {1}",
            [Language.Korean] = "위치: {0}, {1}",
            [Language.German] = "Standort: {0}, {1}",
            [Language.French] = "Position : {0}, {1}",
            [Language.Spanish] = "Ubicación: {0}, {1}",
            [Language.Russian] = "Местоположение: {0}, {1}",
            [Language.System] = ""
        },
        ["SolarDayTemperature"] = new()
        {
            [Language.SimplifiedChinese] = "白天色温",
            [Language.TraditionalChinese] = "白天色溫",
            [Language.English] = "Day temperature",
            [Language.Japanese] = "昼の色温度",
            [Language.Korean] = "낮 색온도",
            [Language.German] = "Tagestemperatur",
            [Language.French] = "Température de jour",
            [Language.Spanish] = "Temperatura de día",
            [Language.Russian] = "Дневная температура",
            [Language.System] = ""
        },
        ["SolarDayBrightness"] = new()
        {
            [Language.SimplifiedChinese] = "白天亮度",
            [Language.TraditionalChinese] = "白天亮度",
            [Language.English] = "Day brightness",
            [Language.Japanese] = "昼の明るさ",
            [Language.Korean] = "낮 밝기",
            [Language.German] = "Tageshelligkeit",
            [Language.French] = "Luminosité de jour",
            [Language.Spanish] = "Brillo de día",
            [Language.Russian] = "Дневная яркость",
            [Language.System] = ""
        },
        ["SolarNightTemperature"] = new()
        {
            [Language.SimplifiedChinese] = "夜晚色温",
            [Language.TraditionalChinese] = "夜晚色溫",
            [Language.English] = "Night temperature",
            [Language.Japanese] = "夜の色温度",
            [Language.Korean] = "밤 색온도",
            [Language.German] = "Nachttemperatur",
            [Language.French] = "Température de nuit",
            [Language.Spanish] = "Temperatura de noche",
            [Language.Russian] = "Ночная температура",
            [Language.System] = ""
        },
        ["SolarNightBrightness"] = new()
        {
            [Language.SimplifiedChinese] = "夜晚亮度",
            [Language.TraditionalChinese] = "夜晚亮度",
            [Language.English] = "Night brightness",
            [Language.Japanese] = "夜の明るさ",
            [Language.Korean] = "밤 밝기",
            [Language.German] = "Nachthelligkeit",
            [Language.French] = "Luminosité de nuit",
            [Language.Spanish] = "Brillo de noche",
            [Language.Russian] = "Ночная яркость",
            [Language.System] = ""
        },
        ["SolarTransition"] = new()
        {
            [Language.SimplifiedChinese] = "过渡时长",
            [Language.TraditionalChinese] = "過渡時長",
            [Language.English] = "Transition duration",
            [Language.Japanese] = "移行時間",
            [Language.Korean] = "전환 시간",
            [Language.German] = "Übergangsdauer",
            [Language.French] = "Durée de transition",
            [Language.Spanish] = "Duración de transición",
            [Language.Russian] = "Длительность перехода",
            [Language.System] = ""
        },
        ["SolarTransitionMinutes"] = new()
        {
            [Language.SimplifiedChinese] = "{0} 分钟",
            [Language.TraditionalChinese] = "{0} 分鐘",
            [Language.English] = "{0} min",
            [Language.Japanese] = "{0} 分",
            [Language.Korean] = "{0}분",
            [Language.German] = "{0} Min.",
            [Language.French] = "{0} min",
            [Language.Spanish] = "{0} min",
            [Language.Russian] = "{0} мин",
            [Language.System] = ""
        },
        ["SolarTemperatureUnit"] = new()
        {
            [Language.SimplifiedChinese] = "{0}K",
            [Language.TraditionalChinese] = "{0}K",
            [Language.English] = "{0}K",
            [Language.Japanese] = "{0}K",
            [Language.Korean] = "{0}K",
            [Language.German] = "{0}K",
            [Language.French] = "{0}K",
            [Language.Spanish] = "{0}K",
            [Language.Russian] = "{0}K",
            [Language.System] = ""
        },
        ["SolarBrightnessUnit"] = new()
        {
            [Language.SimplifiedChinese] = "{0}%",
            [Language.TraditionalChinese] = "{0}%",
            [Language.English] = "{0}%",
            [Language.Japanese] = "{0}%",
            [Language.Korean] = "{0}%",
            [Language.German] = "{0}%",
            [Language.French] = "{0}%",
            [Language.Spanish] = "{0}%",
            [Language.Russian] = "{0}%",
            [Language.System] = ""
        },
        ["SolarManualOverridden"] = new()
        {
            [Language.SimplifiedChinese] = "已手动调节，自动调度已暂停。关闭再开启总开关可恢复。",
            [Language.TraditionalChinese] = "已手動調節，自動排程已暫停。關閉再開啟總開關可恢復。",
            [Language.English] = "Manually adjusted; auto schedule paused. Toggle the master switch off and on to resume.",
            [Language.Japanese] = "手動調整済み。自動スケジュールは一時停止中。総スイッチをオフ→オンで再開します。",
            [Language.Korean] = "수동으로 조정되어 자동 일정이 일시 중지되었습니다. 총 스위치를 껐다 켜면 다시 시작됩니다.",
            [Language.German] = "Manuell angepasst; automatischer Zeitplan pausiert. Schalten Sie den Hauptschalter aus und wieder ein, um fortzufahren.",
            [Language.French] = "Ajusté manuellement ; programme automatique en pause. Basculez l'interrupteur principal pour reprendre.",
            [Language.Spanish] = "Ajustado manualmente; programación automática en pausa. Active y desactive el interruptor principal para reanudar.",
            [Language.Russian] = "Настроено вручную; авто-расписание приостановлено. Переключите главный выключатель для возобновления.",
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
