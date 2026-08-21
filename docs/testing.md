# Audio Source Mixer 0.2.2 测试报告

当前验证日期：2026-08-21。环境：Windows 11 x64（build 26200）、.NET SDK 8.0.423、Node 24.18.1、Google Chrome 151、Microsoft Edge 151。

## 2026-08-21 拖拽动画与隐藏来源弹窗补丁

拖拽回归不再只检查最终集合索引。`SourceDragPreviewCoordinator` 单测覆盖上下中线、8 DIP 滞回、相同目标不重复 Move、首尾、不等高/EQ 状态、自动滚动后的重新定位、单次提交、Esc 回滚、来源中途消失及 ViewModel/音频属性保持；STA WPF 测试真实创建 `SessionDragAdorner`，核对预览尺寸、原卡片占位、FLIP Transform、150ms 后归零、插入线只淡入一次以及提交/取消后的完整清理。

真实桌面交互由以下命令使用系统鼠标和键盘执行，不以直接调用集合方法代替拖放：

```powershell
.\scripts\verify-ui-interactions.ps1 -Executable <待验证的 AudioSourceMixer.exe> -OutputDirectory <截图目录>
```

脚本创建三个确定性来源并真实 `Show()` 窗口，依次记录拖动开始、相邻卡片实时让位、Drop、边缘自动滚动、Esc 清理、两个手动隐藏来源的弹窗、单项恢复关闭和全部恢复关闭，共 8 张连续截图；同时要求日志出现一次发生变化的提交和一次取消。交互模式使用按进程唯一的退出事件，结束时走产品自己的音频恢复/清理路径，不遗留诊断进程。

隐藏来源的回归把手动隐藏与浏览器聚合自动过滤完全分开：“已隐藏 N”和 Popup 只包含手动隐藏，单项/全部恢复先关闭 Popup 且不改变音量、平衡、静音、路由、EQ、`HideBrowserAggregateSessions` 或 `BrowserStatus`。WPF 测试还直接检查 Popup 内容与 HWND 均不可见、没有额外 `Window`，以及排序按钮、隐藏按钮、浏览器状态和列表顶部在恢复前后及状态文字变长后坐标变化不超过 1 DIP。旧 schema 6 中的 `VisibleBrowserAggregates` 在 schema 7 迁移时安全清空；产品版本仍为 0.2.2。

本补丁最终结果：Debug/Release 均为 11 个项目、0 警告、0 错误；两种配置的 .NET 均为 141/141 通过（Core 90、WindowsAudio 15、Installer 8、Desktop/WPF 26、NativeHost 2），Node 为 42/42。源码 Release、portable 与 installed UI smoke 均退出 0；Debug、源码 Release、portable 和 installed 的真实鼠标交互均退出 0，最终安装版 8 张截图位于 `artifacts\ui-interaction-v0.2.2-installed`。Chrome/Edge Web Audio EQ 与 Native Messaging/授权运行验证全部通过，两种浏览器各 4 次授权操作且 runtime exception、错误日志、未处理 rejection、service worker error 均为 0。

安装矩阵的 18 项全部通过，包括 fresh install、同版本 repair、故障回滚、空格/中文路径、0.2.1 原位升级、普通可见启动、installed UI smoke、真实实时电平、运行中优雅卸载、静默卸载、注册表清理以及完整 payload 比较。最终实时电平报告为 74 个样本，最大 Raw/UI Peak `0.36621094`，最大 Indicator `49.3333 DIP`，停播后 Raw/UI/Indicator 全部回到 0。验证矩阵清理后使用最终安装器重新安装到 `%LocalAppData%\Programs\AudioSourceMixer`，文件/产品版本为 0.2.2.0/0.2.2，开机启动保持关闭。

最终产物与哈希：

- publish / portable / installed `AudioSourceMixer.exe`：162,266,804 字节，SHA-256 `A3934AC025C6A6367CA831F4D5DC51FEA360E919AA60350DFB821C309B8364A4`（三者完全一致）。
- portable ZIP：95,547,592 字节，SHA-256 `F620F5622BC72F9A666D2E75A06862859ABA7AE0942E311EE5C6868A73714A2E`。
- installer：257,240,673 字节，SHA-256 `1E0F0A2A4DA11875ADAA2D0915D12ABC0ADFCEA03276ABC1FE5DB560C126E6D2`。

