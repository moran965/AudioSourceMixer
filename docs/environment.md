# 本地开发环境检查

初始检查日期：2026-08-03；复测日期：2026-08-10（Asia/Shanghai）。未记录密码、令牌或完整环境变量。

| 项目 | 实测结果 |
|---|---|
| 操作系统 | Windows NT 10.0.26200.0，DisplayVersion 25H2，build 26200.8875，x64 |
| Git | 2.53.0.windows.3 |
| .NET SDK | 8.0.423，位于用户级 `.dotnet`；MSBuild 17.11.48 |
| .NET Runtime | Microsoft.NETCore.App / WindowsDesktop.App 8.0.29 |
| Visual Studio | Visual Studio Community 2022 17.14.8 |
| 独立 MSBuild | Visual Studio 内 17.14.14.31908 |
| Windows SDK | Visual Studio 组件中存在 Windows 11 SDK 10.0.26100.0 |
| C/C++ | MSVC 14.44.35207，x64 `cl.exe` 存在 |
| CMake | PATH 中未安装；本项目不需要 |
| Node.js | 24.18.1 |
| npm / npx | 11.16.0；PowerShell 策略拦截 `.ps1` shim，使用未改变安全策略的 `npm.cmd` / `npx.cmd` |
| PowerShell | Windows PowerShell 5.1.26100.8875 |
| 安装包工具 | 未发现 Inno Setup、WiX、NSIS、7-Zip 或 winget；因此实现自包含的每用户安装器 |
| Google Chrome | 151；当前用户配置未安装 Audio Source Mixer unpacked 扩展 |
| Microsoft Edge | 151；当前用户配置已从仓库源码目录加载固定 ID unpacked 扩展 |
| 默认音频设备 | 耳机 (WH-1000XM5)，2 声道/48 kHz |
| 其他活动音频设备 | 扬声器 (Realtek(R) Audio)，2 声道/48 kHz |

当前 PATH 中的系统 `dotnet` 只有 Runtime，用户目录中的 `dotnet.exe` 才包含 SDK。构建脚本会检测候选路径并选择真正能列出 SDK 的版本。

项目目录初始为空且不是 Git 仓库，已执行 `git init` 并创建分层解决方案。
