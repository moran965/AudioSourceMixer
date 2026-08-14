# Audio Source Mixer 0.2.2 测试报告

测试日期：2026-08-14。环境：Windows 11 x64（build 26200）、.NET SDK 8.0.423、Node 24.18.1、Google Chrome 151、Microsoft Edge 151。

## 最终自动化结果

最终命令 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-all.ps1` 退出码为 0，覆盖 Release restore/build、测试、真实 WPF 窗口、系统浏览器隔离运行、打包、安装、升级、卸载和最终清单/哈希检查。

- Release：11 个项目，0 警告、0 错误。
- .NET：107/107 通过（Core 84、WindowsAudio 10、Installer 8、Desktop/WPF 3、NativeHost 2）。
- 浏览器扩展 Node：39/39 通过；其中 9 项直接执行授权事务并覆盖同步/异步 storage 事件、双击、候选重选、waiter 精确删除、通知失败与重试、无未处理 rejection。
- Web Audio EQ：Chrome、Edge 各通过；100%→50% RMS 比均为 0.5，左声道测试的右侧泄漏比均为 0。为避免 branded browser 首次模块加载偶发提前 `dump-dom`，测试只对仍为 `WAIT` 的状态使用全新 profile 有界重试；任何 `FAIL` 不重试。
- 源码 Release、portable、每个安装路径和最终实际安装版的 UI smoke 均退出 0。
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

UI smoke 必须调用真实 `MainWindow.Show()`，等待 `Loaded`、ApplicationIdle 和 Render，并注入三个确定性来源：普通 Windows 会话、超长中文 Edge 标签、超长英文 Chrome 标签。场景同时覆盖 200% 增益、非默认长设备名、路由/授权错误、EQ 展开和实时峰值。验证器强制物化每个 `ListBoxItem`、来源卡、ProgressBar 和 10 段 EQ DataTemplate；缺少容器、绑定错误、XAML 错误、Dispatcher 异常或未观察异步异常都会返回非零。

STA 回归在 880×600、1180×760、1600×900 三种窗口尺寸验证标签/数值不相交、输出选择框至少 180 DIP、可见按钮不裁剪和无横向溢出。来源列表保持 `CanContentScroll=True`、Recycling、`ScrollUnit=Pixel`、`IsDeferredScrollingEnabled=False`、`PanningMode=VerticalOnly` 和横向滚动禁用。设置页与浏览器引导页也被真实物化并包含可键盘聚焦控件。

全局 `ApplicationFont` 当前为 `Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Global User Interface`。运行时断言验证 MainWindow 及 TextBlock、Button、CheckBox、RadioButton、ComboBox 使用同一资源，语言为 `zh-CN`，Display/ClearType 文本选项生效；本机 Microsoft YaHei UI 的 GlyphTypeface 包含“音频来源设置浏览器均衡器输出设备”。32 个 Desktop XAML/C# 源文件通过严格 UTF-8、Unicode replacement、私用区/兼容汉字和已知乱码片段扫描，关键设置文案逐字匹配。

设置页 7 个 CheckBox 和 EQ“启用均衡器”由真实 WPF 模板完成布局。测试确认视觉树内没有白色 Check Path 或替代勾选字符；未选中为表面色空框，选中为 `PrimaryDarkBrush` 实心块，禁用选中仍保持主色并降低整体不透明度，切换前后 16×16 DIP 方块位置不变。Automation `TogglePattern` 与 Space 键均可切换；宽容器中控件实际宽度等于 16 DIP 方块 + 8 DIP 间距 + ContentPresenter 宽度，长短标签得到不同宽度，标签右侧 30 DIP 空白 hit-test 不属于 CheckBox。紧凑焦点模板无固定宽度，只以 -3 DIP 外边距包围控件实际内容。

全部产品 XAML `{Binding}` 都显式声明模式。只读显示属性（包括 `PeakPercent`、名称、状态、命令、可见性、设备集合、音量/平衡显示文本及 EQ 汇总）均为 OneWay；有效 TwoWay 仅包括具有 public setter 的 `VolumePercent`、`BalancePercent`、`IsEqualizerExpanded`、`IsEqualizerEnabled`、`SelectedEqualizerPresetId`、`EqualizerPreampDb`、`EqualizerBandViewModel.GainDb` 和设置 CheckBox 属性。峰值快照触发 `PropertyChanged(PeakPercent)` 后 ProgressBar 实测更新到 73%。

最终 11 张截图由安装版 0.2.2 的真实 WPF 窗口生成，位于 `artifacts\screenshots-v0.2.2-ui-detail-final`：普通混音器、浏览器增强来源、EQ 展开、浏览器引导、设置页 880×600 / 1180×760 / 1600×900 的 100 DPI 渲染、设置页 125/150/200 DPI 渲染和最小窗口。逐图复核未发现非标准简体字、白色对勾、基线漂移、裁切、重叠或行尾焦点框；禁用未选中与禁用选中状态可区分。

## 安装、升级和发行负载

安装验证全部通过：fresh install、同版本 repair、backup 后注入失败回滚、空格路径、中文路径、开机启动/后台托盘、默认不打开浏览器向导、显式 `--browser-setup` 显示真实窗口、无参数卸载 UI、运行中优雅卸载、0.2.1→0.2.2 原位升级、最终普通启动和注册表清理。验证完成后又在用户原默认路径安装最终 0.2.2，恢复原有选择：桌面快捷方式开启、开机启动关闭。

严格 allowlist 下安装目录为 29 个文件、486,989,849 字节；没有把截图、测试源或开发报告装入用户目录：

- 三个可执行文件：`AudioSourceMixer.exe`、`AudioSourceMixer.NativeHost.exe`、`AudioSourceMixer.Uninstall.exe`。
- 身份/桥配置：`install-identity.json`、`native-host-manifest.json`、`browser-extension-origins.json`。
- 法律文件：`LICENSE`、`THIRD_PARTY_NOTICES.md`。
- 扩展 21 个文件：4 个图标、manifest、offscreen 2 个、onboarding 4 个、授权页/控制器 6 个、service worker 2 个、shared 2 个。

安装目录不含 docs、tests、tools、源码、PDB、package.json、测试脚本或构建机绝对路径。机器可读完整清单和安装矩阵位于 `artifacts\AudioSourceMixer-0.2.2-build-manifest.json`。

## 最终交付物与哈希

- publish / portable / installed `AudioSourceMixer.exe`：162,101,428 字节，SHA-256 `AAEA9FF6C7F42CA0CF71F1F0CA06C4B5066EDC1F5966248804FE398F2C59E524`（三者完全一致，文件版本 `0.2.2.0`，产品版本 `0.2.2`）。
- portable ZIP：95,449,119 字节，SHA-256 `3AC36B3A5AD8A0F27EABF0838B963A4E0539F5B0E33DFC077B3F6B691B76999F`。
- installer：257,109,601 字节，SHA-256 `265768562E1E58C5F00EF1FC9E51B60BDD785732445D0A2836F53D659B564C8A`（文件版本 `0.2.2.0`，产品版本 `0.2.2`）。

最终 `build-all.ps1` 退出码为 0。此前诊断轮次分别暴露了“机器当时无活动音频会话”、测试播放器占用仓库构建输出，以及一次 Edge 隔离 profile 模块尚未就绪；这些轮次均按失败处理。最终轮次从独立临时目录运行受控音源，Edge 使用全新隔离 profile 后通过，未放宽任何产品、浏览器或安装器门禁。

## 人工边界

自动化没有把 API 状态伪装成真人听感。当前扩展尚未发布到 Chrome Web Store/Edge Add-ons，普通用户仍需在程序内向导指引下手动开启开发者模式并“加载已解压的扩展程序”。真实硬件的扬声器/蓝牙听感、受保护内容、DRM、休眠、标签丢弃和触控板主观手感仍需人工矩阵；测试不会修改个人浏览器配置或模拟安全确认。

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
