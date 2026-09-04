# Gamma Brightness Tool v3.6.0

### Downloads
- **Installer**: `GammaBrightnessTool_Setup.exe` (~53 MB, self-contained loose files — .NET 8.0.29 Desktop Runtime is bundled inside the app folder; nothing is installed machine-wide)
- **Portable**: `GammaBrightnessTool-Portable-v3.6.0.zip` (~69 MB, single-file self-contained exe)
- **Source**: `3.6.0/` in this repository

---

### English

#### ✨ New in 3.6.0 (vs 3.5.0)

- **Multi-monitor independent control** — the flagship feature. Turn it on in the new "Monitors" settings page and every display gets its own brightness & color temperature: the left-click popup and the wheel OSD each show one slider row per display, drag any row to adjust that display only
- **Stable EDID-based display identity** — displays are keyed by their EDID instance ID (`MONITOR\...\{GUID}`), never by `\\.\DISPLAYn` (which changes on hot-plug/reboot); clone/duplicate outputs of one physical panel are merged into a single control unit
- **Per-monitor enable / freeze** — each display can be individually disabled; a disabled display keeps its current value frozen and ignores all adjustments, and remembers it when re-enabled
- **"Monitors" settings page** — new 7th page with a master switch and three expandable sections: controlled displays (per-display on/off), rename displays (custom names), and display info (friendly name + resolution & scaling)
- **Friendly display names** — names are read via DisplayConfig `GET_TARGET_NAME` (same source as Windows Settings, e.g. "G5c II") with a three-level fallback: custom name → EDID friendly name → model segment
- **Neutral-point dwell on the temperature slider** — dragging past the default 6600K briefly dwells with a blue highlight (140 ms) then follows smoothly; releasing exactly on 6600K keeps 6600K. No sticking, no step jumps
- **Popup / OSD opacity sliders** — independent 40–100% opacity for the left-click popup and the wheel OSD (General settings), applied live and persisted; the popup draws its first frame opaque to avoid a white/black flash
- **Smoother dragging** — slider drags are coalesced on a 24 ms frame timer (gamma write + tooltip + save + events merged), so fast large drags no longer stutter
- **Packaging (final, 2026-09-04)** — the installer ships self-contained **loose files**: the app exe (185 KB) + `.NET 8.0.29 Desktop Runtime` files are laid out in the same folder, nothing is written machine-wide (`PrivilegesRequired=lowest`); Setup ≈ 53 MB, cold start ≈ 110 ms. Installer tasks ("Start with Windows" / desktop icon) are user-chosen, **not** default-checked
- **Config preserved on uninstall** — `%APPDATA%\GammaBrightnessTool` is intentionally kept by both the installer and the green build

#### 🐛 Bug Fixes (vs 3.5.0)

- Fixed popup/OSD slider rows not following display count in independent mode (window height now tracks the row count)
- Fixed disabled (frozen) displays still being adjusted by the global wheel/hotkeys/levels
- Fixed the popup layout breaking when toggling independent mode while it was open (could not recover to single-row mode)
- Fixed the theme dropdown "-1 pulse" (clicking the already-selected item briefly degraded the theme to "follow system" and flashed)
- Fixed the first-frame flash of large windows when system theme ≠ window theme (opaque/transparent first-frame sequencing)
- Fixed the OSD becoming invisible / text being clipped at high DPI in multi-row mode (per-row paint order & row sizing)
- Fixed temperature not being applied per-display under independent mode (solar & preset changes now write every enabled display)
- Fixed the app failing to update scaling at runtime — a display-scaling change now restarts the process automatically, preserving the current page and scroll position
- Fixed fullscreen-pause not taking effect until restart (now active immediately); hot-plugged displays are seeded from the average of enabled displays
- Fixed a crash when closing the settings window right after a DPI change (font disposal during handle rebuild)

---

### 中文

#### ✨ 3.6.0 新增（相对 3.5.0）

