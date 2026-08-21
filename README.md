# Audio Source Mixer

当前版本：`0.2.2`。这是面向 Windows 11 x64 的本地音频源控制工具，可分别控制 Windows Core Audio 会话及用户主动启用的 Chrome/Edge 增强标签页。

## 功能

- 普通 Windows 会话：0–100% 主音量、静音、声道平衡、峰值和按应用输出设备路由。
- 浏览器增强标签页：0–200% Web Audio 增益、平衡、静音、10 段均衡器和经用户授权的输出 sink；多标签页相互独立。
- Windows 会话以约 13Hz、浏览器增强标签页以 10Hz 独立刷新实时电平；快速响应上升、约 350ms 平滑衰减，停止播放后归零，不触发完整来源重建。
- 来源只采用手动顺序：音量、平衡、静音、输出、EQ、实时电平和设备刷新都不会移动卡片；从六点把手拖动时，完整卡片预览随指针移动，相邻卡片在越过中线后以 FLIP 动画实时让位，并支持边缘自动滚动与 Esc 无保存回滚。
- 单个来源可从省略号菜单隐藏并恢复；“已隐藏 N”和“全部恢复显示”只处理用户手动隐藏的会话。浏览器增强活动时是否隐藏对应 `msedge.exe`/`chrome.exe` 聚合会话，仅由设置页的“浏览器增强时隐藏浏览器聚合会话”开关控制。
- 输出授权向导：系统设备选择、低音量短测试音、明确确认、名称不匹配二次确认、修改/删除/清空映射。
- Fluent 风格“混音器 / 浏览器增强 / 设置”导航；来源卡、10 段 EQ、长文本和 880×600 最小窗口使用响应式布局，来源列表保留 Recycling 并按像素连续滚动。
- 普通会话名称优先使用可读会话名、EXE FileDescription/ProductName 和已知应用映射，并异步缓存进程图标；浏览器增强标题显示浏览器、页面标题和域名。
- 中文界面优先使用微软雅黑 UI 并提供明确回退链；设置型开关以无对勾实心色块表示选中，鼠标和键盘焦点范围只包围方块与实际标签，不占用行尾空白。
- 新用户可选浏览器增强向导；旧用户升级不会被强制打扰，设置页可随时重新打开。扩展首次安装也只显示一次本地欢迎页。
- 可选用户级开机启动。默认关闭；安装版使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，可选择 `--background` 托盘启动。
- 平衡滑块在中心有 ±5 进入、±8 离开的迟滞吸附；只处理用户输入，不改写快照或配置恢复值。
- 正常或安装器请求退出时恢复音频；异常恢复使用本地回滚日志。

项目不录音、不保存 PCM、不上传数据、不安装虚拟声卡、不修改系统默认音频设备，也不需要管理员权限。普通 Windows 会话不支持 200% 增益或逐应用 EQ；这些能力只在用户主动启用的浏览器标签页 Web Audio 图中提供。

## 使用交付物

便携版：解压 `AudioSourceMixer-0.2.2-win-x64-portable.zip` 并运行 `AudioSourceMixer.exe`。

安装版：运行 `AudioSourceMixer-0.2.2-win-x64-setup.exe`。安装界面可选择路径、桌面快捷方式和开机启动，并可选择“安装完成后设置浏览器标签页增强”；该项默认关闭，静默安装也不会自行打开浏览器或桌面向导。默认路径是 `%LocalAppData%\Programs\AudioSourceMixer`，开机启动默认关闭。路径可包含空格或中文，但不能是磁盘根、Windows/Program Files、用户根或仓库根，也不会覆盖含无关文件的目录。

安装目录中的 `AudioSourceMixer.Uninstall.exe` 无参数双击会直接显示卸载页。默认保留用户设置；卸载会先通知正在运行的程序恢复音频并退出，再清理本产品的快捷方式、Native Messaging、卸载项和开机启动值。

## Chrome / Edge 增强

扩展固定 ID：`edbfelppckjcfhadggldaifbleoofkio`。