## 2026-08-21 手动排序与浏览器默认路由修复

最终命令 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-all.ps1` 退出码为 0。Release restore/build 共 11 个项目，0 警告、0 错误；.NET 134/134 通过（Core 89、WindowsAudio 15、Installer 8、Desktop/WPF 20、NativeHost 2），浏览器扩展 Node 42/42 通过。Chrome/Edge Web Audio 引擎的 100%→50% RMS 比均为 0.5、左声道测试右侧泄漏为 0；两种浏览器各完成 4 次隔离授权操作，runtime exception、错误日志、未处理 rejection 和 service worker error 均为 0。

新的 WPF 回归与 UI smoke 覆盖以下行为：

- 来源列表是非选择型虚拟化 `ItemsControl`；真实 `MainWindow.Show()` 后至少一个 `ContentPresenter`、来源卡和 DataTemplate 必须物化，卡片间隙 hit-test 不得命中按钮或产生选择语义。
- 音量、平衡、静音、输出、EQ、实时电平和超过旧 300ms 延迟后的顺序均保持不变；旧 `recent` 设置迁移为 manual。
- 拖放索引计算覆盖首项前、卡片上/下半部、不等高卡片、卡片间隙、虚拟化空洞和列表末尾。一次 Drop 只在最终 `ObservableCollection.Move` 后保存一次；当前可见插入线使用 120ms 淡入。
- XAML 绑定审计仍要求全部 `{Binding}` 显式声明模式；getter-only 显示属性均为 OneWay，TwoWay 仅允许有 setter 的音量、平衡、EQ 与设置属性。
- `FollowSystemDefault`、解析的 Windows endpoint、browser deviceId 和实际 sink 分开验证；缺少默认设备映射为 `PendingAuthorization`，目标改变会清除旧映射，实际 sink 不一致为失败。
- 自动隐藏的 Edge/Chrome 聚合会话不进入手动隐藏列表；单项恢复与“恢复全部来源”不改变音频参数或自动隐藏开关，旧强制显示例外迁移后被清空。

响应式测试实际记录（字体 12 DIP，全部 `ScaleTransform=False`）：

| 窗口 | ItemsControl ActualHeight | ScrollViewer ViewportHeight | 同时可见卡片数 |
|---|---:|---:|---:|
| 880×600 | 1134.0 | 314.7 | 1 |
| 1240×820（默认） | 1116.0 | 534.7 | 2 |
| 1600×900 | 1116.0 | 614.7 | 3 |
| 1920×1080 | 1116.0 | 794.7 | 3 |

1920×1080 的 viewport 比默认窗口增加 260 DIP，并实际显示更多卡片/展开内容，没有按比例放大字体、按钮、图标或滑块。最终 portable 生成 12 张真实 WPF 图，位于 `artifacts\screenshots-v0.2.2-manual-order-final-20260821`，覆盖 880×600、1240×820、1600×900、1920×1080 与 100/125/150/200% 渲染；逐图检查未发现裁切、重叠、乱码或整体缩放，展开 EQ 底部可通过同一垂直列表滚动到可见。

浏览器管理页使用最终 portable 的真实 WPF 按钮触发，并通过 UI Automation 读取真实 Chromium omnibox；机器可读报告为 `artifacts\browser-management-pages-final.json`：Chrome 测试前完全关闭，第一次冷启动和第二次已打开状态均得到 `chrome://extensions`；Edge 已打开状态连续两次均得到 `edge://extensions`。源码传入地址包含尾斜杠，Chromium 地址栏按自身规则规范化隐藏尾斜杠。Chrome/Edge 按钮不再包含 `--new-tab` 或商店地址；Chromium 151 冷启动若丢弃内部 URL，桌面程序在用户这次明确点击后把同一地址写入对应浏览器真实 omnibox、提交并验证。

