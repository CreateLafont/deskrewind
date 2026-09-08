# DeskRewind v1.0.0

Windows 桌面图标布局保存与恢复工具。主界面和布局恢复引擎均内嵌于一个 WPF EXE。

## 下载

[下载 DeskRewind v1.0.0（Windows x64 单文件 EXE）](https://github.com/CreateLafont/deskrewind/releases/download/v1.0.0/DeskRewind.exe)

SHA-256：`BE9CF0CF1D248CC368DC70FDD9435F4D2880370E77BDCF95365518C9A77C3E0D`

程序无需安装 Python、PowerShell 或其他运行环境。首次运行会在 `%LOCALAPPDATA%\DeskRewind` 建立私有运行缓存与布局记录。

构建：运行 `build-wpf.cmd`。此脚本只依赖 Windows 自带的 .NET Framework 4.x C# 编译器；原生引擎源码位于 `native/`，包括桌面 Shell 状态、系统缩放和布局存档，并在构建时并入主 EXE。

用户布局存放于 `%LOCALAPPDATA%\DeskRewind\layouts.profiles.json`，不应提交到 Git。程序目录下的布局文件会在首次运行时自动迁移到该位置。