1. 安装版会注册 Native Messaging Host；便携版先运行 `scripts\register-native-host.ps1`。
2. 在桌面程序的“浏览器增强”页选择 Chrome 或 Edge；程序只调用对应浏览器并打开 `chrome://extensions/` 或 `edge://extensions/`。Chromium 151 冷启动若丢弃内部 URL，程序会在这次明确点击后通过真实地址栏完成导航并验证结果；失败时显示可复制的内部地址。
3. 当前扩展尚未发布到 Chrome Web Store 或 Edge Add-ons，因此需要在扩展管理页开启开发者模式并手动选择“加载已解压的扩展程序”。程序不会修改浏览器配置、企业策略或模拟安全确认。若以后配置官方商店页面，只接受与明确受信扩展 ID 匹配的官方 HTTPS URL。
4. 先启动 Audio Source Mixer，再在有声音的标签页点击扩展按钮。首次安装会显示一次扩展自带指南；阅读完成后返回原标签页再次点击。桌面程序未运行时扩展只提示“请先打开 Audio Source Mixer”，不会替你启动桌面程序。
5. 为增强标签页选择输出时，浏览器会打开设备管理页。点击系统设备选择器，试听约 0.7 秒的保守音量提示音，确认实际设备后才保存。“系统默认”保留为跟随语义，但每次解析为当前 Windows Multimedia 物理端点并使用其已授权的具体 browser `deviceId`，不会退回可能继承浏览器进程路由的空 sink。
6. 管理页可测试、修改/重新授权、删除单条映射，或只清除当前浏览器配置的全部映射。确认操作使用不可变快照和串行事务；映射已保存但标签通知失败时可以安全重试。
7. 在来源卡片展开“音效”，可启用平直、低频增强、人声清晰、高频增强、温暖或自定义 10 段 EQ。正增益会自动保留防削波余量，主音量数值不会被改写。

授权映射以浏览器 + Windows endpoint ID 为键，schema 3 保存验证状态。0.1.2 映射会迁移并保留，但标记为“未验证”，必须经过新试听流程才重新可信。扩展不请求 `audioCapture`、`<all_urls>` 或 host permissions。

## 构建与验证

需要 .NET 8 SDK 和 Node.js。交付物是 self-contained x64，不要求用户另装 .NET Runtime。

```powershell
.\scripts\test.ps1 -Configuration Release
.\scripts\package-portable.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
.\scripts\verify-installer.ps1 -BaselineInstallerPath .\artifacts\AudioSourceMixer-0.2.1-win-x64-setup.exe
```

一键完整构建：

```powershell
.\scripts\build-all.ps1
```

完整流程执行 Release restore/build、全部 .NET/Node 测试、系统 Chrome/Edge 隔离 profile 的扩展授权与 Web Audio 运行验证、源码/portable/installed WPF UI smoke、受控真实 WASAPI 会话到可见 WPF Indicator 的逐样本电平验证、严格发行 allowlist、默认/自定义路径安装、同版本 repair、故障回滚、0.2.1 原位升级、可选引导、开机启动、后台托盘、无参数卸载 UI、运行中优雅卸载、注册表清理，以及 publish/portable/installed SHA-256 和扩展清单比较。详见 [测试报告](docs/testing.md)、[隐私说明](docs/privacy.md)、[商店资料草案](CHROMEWEBSTORE.md) 和 [核心代码图](docs/core-code-map.md)。

## 数据与限制

设置、应用配置、回滚事务和日志位于 `%LocalAppData%\AudioSourceMixer`。设置与来源配置写入串行化并以临时文件原子替换；音频 profile 为 schema 3，界面设置迁移到 schema 7。schema 6 把旧 `recent` 迁移为永久手动顺序；schema 7 清除旧的 Edge/Chrome 强制显示例外，使自动隐藏开关重新成为唯一规则。设置只保存安全的 Windows 会话顺序和精确手动隐藏记录（各有容量/过期限制），不持久化浏览器临时 Tab ID、标签页标题或 URL。

- 应用是否立即迁移既有共享模式音频流由应用/音频引擎决定；`PendingStreamRestart` 通常需暂停/恢复或重启目标应用。
- 浏览器设备授权必须由可见页面中的用户手势触发；桌面程序不能直接读取其他浏览器配置文件的 `chrome.storage.local`。
- `setSinkId()` 成功和 endpoint meter 只能证明 API 状态，不能替代真人听感。
- 当前安装程序未签名，Windows 可能显示未知发布者。
