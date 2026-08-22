# Changelog

## 1.0.0 - 2026-08-22

- Fixed browser output authorization so a test tone is created only after a concrete, currently enumerated physical device is selected and the effective `sinkId` is read back exactly before and after resume. Test results are now bound to the browser, Windows endpoint, browser device, and candidate/device-list generations; untested or stale candidates cannot be saved.
- Fixed the global WPF ComboBox template to honor `SelectionBoxItem`, `DisplayMemberPath`, and custom item templates, so the current application language is visible when the list is closed. Added real WPF coverage for language, output-device, EQ-preset, string, and custom-template selectors.
- Added complete Simplified Chinese and English localization for WPF views, dynamic status text, accessibility names, tray UI, installer, uninstaller, onboarding, and the Manifest V3 extension. Language changes apply immediately without rebuilding audio services or changing active source state.
- Added settings schema 8 with persistent `zh-CN` / `en-US` selection. Existing users without a language setting remain on Simplified Chinese; fresh installs inherit the installer language only when no settings file exists.
- Localized the installer and uninstaller, including a bilingual first page, saved uninstall language, and `--language zh-CN|en-US` for silent workflows.
- Internationalized the extension with matching `_locales/zh_CN` and `_locales/en` resources, visible per-profile language selection, stable protocol status/error codes, and no new permissions or remote code.
- Removed the standalone portable distribution, portable registration scripts, and user-facing portable instructions. Installer payloads now come directly from fresh self-contained Release publishes and a strict allowlist.
- Removed obsolete agent-only assets and the unused ProcessLoopbackProbe experiment while retaining active regression tests, audio fixtures, CapabilityProbe live-meter coverage, browser runtime probes, and architecture records.
- Unified product, file, extension, installer, uninstall registration, documentation, and artifact versions at 1.0.0 / 1.0.0.0.
- Expanded release gates for bilingual resource parity, hard-coded UI text audits, runtime language switching, localized extension pages, bilingual install/uninstall, 0.2.2 settings-preserving upgrade, maximum-window screenshots, and publish → installer payload → installed hash equality.
- Corrected normal-startup UI materialization waiting, removed an English automation-name ambiguity in the hidden-source popup, and made the final installation gate exercise the mixer separately from intentional first-run browser onboarding.

## 0.2.2 - 2026-08-21

- 拖拽改为一次捕获的完整卡片 `Adorner`，由渲染帧驱动位置、平滑边缘滚动和中线滞回；预览顺序实时执行 `ObservableCollection.Move`，相邻已物化卡片使用 150ms FLIP 让位，Drop 仅保存一次，Esc/失焦/异常恢复原顺序并清理全部临时视觉。
- “已隐藏 N”与恢复弹窗现在只表示手动隐藏；“全部恢复显示”不再暂停浏览器聚合自动隐藏，也不再写入浏览器连接状态。设置 schema 升至 7 并清理旧 `VisibleBrowserAggregates` 例外，产品与文件版本保持 0.2.2/0.2.2.0。
- 修复恢复来源后 `Popup` 仍锚定已隐藏或移动按钮所产生的大型空白浮框：恢复前显式关闭，页面切换、拖拽、窗口移动/失焦/关闭和 PlacementTarget 变化时统一清理；顶部工具区改为固定 Grid 列，状态文字与隐藏计数变化不再推动按钮。
- 新增拖拽状态机、真实 WPF Adorner/FLIP/清理、手动隐藏语义、Popup HWND 生命周期和顶部坐标回归；系统鼠标交互脚本连续验证完整预览、实时让位、边缘滚动、Drop、Esc、单项恢复与全部恢复并保存截图。
- “打开 Edge/Chrome 扩展页”现严格使用对应浏览器和 `edge://extensions/` / `chrome://extensions/`；移除商店 URL 与 `--new-tab` 混用，并为 Chromium 151 冷启动丢弃内部 URL 的情况增加真实 omnibox 导航与结果验证。
- 完全删除“最近调整”自动置顶及 300ms 定时重排；来源始终保持手动顺序，旧 `recent` 设置原子迁移到 schema 6，新来源稳定追加，浏览器临时 Tab ID 不跨重启持久化。
- 拖动只从六点把手启动，按可见卡片中线计算插入位置，支持首尾、卡片间隙、不等高/EQ 卡片、虚拟化和边缘自动滚动；插入线使用 120ms 淡入，Drop 最多保存一次。
- 来源容器从可选择 `ListBox` 改为非选择型虚拟化 `ItemsControl`，卡片间隙和列表空白不再选择、BringIntoView 或滚动；省略号菜单改为主题化 Flyout，补齐置顶、上移、下移、置底和隐藏。
- 普通 Windows 会话增加友好名称解析和异步冻结图标缓存；浏览器增强名称显示 `[Edge]/[Chrome]`、页面标题和域名。
- 默认窗口调整为 1240×820 并受工作区约束；最大化只增加列表 viewport，不使用整体 ScaleTransform，展开 EQ 会滚动到可见区域。
- Edge/Chrome 聚合会话仍可由设置页统一自动隐藏；自动过滤不计入手动隐藏列表，也不会被“全部恢复显示”改写。
- 浏览器“系统默认”改为 `FollowSystemDefault + 当前 Windows Multimedia 物理端点 + 已授权 browser deviceId + 实际 sink` 四层状态；默认设备变化会重绑定，缺少映射进入 `PendingAuthorization`，实际 sink 不一致进入失败，绝不静默使用空 sink。
- 修复 offscreen 在物理目标变化时复用旧 browser deviceId，以及来源消失后延迟控制任务形成未观察异步异常的竞态；扩展仍为 Manifest V3，权限未增加，产品/文件/安装器版本仍为 0.2.2/0.2.2.0。

