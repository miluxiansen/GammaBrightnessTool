# Gamma Brightness Tool v3.5.0

### Downloads
- **Installer**: `GammaBrightnessTool_Setup.exe` (53 MB)
- **Portable**: `GammaBrightnessTool-Portable-v3.5.0.zip` (69 MB)

---

### English

#### ✨ New in 3.5.0 (vs 3.4.0)

- **Gamma self-heal** — automatically re-applies the gamma ramp after sleep/resume and monitor hot-plug (default on)
- **Pause in fullscreen** — pauses gamma adjustment while a fullscreen app (game/video) is focused, with smooth 1.2s transitions on enter/exit; wheel, hotkeys and the popup slider are all ignored while paused
- **"Disable" from the tray menu** — right-click → Disable: permanently, for 1/5/15/30 min, 1/3/5/12 h, 1 day, or until sunrise/sunset (the solar options require time-based adjustment); gamma returns to native colors and auto-recovers when the timer expires
- **"Feature off" dropdown in settings** — the General page mirrors the tray Disable menu (countdown display, locks the brightness/color-temp sliders and level dropdowns while active)
- **Settings export/import** — save your full configuration to a JSON file and restore it on another machine (values are clamped and validated on import)
- **Hotkey master-switch rework** — master on → all sub-toggles turn on; color-temp sub-toggles follow the color-temp switch (three-way AND logic), no more dead-locked switches
- **Solar changes sync the level dropdowns** — the brightness/color-temp level dropdowns follow sunrise/sunset adjustments in real time
- **Smooth switch coverage** — startup, level/preset switches, color-temp master switch, temperature-range clamp, reset, fullscreen enter/exit and Disable all respect the Brightness/Temperature smooth toggles (instant jump when disabled)

#### 🐛 Bug Fixes (vs 3.4.0)

- Fixed color temperature falling back to 6000K after toggling the color-temp switch off/on (LastTemperature was saved with the pre-animation value)
- Fixed "time adjustment" not turning off the night profile when disabled (now restores the day targets instead of stale night values)
- Fixed the crash when opening Settings on a second monitor with a different DPI (nav list draw out-of-range)
- Fixed the process crash caused by a garbage-collected WinEvent hook delegate (FailFast 0x80131623) when switching language
- Fixed fullscreen detection falsely triggering on the desktop (system window class filter) and missing F11 toggles (added a polling fallback)
- Fixed the sudden brightness jump when exiting fullscreen (pause flag is now kept during the exit animation)
- Fixed the tray Disable submenu checking both "1 minute" and "5 minutes" at once (nearest-match only)
- Fixed "Reset settings" / "Import settings" leaving the Disable state inconsistent

---

### 中文

#### ✨ 3.5.0 新增（相对 3.4.0）

- **gamma 自愈** — 睡眠唤醒、显示器热插拔后自动重新应用 gamma 曲线（默认开启）
- **全屏自动暂停** — 检测到全屏应用（游戏/视频）时暂停 gamma 调节，进入/退出全屏平滑过渡（1.2s）；暂停期间滚轮、热键、弹窗滑块全部忽略
- **托盘菜单"禁用"** — 右键 → 禁用：永久 / 1 / 5 / 15 / 30 分钟 / 1 / 3 / 5 / 12 小时 / 1 天 / 至日出日落（日出日落项需开启时间调整）；禁用时恢复原生色彩，到期自动平滑恢复
- **设置窗"功能停用"下拉** — 通用设置页与托盘禁用菜单同步（倒计时显示；停用期间亮度/色温滑轨与挡位下拉锁定）
- **设置导出/导入** — 将完整配置导出为 JSON，可在另一台电脑一键恢复（导入时自动校验并约束取值范围）
- **快捷键主开关联动重做** — 主开关开 → 子开关全开；色温子开关跟随色温总开关（三者"与"逻辑），不再出现开关死锁
- **时间调整同步挡位下拉** — 日出日落自动变化实时同步到亮度/色温挡位下拉显示
- **平滑开关全覆盖** — 启动、挡位/预设切换、色温总开关、色温范围钳制、重置、全屏进出、禁用均遵循亮度/色温平滑开关（关闭时瞬间切换）

#### 🐛 3.5.0 修复（相对 3.4.0）

- 修复色温开关关→开掉回 6000K（LastTemperature 被存成动画前旧值）
- 修复关闭"时间调整"后夜间模式无法关闭（改为恢复白天目标值而非过期的夜晚值）
- 修复跨 DPI 副屏打开设置窗口崩溃（导航列表自绘越界）
- 修复切换语言时 WinEvent 钩子委托被 GC 回收导致进程崩溃（FailFast 0x80131623）
- 修复全屏检测误判桌面（系统窗口类名过滤）、漏判 F11 切换（增加轮询兜底）
- 修复退出全屏时亮度突变（退出动画期间保持暂停标志）
- 修复托盘禁用菜单"1 分钟"与"5 分钟"同时打勾（改为只勾最接近档位）
- 修复"重置设置"/"导入设置"后禁用状态不一致
