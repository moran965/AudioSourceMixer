# Skills 搜索与采用记录

## 实际命令格式

先读取本机 `find-skills` 的 `SKILL.md`，再运行 `npx.cmd skills --help`。实际搜索命令为 `npx skills find [query] [--owner <owner>]`，没有猜测额外参数。

## 搜索词

- `Windows Core Audio WASAPI audio sessions COM Interop`
- `WPF WinUI Windows system tray desktop UI testing`
- `Chromium Manifest V3 tabCapture offscreen Native Messaging`
- `Windows installer .NET automated testing`

搜索覆盖了任务列出的 Core Audio、WASAPI、Windows 会话、COM、WPF/WinUI、托盘、MV3、tabCapture、offscreen、Native Messaging、安装器、.NET 测试和桌面 UI 测试主题。

## 找到的相关候选

- `googlechrome/modern-web-guidance@chrome-extensions`（搜索时约 2.5K installs）。
- `affaan-m/everything-claude-code@windows-desktop-e2e`（约 2.4K）。
- `novotnyllc/dotnet-artisan@dotnet-ui`（约 182）。
- `404kidwiz/claude-supercode-skills@windows-app-developer`（约 141）。
- `wshaddix/dotnet-skills` 的 WinForms/WPF/UI/CI 候选（约 59–213）。
- Core Audio 查询返回 Godot/Expo/游戏音频等不相关候选，没有专门覆盖 Windows Core Audio/WASAPI 的可信结果。

同时检查了 skills.sh leaderboard；榜单没有直接覆盖本任务的 Windows Core Audio/WASAPI 细分领域。

## 实际采用

采用并复制到项目的只有 `googlechrome/modern-web-guidance@chrome-extensions`。该仓库属于 GoogleChrome，README 说明由 Chrome、Edge 团队和 Web 社区支持。完整阅读了 `SKILL.md`，并在编码前阅读：

- `references/extensions/media-capture.md`
- `references/extensions/service-worker.md`
- `references/extensions/message-passing.md`
- `references/extensions/storage.md`

它影响的实现包括：始终使用 MV3；service worker 状态存入 `chrome.storage.session`；tabCapture 使用逐标签页状态锁；offscreen document 只调用 `chrome.runtime` 与 Web API；所有异步代码使用 async/await；不引用不存在的图标；权限保持最小化。

## 排除项与原因

- Windows Core Audio 查询结果主要是游戏引擎或移动音频，领域不匹配。
- 非官方 WPF/Windows/E2E 候选安装量低、来源可信度不足，且没有 Core Audio 专项价值；引入会扩大供应链和流程风险。
- Auth0 WPF/WinForms 技能只处理身份验证，而产品明确不需要账户或登录。
- Electron 候选与固定“禁止 Electron”约束冲突。
- 没有找到可信的 Windows 安装器专用 skill；继续使用官方 API、.NET SDK 和本地工程能力。