安装矩阵全部通过：fresh install、同版本 repair、注入失败回滚、空格/中文路径、显式浏览器引导、开机启动/后台托盘、0.2.1→0.2.2 原位升级、安装版 UI smoke、普通可见启动、受控 WaveOut 实时电平、运行中卸载、静默卸载与注册表清理。普通启动得到 `WindowShown=True; Sources=1; MaterializedItems=1`；实时电平共 74 个样本，最大原始/平滑 Peak 为 `0.3662`、Indicator 最大 `49.33 DIP`，停止后回到 0。最终构建随后重新安装到 `%LocalAppData%\Programs\AudioSourceMixer`，再次通过 UI smoke，文件版本 `0.2.2.0`、产品版本 `0.2.2`。

该轮基线产物（已由本页顶部的拖拽/Popup 补丁产物替换）：

- publish / portable / installed `AudioSourceMixer.exe`：162,246,324 字节，SHA-256 `5A2A698A950203DB35B2D4458038BD91A3A397169D9BEEDDD192360BADBF718F`（三者完全一致）。
- portable ZIP：95,549,428 字节，SHA-256 `4EBA9FD69BAA70A05059CEE7C31AF8C4AC0A4C2E84F3AD838FC52450011BB8A4`。
- installer：257,244,769 字节，SHA-256 `AB68FF460583C22F1B8986DA49A108F4DF02A6FA3E03E0F9B04E5B01FB8B514D`。

真实硬件边界：2026-08-21 的能力探针只看到两个 active 物理端点——默认 `耳机 (Realtek(R) Audio)`（`{0.0.0.00000000}.{b71ffe5b-365e-4c0a-8b0d-8713537f9168}`）和 `扬声器 (Realtek(R) Audio)`（`{0.0.0.00000000}.{2ba97857-afaf-4e83-aa26-d2fc73c3c4df}`），没有 active Bluetooth/WH-1000XM5 端点。因此本轮没有伪报“Windows 默认蓝牙耳机、普通 msedge 路由扬声器、增强标签页仍实际从蓝牙出声”或双标签真人听感通过；这些仍需连接蓝牙设备、在真实媒体标签点击扩展并完成浏览器设备授权后人工复核。自动化已验证所需 endpoint/deviceId/sink 状态机和失败门禁，但 API 读回不能替代听感。

## 历史记录：2026-08-14 发行验证

最终命令 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-all.ps1` 退出码为 0，覆盖 Release restore/build、测试、真实 WPF 窗口、系统浏览器隔离运行、打包、安装、升级、卸载和最终清单/哈希检查。

- Debug 与 Release：各 11 个项目，0 警告、0 错误；两种配置的 .NET 均为 116/116 通过（Core 87、WindowsAudio 11、Installer 8、Desktop/WPF 8、NativeHost 2）。
- 浏览器扩展 Node：41/41 通过；新增项目直接验证每标签页电平状态相互独立、快速上升、约 350ms 衰减、非法值截断和最终归零；原有授权事务、同步/异步 storage、重试及未处理 rejection 回归继续通过。
- Web Audio EQ：Chrome、Edge 各通过；100%→50% RMS 比均为 0.5，左声道测试的右侧泄漏比均为 0。为避免 branded browser 首次模块加载偶发提前 `dump-dom`，测试只对仍为 `WAIT` 的状态使用全新 profile 有界重试；任何 `FAIL` 不重试。
- Debug、源码 Release、portable、每个安装路径和最终实际安装版的 UI smoke 均退出 0；最终安装版另生成 11 张真实 WPF 截图。
- Debug、源码 Release 和安装版均通过受控真实 WASAPI→WPF 电平验证；最终 Release/installed 各采样 74 次，原始与平滑峰值最大 `0.36621094`，Track `134 DIP`、Indicator 最大 `49.3333 DIP`，最后一个样本的 Raw/UI/Indicator 全为 0。
- 最终实际安装版普通可见启动使用独立临时目录中的受控 WaveOut 会话：`WindowShown=True; Sources=1; MaterializedItems=1`；随后应用自有退出信号完成恢复并以退出码 0 结束，受控音源及临时目录也确定性清理。

## 授权竞态与 Chrome/Edge 实际运行

授权控制器在任何 `await` 之前冻结 candidate、request、browser、endpoint、waiter 和 operation token；单飞门防止重复确认，storage 刷新在事务后合并一次。保存映射与通知标签页分阶段处理：通知失败保留映射并允许幂等重试。

真实运行脚本通过 CDP `Extensions.loadUnpacked` 在完全独立、测试完成即删除的临时 profile 中加载源码或 portable 扩展，不读取或修改用户浏览器配置。Chrome 与 Edge 各执行 4 次授权操作（添加、修改、删除、重复授权），最终结果均为：

- `Runtime.exceptionThrown = 0`
- `Log.entryAdded` error = 0
- 页面 `unhandledrejection = 0`
- service worker error = 0

空闲运行还确认扩展版本 0.2.2、MV3、`setSinkId` 可用，且打开浏览器/启动 service worker 不请求 Native Messaging；packaged 运行在明确启动 portable 桌面端后收到 `bridge.status`。Native Host 注册值和 `%LocalAppData%\AudioSourceMixer` 在测试前逐字节/逐哈希备份，测试使用全新数据目录，结束后恢复并比较；没有触碰个人浏览器 profile。

## WPF、响应式布局与绑定

UI smoke 必须调用真实 `MainWindow.Show()`，等待 `Loaded`、ApplicationIdle 和 Render，并注入三个确定性来源：普通 Windows 会话、超长中文 Edge 标签、超长英文 Chrome 标签。场景同时覆盖 200% 增益、非默认长设备名、路由/授权错误、EQ 展开和实时峰值。验证器强制物化每个 `ContentPresenter`、来源卡、ProgressBar 和 10 段 EQ DataTemplate；还必须找到 `PART_Track`/`PART_Indicator` 并实测 Value=0/50/100 时宽度约为 0/一半/全部，动态 Peak=73% 时 Indicator 必须真正变宽。缺少容器、绑定错误、XAML 错误、Dispatcher 异常或未观察异步异常都会返回非零。

STA 回归在 880×600、1180×760、1600×900 三种窗口尺寸验证标签/数值不相交、输出选择框至少 180 DIP、可见按钮不裁剪和无横向溢出。来源列表保持 `CanContentScroll=True`、Recycling、`ScrollUnit=Pixel`、`IsDeferredScrollingEnabled=False`、`PanningMode=VerticalOnly` 和横向滚动禁用。设置页与浏览器引导页也被真实物化并包含可键盘聚焦控件。

全局 `ApplicationFont` 当前为 `Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Global User Interface`。运行时断言验证 MainWindow 及 TextBlock、Button、CheckBox、RadioButton、ComboBox 使用同一资源，语言为 `zh-CN`，Display/ClearType 文本选项生效；本机 Microsoft YaHei UI 的 GlyphTypeface 包含“音频来源设置浏览器均衡器输出设备”。32 个 Desktop XAML/C# 源文件通过严格 UTF-8、Unicode replacement、私用区/兼容汉字和已知乱码片段扫描，关键设置文案逐字匹配。

设置页 8 个 CheckBox（含“浏览器增强时隐藏浏览器聚合会话”）和 EQ“启用均衡器”由真实 WPF 模板完成布局。测试确认视觉树内没有白色 Check Path 或替代勾选字符；未选中为表面色空框，选中为 `PrimaryDarkBrush` 实心块，禁用选中仍保持主色并降低整体不透明度，切换前后 16×16 DIP 方块位置不变。Automation `TogglePattern` 与 Space 键均可切换；宽容器中控件实际宽度等于 16 DIP 方块 + 8 DIP 间距 + ContentPresenter 宽度，长短标签得到不同宽度，标签右侧 30 DIP 空白 hit-test 不属于 CheckBox。紧凑焦点模板无固定宽度，只以 -3 DIP 外边距包围控件实际内容。

全部产品 XAML `{Binding}` 都显式声明模式。只读显示属性（包括 `PeakPercent`、`DragPlaceholderOpacity`、名称、状态、命令、可见性、设备集合、音量/平衡显示文本及 EQ 汇总）均为 OneWay；有效 TwoWay 仅包括具有 setter 的 `VolumePercent`、`BalancePercent`、`IsEqualizerExpanded`、`IsEqualizerEnabled`、`SelectedEqualizerPresetId`、`EqualizerPreampDb`、`EqualizerBandViewModel.GainDb`、`IsHiddenSourcesPopupOpen` 和设置 CheckBox 属性。峰值快照触发 `PropertyChanged(PeakPercent)` 后 ProgressBar 实测更新到 73%。

最终 11 张截图由安装版 0.2.2 的真实 WPF 窗口生成，位于 `artifacts\screenshots-v0.2.2-session-meter-final-20260814`：普通混音器、浏览器增强来源、EQ 展开、浏览器引导、设置页 880×600 / 1180×760 / 1600×900 的 100 DPI 渲染、设置页 125/150/200 DPI 渲染和最小窗口。逐图复核页内 PNG Logo 在各 DPI 下边缘清晰，电平指示条有可见宽度，六点把手/省略号低调且不挤压标题；未发现裁切、重叠、乱码或行尾焦点框。

## Logo、实时电平与来源展示

`assets/product-icon.svg` 保持唯一设计源。生成脚本从其 XML 几何/颜色生成 512×512 RGBA 页内 PNG、精确 16/32/48/128 浏览器 PNG 和含 16/20/24/32/40/48/64/96/128/256 帧的 ICO；小尺寸先 4× 超采样后高质量缩小。MainWindow 页内只加载 PNG（DecodePixelWidth=128、HighQuality、布局取整），ICO 仅用于 exe/窗口/任务栏/快捷方式/安装器。测试读取 PNG IHDR/像素确认 512×512 RGBA 和透明角点，并解析 ICO 帧目录确认十个尺寸。

普通 Windows 会话保留约 1 秒的低频拓扑枚举，另由 AudioWorker 每 75ms（约 13.3Hz）只读取已有 `IAudioMeterInformation` 句柄；浏览器 offscreen 图每 100ms（10Hz）独立读取各自 Analyser。两条路径都以来源 ID 发送轻量电平事件，MainViewModel 只调用对应 ViewModel 的 `UpdatePeak`，不执行 Reconcile、配置读取或路由应用；上升立即响应、下降约 350ms 线性衰减并最终精确归零。真实报告位于 `artifacts\live-meter-source-release.json`、`live-meter-installed.json` 和 `live-meter-debug.json`。

来源展示固定按“无声会话设置→精确手动隐藏→Edge/Chrome 聚合自动过滤→永久手动顺序→ObservableCollection.Move”处理。测试覆盖同 exe 两个会话只隐藏一个、自动过滤不进入隐藏 Popup、Electron/其他 Chromium 不误判、最后一个增强标签结束后聚合会话恢复、Peak/自动应用不改顺序、拖拽期间预览 Move 不触发持久化，以及 Move 后原 ViewModel 实例、EQ 展开和音频状态不变。设置 schema 7 保留原 onboarding、手动顺序和隐藏记录，同时清空旧的浏览器强制显示例外；手动顺序/隐藏仅保存 `win:` 精确身份，各限 256 项并清理超过 30 天的隐藏记录，不保存标签页标题或 URL。

## 安装、升级和发行负载

安装验证全部通过：fresh install、同版本 repair、backup 后注入失败回滚、空格路径、中文路径、开机启动/后台托盘、默认不打开浏览器向导、显式 `--browser-setup` 显示真实窗口、无参数卸载 UI、运行中优雅卸载、0.2.1→0.2.2 原位升级、最终普通启动和注册表清理。验证完成后又在用户原默认路径安装最终 0.2.2，恢复原有选择：桌面快捷方式开启、开机启动关闭。

严格 allowlist 下安装目录为 30 个文件、487,251,507 字节；没有把截图、测试源或开发报告装入用户目录：

- 三个可执行文件：`AudioSourceMixer.exe`、`AudioSourceMixer.NativeHost.exe`、`AudioSourceMixer.Uninstall.exe`。
- 身份/桥配置：`install-identity.json`、`native-host-manifest.json`、`browser-extension-origins.json`。
- 法律文件：`LICENSE`、`THIRD_PARTY_NOTICES.md`。
- 扩展 22 个文件：4 个图标、manifest、offscreen 2 个、onboarding 4 个、授权页/控制器 6 个、service worker 2 个、shared 3 个（新增 `levels.js`）。

安装目录不含 docs、tests、tools、源码、PDB、package.json、测试脚本或构建机绝对路径。机器可读完整清单和安装矩阵位于 `artifacts\AudioSourceMixer-0.2.2-build-manifest.json`。

## 2026-08-14 交付物与哈希（历史）

- publish / portable / installed `AudioSourceMixer.exe`：162,223,796 字节，SHA-256 `AF4668315991E4D46EEA256DCA59C07386FF25BA6EC13C06D1D3ED072B7F87BE`（三者完全一致，文件版本 `0.2.2.0`，产品版本 `0.2.2`）。
- portable ZIP：95,541,574 字节，SHA-256 `21BEAC258C58B5EF1F6DEA17D9498C4A609CE01EB1ABF1DDE6304A65A9CDCDF2`。
- installer：257,236,577 字节，SHA-256 `07FAA76E188A43A20CDBBBC426FF8783E3FBB36457EB754208BECEB333C1F0AD`（文件版本 `0.2.2.0`，产品版本 `0.2.2`）。

最终 `build-all.ps1` 退出码为 0。此前两轮分别被“默认路径已有安装，验证器拒绝覆盖”和“普通启动未创建真实音频源，得到 Sources=0”阻断；均按失败处理。旧安装通过其自带卸载器保留用户设置后移除，普通启动验证器改为从独立临时目录运行受控 WaveOut 来源，最终真实得到 `WindowShown=True; Sources=1; MaterializedItems=1`，没有放宽产品、浏览器或安装器门禁。完整矩阵结束后又用最终安装器安装到用户默认路径，并再次通过 installed UI/screenshot smoke；卸载项存在、开机启动仍为空、无遗留产品进程。

## 人工边界

自动化没有把 API 状态伪装成真人听感。当前扩展尚未发布到 Chrome Web Store/Edge Add-ons；Chrome/Edge 隔离 profile 已验证 MV3、授权事务、Web Audio EQ、Native Messaging 和独立电平算法，但浏览器安全模型要求真实标签页捕获由用户点击扩展图标触发，自动化没有接管个人浏览器或伪造该手势。因此个人浏览器中“双真实媒体标签独立电平、聚合会话隐藏/返回”和蓝牙耳机主观听感仍需人工复核；受保护内容、DRM、休眠、标签丢弃和触控板手感同样属于限制。

---

## 历史记录：v0.2.1

测试日期：2026-08-13。环境：Windows 11 x64（build 26200）、.NET SDK 8.0.423、Node 24.18.1、Chrome/Edge 151。自动测试、浏览器 API 客观验证和必须由用户完成的真人听感严格分开。

## 自动化总结果

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-all.ps1` 最终退出码 0。

