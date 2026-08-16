# Gamma Brightness Tool - 3.3.0

> **English** | [中文](#中文版--gamma-brightness-tool-330)

A Windows screen brightness & color temperature adjustment tool built on .NET 8 / WinForms. It adjusts the display via Gamma Ramp (`SetDeviceGammaRamp`), lives in the system tray, and supports wheel / popup / hotkey adjustment.

---

## ✨ Features

- **Brightness 0–100%** — adjust via tray wheel, left-click popup slider, hotkeys, or tray menu presets
- **Color temperature 3300–10000K** — warm orange → neutral 6600K (default) → cool blue; per-channel gamma multipliers (Tanner Helland algorithm), fully independent of and stackable with brightness
- **Popup mode switch** — the popup slider switches between brightness and temperature modes; the sliding knob carries the active mode's icon; hover shows a tooltip
- **Independent temperature step** — configurable 50–3000K wheel step, separate from the brightness step
- **Temperature hotkeys** — increase / decrease temperature hotkeys (ignored while temperature is off)
- **Settings window** — 4 pages: General / Color temperature / Hotkeys / About; theme switch is instant (no rebuild)
- **Themes** — dark / light / follow-system for both the main UI and the floating popups (independent)
- **9 languages** — Simplified/Traditional Chinese, English, Japanese, Korean, German, French, Spanish, Russian
- **Per-size tray icons** — pre-rendered 16–256px PNG sets (black/white for light/dark taskbars), crisp at every DPI
- **PerMonitorV2 high-DPI** — popups re-anchor in real time when display scaling changes
- **Silent startup, single instance, auto-start, portable mode** — JSON settings beside the exe

## 📦 Download & Usage

### Option 1: Installer
Download `GammaBrightnessTool_Setup.exe` from the **Releases** page (Inno Setup installer, installs to D: drive, optional auto-start).

### Option 2: Portable (recommended)
Download `GammaBrightnessTool-Portable-v3.3.0.zip`:
1. Extract to any folder, double-click `GammaBrightnessTool.exe`
2. Settings are saved to `settings.json` next to the exe
3. No .NET runtime needed (self-contained)

### Option 3: Build from source
```bash
# Requires .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 🖱️ Usage

| Action | Effect |
|--------|--------|
| Hover tray icon + wheel | Adjust brightness (or temperature in the popup) |
| Left-click tray icon | Open popup: slider + mode switch + settings + screen-off |
| Right-click tray icon | Brightness presets, auto-start, language, restart, uninstall, exit |
| Hotkeys | Increase/decrease brightness, increase/decrease temperature, screen off |
| Click outside the popup | Close the popup |

## ⚠️ Notes

- Brightness is based on Gamma Ramp; some GPUs/drivers reject ramps with too-low peaks (measured floor ~50%). The tool remaps the physical floor to 0.5~1.0 so 0%~100% always works
- Warm temperatures compress the perceived brightness range (green/blue channels dominate human perception) — this is inherent to all temperature tools (LightBulb / f.lux alike)
- First run as administrator is recommended (to ensure gamma takes effect; may also work without, depending on the driver)
- Brightness is restored to 100% on exit; the last brightness & temperature values are saved and restored on next launch

## 🔧 Technical Details

- **Gamma**: `SetDeviceGammaRamp`, linear ramp scaling + counter noise (+0/+1, non-peak) to defeat driver caching; double-precision math
- **Temperature**: Tanner Helland algorithm → per-channel multipliers, applied in the same ramp; 6600K = all 1.0 (pure brightness, backward compatible)
- **Mouse hook**: `WH_MOUSE_LL`, only intercepts wheel events over the icon area; 50ms throttle
- **Popup anchoring**: `Shell_NotifyIconGetRect` physical coordinates, 200ms polling re-anchor, DPI-change relayout
- **Thread safety**: gamma updates locked; hook callbacks marshal to the UI thread via `Invoke`
- **Performance**: ~0% idle CPU, no GDI/handle leaks

## 📁 File Structure

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # Project (net8.0-windows, WinForms, embedded icon PNGs)
├── app.manifest                  # PerMonitorV2 DPI declaration
├── Program.cs                    # Entry point (single-instance mutex, CLI tools)
├── MainController.cs             # Orchestration, popup anchoring, hotkey callbacks
├── NativeMethods.cs              # P/Invoke declarations
├── Monitor.cs                    # Monitor enumeration + DeviceContext (gamma)
├── GammaController.cs            # Brightness + temperature core (ramp math)
├── GlobalMouseHook.cs            # Global mouse hook (wheel/click hit-testing)
├── TrayIconManager.cs            # Tray icon (pre-rendered PNG frames, tooltip, menu)
├── BrightnessPopup.cs            # Left-click popup (slider + mode switch + buttons)
├── BrightnessOverlay.cs          # Wheel OSD overlay
├── PowerTipForm.cs               # Button tooltips (screen-off / mode / settings)
├── ToggleSwitch.cs               # Themed sliding switch (mode icon on knob)
├── ColorTemperature.cs           # Kelvin → display color (slider fill feedback)
├── SettingsForm.cs               # Settings window (4 pages, instant theme refresh)
├── HotKeyService.cs              # Global hotkey registration (WM_HOTKEY)
├── IconGenerator.cs              # Multi-size tray icon assembly
├── Localization.cs               # 9 languages
├── ThemeManager.cs               # Theme resolution + popup palette
├── ...                           # StartupManager / SettingsManager / helpers
└── Resources/
    ├── tray-icons/               # Per-size sun PNGs (black/white)
    ├── colortemp-icons-final/    # Temperature ring icons (color/black/white)
    ├── gear-icons-original/      # Settings gear icons (black/white)
    └── APP.ico                   # App icon
```

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors

---

# 中文版 | Gamma Brightness Tool 3.3.0

> [English](#gamma-brightness-tool---330) | **中文**

一个基于 .NET 8 / WinForms 的 Windows 屏幕亮度与色温调节工具。通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器，常驻系统托盘，支持滚轮 / 弹窗 / 快捷键调节。

## ✨ 功能特性

- ✅ **亮度调节 0~100%** - 托盘滚轮、左键弹窗滑块、快捷键、托盘菜单挡位
- ✅ **色温调节 3300~10000K** - 暖橙 → 中性 6600K（默认）→ 冷蓝；三通道独立 gamma 缩放（Tanner Helland 算法），与亮度天然正交可叠加
- ✅ **弹窗模式切换** - 滑块在亮度/色温模式间切换，滑动开关旋钮带当前模式图标，悬停有提示
- ✅ **独立色温步进** - 色温滚轮步进 50~3000K 可配置，与亮度步进互不影响
- ✅ **色温快捷键** - 增加/降低色温热键（色温关闭时自动忽略）
- ✅ **设置窗口 4 页导航** - 通用 / 色温调节 / 快捷键 / 版本；主题切换即时生效（不重建窗口）
- ✅ **双主题体系** - 主界面与浮窗各自独立深色/浅色/跟随系统
- ✅ **9 种语言** - 简中/繁中/英/日/韩/德/法/西/俄
- ✅ **多尺寸托盘图标** - 16~256px 预渲染 PNG（深色任务栏白色、浅色任务栏黑色），各 DPI 清晰
- ✅ **PerMonitorV2 高 DPI** - 弹窗在缩放比例切换时实时锚定，不错位
- ✅ **静默启动 / 单实例 / 开机自启 / 绿色便携** - 配置存 exe 旁 settings.json

## 📦 下载与使用

### 方式一：安装包
从 **Releases** 页面下载 `GammaBrightnessTool_Setup.exe`（Inno Setup 安装包，自动安装到 D 盘，可选开机自启）。

### 方式二：绿色版（推荐）
下载 `GammaBrightnessTool-Portable-v3.3.0.zip`：
1. 解压到任意文件夹，双击 `GammaBrightnessTool.exe` 运行
2. 设置自动保存在 exe 同目录的 `settings.json`
3. 无需安装 .NET 运行时（已自包含）

### 方式三：源码编译
```bash
# 需要 .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 🖱️ 使用说明

| 操作 | 效果 |
|------|------|
| 悬停托盘图标 + 滚轮 | 调节亮度（弹窗打开时调节当前模式） |
| 左键点击托盘图标 | 弹出滑块：亮度/色温切换 + 设置按钮 + 一键息屏 |
| 右键点击托盘图标 | 亮度挡位、开机启动、语言、重启、卸载、退出 |
| 快捷键 | 增/减亮度、增/减色温、息屏 |
| 点击弹窗外部 | 关闭弹窗 |

## ⚠️ 注意事项

- 亮度调节基于 Gamma Ramp，部分 GPU/驱动会拒绝峰值过低的 Ramp（实测下限约 50%），程序已做物理下限重映射（0.5~1.0），保证 0%~100% 全程有效
- 暖色温会压缩可感知亮度范围（人眼对红通道不敏感、绿蓝通道权重高）——这是所有色温工具（LightBulb/f.lux）共有的物理特性，非缺陷
- 首次运行建议以管理员权限运行（确保 gamma 调节生效；非管理员也可能生效，视驱动而定）
- 退出程序时自动恢复 100% 亮度；上次的亮度与色温值自动保存，下次启动恢复

## 🔧 技术细节

- **Gamma 调节**: `SetDeviceGammaRamp`，线性缩放 Ramp + 计数器噪声（+0/+1，仅非峰值）破坏驱动缓存；全程 double 计算
- **色温算法**: Tanner Helland 算法 → 三通道乘数，与亮度同一条 ramp 叠加；6600K 三通道全 1.0（退化为纯亮度，向后兼容）
- **鼠标钩子**: `WH_MOUSE_LL`，仅拦截图标区域滚轮；50ms 节流
- **弹窗锚定**: `Shell_NotifyIconGetRect` 物理坐标 + 200ms 轮询实时锚定，DPI 变化自动重排
- **线程安全**: Gamma 更新加锁；钩子回调经 `Invoke` 封送到 UI 线程
- **性能**: 空闲 CPU ≈ 0%，无 GDI/句柄泄漏

## 📁 文件结构

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # 项目文件（net8.0-windows, WinForms, 嵌入图标 PNG）
├── app.manifest                  # PerMonitorV2 DPI 声明
├── Program.cs                    # 入口点（单实例互斥锁、CLI 工具）
├── MainController.cs             # 主控制器（组件协调、弹窗锚定轮询、快捷键回调）
├── NativeMethods.cs              # P/Invoke 声明
├── Monitor.cs                    # 显示器枚举 + DeviceContext（gamma）
├── GammaController.cs            # 亮度 + 色温核心（ramp 数学）
├── GlobalMouseHook.cs            # 全局鼠标钩子（滚轮/点击判定）
├── TrayIconManager.cs            # 托盘图标（预渲染 PNG 帧、提示、菜单）
├── BrightnessPopup.cs            # 左键弹窗（滑块 + 模式开关 + 按钮）
├── BrightnessOverlay.cs          # 滚轮 OSD 浮窗
├── PowerTipForm.cs               # 按钮悬停提示（息屏/模式/设置）
├── ToggleSwitch.cs               # 主题化滑动开关（旋钮带模式图标）
├── ColorTemperature.cs           # 色温 → 显示颜色（滑块填充反馈）
├── SettingsForm.cs               # 设置窗口（4 页导航、主题即时刷新）
├── HotKeyService.cs              # 全局热键注册（WM_HOTKEY）
├── IconGenerator.cs              # 多尺寸托盘图标组装
├── Localization.cs               # 9 语言
├── ThemeManager.cs               # 主题解析 + 浮窗调色板
├── ...                           # StartupManager / SettingsManager / 工具类
└── Resources/
    ├── tray-icons/               # 多尺寸太阳图标 PNG（黑/白）
    ├── colortemp-icons-final/    # 色温环形图标（彩色/黑/白）
    ├── gear-icons-original/      # 设置齿轮图标（黑/白）
    └── APP.ico                   # 应用图标
```

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors
