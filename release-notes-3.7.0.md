# Gamma Brightness Tool v3.7.0

### Downloads
- **Installer**: `GammaBrightnessTool_Setup.exe` (~53 MB, self-contained loose files — .NET 8.0.29 Desktop Runtime is bundled inside the app folder; nothing is installed machine-wide)
- **Portable**: `GammaBrightnessTool-Portable-v3.7.0.zip` (single-file self-contained exe)
- **Source**: `3.7.0/` in this repository

---

### English

#### ✨ New in 3.7.0 (vs 3.6.0)

- **Per-display app whitelist** — pick any running app (window + tray detection, UWP frame-host aware, product-name grouping) and gamma pauses only on the display(s) its windows actually fill; windows dragged across displays switch only when fully dropped on the new one; a global (all displays) mode is also available
- **Per-display fullscreen pause** — when a fullscreen app (game/video) is detected, only the display it covers pauses; your other displays keep their brightness & temperature
- **Layered pause semantics** — master disable > fullscreen / whitelist pause. Paused displays show native colors while their settings are preserved; changes made during a pause are recorded and applied when the pause ends
- **Tray icon "always visible"** — pin/unpin the icon from the Windows 11 overflow menu right in General settings. Implemented via `IsPromoted` with a write-retry ladder, `TaskbarCreated` re-registration after explorer restarts, and guarded self-heal for missing entries — never touches the system-wide tray icon caches
- **Startup target arbitration** — running several copies (portable + installed) is safe: the autorun entry follows "last run wins", and a dangling autorun path is repaired automatically with drive-readiness checks
- **Automation interface** — `--auto <command>` CLI over a `CurrentUserOnly` named pipe: read/click every custom control, business-level brightness/temperature actions, diagnostic probes. Destructive commands require an explicit `--allow-destructive` flag
- **Robustness** — ramp-space smooth transitions (no more "net-zero-change" flicker on toggle/startup), per-machine driver gamma-floor protection with passive learning, atomic settings writes, config self-check & repair at startup, global crash log, 82-check automated smoke suite

#### 🐞 Bug Fixes (vs 3.6.0)

- Display hot-plug / resolution change no longer flashes to full brightness (device-context replacement no longer resets gamma; a bounded background re-assert keeps your value on screen)
- Display-scaling change restart no longer blanks the screen (hand-over keep-alive re-asserts the ramp every 100 ms; full-bright dwell 581 ms → ~15–63 ms)
- Fullscreen pause no longer breaks when focus moves to another display, and minimized fullscreen windows no longer hold the pause forever
- The whitelist no longer triggers on system shell windows; stale per-display pauses are cleaned up when independent control is toggled off
- Per-display pause state is runtime-only and never persisted — restarts and hot-plugs can no longer freeze a display unexpectedly
- Extensive leak/robustness fixes: GDI handles, heavy work moved off hook callbacks, pipe-server retry, duplicated-display merges, and more

---

### 中文

#### ✨ 3.7.0 新增（相对 3.6.0）
- **逐屏应用白名单** —— 勾选任意运行中的应用（窗口 + 托盘双路识别、UWP 帧宿主下钻、按产品名归并），gamma 只在"其窗口完全落入"的显示器上暂停；跨屏窗口遵循"完全拖入新屏才切换"；也保留全局（全部屏）模式
- **全屏暂停按显示器生效** —— 检测到全屏应用时只暂停它所在的那块屏，其余显示器保持设定值
- **分层暂停语义** —— 功能停用 > 全屏/白名单暂停；暂停屏显示原生色彩并保留设定，暂停期间的设定修改会在解除后生效
- **托盘图标常驻显示** —— 通用设置里直接把图标固定到 Win11 溢出菜单（写 `IsPromoted`），带写入重试阶梯、explorer 重启后的 `TaskbarCreated` 重注册、缺失条目的护栏式自愈；绝不触碰系统级托盘缓存
- **自启目标仲裁** —— 绿色版/安装版多份共存时按"最后运行者优先"自动仲裁自启指向；悬空路径带盘符就绪检查后自动修复
- **自动化接口** —— `--auto <命令>` 经 `CurrentUserOnly` 命名管道驱动：读写/点击全部自绘控件、业务级亮度色温动作、诊断探针；破坏性命令必须显式 `--allow-destructive`
- **健壮性** —— ramp 空间平滑过渡（消灭"净变化 0"的开关/启动闪）、按机驱动的 gamma 下限保护（被动学习）、配置原子写、启动自检修复、全局崩溃日志、82 项自动化冒烟

#### 🐞 3.7.0 修复（相对 3.6.0）
- 拔插显示器/改分辨率不再闪全亮（更换 DC 不再复位 gamma；有界后台重申保持设定值）
- 改缩放自动重启不再黑屏（交接期每 100ms 重申 ramp；全亮停留 581ms → 约 15–63ms）
- 前台切走不再误判退出全屏；最小化的全屏窗口不再永久占住暂停
- 白名单不再被系统壳窗口误触发；关闭独立控制时逐屏暂停集合同步清干净
- 逐屏暂停状态改为纯运行时、绝不落盘 —— 重启/热插拔不会再意外冻结某块屏
- 大量泄漏与健壮性修复：GDI 句柄、钩子回调卸载重活、管道服务端重试等

---
*Full changelog details and technical notes: see `3.7.0/` source folder.*
*完整变更记录与技术细节见 `3.7.0/` 源码目录。*