- **多显示器独立控制（核心功能）** — 在新增的"显示器"设置页开启后，每台显示器拥有独立的亮度与色温：左键弹窗与滚轮 OSD 各按显示器显示一行滑轨，拖动哪行就调哪台屏
- **基于 EDID 的稳定显示器标识** — 显示器一律以 EDID 实例 ID（`MONITOR\...\{GUID}`）作为持久键，绝不使用 `\\.\DISPLAYn` 序号（热插拔/重启会变）；同一物理面板的克隆/多路输出合并为单一控制单元
- **逐屏启用/冻结** — 每台显示器可单独停用；停用屏冻结当前值、不参与任何调节，重新启用后保持自己的值
- **"显示器"设置页** — 新增第 7 页：独立控制总开关 + 三个折叠菜单（受控显示器逐屏开关、重命名显示器自定义名、显示器信息）
- **友好显示器名称** — 通过 DisplayConfig `GET_TARGET_NAME` 读取（与 Windows 设置同源，如 "G5c II"），三级回退：自定义名 → EDID 友好名 → 型号段
- **色温滑轨 6600K 中性点轻顿** — 拖过默认 6600K 时短暂停顿并蓝色高亮（140ms），随后平滑跟手；恰好松手在 6600K 则保持 6600K，不粘手、不跳档
- **弹窗 / OSD 透明度滑轨** — 通用设置新增左键弹窗与 OSD 浮窗两条独立透明度滑轨（40–100%，默认 90/70），实时生效并持久化；弹窗首帧先不透明绘制避免白/黑闪
- **拖动更跟手** — 滑轨拖动按 24ms 合帧（gamma 写屏 + tooltip + 保存 + 事件合并），快速大幅拖动不再卡顿
- **打包形态（终版，2026-09-04）** — 安装器为**自包含散文件**：应用 exe（185KB）与 `.NET 8.0.29 Desktop Runtime` 全套散文件同目录铺装，不做任何机器级写入（`PrivilegesRequired=lowest`）；安装包 ≈ 53MB，冷启动 ≈ 110ms。安装任务（开机自启/桌面图标）由用户自选，**非默认勾选**
- **卸载保留配置** — 安装版与绿色版卸载均刻意保留 `%APPDATA%\GammaBrightnessTool` 用户配置

#### 🐛 3.6.0 修复（相对 3.5.0）

- 修复独立模式下弹窗/OSD 滑轨行数不跟随显示器数量（窗口高度现按行数自适应）
- 修复停用（冻结）屏仍被全局滚轮/热键/挡位调节
- 修复弹窗打开时切换独立控制导致布局崩坏、无法回到单行模式
- 修复主题下拉"-1 脉冲"（点击已选中项会瞬时把主题退化为"跟随系统"并闪烁）
- 修复系统主题与窗口主题不一致时大窗口首帧闪烁（不透明/透明首帧时序）
- 修复高 DPI 下多行 OSD 消失与文字被滑轨遮挡（逐行绘制顺序与行高）
- 修复独立模式下色温不逐屏生效（太阳调度/预设变更现会写入每台启用屏）
- 修复运行中无法响应缩放变更——检测到显示缩放变化会自动重启进程并保留当前页与滚动位置
- 修复"全屏自动暂停"需重启才生效（现即时生效）；热插拔新屏以启用屏平均值播种
- 修复 DPI 变更后立即关闭设置窗口偶发崩溃（句柄重建期字体释放）

#### 🔒 Final-release hardening (2026-09-04)

- Atomic settings save (temp file + rename) — a crash/power loss mid-write can no longer truncate `settings.json`
- Settings window no longer re-subscribes to controller events on every rebuild (leak/stale-closure fix)
- Theme polling moved to the UI thread; tray-cache self-heal no longer deletes the system-wide icon stream; `NOTIFYICONDATA` switched to Unicode with 127-char tooltip truncation
- GDI/handle leaks fixed (rounded-corner paths/regions, animation timers, shared-font lifecycle, bitmap stream lifetime); global mouse-hook work deferred off the hook callback
- Green uninstall no longer deletes shared `%APPDATA%` config; tray **brightness levels** submenu restored (no stale OSD on preset switch)
- Added in-app operation log (`%TEMP%\GammaBrightnessTool_ops.log`) and a `--selftest` automated check suite (9 checks)

#### 🔒 终版加固（2026-09-04）

- 配置原子保存（临时文件+改名），写盘中途崩溃不再损坏 settings.json
- 设置窗不再每次重建都重复订阅控制器事件（修复累积泄漏与旧闭包）
- 主题轮询改到 UI 线程；托盘缓存自检不再整体删除系统共享图标流；NOTIFYICONDATA 改 Unicode + tooltip 127 字符截断
- 修复 GDI/句柄泄漏（圆角 Path/Region、动画 Timer、共享字体生命周期、Bitmap 流存活期）；全局钩子重活移出钩子回调
- 绿色版卸载不再删除共享 %APPDATA% 配置；右键菜单恢复"亮度挡位"子菜单（切换挡位不再弹旧值 OSD）
- 内置操作日志（%TEMP%\GammaBrightnessTool_ops.log）与 `--selftest` 自动化自检（9 项）

---
*Full changelog details and technical notes: see `3.6.0/` source folder.*
*完整变更记录与技术细节见 `3.6.0/` 源码目录。*
