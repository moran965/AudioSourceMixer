# Audio Source Mixer 0.2.0 测试报告

测试日期：2026-08-13。环境：Windows 11 x64（build 26200）、.NET SDK 8.0.423、Node 24.18.1、Chrome/Edge 151。自动测试与需要真人听感的项目严格分开。

## 自动测试结果

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1 -Configuration Release`：退出码 0。

- Release restore/build：11 个项目，0 警告、0 错误。
- .NET：92/92 通过（Core 72、WindowsAudio 10、Desktop/WPF 3、NativeHost 2、Installer 5）。
- 浏览器扩展 Node：21/21 通过。
- 源码 Release UI smoke：退出码 0。
- portable UI smoke：退出码 0。
- installed UI smoke：默认路径、同版本 repair、空格路径、中文路径、0.1.2 升级、最终哈希安装均退出码 0。

新增回归覆盖：

- 系统选择器只创建内存候选；未确认/取消不污染旧映射；确认后才写 schema 3。
- 旧映射迁移为 `unverified`，错误映射可替换、单条删除和按浏览器清空。
- 明显名称不匹配产生警告；测试音结束或异常均停止节点并关闭临时 AudioContext。
- 两个 endpoint 与多个等待标签页隔离，删除/清空会重新验证所有匹配活动标签页。
- 空闲恢复策略在无活动 graph 时返回 false，service worker 恢复体不调用 `ensureNativePort()`。
- Native Host 对不存在的随机管道超时返回 2，输出“请先打开”，并确认没有新增 `AudioSourceMixer.exe` 进程；源码中已无启动桌面的 `Process.Start`。
- 桌面桥接记录 Chrome/Edge 和扩展版本，可定向发送 `bridge.openOptions` / `bridge.clearMappings`，未连接浏览器返回明确错误。
- 设置 schema 保留 0.1.2 字段，设置写入继续串行化和临时文件原子替换。
- 平衡中心吸附进入/离开迟滞、跨中心和程序化更新不吸附。
- 安装路径拒绝磁盘/系统/用户/仓库根，接受空格和中文路径；开机启动新安装默认关闭。
- installed uninstaller 文件身份的无参数模式进入专用卸载页，页面不存在“安装”按钮。

## WPF UI smoke 的真实覆盖

测试必须调用 `MainWindow.Show()`，等待 `Loaded`、ApplicationIdle 和 Render，注入一个确定存在的诊断音频源，确认 `ItemsControl` 生成 `ContentPresenter` 且 DataTemplate 有视觉子树。它读取 Peak ProgressBar 的有效绑定方向，触发 `PeakPercent` 的 `PropertyChanged` 并验证数值更新到 73%。随后切换到“设置”页、物化控件并再次遍历绑定。

旧后台 smoke 在 `_window.Show()` 前退出，`ItemsControl.ItemTemplate` 从未实例化，因此原 `ProgressBar.Value` 对 getter-only `PeakPercent` 的隐式 TwoWay 错误会产生假阳性。当前 smoke 的任意 Dispatcher 异常、XAML 解析异常、绑定 trace 错误或未观察异步异常都会产生非零退出码。

只读 UI 属性（包括 `DeviceName`、`BrowserStatus`、`PeakPercent`、显示文本/可见性/能力、浏览器连接状态、版本和部署类型）全部显式 `Mode=OneWay`。合法 TwoWay 源属性为：`VolumePercent`、`BalancePercent`、`CloseToTray`、`AutoApplyProfiles`、`RememberProfiles`、`ShowInactiveSessions`、`StartupEnabled`、`StartMinimizedToTray`；它们均有 public setter。

## 安装、升级和卸载矩阵

`scripts\verify-installer.ps1 -BaselineInstallerPath artifacts\AudioSourceMixer-0.1.2-win-x64-setup.exe`：最终退出码 0。

- 默认路径 fresh install；确认 Run 值不存在。
- 无参数双击 installed `AudioSourceMixer.Uninstall.exe`：主窗口标题为“卸载 Audio Source Mixer”，关闭探针后未执行卸载。
- 0.2.0 同版本 repair：旧 sentinel 消失，目录原子替换。
- backup 后注入失败：安装器退出 1，原 sentinel 和主程序哈希恢复。
- 含空格自定义路径、含中文自定义路径：安装、manifest host path、卸载项路径和 UI smoke 全部通过。
- `--startup-background`：Run 命令精确引用自定义可执行文件并带 `--background`；进程保持运行且无主窗口。
- 安装 0.1.2 正式包后原位升级到 0.2.0：保留默认位置，旧 sentinel 消失，文件版本和哈希更新。
- 正在运行的后台桌面程序卸载：命名事件触发音频恢复与退出，卸载器等待完成后清理。
- 普通安装版启动：窗口可见，日志为 `WindowShown=True; Sources=1; MaterializedItems=1`；随后同一恢复退出路径返回 0。
- 每轮静默卸载后，安装目录、Chrome/Edge Native Messaging、卸载项和本产品 Run 值均不存在；用户数据按相对路径与 SHA-256 恢复。

首次打包检查发现一个无注册的旧 0.1.2 产品目录和一个仍引用它的 Native Host 进程。该进程只在可执行路径精确属于旧目录时被停止；目录没有删除，而是移动到 `artifacts\diagnostics\pre-v020-orphan-install-20260813-182247`。三项路径已失效的旧注册也在删除前导出到 `artifacts\diagnostics\pre-v020-orphan-registry-20260813.json`（SHA-256 `8B5C897AAB82D14E482809E2B590DA1E4E84733B2B81A626A84BADD4A448BB2F`）。

## 浏览器实测

Chrome Default 配置的 Secure Preferences 中确认固定 ID `edbfelppckjcfhadggldaifbleoofkio` 指向本仓库 `src\AudioSourceMixer.BrowserExtension`。在启动前 Chrome 与桌面进程都不存在，临时注册 portable Native Host 后正常启动真实 Chrome Default 配置，等待 12 秒：出现 10 个 Chrome 进程，`AudioSourceMixer.exe` 进程为 0；随后只终止本次产生的 Chrome 进程并注销 Host。此项证明用户实际配置中“打开 Chrome 不启动桌面程序”。

尝试用隔离 `--user-data-dir` + `--load-extension` 自动加载时，Chrome/Edge 151 都忽略命令行扩展加载，目标页是错误页且 `chrome.runtime` 不存在。因此该尝试退出 1，明确不计为扩展运行通过，也未触碰真实浏览器数据。空闲生命周期仍由 Node 策略测试、service worker 源码断言、Native Host 进程测试和上述真实 Chrome Default 启动共同覆盖。

本轮没有自动宣称物理声学路由成功：浏览器系统设备选择与确认需要用户手势，测试音来自哪台物理设备需要人耳判断。0.1.2 已有 Chrome 双标签/Realtek/WH-1000XM5 API 矩阵属于历史证据，不能替代 0.2.0 新向导的最终听音确认。

## 最终哈希

- publish 主程序：`8F73E92C64423BBD7E961E73E8A07B76193F0CF14194995A09FF19CD065BD00E`
- portable 主程序：相同。
- installed 主程序：相同。

机器可读记录：`artifacts\AudioSourceMixer-0.2.0-build-manifest.json`。最终 ZIP 和 setup 自身哈希应以该文件最后一次重新打包后的交付报告为准。

## 人工 A/B 听音清单

1. 在授权页为 Realtek 扬声器选择候选，点“播放测试声音”，确认声音来自扬声器后保存。
2. 为 WH-1000XM5 重复；故意选错一次并取消，确认旧映射未变化。
3. 两个增强标签页分别路由到扬声器/耳机，做 A→B→A 快切并听音；同时移动音量和平衡，确认左右与峰值动态正确。
4. 退出程序，确认普通播放器恢复原音量、平衡和路由。
