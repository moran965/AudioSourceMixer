# Audio Source Mixer

当前版本：`0.2.0`。这是面向 Windows 11 x64 的本地音频源控制工具，可分别控制 Windows Core Audio 会话及用户主动启用的 Chrome/Edge 增强标签页。

## 功能

- 普通 Windows 会话：0–100% 主音量、静音、声道平衡、峰值和按应用输出设备路由。
- 浏览器增强标签页：0–200% Web Audio 增益、平衡、静音和经用户授权的输出 sink；多标签页相互独立。
- 输出授权向导：系统设备选择、低音量短测试音、明确确认、名称不匹配二次确认、修改/删除/清空映射。
- 独立“混音器 / 设置”页；设置页管理窗口行为、记忆、浏览器授权、日志、安装目录和恢复默认。
- 可选用户级开机启动。默认关闭；安装版使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，可选择 `--background` 托盘启动。
- 平衡滑块在中心有 ±5 进入、±8 离开的迟滞吸附；只处理用户输入，不改写快照或配置恢复值。
- 正常或安装器请求退出时恢复音频；异常恢复使用本地回滚日志。

项目不录音、不保存 PCM、不上传数据、不安装虚拟声卡、不修改系统默认音频设备，也不需要管理员权限。普通 Windows 会话不支持 200% 增益。

## 使用交付物

便携版：解压 `AudioSourceMixer-0.2.0-win-x64-portable.zip` 并运行 `AudioSourceMixer.exe`。

安装版：运行 `AudioSourceMixer-0.2.0-win-x64-setup.exe`。安装界面可选择路径、桌面快捷方式和开机启动；默认路径是 `%LocalAppData%\Programs\AudioSourceMixer`，开机启动默认关闭。路径可包含空格或中文，但不能是磁盘根、Windows/Program Files、用户根或仓库根，也不会覆盖含无关文件的目录。

安装目录中的 `AudioSourceMixer.Uninstall.exe` 无参数双击会直接显示卸载页。默认保留用户设置；卸载会先通知正在运行的程序恢复音频并退出，再清理本产品的快捷方式、Native Messaging、卸载项和开机启动值。

## Chrome / Edge 增强

扩展固定 ID：`edbfelppckjcfhadggldaifbleoofkio`。

1. 安装版会注册 Native Messaging Host；便携版先运行 `scripts\register-native-host.ps1`。
2. 在 `chrome://extensions` 或 `edge://extensions` 开启开发者模式，加载 `BrowserExtension` 目录。
3. 先启动 Audio Source Mixer，再在有声音的标签页点击扩展按钮。桌面程序未运行时扩展只提示“请先打开 Audio Source Mixer”，不会替你启动桌面程序。
4. 为增强标签页选择非默认输出时，浏览器会打开设备管理页。点击系统设备选择器，试听约 0.7 秒的保守音量提示音，确认实际设备后才保存。
5. 管理页可测试、修改/重新授权、删除单条映射，或只清除当前浏览器配置的全部映射。

授权映射以浏览器 + Windows endpoint ID 为键，schema 3 保存验证状态。0.1.2 映射会迁移并保留，但标记为“未验证”，必须经过新试听流程才重新可信。扩展不请求 `audioCapture`、`<all_urls>` 或 host permissions。

## 构建与验证

需要 .NET 8 SDK 和 Node.js。交付物是 self-contained x64，不要求用户另装 .NET Runtime。

```powershell
.\scripts\test.ps1 -Configuration Release
.\scripts\package-portable.ps1 -Configuration Release
.\scripts\package-installer.ps1 -Configuration Release
.\scripts\verify-installer.ps1 -BaselineInstallerPath .\artifacts\AudioSourceMixer-0.1.2-win-x64-setup.exe
```

一键完整构建：

```powershell
.\scripts\build-all.ps1
```

完整流程执行 Release restore/build、全部 .NET/Node 测试、源码/portable/installed WPF UI smoke、默认/自定义路径安装、同版本 repair、故障回滚、0.1.2 原位升级、开机启动、后台托盘、无参数卸载 UI、运行中优雅卸载、注册表清理，以及 publish/portable/installed SHA-256 和扩展清单比较。详见 [测试报告](docs/testing.md) 和 [核心代码图](docs/core-code-map.md)。

## 数据与限制

设置、应用配置、回滚事务和日志位于 `%LocalAppData%\AudioSourceMixer`。设置写入串行化并以临时文件原子替换；0.1.2 JSON 字段会保留并迁移。

- 应用是否立即迁移既有共享模式音频流由应用/音频引擎决定；`PendingStreamRestart` 通常需暂停/恢复或重启目标应用。
- 浏览器设备授权必须由可见页面中的用户手势触发；桌面程序不能直接读取其他浏览器配置文件的 `chrome.storage.local`。
- `setSinkId()` 成功和 endpoint meter 只能证明 API 状态，不能替代真人听感。
- 当前安装程序未签名，Windows 可能显示未知发布者。
