# Gamma Brightness Tool - 1.0.0

> **English** | [中文](#中文版)

> ⚠️ **Historical archive — source only.** This version has known bugs (DPI handling issues). It is kept for reference only; **no binary release is provided**. Use **v3.0.0** instead: see [`../README.md`](../README.md).

Initial release of the Gamma Brightness Tool — a Windows screen brightness adjustment tool built on .NET 8 / WinForms, adjusting monitor brightness via Gamma Ramp (`SetDeviceGammaRamp`).

---

## ✨ Features

- ✅ **Silent startup** - No window pops up, only a tray icon appears
- ✅ **Mouse wheel adjustment** - Adjust brightness by scrolling over the tray icon
- ✅ **Brightness range** - 10% ~ 100%, 5% steps
- ✅ **OSD overlay** - Shows current brightness percentage and progress bar while adjusting
- ✅ **Right-click menu** - Brightness presets (100%/75%/50%/25%/10%), auto-start, exit
- ✅ **Auto-start** - Managed via registry with `--silent` argument
- ✅ **Single instance** - Mutex prevents multiple instances
- ✅ **Settings persistence** - JSON config file
- ✅ **PerMonitorV2 DPI awareness** - manifest-declared high-DPI support

## 🖱️ Usage

| Action | Effect |
|--------|--------|
| Hover tray icon + wheel | Adjust brightness (5% steps) |
| Right-click tray icon | Brightness presets, auto-start, exit |

## ⚠️ Notes

- Brightness is based on Gamma Ramp; minimum is 10% (some GPUs reject too-low peaks)
- First run as administrator is recommended
- Brightness is restored to 100% on exit

## 🛠️ Build

```bash
# Requires .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true
```

## 📜 License

MIT License © 2026 GammaBrightnessTool Contributors

---

# 中文版

# Gamma Brightness Tool 1.0.0

> [English](#gamma-brightness-tool---100) | **中文**

> ⚠️ **历史存档——仅源码。** 本版本存在已知 bug（DPI 处理问题），仅供存档参考，**不提供二进制发布**。请使用 **v3.0.0**：见 [`../README.md`](../README.md)。

Gamma Brightness Tool 的初版——基于 .NET 8 / WinForms 的 Windows 屏幕亮度调节工具，通过 Gamma Ramp（`SetDeviceGammaRamp`）调节显示器亮度。

## ✨ 功能特性

- ✅ **静默启动** - 无窗口弹出，仅显示托盘图标
- ✅ **鼠标滚轮调节** - 在托盘图标上方滚动滚轮调节亮度
- ✅ **亮度范围** - 10% ~ 100%，步进 5%
- ✅ **OSD 浮窗** - 调节时显示当前亮度百分比和进度条
- ✅ **右键菜单** - 亮度挡位（100%/75%/50%/25%/10%）、开机启动、退出
- ✅ **开机自启** - 通过注册表管理，带 `--silent` 参数
- ✅ **单实例** - 互斥锁防止多开
- ✅ **设置持久化** - JSON 配置文件
- ✅ **PerMonitorV2 DPI 感知** - manifest 声明的高 DPI 支持

## 🖱️ 使用说明

| 操作 | 效果 |
|------|------|
| 悬停托盘图标 + 滚轮 | 调节亮度（5% 步进） |
| 右键点击托盘图标 | 亮度挡位、开机启动、退出 |

## ⚠️ 注意事项

- 亮度调节基于 Gamma Ramp，最低 10%（部分 GPU 拒绝峰值过低）
- 首次运行建议以管理员权限运行
- 退出程序时自动恢复 100% 亮度

## 🛠️ 编译

```bash
# 需要 .NET 8 SDK
dotnet publish -c Release -o publish --self-contained true -p:PublishSingleFile=true
```

## 📜 开源协议

MIT License © 2026 GammaBrightnessTool Contributors
