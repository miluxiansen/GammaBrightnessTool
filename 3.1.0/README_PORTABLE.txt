Gamma Brightness Tool v3.1.0 - 绿色免安装版 / Portable Version
================================================================

【简体中文】

使用说明
--------
1. 解压到任意文件夹，双击 GammaBrightnessTool.exe 运行
2. 程序会自动在当前文件夹创建 settings.json 配置文件

正确卸载方式
------------
⚠️ 如果你曾勾选过"开机启动"，直接删除文件夹会残留注册表项！

推荐做法：
  1. 右键托盘图标 → "卸载软件"（程序会自动清理注册表并删除自身）
  2. 若程序已无法启动，可手动删除以下注册表项：
     HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\GammaBrightnessTool

如果从未勾选过开机启动：直接删除本文件夹即可，无残留。

注意事项
--------
- 需要 .NET 8.0 Runtime（缺失时系统会自动提示下载）
- 首次运行建议以管理员权限运行（确保 gamma 调节生效）
- 退出程序时自动恢复 100% 亮度


【English】

Usage
-----
1. Extract to any folder, double-click GammaBrightnessTool.exe to run
2. The program will auto-create settings.json in the same folder

Proper Uninstallation
---------------------
⚠️ If you enabled "Start with Windows", deleting the folder leaves a registry entry behind!

Recommended:
  1. Right-click tray icon → "Uninstall" (auto-cleans registry and removes itself)
  2. If the program won't start, manually delete this registry key:
     HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\GammaBrightnessTool

If "Start with Windows" was never enabled: simply delete this folder, no leftovers.

Notes
-----
- Requires .NET 8.0 Runtime (system will prompt to download if missing)
- Run as administrator on first launch for gamma adjustment to work properly
- Program restores 100% brightness on exit
