# GammaBrightnessTool

English | [中文](#中文版--gammabrightnesstool)

A Windows screen brightness & color temperature adjustment tool built on .NET 8 / WinForms. It adjusts the display via Gamma Ramp (`SetDeviceGammaRamp`), lives in the system tray, and supports quick wheel-based adjustment.

💡 What it is: a lightweight utility for Windows environments where DDC/CI is unavailable. Most brightness-adjusting software relies on DDC/CI, while gamma-based brightness tools usually lack wheel adjustment. This one lives in the system tray and supports mouse wheel brightness adjustment out of the box. No special hardware or drivers required.

🌐 Languages: Simplified Chinese, Traditional Chinese, English, Japanese, Korean, German, French, Spanish, Russian.

## 📦 Version History

| Version | Release Notes | Source |
|---------|---------------|--------|
| **3.5.0 (Latest)** | Gamma self-heal, fullscreen pause & disable | [3.5.0/](3.5.0/README.md) |
| 3.4.0 | Time-based auto adjustment & smooth transitions | [3.4.0/](3.4.0/README.md) |
| 3.3.0 | Color temperature adjustment | [3.3.0/](3.3.0/README.md) |
| 3.2.0 | Global hotkeys | [3.2.0/](3.2.0/README.md) |
| 3.1.0 | Basic settings window + dark theme | — |
| 3.0.0 | Base version for brightness adjustment | [3.0.0/](3.0.0/README.md) |

### ✨ Feature evolution

| Feature | 3.0.0 | 3.1.0 | 3.2.0 | 3.3.0 | 3.4.0 | 3.5.0 |
|---------|:-----:|:-----:|:-----:|:-----:|:-----:|:-----:|
| Settings window | — | ✔ | ✔ | ✔ | ✔ | ✔ |
| Theme switching | — | ✔ | ✔ | ✔ | ✔ | ✔ |
| Global hotkeys | — | — | ✔ | ✔ | ✔ | ✔ |
| Color temperature | — | — | — | ✔ | ✔ | ✔ |
| Time-based adjustment | — | — | — | — | ✔ | ✔ |
| Gamma self-heal (sleep/hot-plug) | — | — | — | — | — | ✔ |
| Pause in fullscreen | — | — | — | — | — | ✔ |
| Disable (tray menu) | — | — | — | — | — | ✔ |
| Settings export/import | — | — | — | — | — | ✔ |

## 📸 Screenshots

| Settings |
|----------|
| ![settings-en](screenshots/settings-en.png) |

| Left-click popup — brightness | Left-click popup — color temp | Left-click popup — default |
|-------------------------------|-------------------------------|----------------------------|
| ![popup-brightness](screenshots/popup-brightness.png) | ![popup-temperature](screenshots/popup-temperature.png) | ![popup-0814](screenshots/popup-0814.png) |

## 🚀 Quick Start

Latest version (3.5.0): see [3.5.0/README.md](3.5.0/README.md) for full features, usage, and build instructions.

## 🖱️ Show the tray icon

If the app icon is not visible in the system tray:

- Open Settings → Personalization → Taskbar
- Click "Other system tray icons"
- Turn on the toggle for GammaBrightnessTool

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors. See [3.5.0/LICENSE](3.5.0/LICENSE).

---

# 中文版 | GammaBrightnessTool

[English](#gammabrightnesstool) | 中文

一个基于 .NET 8 / WinForms 的 Windows 屏幕亮度与色温调节工具。通过 Gamma Ramp（SetDeviceGammaRamp）调节显示器，常驻系统托盘，支持鼠标滚轮快捷调节。

💡 简介：一款简易的小工具，专为无法使用 DDC/CI 的 Windows 电脑环境提供亮度调节能力。许多调整亮度的软件只支持通过 DDC/CI 调光，而通过调节 gamma 值来调整亮度的软件又大多不支持滚轮调节。本工具常驻系统托盘，支持鼠标滚轮快捷调节，无需特殊硬件或驱动，即可获得简单直观的屏幕亮度控制。

🌐 支持语言：简体中文、繁体中文、英语、日语、韩语、德语、法语、西班牙语、俄语。

## 📦 版本历史

| 版本 | 发布说明 | 源码 |
|------|----------|------|
| **3.5.0（最新）** | gamma 自愈 + 全屏暂停 + 禁用 | [3.5.0/](3.5.0/README.md) |
| 3.4.0 | 时间调整 + 平滑过渡 | [3.4.0/](3.4.0/README.md) |
| 3.3.0 | 色温调节 | [3.3.0/](3.3.0/README.md) |
| 3.2.0 | 全局快捷键 | [3.2.0/](3.2.0/README.md) |
| 3.1.0 | 添加基础设置窗口 + 深色主题 | — |
| 3.0.0 | 亮度调节的基础可用版本 | [3.0.0/](3.0.0/README.md) |

### ✨ 功能演进

| 功能 | 3.0.0 | 3.1.0 | 3.2.0 | 3.3.0 | 3.4.0 | 3.5.0 |
|------|:-----:|:-----:|:-----:|:-----:|:-----:|:-----:|
| 设置窗口 | — | ✔ | ✔ | ✔ | ✔ | ✔ |
| 主题切换 | — | ✔ | ✔ | ✔ | ✔ | ✔ |
| 全局快捷键 | — | — | ✔ | ✔ | ✔ | ✔ |
| 色温调节 | — | — | — | ✔ | ✔ | ✔ |
| 时间调整（日出日落） | — | — | — | — | ✔ | ✔ |
| gamma 自愈（唤醒/热插拔） | — | — | — | — | — | ✔ |
| 全屏自动暂停 | — | — | — | — | — | ✔ |
| 禁用（托盘菜单） | — | — | — | — | — | ✔ |
| 设置导出/导入 | — | — | — | — | — | ✔ |

## 📸 界面截图

| 设置窗口 |
|----------|
| ![settings-zh](screenshots/settings-zh.png) |

| 左键弹窗 — 亮度 | 左键弹窗 — 色温 | 左键弹窗 — 默认（色温关闭） |
|-----------------|-----------------|------------------------------|
| ![popup-brightness](screenshots/popup-brightness.png) | ![popup-temperature](screenshots/popup-temperature.png) | ![popup-0814](screenshots/popup-0814.png) |

## 🚀 快速开始

最新版（3.5.0）：完整功能、使用方法与编译说明见 [3.5.0/README.md](3.5.0/README.md)。

## 🖱️ 显示托盘图标

如果任务栏托盘里看不到软件图标：

- 打开 设置 → 个性化 → 任务栏
- 点击「其他系统托盘图标」
- 打开 GammaBrightnessTool 开关

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors，详见 [3.5.0/LICENSE](3.5.0/LICENSE)。
