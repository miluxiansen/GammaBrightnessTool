# GammaBrightnessTool

> **English** | [中文](#中文版--gammabrightnesstool)

A Windows screen brightness adjustment tool built on .NET 8 / WinForms. It adjusts monitor brightness via Gamma Ramp (`SetDeviceGammaRamp`), lives in the system tray, and supports quick wheel-based adjustment.

> 💡 **What it is**: a lightweight utility for Windows environments where **DDC/CI is unavailable** (e.g. desktop PCs, VGA/HDMI setups, monitors that don't expose DDC/CI). It lives in the **system tray** and supports **mouse wheel** brightness adjustment out of the box. No special hardware or drivers required.

This repository contains the **full source history** of the project across three major versions. Each version folder is a complete, independently-buildable source snapshot.

---

## 📦 Version History

| Version | Release Notes | Source | Portable |
|---------|---------------|--------|----------|
| **3.0.0** (Latest) | DPI overhaul + multi-size icon + cleanup | [`3.0.0/`](3.0.0/README.md) | `3.0.0/GammaBrightnessTool_绿色版_v3.0.0_20260805.zip` |
| **2.0.0** | Left-click popup + screen-off button + gamma floor remap | [`2.0.0/`](2.0.0/README.md) | — (source only, historical) |
| **1.0.0** | Initial release: wheel adjustment + OSD | [`1.0.0/`](1.0.0/README.md) | — (source only, historical) |

### ✨ Feature evolution

| Feature | 1.0.0 | 2.0.0 | 3.0.0 |
|---------|:-----:|:-----:|:-----:|
| Tray icon + silent startup | ✅ | ✅ | ✅ |
| Mouse wheel brightness (icon hit-test) | ✅ | ✅ | ✅ (real-time geometry) |
| OSD brightness overlay | ✅ | ✅ | ✅ |
| Brightness range | 10%~100% | 0%~100% (floor remapped) | 0%~100% (floor remapped) |
| Left-click popup slider | — | ✅ | ✅ |
| Screen-off button + tooltip | — | ✅ | ✅ |
| PerMonitorV2 DPI awareness | partial | partial | ✅ (manifest + full physical chain) |
| Real-time popup re-anchor on DPI switch | — | — | ✅ (200ms polling) |
| Multi-size tray icon (16~256, 11 sizes) | — | — | ✅ |
| Icon self-healing (Explorer crash) | — | — | ✅ |
| Settings persistence (portable/AppData) | ✅ | ✅ | ✅ |
| Localization (Simplified/Traditional Chinese, English) | ✅ | ✅ | ✅ |
| Installer (Inno Setup) | ✅ | ✅ | ✅ |

## 🚀 Quick Start

**Latest version (3.0.0):** see [`3.0.0/README.md`](3.0.0/README.md) for full features, usage, and build instructions.

## 🛠️ Build

Each version folder is self-contained and builds independently:

```bash
# Requires .NET 8 SDK
cd 3.0.0
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The installer is built with Inno Setup 6 (`Setup.iss` lives in the original project folder `3.0.0/Setup.iss`).

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors. See `3.0.0/LICENSE`.

---

# 中文版 | GammaBrightnessTool

> [English](#gammabrightnesstool) | **中文**

一个基于 .NET 8 / WinForms 的 Windows 屏幕亮度调节工具。通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器亮度，常驻系统托盘，支持鼠标滚轮快捷调节。

> 💡 **简介**：一款简易的小工具，专为**无法使用 DDC/CI 的 Windows 电脑环境**（如台式机、VGA/HDMI 连接、不暴露 DDC/CI 的显示器）提供亮度调节能力。常驻系统托盘，支持鼠标滚轮快捷调节。无需特殊硬件或驱动，即可获得简单直观的屏幕亮度控制。

本仓库包含项目**三代完整源码历史**，每个版本目录都是完整、可独立编译的源码快照。

## 📦 版本历史

| 版本 | 发布说明 | 源码 | 绿色版 |
|------|---------|------|--------|
| **3.0.0**（最新） | DPI 全面修复 + 多尺寸图标 + 代码清理 | [`3.0.0/`](3.0.0/README.md) | `3.0.0/GammaBrightnessTool_绿色版_v3.0.0_20260805.zip` |
| **2.0.0** | 左键弹窗 + 息屏按钮 + Gamma 下限重映射 | [`2.0.0/`](2.0.0/README.md) | —（仅源码，历史存档） |
| **1.0.0** | 初版：滚轮调节 + OSD | [`1.0.0/`](1.0.0/README.md) | —（仅源码，历史存档） |

### ✨ 功能演进

| 功能 | 1.0.0 | 2.0.0 | 3.0.0 |
|------|:-----:|:-----:|:-----:|
| 托盘图标 + 静默启动 | ✅ | ✅ | ✅ |
| 滚轮调亮度（图标命中判定） | ✅ | ✅ | ✅（实时几何判定） |
| OSD 亮度浮窗 | ✅ | ✅ | ✅ |
| 亮度范围 | 10%~100% | 0%~100%（下限重映射） | 0%~100%（下限重映射） |
| 左键亮度弹窗 | — | ✅ | ✅ |
| 息屏按钮 + 悬浮提示 | — | ✅ | ✅ |
| PerMonitorV2 高 DPI 感知 | 部分 | 部分 | ✅（manifest + 全物理坐标链） |
| DPI 切换实时重锚定 | — | — | ✅（200ms 轮询） |
| 多尺寸托盘图标（16~256 共 11 档） | — | — | ✅ |
| 图标自愈（资源管理器崩溃） | — | — | ✅ |
| 设置持久化（便携/AppData） | ✅ | ✅ | ✅ |
| 多语言（简/繁/英） | ✅ | ✅ | ✅ |
| 安装包（Inno Setup） | ✅ | ✅ | ✅ |

## 🚀 快速开始

**最新版（3.0.0）：** 完整功能、使用方法与编译说明见 [`3.0.0/README.md`](3.0.0/README.md)。

## 🛠️ 编译

每个版本目录独立自包含，可单独编译：

```bash
# 需要 .NET 8 SDK
cd 3.0.0
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

安装包使用 Inno Setup 6 编译（`Setup.iss` 位于原项目目录 `3.0.0/Setup.iss`）。

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors，详见 `3.0.0/LICENSE`。