- Release restore/build：11 个项目，0 警告、0 错误。
- .NET：104/104 通过（Core 82、WindowsAudio 10、Desktop/WPF 3、NativeHost 2、Installer 7）。
- 浏览器扩展 Node：25/25 通过。
- Chrome 与 Edge 真实 Web Audio 引擎：2/2 通过。
- source Release、portable、默认安装、repair、空格路径、中文路径、v0.2.0 升级和最终安装的 UI smoke 全部通过。
- fresh install、same-version repair、注入失败回滚、v0.2.0→v0.2.1 原位升级、自定义路径、开机启动、后台托盘、运行中卸载和注册表清理全部通过。

## EQ 回归与真实浏览器引擎

Core 与 Node 共同固定 31/62/125/250/500 Hz、1/2/4/8/16 kHz 十段的频率、Q、首尾 shelf 和中间 peaking 类型；关闭、平直、低频、人声、高频、温暖、自定义的每段 dB 都有常量和断言。测试还覆盖：

- 关闭和平直全部为 0 dB；手动频段进入 Custom。
- 拒绝错误段数、频率、Q、未知预设、越界、NaN 和 Infinity。
- 独立 headroom 保守抵消最大正增益，不改写主音量。
- generation 旧命令不覆盖新 EQ；快速连续更新以最后值为准。
- EQ 开关不改变 volume、balance、mute 或 output；输出重验/切换保留 EQ。
- Chrome/Edge 同 tab ID 的图彼此独立；停止单图断开 source、10 个滤波器、headroom、主 gain、panner、analyser 并关闭对应 context。
- profile schema 2 浏览器来源迁移为 schema 3 + EQ 关闭；单项恢复与全部恢复关闭 EQ。

