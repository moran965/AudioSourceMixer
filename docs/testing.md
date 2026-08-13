# Audio Source Mixer 0.2.1 测试报告

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