- 修复全局 ProgressBar 模板缺少 `PART_Track` 导致 Peak 值变化但指示条宽度不变；UI smoke 现直接测量 0/50/100% 与动态 Peak 的 Track/Indicator 实际宽度。
- Windows Core Audio 拆分为约 1 秒拓扑刷新和 75ms 轻量电平刷新，浏览器增强改为每标签页独立 100ms 电平；两者均快速上升、约 350ms 衰减并归零，峰值更新不再触发完整 Reconcile、配置重载或路由应用。
- 新增来源展示管线、精确单会话隐藏/恢复、Edge/Chrome 增强时的聚合会话自动隐藏，以及“最近调整/手动排序”；现有 ViewModel 通过 `ObservableCollection.Move` 复用，滑块操作延迟约 300ms 后才重排。
- 页内 Logo 改用从唯一 SVG 设计源生成的 512×512 透明 PNG；ICO 保留给系统用途并补齐 16/20/24/32/40/48/64/96/128/256 帧，浏览器图标仍为精确尺寸 PNG。
- 设置 schema 升至 5（产品版本仍为 0.2.2），新增排序、手动隐藏和浏览器聚合隐藏开关；仅持久化安全的 Windows 精确身份，限制容量并清理长期失效记录。
- 发行测试新增受控真实 WASAPI 会话的逐样本 UI 电平报告，并要求原始 Peak、平滑 Peak、ProgressBar Value、Track/Indicator 可见宽度及停播归零全部成立。
- 桌面端中文字体统一为 `Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Global User Interface` 回退链，窗口、控件、提示及托盘菜单共享一致字体语义；正文保持 Regular，标题和强调层级统一为 Bold，并增加 UTF-8、乱码、替换字符及关键中文字形审计。
- 设置型 CheckBox 改为无对勾的纯实心主色选中态，禁用选中态仍可辨识；模板使用 Auto 列和左对齐，使命中/焦点范围仅覆盖 16 DIP 方块、8 DIP 间距及实际标签文字，不再横跨卡片空白。
- WPF 回归新增真实 SettingsView/EQ 模板、Automation Toggle、Space 键、选中/禁用颜色、焦点模板、不同标签宽度和右侧空白 hit-test；UI screenshot 覆盖 880×600、1180×760、1600×900 及 100/125/150/200 DPI 渲染。
- 修复输出授权页在 `storage.onChanged` 与确认事务交错时清空全局候选、随后读取空对象的竞态；确认过程改用 await 前不可变快照、单飞事务门、合并刷新和精确 waiter 删除。
- 映射保存与标签通知分离：通知失败不回滚已保存映射，页面提供幂等重试；所有页面和 service worker 异步事件入口统一捕获，不再产生 `Uncaught (in promise)`。
- 新增 13 组授权并发执行测试，并在隔离临时 profile 中使用 Chrome/Edge CDP 实际执行添加、修改、删除和重复授权；页面、service worker、日志与未处理 rejection 均为零错误。
- 桌面端重构为自有 Fluent ResourceDictionary、三页现代导航、响应式来源卡和折叠式十段 EQ；没有新增第三方 UI 运行依赖。高对比、键盘焦点、语义色和长文本省略均纳入验证。
- 来源列表保留 Recycling 虚拟化并启用原生像素滚动；音量、平衡和 EQ 使用 wheel-safe 滑块，未聚焦时滚轮交给外层列表。
- 新增桌面与扩展首次使用引导、安装器可选引导项、Chrome/Edge 分离步骤、Native Messaging 连接状态、严格受信扩展 ID 配置和本地隐私说明；更新/reload 不反复打开欢迎页。
- UI smoke 使用三个确定性来源覆盖普通会话、长中英文浏览器标题、200% 音量、非默认设备、授权失败和 EQ，并生成六张真实 WPF 截图；STA 测试验证 880×600、1180×760、1600×900 布局及全部显式绑定模式。
- 发布门禁扩展为 Chrome/Edge 授权运行验证、引导文件可达性、默认静默安装不启动向导、显式 `--browser-setup` 启动、0.2.1 原位升级、portable/installed 清单和三份核心 EXE 哈希一致。

## 0.2.1 - 2026-08-13

- 在 `a094044` 建立的 v0.2.0 Git 基线上完成六个可独立回退的清理、负载、UI、EQ、测试和发布提交。
- 合并仍有效的研究结论后删除旧实施记录与废弃实验残留；`ProcessLoopbackProbe` 改为仅显式 `-IncludeProbes` 构建，产品负载不含探针。
- 精简来源卡片、顶部状态和设置说明：正常状态保持安静，异常状态给出声音是否可用和下一步操作；长来源列表使用 recycling 虚拟化。
- 新增原创混音滑杆产品图标并统一用于 EXE、窗口、托盘、安装器、卸载器和 Chrome/Edge 扩展。
- 发行流程改为机器可读 allowlist 精确组装；portable 与安装目录不再包含开发 docs、tests、tools、package.json、diagnostics、PDB、源码或构建机绝对路径。
- 浏览器增强来源新增真实 10 段 Web Audio EQ：固定 BiquadFilter 链、独立 headroom/preamp、平滑参数更新、关闭时 flat/bypass、多标签隔离、设备切换保留和完整节点清理。
- 浏览器 Native Messaging 协议升级到 3；协议 2 扩展继续支持 200% 增益、平衡和输出路由，仅隐藏 EQ。profile 升级到 schema 3，旧浏览器来源迁移为 EQ 关闭。
- 普通 Windows 会话继续使用原生音量、静音、平衡和 AudioPolicyConfig 路由，不重新引入捕获重放、虚拟设备或伪逐应用 EQ。
- 新增 EQ 参数/预设/非法输入/generation/快速拖动/恢复测试，WPF smoke 展开并物化 10 段模板，安装验证严格比较 installed allowlist 与 portable/installed 哈希。
- 验证 v0.2.0→v0.2.1 原位升级、fresh install、同版本 repair、自定义路径、运行中卸载和注册表清理。

## 0.2.0 - 2026-08-13

- 浏览器输出授权改为三步可见向导：候选映射不会自动保存，支持低音量试听、明确确认、明显名称不匹配的二次确认，以及测试、修改、删除和当前浏览器清空。
- 输出映射升级到 schema 3；保留 0.1.2 数据并标记为未验证，继续只允许唯一 groupId+label 或唯一 label 的安全重绑定。
- 修复浏览器/扩展空闲启动桌面程序：service worker 无活动图时不连接 Native Host；Native Host 管道超时只报告状态并退出，不再启动桌面 UI。
- 新增“混音器 / 设置”独立页面、Chrome/Edge 连接与授权管理、实际 HKCU Run 状态驱动的可控开机启动和 `--background` 托盘模式。
- 平衡滑块增加中心迟滞吸附与“左 / 居中 / 右”标识，不改变原有增益换算或程序化更新。
- 安装器支持选择安装位置、含空格/中文路径、安全路径验证、原位 repair/升级回滚和启动选项；默认不开机启动。
- 修复 installed uninstaller 无参数误进安装页；新增专用卸载界面、运行中优雅恢复退出、产品身份交叉验证和安全自删除日志。
- 扩展 WPF UI smoke 到设置页物化与绑定审计，并新增授权、映射迁移、空闲生命周期、Native Host 超时、中心吸附、路径安全和卸载身份回归测试。

