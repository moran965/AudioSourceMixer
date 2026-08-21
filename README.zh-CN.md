# Audio Source Mixer

[English](README.md) · 版本 **1.0.0**

Audio Source Mixer 是面向 Windows 11 x64 的本地音频源混音器，在同一个 WPF 界面中分别控制 Windows Core Audio 会话，以及由用户主动启用的 Chrome/Edge 标签页。

## 主要功能

- Windows 应用音量、静音、左右声道平衡、实时电平和按应用输出路由。
- Chrome/Edge 增强标签页的 0–200% 增益、平衡、十段 EQ、实时电平和独立输出设备。
- 浏览器输出设备试听、确认、重新授权、修改和删除映射。
- 手动排序、拖拽动画、隐藏/恢复、配置记忆、自动应用、托盘和可选开机启动。
- 桌面程序、托盘、安装/卸载程序和扩展页面均可即时切换简体中文与 English。
- 全部处理留在本机：无分析、广告、远程代码或音频上传。

## 安装

从 GitHub Releases 下载并运行 `AudioSourceMixer-1.0.0-win-x64-setup.exe`。安装器按当前用户安装，无需管理员权限，默认目录为 `%LocalAppData%\Programs\AudioSourceMixer`；可选择桌面快捷方式、开机启动和安装后打开浏览器增强引导。

安装器会自动为 Chrome 和 Edge 注册 Native Messaging Host。本项目不再提供或支持独立便携版。

卸载时使用 Windows“已安装的应用”，或运行安装目录中的 `AudioSourceMixer.Uninstall.exe`。默认保留用户设置；只有明确勾选删除用户数据时才会移除。

## 浏览器增强

在程序内打开“浏览器增强”，进入 Chrome 或 Edge 的扩展管理页，启用开发者模式并选择“加载已解压的扩展程序”，然后选择程序显示的安装目录内 `BrowserExtension` 文件夹。目前尚未发布到浏览器商店。

在标签页开始播放后点击扩展图标；捕获必须由用户主动触发。首次选择非默认输出会打开可见授权页，需要试听并确认物理设备。

## 构建与测试

需要 Windows 11 x64、.NET 8 SDK、Node.js、Chrome 和 Edge。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
.\scripts\verify-installer.ps1 -BaselineInstallerPath .\artifacts\AudioSourceMixer-0.2.2-win-x64-setup.exe
```

`scripts/build-all.ps1` 执行发行门禁，最终只生成安装程序和机器可读验证清单。安装器载荷直接由全新的 Release publish 与严格运行时 allowlist 组装。

详见[使用指南](USER_GUIDE.zh-CN.md)、[测试说明](docs/testing.md)、[架构](docs/architecture.md)、[隐私说明](docs/privacy.zh-CN.md)和[更新记录](CHANGELOG.md)。

## 本地数据

设置、来源配置、音频恢复状态和日志位于 `%LocalAppData%\AudioSourceMixer`。浏览器输出映射和扩展语言设置使用当前浏览器 profile 的 `chrome.storage.local`；活动短期状态使用 `chrome.storage.session`。

## 已知限制

- 浏览器增强依赖 Chromium 的 `tabCapture`、offscreen Web Audio、`setSinkId` 和 Native Messaging。
- DRM/受保护内容、页面导航、休眠标签页、驱动行为及蓝牙重连仍需在真实硬件上人工确认。
- Windows 按应用路由可能要求重新创建音频流；请暂停/继续播放或重启对应应用。

许可证见 [LICENSE](LICENSE)。
