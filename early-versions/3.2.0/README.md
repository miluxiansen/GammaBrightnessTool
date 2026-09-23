# Gamma Brightness Tool - 3.2.0

> **English** | [中文](#中文版--gamma-brightness-tool-320)

A Windows screen brightness adjustment tool built on .NET 8 / WinForms. It adjusts monitor brightness via Gamma Ramp (`SetDeviceGammaRamp`), lives in the system tray, and supports quick wheel-based adjustment.

---

## ✨ Features

- ✅ **Silent startup** - No window pops up, only a tray icon appears
- ✅ **Mouse wheel adjustment** - Adjust brightness by scrolling over the tray icon only (real-time geometric hit-test, no fragile state machine)
- ✅ **Left-click popup** - Click the tray icon to open a brightness slider with drag/wheel adjustment + one-click screen-off button
- ✅ **Global hotkeys** - Bind custom hotkeys for brightness up / brightness down / screen off (Ctrl+Alt+Up style, any key + modifiers)
- ✅ **Hotkey enable switches** - Per-hotkey on/off toggle without unbinding; one-click "clear all" on the hotkeys page
- ✅ **Brightness range** - 0% ~ 100%, 9 step presets (2%~100%, default 5%)
- ✅ **Wheel master switch** - Disable tray-wheel brightness entirely; hotkeys still work independently
- ✅ **OSD overlay** - Shows current brightness percentage and progress bar while adjusting
- ✅ **Right-click menu** - Brightness presets (100%/75%/50%/25%/10%), auto-start, language switch, restart, uninstall, exit
- ✅ **9 languages** - Simplified/Traditional Chinese, English, Japanese, Korean, German, French, Spanish, Russian (+ follow system)
- ✅ **Dark/light theme** - Full dark theme for settings window, tray menu, popups; theme follows system in real time
- ✅ **Independent popup theme** - Popups (slider/OSD) can use a theme different from the main UI
- ✅ **Auto-start** - Managed via registry with `--silent` argument
- ✅ **Single instance** - Mutex prevents multiple instances
- ✅ **Settings persistence** - JSON config file (portable mode auto-follows the exe directory)
- ✅ **Reset settings** - One-click restore all defaults (hotkey bindings are kept)
- ✅ **Settings window always-on-top toggle** - Optional, off by default so it never blocks other windows
- ✅ **PerMonitorV2 high-DPI awareness** - Popup/OSD re-anchor in real time when display scaling changes (e.g. 175% ↔ 150%), no misplacement
- ✅ **Multi-size tray icon** - Built-in 11 sizes from 16 to 256 px `.ico`, crisp at every DPI
- ✅ **Icon self-healing** - Auto-recovery when Explorer restarts or the icon is lost (2s cooldown, async so the mouse never stalls)
- ✅ **Portable** - Single-file, no installation; optional one-click uninstall (cleans registry)

## 📦 Download & Usage

### Option 1: Installer
Download `GammaBrightnessTool_Setup.exe` from the **Releases** page (Inno Setup installer, installs to D: drive, optional auto-start):

### Option 2: Portable (recommended)
Download `GammaBrightnessTool-Portable-v3.2.0.zip`:
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
| Hover tray icon + wheel | Adjust brightness (step size configurable) |
| Left-click tray icon | Open brightness slider (drag / wheel fine-tune / one-click screen off) |
| Right-click tray icon | Brightness presets, auto-start, language, restart, uninstall, exit |
| Global hotkey | Brightness up / down / screen off (custom bindings) |
| Click outside the popup | Close the popup |

## 🎹 Hotkeys (New in 3.2.0)

Open **Settings → Hotkeys** to bind up to three global hotkeys:

| Action | Default | Notes |
|--------|---------|-------|
| Increase brightness | (unbound) | Click the input box, press the combo, click √ |
| Decrease brightness | (unbound) | Same |
| Turn off display | (unbound) | Broadcasts `SC_MONITORPOWER` |

- Each hotkey has an **enable switch** — turn it off to disable without losing the binding
- The **× button** cancels a recording while capturing; deletes the binding when not capturing
- **Clear all** wipes every binding at once

## ⚠️ Notes

- Brightness is based on Gamma Ramp; some GPUs/drivers reject ramps with too-low peaks (measured floor ~50%). The tool remaps the physical floor to 0.5~1.0 so 0%~100% always works
- First run as administrator is recommended (to ensure gamma takes effect; may also work without, depending on the driver)
- Brightness is restored to 100% on exit (a brief driver flicker is normal)
- The last brightness value is saved and restored on next launch

## 🔧 Technical Details

- **Gamma**: `SetDeviceGammaRamp` API, linear ramp scaling + counter-based noise to defeat driver caching
- **Hotkeys**: `RegisterHotKey` + `WM_HOTKEY` dispatch through the hidden tray message window; string round-trip parse/format (Ctrl+Shift+Up style)
- **Mouse hook**: `WH_MOUSE_LL` low-level global hook, only intercepts wheel events over the icon area; 50ms throttle
- **Icon location**: `Shell_NotifyIconGetRect` real-time physical coordinates (200ms cache), auto-recovery when lost
- **DPI**: manifest `PerMonitorV2` + full physical-coordinate chain (MONITORINFO / GetDpiForMonitor), popup re-anchors via 200ms polling
- **Theming**: `ThemeManager` static class with dual-channel system-theme listening (SystemEvents + 500ms registry poll); owner-drawn menus via MF_OWNERDRAW + DWM immersive dark
- **Thread safety**: Gamma updates locked; icon recovery is async via `BeginInvoke`, hook callbacks never block
- **Performance**: ~0% idle CPU, ~55MB RAM (incl. .NET runtime), no GDI/handle leaks (verified over long runs)

## 📁 File Structure

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # Project file (net8.0-windows, WinForms)
├── app.manifest                  # PerMonitorV2 DPI declaration
├── Program.cs                    # Entry point (single-instance mutex, --show-settings)
├── MainController.cs             # Main controller (orchestration, hotkey registration)
├── NativeMethods.cs              # P/Invoke declarations
├── Monitor.cs                    # Monitor enumeration and Device Context management
├── GammaController.cs            # Brightness core (Gamma Ramp + floor remapping)
├── GlobalMouseHook.cs            # Global mouse hook (wheel/click hit-testing)
├── TrayIconManager.cs            # Tray icon management (coordinate cache/self-heal/menu)
├── HotKeyService.cs              # Global hotkey registration / dispatch (WM_HOTKEY)
├── HotKeyCaptureBox.cs           # Hotkey recording input control
├── BrightnessOverlay.cs          # Wheel OSD brightness overlay
├── BrightnessPopup.cs            # Left-click brightness slider popup (with screen-off button)
├── PowerTipForm.cs               # Tooltip for the screen-off button
├── IconGenerator.cs              # Multi-size tray icon generation
├── GenerateIcon.cs               # --generate-icon CLI tool
├── PngToIcoConverter.cs          # PNG → ICO converter
├── StartupManager.cs             # Auto-start management
├── SettingsManager.cs            # Settings persistence (portable/AppData auto-detect)
├── IntegrityChecker.cs           # Startup integrity checks (registry/settings self-heal)
├── Localization.cs               # Localization (9 languages + follow system)
├── ThemeManager.cs               # Theme system (dark/light, popup-independent, system listener)
├── SettingsForm.cs               # Settings window (general / hotkeys / about)
├── ToggleSwitch.cs               # Owner-drawn toggle switch
├── ThemedComboBox.cs             # Dark-theme combo box
├── RoundedButton.cs              # Rounded themed button
├── RoundedTextBox.cs             # Rounded themed text box
├── RoundedCardPanel.cs           # Rounded card container (setting rows)
├── ThemeScrollPanel.cs           # Themed slim scrollbar panel
├── PopupDebug.cs                 # DEBUG-only logging (not compiled in Release)
├── build-green.ps1               # Green single-file build script
├── Setup.iss                     # Inno Setup installer script
└── Resources/
    └── APP.ico                   # App icon
```

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors

---

# 中文版 | Gamma Brightness Tool 3.2.0

> [English](#gamma-brightness-tool---320) | **中文**

一个基于 .NET 8 / WinForms 的 Windows 屏幕亮度调节工具。通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器亮度，常驻系统托盘，支持鼠标滚轮快捷调节。

## ✨ 功能特性

- ✅ **静默启动** - 无窗口弹出，仅显示托盘图标
- ✅ **鼠标滚轮调节** - 仅在托盘图标上方滚动时调节亮度（实时几何判定，不依赖易失效的状态机）
- ✅ **左键快捷弹窗** - 左键点击托盘图标弹出亮度滑块，支持拖动/滚轮调节 + 一键息屏按钮
- ✅ **全局快捷键** - 自定义绑定增加亮度 / 降低亮度 / 熄屏（Ctrl+Alt+↑ 风格，任意键 + 修饰键）
- ✅ **快捷键开关** - 每项独立生效开关（不用解绑即可禁用）；快捷键页支持"一键清除"
- ✅ **亮度范围** - 0% ~ 100%，9 档步进预设（2%~100%，默认 5%）
- ✅ **滚轮总开关** - 可完全关闭托盘滚轮调节；快捷键不受影响独立生效
- ✅ **亮度浮窗 OSD** - 调节时显示当前亮度百分比和进度条
- ✅ **右键菜单** - 亮度挡位（100%/75%/50%/25%/10%）、开机启动、语言切换、重启、卸载、退出
- ✅ **9 种语言** - 简中/繁中/英/日/韩/德/法/西/俄（+ 跟随系统）
- ✅ **深色/浅色主题** - 设置窗口、托盘菜单、浮窗完整深色渲染；主题实时跟随系统
- ✅ **浮窗独立主题** - 浮窗（滑块/OSD）可使用与主界面不同的主题
- ✅ **开机自启** - 通过注册表管理，带 `--silent` 参数
- ✅ **单实例** - 互斥锁防止多开
- ✅ **设置持久化** - JSON 配置文件（便携模式自动跟随 exe 目录）
- ✅ **重置设置** - 一键恢复默认（快捷键绑定不受影响）
- ✅ **设置窗口置顶开关** - 可选，默认关闭不遮挡其他窗口
- ✅ **PerMonitorV2 高 DPI 感知** - 弹窗/OSD 在缩放比例切换（如 175% ↔ 150%）时实时锚定，不错位
- ✅ **多尺寸托盘图标** - 内置 16~256 共 11 档尺寸 .ico，各 DPI 下线条清晰
- ✅ **图标自愈** - 资源管理器重启/图标丢失时自动恢复（2 秒冷却，异步执行不卡鼠标）
- ✅ **绿色便携** - 单文件免安装，可选一键卸载（清理注册表）

## 📦 下载与使用

### 方式一：安装包
从 **Releases** 页面下载 `GammaBrightnessTool_Setup.exe`（Inno Setup 安装包，自动安装到 D 盘，可选开机自启）：

### 方式二：绿色版（推荐）
下载 `GammaBrightnessTool-Portable-v3.2.0.zip`：
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
| 悬停托盘图标 + 滚轮 | 调节亮度（步进可配置） |
| 左键点击托盘图标 | 弹出亮度滑块（拖动调节 / 滚轮微调 / 一键息屏） |
| 右键点击托盘图标 | 亮度挡位、开机启动、语言、重启、卸载、退出 |
| 全局快捷键 | 增加/降低亮度、熄屏（自定义绑定） |
| 点击弹窗外部 | 关闭弹窗 |

## 🎹 快捷键（3.2.0 新增）

打开 **设置 → 快捷键** 可绑定三个全局快捷键：

| 动作 | 默认 | 说明 |
|------|------|------|
| 增加亮度 | （未绑定） | 点击输入框 → 按下组合键 → 点 √ 生效 |
| 降低亮度 | （未绑定） | 同上 |
| 熄屏 | （未绑定） | 广播 `SC_MONITORPOWER` |

- 每个快捷键有**生效开关** —— 关闭即禁用但保留绑定
- **× 按钮**：录制中点它 = 取消录入；非录制点它 = 删除绑定
- **一键清除**：一次性清除全部绑定

## ⚠️ 注意事项

- 亮度调节基于 Gamma Ramp，部分 GPU/驱动会拒绝峰值过低的 Ramp（实测下限约 50%），程序已做物理下限重映射（0.5~1.0），保证 0%~100% 全程有效
- 首次运行建议以管理员权限运行（确保 gamma 调节生效；非管理员也可能生效，视驱动而定）
- 退出程序时自动恢复 100% 亮度（驱动可能短暂闪烁属正常）
- 程序自动保存上次使用的亮度值，下次启动恢复

## 🔧 技术细节

- **Gamma 调节**: `SetDeviceGammaRamp` API，线性缩放 Ramp + 计数器噪声破坏驱动缓存
- **快捷键**: `RegisterHotKey` + `WM_HOTKEY` 经隐藏托盘消息窗口分发；字符串往返解析（Ctrl+Shift+↑ 风格）
- **鼠标钩子**: `WH_MOUSE_LL` 全局低级钩子，仅拦截图标区域内的滚轮事件；50ms 节流
- **图标定位**: `Shell_NotifyIconGetRect` 实时获取图标物理坐标（200ms 缓存），丢失时自动恢复
- **DPI 处理**: manifest `PerMonitorV2` + 全物理坐标链（MONITORINFO/GetDpiForMonitor），弹窗 200ms 轮询实时锚定
- **主题系统**: `ThemeManager` 静态类，双通道系统主题监听（SystemEvents + 500ms 注册表轮询）；菜单 MF_OWNERDRAW 自绘 + DWM 沉浸式深色
- **线程安全**: Gamma 更新加锁；图标恢复经 `BeginInvoke` 异步化，钩子回调不阻塞
- **性能**: 空闲 CPU ≈ 0%，内存 ~55MB（含 .NET 运行时），无 GDI/句柄泄漏（长期运行验证）

## 📁 文件结构

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # 项目文件（net8.0-windows, WinForms）
├── app.manifest                  # PerMonitorV2 DPI 声明
├── Program.cs                    # 入口点（单实例互斥锁、--show-settings）
├── MainController.cs             # 主控制器（组件协调、快捷键注册）
├── NativeMethods.cs              # P/Invoke 声明
├── Monitor.cs                    # 显示器枚举和 Device Context 管理
├── GammaController.cs            # 亮度调节核心（Gamma Ramp + 下限重映射）
├── GlobalMouseHook.cs            # 全局鼠标钩子（滚轮/点击判定）
├── TrayIconManager.cs            # 托盘图标管理（坐标缓存/自愈/菜单）
├── HotKeyService.cs              # 全局快捷键注册/分发（WM_HOTKEY）
├── HotKeyCaptureBox.cs           # 快捷键录入控件
├── BrightnessOverlay.cs          # 滚轮 OSD 亮度浮窗
├── BrightnessPopup.cs            # 左键亮度滑块弹窗（含息屏按钮）
├── PowerTipForm.cs               # 息屏按钮悬浮提示
├── IconGenerator.cs              # 多尺寸托盘图标生成
├── GenerateIcon.cs               # --generate-icon 命令行生成工具
├── PngToIcoConverter.cs          # PNG 转 ICO 工具
├── StartupManager.cs             # 开机自启管理
├── SettingsManager.cs            # 设置持久化（便携/AppData 自适应）
├── IntegrityChecker.cs           # 启动完整性检查（注册表/设置自愈）
├── Localization.cs               # 多语言（9 语言 + 跟随系统）
├── ThemeManager.cs               # 主题系统（深浅色、浮窗独立、系统监听）
├── SettingsForm.cs               # 设置窗口（通用设置/快捷键/版本信息）
├── ToggleSwitch.cs               # 自绘滑动开关
├── ThemedComboBox.cs             # 深色化下拉框
├── RoundedButton.cs              # 圆角主题按钮
├── RoundedTextBox.cs             # 圆角主题输入框
├── RoundedCardPanel.cs           # 圆角卡片容器（设置行）
├── ThemeScrollPanel.cs           # 主题化细滚动条面板
├── PopupDebug.cs                 # DEBUG 构建专用日志（Release 不编译）
├── build-green.ps1               # 绿色版单文件构建脚本
├── Setup.iss                     # Inno Setup 安装包脚本
└── Resources/
    └── APP.ico                   # 应用图标
```

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors

