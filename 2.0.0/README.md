# Gamma Brightness Tool - 2.0.0

> **English** | [中文](#中文版)

> ⚠️ **Historical archive — source only.** This version has known bugs (DPI handling issues). It is kept for reference only; **no binary release is provided**. Use **v3.0.0** instead: see [`../README.md`](../README.md).

Second major version of the Gamma Brightness Tool — a Windows screen brightness adjustment tool built on .NET 8 / WinForms, adjusting monitor brightness via Gamma Ramp (`SetDeviceGammaRamp`).

---

## ✨ Features

- ✅ **Silent startup** - No window pops up, only a tray icon appears
- ✅ **Mouse wheel adjustment** - Adjust brightness by scrolling over the tray icon
- ✅ **Left-click popup** - Click the tray icon to open a brightness slider with drag/wheel adjustment + one-click screen-off button
- ✅ **Brightness range** - 0% ~ 100% (physical floor remapped to 0.5~1.0 gamma so low values always work on GPUs that reject too-low peaks), 5% steps
- ✅ **OSD overlay** - Shows current brightness percentage and progress bar while adjusting
- ✅ **Right-click menu** - Brightness presets (100%/75%/50%/25%/10%), auto-start, language switch (Simplified/Traditional Chinese, English), restart, uninstall, exit
- ✅ **Auto-start** - Managed via registry with `--silent` argument
- ✅ **Single instance** - Mutex prevents multiple instances
- ✅ **Settings persistence** - JSON config file (portable mode auto-follows the exe directory)
- ✅ **PerMonitorV2 DPI awareness** - manifest-declared high-DPI support

## 🖱️ Usage

| Action | Effect |
|--------|--------|
| Hover tray icon + wheel | Adjust brightness (5% steps) |
| Left-click tray icon | Open brightness slider (drag / wheel fine-tune / one-click screen off) |
| Right-click tray icon | Brightness presets, auto-start, language, restart, uninstall, exit |
| Click outside the popup | Close the popup |

## ⚠️ Notes

- Brightness is based on Gamma Ramp; some GPUs/drivers reject ramps with too-low peaks. The tool remaps the physical floor to 0.5~1.0 so 0%~100% always works
- First run as administrator is recommended
- Brightness is restored to 100% on exit (a brief driver flicker is normal)
- The last brightness value is saved and restored on next launch

## 🛠️ Build

```bash
# Requires .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors

---

# 中文版

# Gamma Brightness Tool 2.0.0

> [English](#gamma-brightness-tool---200) | **中文**

> ⚠️ **历史存档——仅源码。** 本版本存在已知 bug（DPI 处理问题），仅供存档参考，**不提供二进制发布**。请使用 **v3.0.0**：见 [`../README.md`](../README.md)。

Gamma Brightness Tool 的第二代——基于 .NET 8 / WinForms 的 Windows 屏幕亮度调节工具，通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器亮度。

## ✨ 功能特性

- ✅ **静默启动** - 无窗口弹出，仅显示托盘图标
- ✅ **鼠标滚轮调节** - 在托盘图标上方滚动滚轮调节亮度
- ✅ **左键快捷弹窗** - 左键点击托盘图标弹出亮度滑块，支持拖动/滚轮调节 + 一键息屏按钮
- ✅ **亮度范围** - 0% ~ 100%（物理下限重映射为 0.5~1.0 gamma，保证拒绝低峰的 GPU 上 0% 也有效），步进 5%
- ✅ **OSD 浮窗** - 调节时显示当前亮度百分比和进度条
- ✅ **右键菜单** - 亮度挡位（100%/75%/50%/25%/10%）、开机启动、语言切换（简/繁/英）、重启、卸载、退出
- ✅ **开机自启** - 通过注册表管理，带 `--silent` 参数
- ✅ **单实例** - 互斥锁防止多开
- ✅ **设置持久化** - JSON 配置文件（便携模式自动跟随 exe 目录）
- ✅ **PerMonitorV2 DPI 感知** - manifest 声明的高 DPI 支持

## 🖱️ 使用说明

| 操作 | 效果 |
|------|------|
| 悬停托盘图标 + 滚轮 | 调节亮度（5% 步进） |
| 左键点击托盘图标 | 弹出亮度滑块（拖动调节 / 滚轮微调 / 一键息屏） |
| 右键点击托盘图标 | 亮度挡位、开机启动、语言、重启、卸载、退出 |
| 点击弹窗外部 | 关闭弹窗 |

## ⚠️ 注意事项

- 亮度调节基于 Gamma Ramp，部分 GPU/驱动会拒绝峰值过低的 Ramp，程序已做物理下限重映射（0.5~1.0），保证 0%~100% 全程有效
- 首次运行建议以管理员权限运行
- 退出程序时自动恢复 100% 亮度（驱动可能短暂闪烁属正常）
- 程序自动保存上次使用的亮度值，下次启动恢复

## 🛠️ 编译

```bash
# 需要 .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors
