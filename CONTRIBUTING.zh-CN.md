# 参与贡献

感谢帮助改进 Audio Source Mixer。参与即表示同意遵守[行为准则](CODE_OF_CONDUCT.md)。

## 提交 Issue 前

请先搜索已有 Issue 并阅读 [SUPPORT.md](SUPPORT.md)。分享诊断信息前必须移除个人数据；不要公开凭据、完整日志、原始 Windows endpoint/browser device ID、浏览器 profile、页面标题/网址或用户目录路径。

## 开发流程

1. Fork 仓库并创建范围明确的分支。
2. 使用 Windows 11 x64、`global.json` 指定的 .NET SDK 和受支持的 Node.js。
3. 运行 `dotnet restore AudioSourceMixer.sln`、`dotnet build AudioSourceMixer.sln -c Release`、`dotnet test AudioSourceMixer.sln -c Release --no-build`，并在 `src/AudioSourceMixer.BrowserExtension` 运行 `npm test`。
4. 运行 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit-repository.ps1`。
5. 在 PR 中说明行为变化、测试、隐私影响和硬件证据。

提交应保持小而专注，并保留音频回滚。不经明确设计与许可审计，不得加入驱动、远程代码、遥测或产品运行依赖。WPF UI 测试必须使用 STA 并捕获绑定错误；浏览器路由变更必须严格验证有效 sink，且不得改变 Windows 默认设备。

可以按 [AI_DEVELOPMENT.md](AI_DEVELOPMENT.md) 使用 AI 辅助，但贡献者仍对审查、许可和测试负责。

本项目采用 MIT License。提交贡献表示你有权提交，并同意按该许可证分发。