`node scripts\verify-browser-equalizer-runtime.mjs` 使用隔离临时 profile 分别启动系统 Chrome 151 和 Edge 151 headless，通过真实 `OfflineAudioContext` 与 `BiquadFilterNode.getFrequencyResponse()` 验证：off/flat 平直，低频/人声/高频预设在目标频段产生可测响应；相同 EQ 下主音量 100%→50% 的 RMS 比为 0.5；仅左声道时右侧泄漏比为 0。临时 profile 在每轮后删除，不读取或修改用户浏览数据。

## WPF UI smoke 的真实覆盖

smoke 调用 `MainWindow.Show()`，等待 `Loaded`、ApplicationIdle 和 Render，注入确定性来源，确认虚拟化 `ListBox` 生成 `ListBoxItem` 且 DataTemplate 有视觉子树。它展开“音效”，确认嵌套 DataTemplate 真正创建 10 个频段滑块；读取 Peak ProgressBar 的有效 OneWay 绑定，触发 `PeakPercent.PropertyChanged` 并验证值更新到 73%；再物化设置页并遍历绑定。

旧后台 smoke 在 `_window.Show()` 前退出，`ItemTemplate` 未实例化，因此只读 `PeakPercent` 对默认 TwoWay 的 `ProgressBar.Value` 错误曾产生假阳性。当前 smoke 的 Dispatcher 异常、XAML 解析异常、绑定 trace 错误、未观察异步异常、缺少容器或 EQ 模板都会返回非零退出码。

