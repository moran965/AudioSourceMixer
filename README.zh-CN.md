# Audio Source Mixer

[English](README.md) · 版本 **1.0.0** · Windows 11 x64

Audio Source Mixer 是一款开源 Windows 音频工具，可分别控制应用程序以及由用户主动启用的浏览器标签页的音量、平衡、EQ、电平和输出路由。

> 本项目采用 vibe coding（氛围编程）与迭代式 AI 辅助开发构建；产品需求、功能取舍、实际使用验收和真实硬件测试由人工维护者主导。AI 辅助不能替代代码审查、测试或维护者责任，详见 [AI 开发说明](AI_DEVELOPMENT.md)。

![Audio Source Mixer 界面](assets/generated/AudioSourceMixer-page.png)

## 主要功能

- Windows 应用音量、静音、左右声道平衡、实时电平和按应用输出路由。
- Chrome/Edge 增强标签页的 0–200% 增益、平衡、十段 EQ、实时电平和独立输出设备。
- 明确的输出授权：所选物理设备必须通过低音量试听和严格有效 sink 验证，才能保存映射。
- 手动排序、拖拽、隐藏/恢复、配置记忆、托盘和可选开机启动。
- 程序、托盘、安装/卸载器和扩展页面均可即时切换简体中文与 English。
- 全部处理留在本机：无分析、广告、远程代码或音频上传。

## 安全安装

只从本仓库的 [GitHub Releases](../../releases) 下载 `AudioSourceMixer-1.0.0-win-x64-setup.exe`，不要使用第三方网站重新打包的安装程序。安装器按当前用户安装，无需管理员权限，默认目录为 `%LocalAppData%\Programs\AudioSourceMixer`。

首次 v1.0.0 安装程序采用透明披露的未签名回退方案。SignPath Foundation 免费可信签名申请已于 2026-08-27 提交并等待人工审核，尚未批准用于此二进制文件。Windows 可能显示“未知发布者”或 SmartScreen 提示。请只从本仓库 Release 页面下载，运行前核对 `SHA256SUMS.txt` 并验证 GitHub Artifact Attestation。GitHub 来源证明只能关联源码工作流与提交，不是 Authenticode，也不会创建 Windows 发布者身份。

SignPath 角色、构建来源、审批原则和首次未签名回退详见中英文[代码签名政策](CODE_SIGNING_POLICY.md)。

本项目不提供便携版。GitHub 自动生成的 “Source code (zip)” 和 “Source code (tar.gz)” 是源码归档，不是可运行的便携程序。

## 浏览器增强与输出授权

在程序中打开“浏览器增强”，然后在 Chrome 或 Edge 中把安装目录内的 `BrowserExtension` 加载为已解压扩展。目前扩展尚未上架浏览器商店。

标签页开始播放后点击扩展图标；捕获始终由用户主动触发。选择非默认输出会打开可见授权页。只有目标设备仍在枚举列表中、浏览器回读的有效 `sinkId` 与请求严格一致、试听完成且确认的是同一个候选时，才会保存映射；设备变化会让旧测试结果失效。详见[使用指南](USER_GUIDE.zh-CN.md)和[浏览器限制](docs/browser-tab-limitations.md)。

## 语言

在“设置 → 语言”中选择“简体中文”或 “English”，界面会立即切换并记忆。1.0 以前且没有语言设置的用户默认使用简体中文。

## 隐私与本地数据

音频只在本机 Windows/Chromium 音频图中处理，不录制、不上传。设置、来源配置、恢复状态和日志位于 `%LocalAppData%\AudioSourceMixer`；浏览器映射保存在当前浏览器 profile。详见[隐私说明](docs/privacy.zh-CN.md)。

## 验证下载

将安装程序哈希与同一 Release 中的 `SHA256SUMS.txt` 比较：

```powershell
Get-FileHash .\AudioSourceMixer-1.0.0-win-x64-setup.exe -Algorithm SHA256
```

验证 Authenticode 状态、发布者和时间戳：

```powershell
Get-AuthenticodeSignature .\AudioSourceMixer-1.0.0-win-x64-setup.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

首次 v1.0.0 未签名回退版本的预期 Authenticode 状态是 `NotSigned`，Release 说明必须与之完全一致。未来的可信签名版本会列出真实签名者和时间戳；即使签名有效，新发布者仍可能需要逐步积累 SmartScreen 信誉。

## 构建与测试

需要 Windows 11 x64、`global.json` 指定的 .NET 8 SDK、Node.js、Chrome 和 Edge。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
```

最终发行门禁还包括中英文 WPF smoke、Chrome/Edge 真实运行、物理输出设备、安装/修复/升级/卸载矩阵、仓库审计、秘密扫描、签名验证、SBOM 和来源证明。详见[构建](docs/building.md)、[测试](docs/testing.md)与[发行](docs/releasing.md)。

## 已知限制

- 浏览器增强依赖 Chromium 的 `tabCapture`、offscreen Web Audio、`setSinkId` 和 Native Messaging。
- DRM 内容、页面导航、浏览器休眠、驱动、蓝牙重连和物理端点路由必须在目标硬件上人工验收。
- Windows 按应用路由可能要求来源程序重建音频流。
- 当前需要手动加载已解压扩展。

## 贡献、支持与安全

提交 PR 前请阅读 [CONTRIBUTING.zh-CN.md](CONTRIBUTING.zh-CN.md)。非敏感问题按 [SUPPORT.md](SUPPORT.md) 使用 GitHub Issues。不要公开发布秘密、完整日志、设备 ID、浏览器 profile、网址/页面标题或用户目录路径。安全漏洞请按 [SECURITY.md](SECURITY.md) 私下报告。

项目采用 [MIT License](LICENSE)，第三方声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
