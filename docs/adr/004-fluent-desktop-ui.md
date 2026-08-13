# ADR 004：Fluent 桌面界面实现方式

- 状态：接受
- 日期：2026-08-14
- 适用版本：0.2.2

## 背景

桌面端继续使用 .NET 8 WPF、MVVM、self-contained x64 单文件发布、WinForms 托盘和当前显式退出/恢复流程。本次需要现代 Windows 11 风格、响应式来源卡、浏览器引导、像素滚动和可访问性，但不能为了换皮重写稳定的音频和窗口生命周期。

## Skills 调研

按任务要求使用 `npx skills find` 搜索了 `WPF modern Fluent UI`、`WPF accessibility`、`WPF visual regression testing` 和 `desktop onboarding wizard`。

| 候选 | skills.sh 安装量 | 来源与许可证 | 维护/匹配度 | 结论 |
|---|---:|---|---|---|
| `wshaddix/dotnet-skills@dotnet-wpf-modern` | 188 | `wshaddix/dotnet-skills`，README 声明 MIT；GitHub 71 stars | 仓库未归档，但技能安装量低于 1,000，且内容是通用模式 | 不安装，仅参考 |
| `jcurbelo/skills@wpf-best-practices` | 453 | GitHub 4 stars，仓库未提供可由 API 识别的许可证 | 来源和成熟度不足 | 不安装 |
| `aj-geddes/useful-ai-prompts@visual-regression-testing` | 642 | MIT，GitHub 315 stars | 通用 Web 视觉回归提示，不是 WPF 渲染工具 | 不安装 |
| `rampstackco/claude-skills@onboarding-wizard-design` | 146 | MIT，GitHub 538 stars，近期维护 | 面向网站/品牌流程，不处理 WPF 生命周期 | 不安装 |

没有候选同时满足 WPF 专用、成熟安装量、明确许可证和本项目测试需求，因此直接使用 WPF/.NET 官方能力实现，不把低成熟度 Skill 或其脚本加入仓库。

## UI 框架比较

| 方案 | 官方包/许可与维护 | .NET 8 与发布影响 | 产品匹配 | 决策 |
|---|---|---|---|---|
| [WPF UI](https://github.com/lepoco/wpfui) | `WPF-UI` 4.3.0，MIT，约 9.6k stars；2026-05 发布 | 提供 `net8.0-windows7.0`，依赖 `WPF-UI.Abstractions`；理论上可进入 single-file，但会增加程序集、字体和主题资源 | Fluent 最接近，但 Navigation/Window/Tray 与现有职责重叠，迁移面大 | 本轮不采用 |
| [MaterialDesignInXAML](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | 5.3.2，MIT，约 16.2k stars；持续维护 | 支持 `net8.0-windows7.0`，传递依赖 `MaterialDesignColors`、`Microsoft.Xaml.Behaviors.Wpf` | 成熟，但视觉语言是 Material，不是 Windows 11 Fluent | 不采用 |
| [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) | 2.4.11，MIT，约 9.8k stars | NuGet 元数据以 .NET Framework/.NET Core 3.x 为目标并依赖 ControlzEx；可兼容但不是当前项目的最小风险路径 | Metro 风格较重，响应式卡片和引导仍需自行实现 | 不采用 |
| 自定义 WPF ResourceDictionary | 仓库自有代码，无新增第三方许可 | 不增加 NuGet、程序集、字体或运行时文件；不改变 self-contained single-file、托盘和窗口关闭行为 | 可精确实现语义 token、卡片、导航、焦点、像素滚动和现有 MVVM | **采用** |

## 决定

采用 `Themes/Colors.xaml`、`Themes/Typography.xaml`、`Themes/Controls.xaml` 管理语义资源；将 Shell、混音器、来源卡、EQ、浏览器引导和设置页拆分。使用系统字体 `Segoe UI Variable, Segoe UI` 和本地矢量/产品图标，不下载运行时代码或字体。

使用 WPF 原生虚拟化与 `VirtualizingPanel.ScrollUnit="Pixel"`，不增加平滑滚动动画。这样滚动条拖动与内容同步，也天然尊重系统减少动画设置。所有状态文本提供文字含义，交互控件设置可访问名称和可见焦点。

## 后果与验证

- 优点：无新依赖、无第三方声明变化、启动和发布负载稳定；现有 MVVM、托盘、单文件和音频处理链保持不变。
- 代价：按钮、导航、卡片、焦点和响应式 EQ 由项目维护。
- 控制措施：WPF STA 测试验证三种窗口尺寸、长文本边界、像素滚动、Recycling、横向滚动禁用、键盘焦点、显式绑定方向；UI smoke 继续真实 `Show()` 并物化来源与 EQ 模板。