所有纯显示属性都显式 OneWay。合法 TwoWay 源包括 `VolumePercent`、`BalancePercent`、`IsEqualizerExpanded`、`IsEqualizerEnabled`、`SelectedEqualizerPresetId`、`EqualizerPreampDb`、`EqualizerBandViewModel.GainDb` 和设置页开关；测试反射确认它们都有 public setter。来源列表启用 recycling virtualization 且禁用横向滚动。

## 安装、升级、启动与卸载

验证器先逐字节备份 `%LocalAppData%\AudioSourceMixer`，结束后恢复并比较相对路径与 SHA-256。实际结果：

- 默认路径 fresh install：strict installed allowlist 与 UI smoke 通过；开机启动默认关闭。
- installed uninstaller 无参数显示专用卸载窗口；探针关闭窗口而不执行卸载。
- 同版本 repair 原子替换旧目录；注入 backup 后失败返回 1 并恢复原目录和哈希。
- 含空格与中文自定义路径安装、Native Host 路径、卸载项、启动项和 UI smoke 通过。
- v0.2.0 正式安装器安装后原位升级到 v0.2.1：旧 sentinel 消失，文件版本与哈希更新。
- 普通安装版可见启动成功；日志为 `WindowShown=True; Sources=8; MaterializedItems=5`。命名退出事件随后走音频恢复路径并返回 0。
- 后台程序运行时静默卸载会先请求恢复并等待退出；每轮卸载后安装目录、Chrome/Edge Native Messaging、卸载项和本产品 Run 值均不存在。

## 发行 allowlist 与负载变化

安装目录由 `packaging/runtime-allowlist.json` 精确组装并做“完全相等”比较。扩展文件还从 manifest 根、动态 offscreen 根和 ES module/HTML/CSS 引用图验证可达性；`package.json`、`diagnostics`、Node 测试和孤儿开发文件不进入交付物。文本扫描确认不存在仓库绝对路径、用户名、`/mnt/data`、tests 或 src 路径。

v0.2.0 安装目录实测 43 个文件、486,817,471 字节；v0.2.1 为 23 个文件、486,865,564 字节。减少 20 个开发/文档文件；因新增 EQ 和图标代码，self-contained 二进制总量增加 48,093 字节（约 0.01%）。没有为了减小文件数移除 self-contained .NET。