## 0.1.2 - 2026-08-09

### Added

- 应用级 `ApplicationRouteCoordinator`：以可执行文件、PID、进程启动时间隔离事务，提供 generation/cancellation、同目标幂等、last-write-wins 和 sibling session 共享状态。
- 基于 EarTrumpet 当前源码核对的隔离 AudioPolicyConfig current/downlevel ABI；三 role 事务设置、原值回滚和目标 EndpointContext 真实会话迁移验证。
- Chrome/Edge 增强标签页支持最高 200% 的逐源 Web Audio 增益，Node 回归测试覆盖实际 offscreen 图的 `GainNode`、sink 精确读回和资源关闭。
- 每个音频源显示请求、持久策略和全部活动流观察结果，以及 `SystemDefault`、`PendingStreamRestart`、`Partial`、`Applied`、`Disconnected`、`Failed` 状态。
- 活动 render endpoint 枚举、默认角色/mix format 信息、`IMMNotificationClient` 热插拔更新。
- 配置 schemaVersion 2，以及 v0.1.1 `volume: 0.0–1.0` 到相同百分比语义的自动迁移。
- v0.1.1→v0.1.2 原位升级、旧文件排除、扩展/Native Host 注册修复和用户数据保全验证。
- 无第三方依赖的 x64 Release `ProcessLoopbackProbe` 与六组矩阵脚本，记录 PID/tree、OS build、flags/options、HRESULT、PCM 格式、五秒 RMS/peak/非静音帧及逐项恢复。
- Chrome/Edge 可见输出授权页自动消费桌面端 endpoint ID/name 请求；映射保存 endpoint ID、browser deviceId/label/groupId，兼容麦克风 track 立即停止。

### Changed

- 浏览器 Native Messaging 协议升级到 2，同时保留协议 1 的明确兼容模式；协议 1 不会静默接受增强增益或输出路由。
- 安装器改为同级 staging/backup 目录的原子目录替换，只嵌入本次 Release portable payload。
- 普通 Windows 会话固定为原生 0–100%；浏览器增强标签页独立保留 0–200% `GainNode`。旧普通来源配置中大于 1 的值会截断为 1 并原子改写，带浏览器来源类型的配置可保留到 2。
- Windows 会话发现改为每个 active render endpoint 一个 `EndpointContext`，全局合并来源并对设备拓扑事件作 300 ms 防抖；来源身份包含 endpoint，配置身份跨 endpoint 保持稳定。
- 全量构建不再递归删除工作区 `bin/obj` 或整个 `artifacts`，保留未跟踪用户内容与诊断证据，仅替换明确的版本化生成目标。

### Fixed

