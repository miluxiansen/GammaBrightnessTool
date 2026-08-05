# Gamma Brightness Tool - 3.0.0

> **English** | [中文](#中文版--gamma-brightness-tool-300)

A Windows screen brightness adjustment tool built on .NET 8 / WinForms. It adjusts monitor brightness via Gamma Ramp (`SetDeviceGammaRamp`), lives in the system tray, and supports quick wheel-based adjustment.

---

## ✨ Features

- ✅ **Silent startup** - No window pops up, only a tray icon appears
- ✅ **Mouse wheel adjustment** - Adjust brightness by scrolling over the tray icon only (real-time geometric hit-test, no fragile state machine)
- ✅ **Left-click popup** - Click the tray icon to open a brightness slider with drag/wheel adjustment + one-click screen-off button
- ✅ **Brightness range** - 0% ~ 100%, 5% steps
- ✅ **OSD overlay** - Shows current brightness percentage and progress bar while adjusting
- ✅ **Right-click menu** - Brightness presets (100%/75%/50%/25%/10%), auto-start, language switch (Simplified/Traditional Chinese, English), restart, uninstall, exit
- ✅ **Auto-start** - Managed via registry with `--silent` argument
- ✅ **Single instance** - Mutex prevents multiple instances
- ✅ **Settings persistence** - JSON config file (portable mode auto-follows the exe directory)
- ✅ **PerMonitorV2 high-DPI awareness** - Popup/OSD re-anchor in real time when display scaling changes (e.g. 175% ↔ 150%), no misplacement
- ✅ **Multi-size tray icon** - Built-in 11 sizes from 16 to 256 px `.ico`, crisp at every DPI
- ✅ **Icon self-healing** - Auto-recovery when Explorer restarts or the icon is lost (2s cooldown, async so the mouse never stalls)
- ✅ **Portable** - Single-file, no installation; optional one-click uninstall (cleans registry)

## 📦 Download & Usage

### Option 1: Installer
Download `GammaBrightnessTool_Setup.exe` from the **Releases** page (Inno Setup installer, installs to D: drive, optional auto-start):

### Option 2: Portable (recommended)
Download `GammaBrightnessTool-Portable-v3.0.0.zip`:
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
| Hover tray icon + wheel | Adjust brightness (5% steps) |
| Left-click tray icon | Open brightness slider (drag / wheel fine-tune / one-click screen off) |
| Right-click tray icon | Brightness presets, auto-start, language, restart, uninstall, exit |
| Click outside the popup | Close the popup |

## ⚠️ Notes

- Brightness is based on Gamma Ramp; some GPUs/drivers reject ramps with too-low peaks (measured floor ~50%). The tool remaps the physical floor to 0.5~1.0 so 0%~100% always works
- First run as administrator is recommended (to ensure gamma takes effect; may also work without, depending on the driver)
- Brightness is restored to 100% on exit (a brief driver flicker is normal)
- The last brightness value is saved and restored on next launch

## 🔧 Technical Details

- **Gamma**: `SetDeviceGammaRamp` API, linear ramp scaling + 1/65535 random noise to defeat driver caching
- **Mouse hook**: `WH_MOUSE_LL` low-level global hook, only intercepts wheel events over the icon area; 50ms throttle
- **Icon location**: `Shell_NotifyIconGetRect` real-time physical coordinates (200ms cache), auto-recovery when lost
- **DPI**: manifest `PerMonitorV2` + full physical-coordinate chain (MONITORINFO / GetDpiForMonitor), popup re-anchors via 200ms polling
- **Thread safety**: Gamma updates locked; icon recovery is async via `BeginInvoke`, hook callbacks never block
- **Performance**: ~0% idle CPU, ~55MB RAM (incl. .NET runtime), no GDI/handle leaks (verified over long runs)

## 📁 File Structure

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # Project file (net8.0-windows, WinForms)
├── app.manifest                  # PerMonitorV2 DPI declaration
├── Program.cs                    # Entry point (single-instance mutex)
├── MainController.cs             # Main controller (orchestration, popup anchor polling)
├── NativeMethods.cs              # P/Invoke declarations
├── Monitor.cs                    # Monitor enumeration and Device Context management
├── GammaController.cs            # Brightness core (Gamma Ramp + floor remapping)
├── GlobalMouseHook.cs            # Global mouse hook (wheel/click hit-testing)
├── TrayIconManager.cs            # Tray icon management (coordinate cache/self-heal/menu)
├── BrightnessOverlay.cs          # Wheel OSD brightness overlay
├── BrightnessPopup.cs            # Left-click brightness slider popup (with screen-off button)
├── PowerTipForm.cs               # Tooltip for the screen-off button
├── IconGenerator.cs              # Multi-size tray icon generation
├── GenerateIcon.cs               # --generate-icon CLI tool
├── PngToIcoConverter.cs          # PNG → ICO converter
├── StartupManager.cs             # Auto-start management
├── SettingsManager.cs            # Settings persistence (portable/AppData auto-detect)
├── IntegrityChecker.cs           # Startup integrity checks (registry/settings self-heal)
├── Localization.cs               # Localization (Simplified/Traditional Chinese, English)
├── PopupDebug.cs                 # DEBUG-only logging (not compiled in Release)
└── Resources/
    └── APP.ico                   # App icon
```

## 📜 License

MIT License © 2025 GammaBrightnessTool Contributors

---

# 中文版 | Gamma Brightness Tool 3.0.0

> [English](#gamma-brightness-tool---300) | **中文**

一个基于 .NET 8 / WinForms 的 Windows 屏幕亮度调节工具。通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器亮度，常驻系统托盘，支持鼠标滚轮快捷调节。

## ✨ 功能特性

- ✅ **静默启动** - 无窗口弹出，仅显示托盘图标
- ✅ **鼠标滚轮调节** - 仅在托盘图标上方滚动时调节亮度（实时几何判定，不依赖易失效的状态机）
- ✅ **左键快捷弹窗** - 左键点击托盘图标弹出亮度滑块，支持拖动/滚轮调节 + 一键息屏按钮
- ✅ **亮度范围** - 0% ~ 100%，步进 5%
- ✅ **亮度浮窗 OSD** - 调节时显示当前亮度百分比和进度条
- ✅ **右键菜单** - 亮度挡位（100%/75%/50%/25%/10%）、开机启动、语言切换（简/繁/英）、重启、卸载、退出
- ✅ **开机自启** - 通过注册表管理，带 `--silent` 参数
- ✅ **单实例** - 互斥锁防止多开
- ✅ **设置持久化** - JSON 配置文件（便携模式自动跟随 exe 目录）
- ✅ **PerMonitorV2 高 DPI 感知** - 弹窗/OSD 在缩放比例切换（如 175% ↔ 150%）时实时锚定，不错位
- ✅ **多尺寸托盘图标** - 内置 16~256 共 11 档尺寸 .ico，各 DPI 下线条清晰
- ✅ **图标自愈** - 资源管理器重启/图标丢失时自动恢复（2 秒冷却，异步执行不卡鼠标）
- ✅ **绿色便携** - 单文件免安装，可选一键卸载（清理注册表）

## 📦 下载与使用

### 方式一：安装包
从 **Releases** 页面下载 `GammaBrightnessTool_Setup.exe`（Inno Setup 安装包，自动安装到 D 盘，可选开机自启）：

### 方式二：绿色版（推荐）
下载 `GammaBrightnessTool-Portable-v3.0.0.zip`：
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
| 悬停托盘图标 + 滚轮 | 调节亮度（5% 步进） |
| 左键点击托盘图标 | 弹出亮度滑块（拖动调节 / 滚轮微调 / 一键息屏） |
| 右键点击托盘图标 | 亮度挡位、开机启动、语言、重启、卸载、退出 |
| 点击弹窗外部 | 关闭弹窗 |

## ⚠️ 注意事项

- 亮度调节基于 Gamma Ramp，部分 GPU/驱动会拒绝峰值过低的 Ramp（实测下限约 50%），程序已做物理下限重映射（0.5~1.0），保证 0%~100% 全程有效
- 首次运行建议以管理员权限运行（确保 gamma 调节生效；非管理员也可能生效，视驱动而定）
- 退出程序时自动恢复 100% 亮度（驱动可能短暂闪烁属正常）
- 程序自动保存上次使用的亮度值，下次启动恢复

## 🔧 技术细节

- **Gamma 调节**: `SetDeviceGammaRamp` API，线性缩放 Ramp + 1/65535 随机噪声破坏驱动缓存
- **鼠标钩子**: `WH_MOUSE_LL` 全局低级钩子，仅拦截图标区域内的滚轮事件；50ms 节流
- **图标定位**: `Shell_NotifyIconGetRect` 实时获取图标物理坐标（200ms 缓存），丢失时自动恢复
- **DPI 处理**: manifest `PerMonitorV2` + 全物理坐标链（MONITORINFO/GetDpiForMonitor），弹窗 200ms 轮询实时锚定
- **线程安全**: Gamma 更新加锁；图标恢复经 `BeginInvoke` 异步化，钩子回调不阻塞
- **性能**: 空闲 CPU ≈ 0%，内存 ~55MB（含 .NET 运行时），无 GDI/句柄泄漏（长期运行验证）

## 📁 文件结构

```
GammaBrightnessTool/
├── GammaBrightnessTool.csproj    # 项目文件（net8.0-windows, WinForms）
├── app.manifest                  # PerMonitorV2 DPI 声明
├── Program.cs                    # 入口点（单实例互斥锁）
├── MainController.cs             # 主控制器（组件协调、弹窗锚定轮询）
├── NativeMethods.cs              # P/Invoke 声明
├── Monitor.cs                    # 显示器枚举和 Device Context 管理
├── GammaController.cs            # 亮度调节核心（Gamma Ramp + 下限重映射）
├── GlobalMouseHook.cs            # 全局鼠标钩子（滚轮/点击判定）
├── TrayIconManager.cs            # 托盘图标管理（坐标缓存/自愈/菜单）
├── BrightnessOverlay.cs          # 滚轮 OSD 亮度浮窗
├── BrightnessPopup.cs            # 左键亮度滑块弹窗（含息屏按钮）
├── PowerTipForm.cs               # 息屏按钮悬浮提示
├── IconGenerator.cs              # 多尺寸托盘图标生成
├── GenerateIcon.cs               # --generate-icon 命令行生成工具
├── PngToIcoConverter.cs          # PNG 转 ICO 工具
├── StartupManager.cs             # 开机自启管理
├── SettingsManager.cs            # 设置持久化（便携/AppData 自适应）
├── IntegrityChecker.cs           # 启动完整性检查（注册表/设置自愈）
├── Localization.cs               # 多语言（简/繁/英）
├── PopupDebug.cs                 # DEBUG 构建专用日志（Release 不编译）
└── Resources/
    └── APP.ico                   # 应用图标
```

## 📜 开源协议

MIT License © 2025 GammaBrightnessTool Contributors