v0.2.1 安装目录完整清单（括号内为用途）：

- `AudioSourceMixer.exe`（WPF 主程序与 self-contained runtime）
- `AudioSourceMixer.NativeHost.exe`（Native Messaging 桥）
- `AudioSourceMixer.Uninstall.exe`（专用卸载器）
- `install-identity.json`（安全卸载产品身份）
- `native-host-manifest.json`（安装路径 Native Host 注册目标）
- `LICENSE`（许可证）
- `THIRD_PARTY_NOTICES.md`（第三方法律声明）
- `BrowserExtension/manifest.json`（MV3 声明）
- `BrowserExtension/assets/icon-16.png`、`icon-32.png`、`icon-48.png`、`icon-128.png`（各尺寸扩展图标）
- `BrowserExtension/shared/protocol.js`（协议 3 校验）
- `BrowserExtension/shared/equalizer.js`（10 段定义、预设、范围与 headroom）
- `BrowserExtension/service-worker/service-worker.js`（事件、Native Messaging 和状态协调）
- `BrowserExtension/service-worker/lifecycle-policy.js`（空闲恢复策略）
- `BrowserExtension/offscreen/offscreen.html`（动态 offscreen 入口）
- `BrowserExtension/offscreen/offscreen.js`（tabCapture、EQ、主增益、平衡与 sink 图）
- `BrowserExtension/output-authorization/authorize.html`、`authorize.css`、`authorize.js`（可见设备授权页）
- `BrowserExtension/output-authorization/authorization-workflow.js`（候选和测试音）
- `BrowserExtension/output-authorization/mappings.js`（授权映射、迁移和重绑定）

portable 以三项用户文件替换三项安装器生成文件：`USER_GUIDE.md`、`scripts/register-native-host.ps1`、`scripts/unregister-native-host.ps1`，所以同为 23 个文件。

## 浏览器安装状态与人工边界

只读检查确认 Chrome Default 与 Edge Default 的固定扩展 ID `edbfelppckjcfhadggldaifbleoofkio` 都指向本仓库 `src\AudioSourceMixer.BrowserExtension`。检查时两个浏览器均为用户正在使用的普通会话且未启用远程调试；验证没有终止、重启、接管或改写这些个人会话。

自动化没有把 UI 滑块变化冒充声学成功，也没有宣称完成以下需要扩展 action/用户手势/人耳的项目：真实标签页点击捕获、系统设备选择器授权、扬声器与蓝牙耳机实际出声、双标签不同物理 sink、DRM/全屏/休眠以及主观平直 A/B。系统浏览器引擎的 DSP 数学和声道/音量关系已经客观通过；物理设备与听感仍按下方清单手工验收。

## 最终哈希

- publish 主程序：`550509A04D7DDA6B834444CF59158A997BE98164954029F571E73708B1CCB39C`
- portable 主程序：相同。
- installed 主程序：相同。
- portable ZIP：`922263065AD5279D47DC9CD9C4ACF590D7659A7B3F0B813D11B2E3A8920C5BC8`
- installer：`86D3946E30260E845B42166843598366A1F320C9EDCFC923C7D34486CF4FCB2C`

机器可读记录：`artifacts\AudioSourceMixer-0.2.1-build-manifest.json`。

## 真人 A/B 验证清单

1. 在 Chrome 与 Edge 的扩展页点击“重新加载”，确认版本 0.2.1。
2. 分别在有声音的标签页点击扩展按钮，展开桌面来源卡片“音效”。
3. 对测试音切换关闭/平直，确认主观等响；切换低频、人声、高频，确认目标频段可闻变化且无爆音。
4. EQ 保持不变，音量从 100% 调到 50%，确认响度约减半；设置仅左/仅右，确认无明显串音。
5. 分别授权 Realtek 扬声器和蓝牙耳机，切换输出后确认 EQ 保留。
6. 两个标签页设置不同 EQ 与不同输出，停止其中一个，确认另一个不受影响。
7. 重启浏览器、扩展和桌面程序，确认 profile 行为符合“记住/自动应用”设置；退出程序确认增强图停止并恢复正常页面播放。
