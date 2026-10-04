# Gamma Brightness Tool v3.7.1

### Downloads
- **Installer**: `GammaBrightnessTool_Setup_v3.7.1.exe` (~51 MB, self-contained loose files — .NET 8.0.29 Desktop Runtime is bundled inside the app folder; nothing is installed machine-wide)
- **Portable**: `GammaBrightnessTool-Portable-v3.7.1.zip` (single-file self-contained exe)
- **Source**: `3.7.1/` in this repository

---

### English

#### ✨ New in 3.7.1 (vs 3.7.0)

- **Color-temperature OSD** — pressing a temperature hotkey now shows an on-screen temperature slider (previously only brightness had one); shares the same overlay window with the brightness OSD and the two never appear at once; with per-display control enabled, every controlled display gets its own slider row; 6600 K is highlighted in accent blue (consistent with the popup), and the slider applies instantly while dragging
- **Startup smooth transition** — restoring your last settings at launch now ramps smoothly (~40 frames) instead of jumping, eliminating the cold-start flicker
- **NVIDIA driver compatibility** — the app passively learns how restrictive the current GPU driver is and, when needed, switches to a "near-native" gamma table (65279 peak) that restrictive NVIDIA drivers accept instead of rejecting outright — structurally immune to the driver's LUT-reclamation brightness pulse

#### 🔧 Adjustments in 3.7.1

- **Per-axis manual takeover** — manually adjusting brightness or temperature now takes over **only that axis** from the Solar schedule (previously both axes were taken over together, freezing the other one); closing "Time adjust" also restores per axis
- **Single smoothing switch** — brightness/temperature smoothing is now one combined state instead of two separate toggles (smoothing always applies to both axes or neither)
- **Unified brightness-hotkey path** — brightness hotkeys now go through the exact same channel as scroll-wheel/popup adjustments, so hotkey, wheel and OSD behave identically and stay decoupled
- **Temperature readout fix** — actual temperature is computed from non-clamped ramp samples (previously the full-scale clamp skewed the reading)

#### 🐞 Bug Fixes (vs 3.7.0)

- Minimizing a fullscreen window no longer keeps the fullscreen pause active forever (and moving focus away from a still-fullscreen window still does **not** end the pause)
- Smooth-transition family of flickers fixed (axis bounce-back, toggle flash, startup flash — "cancel in-flight transition" is now handled completely)
- No more brightness pulse when re-opening the settings window or after idle (near-native gamma table is immune to the NVIDIA LUT reclamation)
- Secondary displays no longer get wiped back to native colors when a fullscreen app runs on another display
- Per-display toggle no longer makes popup sliders jump; popup temperature-slider direction and hotkey direction no longer disagree; hotkeys take effect immediately after entry without a restart

---

### 中文

#### ✨ 3.7.1 新增（相对 3.7.0）

- **色温 OSD** —— 按色温热键时在屏幕上显示色温调节滑轨（此前只有亮度有）；与亮度 OSD **同窗互切**、永不同时出现；「按显示器生效」开启时每块受控屏有自己的滑轨行；6600 K 中性点着**强调蓝**（与弹窗一致），拖动即时生效
- **启动平滑过渡** —— 启动恢复上次设定改走**平滑过渡**（约 40 帧），消除冷启动硬跳闪变
- **NVIDIA 驱动兼容** —— 启动时**被动学习**当前驱动的 gamma 接受度，必要时自动切换**近原生 gamma 表**（65279 峰值），限制型 NVIDIA 驱动不再整份拒收 —— 从结构上免疫驱动 LUT 回收导致的亮度脉冲

#### 🔧 3.7.1 调整（相对 3.7.0）
- **手动接管拆轴** —— 手动调亮度或色温只**接管对应轴**（此前两轴一起被接管，Solar 时间调整无法继续控制另一轴）；关闭「时间调整」也按轴恢复
- **平滑归并为唯一总开关** —— 亮度/色温平滑不再分两个开关，归并为**单一状态**（要么两轴一起平滑、要么都不平滑）
- **亮度热键统一机制** —— 亮度热键改走与滚轮/弹窗**完全相同**的调节通道，热键/滚轮/OSD 三种入口行为一致、互不耦合
- **色温读数修正** —— 实际色温按**未触顶采样点**计算（此前受 gamma 表满值钳制影响，读数与设定值有偏差）

#### 🐞 3.7.1 修复（相对 3.7.0）
- 全屏窗口被**最小化**后，全屏暂停不再永久占住（而窗口仍全屏、仅前台切走时**依然不会**误解除暂停）
- 平滑过渡一族闪变修复（轴回弹、开关闪、启动闪 ——「取消在途过渡」语义现已完整）
- 重开设置窗 / 空闲后不再出现亮度脉冲（近原生 gamma 表免疫 NVIDIA LUT 回收）
- 副屏不再因另一屏跑全屏应用而被抹回原生色彩
- 「按显示器生效」开关不再让弹窗滑轨跳变；弹窗色温滑轨方向与热键方向不再相反；热键录入后**不重启即生效**

---

*Gamma Brightness Tool — miluxiansen*