- 浏览器 generation 改为 JavaScript 安全整数范围内的逐源单调序列；PendingAuthorization 在打开授权 UI 前先携带原 correlation/generation 回 ACK，避免桌面误超时。
- 浏览器授权、持久映射和失效重绑定排除 Chromium 虚拟 `default`/`communications`，只允许具体物理输出；`setSinkId()` 的成功读回不再可能被系统默认设备切换伪装。
- 相同 origin 的多个已捕获标签页只在首次发现时读取保存 profile，运行中的 sibling 不再被后来保存的来源配置覆盖。
- 托盘回调统一切回 WPF Dispatcher；双击恢复窗口和“恢复全部”不再从 WinForms 线程触碰 WPF 对象，异常退出清理也保持确定性。
- 修复用户选择输出设备后 Windows 音频会话迁移时旧 `AudioSourceViewModel.Dispose()` 取消路由、短暂零会话清除 profile-apply 标记，继而由 `ProfileRestore` 把目标覆盖回旧设备的竞态。路由意图和取消令牌现归稳定应用实例所有，用户意图先记录，后端读回（包括 `PendingStreamRestart`）后再保存，并按 User > DeviceReconnect > ProfileRestore 仲裁。
- 输出设备集合改为按 endpoint ID 增量同步；ComboBox 使用稳定 `SelectedValue`，程序化刷新不再触发命令，只有实际项目点击或 Enter 才提交。
- 浏览器授权从单一请求槽改为 browser+Windows endpoint 队列，每项保留全部标签页 waiter；失效 deviceId 支持无歧义重绑定，桌面提供显式重新授权。移除 manifest 中无效的 `audioCapture` 权限。
- 浏览器图、generation 和串行队列按 browser+tab 隔离；service worker 重启会与 offscreen/现存 capture 对账并重注册 Native Host。桌面桥等待匹配 correlation+generation 的 Pending/Applied/Failed ACK，忽略过期响应并对无响应命令超时失败。
- 设置在窗口绑定前载入，变更按发生顺序串行原子保存并在退出前等待最新写入；关闭到托盘、恢复窗口或恢复并退出的决策写入日志。
- Native bridge 关闭时先断开底层流并等待连接处理任务，避免测试或退出时挂起。
- 浏览器输出设备改为 endpoint-ID 主键；`setSinkId()` resolve 后还必须验证非空且匹配的 `context.sinkId`，失效时要求重新授权，不静默回退默认。
- 修复路由策略写入成功却因现有流未在超时内迁移而被回滚的问题；现在保留持久策略并明确提示流重启，同时后台继续观察。
- 修复同进程多会话只检查第一个目标会话、设备刷新重复写策略、迁移后新 `AudioSourceId` 重复应用配置、ComboBox 程序化刷新触发路由的问题。
- 单项恢复和全部恢复现在会先取消并等待防抖保存/路由命令，再恢复运行时、删除对应配置或全部配置并清理内存应用状态，防止延迟保存复活已删除配置。
- 安装验证器新增 0.1.2→0.1.2 修复模式，以原子替换哨兵、版本/注册表、真实 UI、三份哈希和卸载清理验证旧安装可被确定性替换；测试回滚日志与用户原有离线端点恢复项隔离，结束后按哈希恢复原数据。
- 安装/卸载前只终止可执行路径精确位于产品安装目录的 Native Host，修复浏览器持有 bridge 文件时同版本 repair 无法原子替换目录的问题。

### Removed

- 移除普通 Windows 来源的 100–200% UI、`SetUserGainAsync`、进程增强状态/回滚字段、`ProcessBoostPipelineManager`、`ProcessBoostHost` 构建与安装 payload；`ProcessLoopbackProbe` 仅保留为开发探针。
- 经审计确认无引用的 v0.1.0 便携包、安装包和坏版展开目录；保留版本历史、迁移和协议兼容代码。

## 0.1.1 - 2026-08-03

- 修复 `ProgressBar.Value` 默认 TwoWay 绑定到只读 `PeakPercent` 导致的启动 `XamlParseException`；全部纯显示绑定改为显式 OneWay。
- 新增真实窗口 UI smoke：固定测试源、`Show()`、Loaded/ApplicationIdle、ItemsControl 容器、DataTemplate、峰值动态更新和 WPF 绑定错误检测。
- 新增 STA WPF 回归测试，审计全部 26 个 XAML 绑定，并验证音量/平衡 TwoWay 控制仍可写入控制器。
- 启动异常按日志、浏览器桥、UI、托盘、音频和首次布局分阶段处理；完整记录异常堆栈并独立清理所有资源。
- 打包流程始终从当前 Release 源码重建 portable；增加 publish/portable/installed SHA-256 一致性和安装/卸载闭环验证。
- 将程序集、扩展、安装器卸载项和产物版本统一为 0.1.1。

## 0.1.0 - 2026-08-03

- 首个 Windows 11 x64 MVP。
- Core Audio 会话枚举、独立音量/静音/双声道平衡、峰值、通知与恢复。
- WPF UI、系统托盘、偏好和异常恢复日志。
- Chrome/Edge MV3 标签页增强、Native Messaging 和 Named Pipe bridge。
- 自动化测试、便携包和每用户安装器。
